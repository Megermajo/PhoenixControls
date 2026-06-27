using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Clipboard;
using Phoenix.Controls.Visualist.WinUI.Controls;
using Phoenix.Controls.Visualist.WinUI.Core;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

public sealed partial class WidgetEditorView : UserControl
{
    private VisualistViewModel? _vm;

    // ─── Live-preview / bus / transport wiring (Lane B — Tasks 3-8) ──────
    //
    // The pre-T15 WinForms WidgetEditorForm carried: a green Test Run button →
    // VisualistBusClient.SendVisualTriggerAsync; Preview Layer / Preview Widget
    // popouts; a floating WidgetSinglePreviewPanel; a TimelinePlayback transport;
    // and a bottom StatusStrip. This restores all of them on WinUI. Per
    // feedback_visualist_architect_chrome_independence the chrome stays
    // Visualist-local; per feedback_no_modal_dialogs_for_repeatable_rejections
    // failures + offline rejections route to the status bar / GlobalLogger, not
    // modal dialogs.

    // Design-time playback engine for the active trigger's timeline. Drives
    // VM.PlayheadMs per tick + bridges PLAY/SCRUB/STOP into the open preview
    // surfaces. Created lazily on first use; disposed on Unloaded.
    private TimelinePlayback? _playback;

    // Popout windows. R22 — the LAYER preview window is now owned by the pillar
    // shell (MainView.PreviewLayer / ActiveLayerPreviewWindow) so the editor
    // toolbar and the layer-canvas command bar share one instance; the editor
    // still owns the single-WIDGET preview popout.
    private Phoenix.Controls.Visualist.WinUI.Hosting.LayerPreviewWindow? _widgetPreviewWindow;

    // Bus + config subscriptions — detached on Unloaded so a recycled editor
    // doesn't keep responding to a disposed surface's events.
    private Action<bool>?   _onBusConnChanged;
    private Action?         _onConfigChanged;

    // Status-bar auto-clear timer (Area 5 P2) — resets the label to "Ready"
    // ~5s after the last SetStatus call.
    private DispatcherTimer? _statusClearTimer;

    // Trigger-tab drop insertion marker (Area 1 P2) — a thin vertical line added
    // to TabDropMarkerLayer during the armed drag, cleared on release.
    private Microsoft.UI.Xaml.Shapes.Rectangle? _tabDropMarker;

    // Sentinel — true once the timeline scrub handlers are subscribed. Set on
    // Loaded, cleared on Unloaded. Cheaper than the redraw-time detach/reattach
    // dance and rules out the "doubled-up handler" hazard a missed unsubscribe
    // would have produced.
    private bool _timelineHandlersSubscribed;

    // Last surface dimensions a RedrawTimeline ran against. SizeChanged fires
    // many times per frame during a window resize; a sub-pixel-delta guard
    // skips the full visual-tree rebuild when nothing actually changed.
    private double _lastTimelineSurfaceW = double.NaN;
    private double _lastTimelineSurfaceH = double.NaN;

    public WidgetEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
        // R28 — Delete-key keyframe removal. The view is IsTabStop so a timeline
        // interaction can focus it; the guarded handler only consumes Delete
        // when a keyframe is selected (otherwise it bubbles to the graph canvas).
        KeyDown += OnEditorKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeTimelineHandlers();
        InitPreviewAndTransport();
        // Re-navigate the (kept-alive) embedded preview on re-entry. Re-entering
        // the SAME widget doesn't change SelectedWidget, so the property-changed
        // ReloadEmbeddedPreview wouldn't fire on its own — without this a
        // re-entered editor would sit on the previous page until the next
        // selection change.
        ReloadEmbeddedPreview();
        // Bug #3 — repaint the timeline ruler on EVERY entry. RedrawTimeline is
        // otherwise only driven from OnDataContextChanged, which does NOT fire when
        // re-entering the same widget (unchanged DataContext) — so the ticks went
        // missing after enter → Back → re-enter. Deferred to a dispatcher tick so
        // the TimelineSurface has been arranged (non-zero ActualWidth) by the time
        // RedrawTimeline runs; the zero-size guard inside it makes a too-early call
        // harmless either way.
        DispatcherQueue?.TryEnqueue(RedrawTimeline);
    }

    // ─── Lane B init / teardown ──────────────────────────────────────────

    private void InitPreviewAndTransport()
    {
        // Playback engine — created on the UI thread (DispatcherTimer affinity).
        if (_playback is null)
        {
            _playback = new TimelinePlayback();
            _playback.OnTimeChanged    += OnPlaybackTimeChanged;
            _playback.OnPlayingChanged += OnPlaybackPlayingChanged;
        }
        SyncPlaybackTimeline();

        // Bus presence — paint the connection dot + gate Test Run.
        if (_onBusConnChanged is null)
        {
            _onBusConnChanged = OnBusConnectionChanged;
            VisualistBusClient.Instance.OnConnectionStatusChanged += _onBusConnChanged;
        }
        // The VisualistBusClient (Lane A foundation) connects to Hub's bus
        // (port 18081). Nothing in the WinUI assembly starts it today, so we
        // ensure it's running here — guarded process-wide so multiple editor
        // instances don't spawn duplicate connect loops. The CANONICAL start
        // belongs in the Visualist pillar bootstrap (MainView), which is NOT an
        // owned file for this task — REPORTED rather than edited. Starting it
        // here is idempotent via the guard and harmless when Hub is unreachable
        // (the connect loop just retries every 5s; the dot stays red, Test Run
        // stays disabled).
        EnsureBusStarted();
        ApplyBusConnectionState(VisualistBusClient.Instance.IsConnected);

        // Config — embedded-preview visibility + popout-button gating.
        if (_onConfigChanged is null)
        {
            _onConfigChanged = OnPreviewConfigChanged;
            VisualistUserConfig.Instance.OnChanged += _onConfigChanged;
        }

        // Wire the embedded preview pane once.
        if (EmbeddedPreview is not null)
        {
            EmbeddedPreview.OnManipulatorAttrChanged -= OnManipulatorAttrChanged;
            EmbeddedPreview.OnManipulatorAttrChanged += OnManipulatorAttrChanged;
            EmbeddedPreview.OnResized -= OnEmbeddedPreviewResized;
            EmbeddedPreview.OnResized += OnEmbeddedPreviewResized;
            // Bug #5 — let the embedded preview forward Ctrl+Z / Ctrl+Y to the
            // document when its WebView2 holds focus (manipulator drags leave focus
            // there, so the canvas hotkey can't see the keypress).
            EmbeddedPreview.OnUndoRequested -= OnPreviewUndoRequested;
            EmbeddedPreview.OnUndoRequested += OnPreviewUndoRequested;
            EmbeddedPreview.OnRedoRequested -= OnPreviewRedoRequested;
            EmbeddedPreview.OnRedoRequested += OnPreviewRedoRequested;
            // Restore the persisted width.
            EmbeddedPreview.Width = Math.Clamp(
                VisualistUserConfig.Instance.EditorPreviewWidth,
                Controls.WidgetSinglePreviewPanel.MinPreviewWidth,
                Controls.WidgetSinglePreviewPanel.MaxPreviewWidth);
        }

        // R6 — forward graph-canvas node selection into the embedded preview's
        // manipulator overlay, so selecting a spatial node lights its drag handles
        // (parity with the WinForms WidgetEditorForm._canvas.OnSelectedNodeChanged
        // → _widgetPreview.SetActiveNode wire). WidgetGraphCanvas now exposes
        // OnSelectedNodeChanged (R6). Idempotent across Loaded cycles. The inbound
        // ATTR_CHANGED → node merge (OnManipulatorAttrChanged) was already wired;
        // this closes the outbound select → manipulator direction.
        GraphCanvas.OnSelectedNodeChanged -= OnGraphNodeSelectionChanged;
        GraphCanvas.OnSelectedNodeChanged += OnGraphNodeSelectionChanged;

        ApplyEmbeddedPreviewVisibility();
        ApplyButtonGating();

        // Track E — rebuild the audio-mixer rows each time its flyout opens so it
        // always reflects the live active trigger. Idempotent across Loaded cycles.
        // Seed once now too: on some WinUI builds Opening doesn't fire before the
        // first show (same quirk WidgetGraphCanvas's node flyout guards against).
        if (AudioMixerFlyout is not null)
        {
            AudioMixerFlyout.Opening -= OnAudioMixerOpening;
            AudioMixerFlyout.Opening += OnAudioMixerOpening;
            RebuildAudioMixer();
        }
    }

    private void SubscribeTimelineHandlers()
    {
        if (_timelineHandlersSubscribed) return;
        TimelineSurface.PointerPressed      += OnTimelineScrubPressed;
        TimelineSurface.PointerMoved        += OnTimelineScrubMoved;
        TimelineSurface.PointerReleased     += OnTimelineScrubReleased;
        // QC52-03: Ctrl+wheel zoom anchored on the pointer, plain wheel pans
        // the visible window when zoomed in past fit-to-width.
        TimelineSurface.PointerWheelChanged += OnTimelineWheelChanged;
        // R28 — double-click an empty track adds a keyframe on that row.
        TimelineSurface.DoubleTapped        += OnTimelineDoubleTapped;
        _timelineHandlersSubscribed = true;
    }

    private void UnsubscribeTimelineHandlers()
    {
        if (!_timelineHandlersSubscribed) return;
        TimelineSurface.PointerPressed      -= OnTimelineScrubPressed;
        TimelineSurface.PointerMoved        -= OnTimelineScrubMoved;
        TimelineSurface.PointerReleased     -= OnTimelineScrubReleased;
        TimelineSurface.PointerWheelChanged -= OnTimelineWheelChanged;
        TimelineSurface.DoubleTapped        -= OnTimelineDoubleTapped;
        _timelineHandlersSubscribed = false;
    }

    // Theme-key + literal-ARGB fallback for tinted brushes. Per-pillar copy
    // of WidgetGraphCanvas.ResolveBrush per
    // feedback_visualist_architect_chrome_independence (Visualist owns its own
    // paint helpers, never lifted to Shared). Theme lookup wins; the literal
    // ARGB triple is the designer / pre-app / missing-key fallback.
    private static Brush ResolveBrushTinted(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush sb)
            {
                return new SolidColorBrush(Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B));
            }
        }
        catch { /* designer / pre-app — fall through to literal */ }
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    // [LANE-C P2] Safe theme-brush resolve with a literal-ARGB fallback. The
    // RedrawTimeline path previously mixed safe (ResolveBrushTinted) and UNSAFE
    // direct `(Brush)Application.Current.Resources[key]` casts — a missing key or
    // a non-Brush value (theme swap, designer load, resource-injection test)
    // throws InvalidCastException / KeyNotFoundException and aborts the whole
    // redraw. This mirrors CurveEditorDialog.ResolveBrush: theme lookup wins, the
    // ARGB triple is the fallback. Per feedback_visualist_architect_chrome_
    // independence this is a per-pillar copy, never lifted to Shared.
    private static Brush ResolveBrush(string key, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is Brush brush)
            {
                return brush;
            }
        }
        catch { /* designer / pre-app — fall through to literal */ }
        return new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Detach the VM PropertyChanged on tear-down so a recycled editor
        // view doesn't leave a stale handler pinning it through the VM.
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }
        // Drop the timeline pointer handlers too — paired with Loaded.
        UnsubscribeTimelineHandlers();
        // Zoom state is session-local; drop it on unload so a recycled
        // editor view doesn't leak the dictionary across re-mounts.
        _zoomByTrigger.Clear();
        _zoom = null;

        // ─── Lane B teardown ──────────────────────────────────────────────
        if (_onBusConnChanged is not null)
        {
            try { VisualistBusClient.Instance.OnConnectionStatusChanged -= _onBusConnChanged; } catch { }
            _onBusConnChanged = null;
        }
        if (_onConfigChanged is not null)
        {
            try { VisualistUserConfig.Instance.OnChanged -= _onConfigChanged; } catch { }
            _onConfigChanged = null;
        }
        if (_playback is not null)
        {
            try { _playback.OnTimeChanged    -= OnPlaybackTimeChanged; } catch { }
            try { _playback.OnPlayingChanged -= OnPlaybackPlayingChanged; } catch { }
            try { _playback.Dispose(); } catch { }
            _playback = null;
        }
        // R6 — drop the canvas node-selection forward.
        try { GraphCanvas.OnSelectedNodeChanged -= OnGraphNodeSelectionChanged; } catch { }
        if (EmbeddedPreview is not null)
        {
            try { EmbeddedPreview.OnManipulatorAttrChanged -= OnManipulatorAttrChanged; } catch { }
            try { EmbeddedPreview.OnResized -= OnEmbeddedPreviewResized; } catch { }
            try { EmbeddedPreview.OnUndoRequested -= OnPreviewUndoRequested; } catch { }
            try { EmbeddedPreview.OnRedoRequested -= OnPreviewRedoRequested; } catch { }
            // Do NOT Shutdown() the embedded WebView2 here. This editor view is a
            // REUSED singleton (MainView swaps it in/out of the content pane on
            // every enter/exit-widget), so Unloaded fires on every "Back". Closing
            // the WebView2 here left _coreInitialized=true with a now-disposed
            // CoreWebView2, so the NEXT enter showed "WebView2 did not initialise"
            // (a Closed WinUI WebView2 can't be re-initialised in place — Majo's
            // "open a widget (works), exit, re-enter → crashes"). Keep it alive
            // across recycle; OnLoaded re-navigates it. The single WebView2 is
            // released with the pillar window on real teardown.
        }
        try { _statusClearTimer?.Stop(); } catch { }
        _statusClearTimer = null;

        // Track E — drop the audio-mixer flyout subscription.
        if (AudioMixerFlyout is not null)
        {
            try { AudioMixerFlyout.Opening -= OnAudioMixerOpening; } catch { }
        }

        // Close the editor-owned widget popout so its WebView2 process tears down
        // with the editor. The shared LAYER preview window (R22) is owned by the
        // pillar shell, which closes it on its own teardown.
        try { _widgetPreviewWindow?.Close(); } catch { }
        _widgetPreviewWindow = null;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            old.NodeParamCommitted -= OnNodeParamCommitted;
            old.NodeBodyCommitted -= OnNodeBodyCommitted;
        }
        _vm = args.NewValue as VisualistViewModel;
        if (_vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            // Bug #3 — the VM raised NodeParamCommitted on every Inspector edit but
            // nothing was wired to it (the "wired by WidgetEditorView" comment on the
            // event was aspirational), so right-pane edits never reached the node-body
            // inline pills. Subscribe so the two surfaces stay in sync.
            vm.NodeParamCommitted += OnNodeParamCommitted;
            // Live-preview bridge (Majo): an inline body-pill edit (NodeBodyCommitted)
            // and an Inspector edit (NodeParamCommitted) must BOTH re-render the
            // embedded preview — a manipulator drag updates the preview in-page, but a
            // typed TranslateX/Y / Scale edit previously only refreshed the pills and
            // never reached compositor.js, so the preview "did nothing". Subscribe the
            // body path too (the Inspector listens to NodeBodyCommitted for its own
            // echo, but nothing pushed it to the preview).
            vm.NodeBodyCommitted += OnNodeBodyCommitted;
            RebuildTriggerTabs(vm);
            RebuildGraphView(vm);
            UpdateTimelineHeader(vm);
            RedrawTimeline();
            SyncPlaybackTimeline();
            ApplyButtonGating();
            ReloadEmbeddedPreview();
            ApplyEmptyState();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VisualistViewModel vm) return;
        switch (e.PropertyName)
        {
            case nameof(VisualistViewModel.SelectedWidget):
                RebuildTriggerTabs(vm);
                RebuildGraphView(vm);
                UpdateTimelineHeader(vm);
                RedrawTimeline();
                SyncPlaybackTimeline();
                ApplyButtonGating();
                ReloadEmbeddedPreview();
                ApplyEmptyState();
                break;
            case nameof(VisualistViewModel.ActiveTrigger):
            case nameof(VisualistViewModel.ActiveTriggerObject):
                // Drop any keyframe selection/drag bound to the OUTGOING trigger
                // before the redraw repaints markers from the new trigger's
                // timeline. Without this, a mid-drag trigger switch would leave
                // _draggingKeyframe pointing at a Keyframe from the old timeline,
                // so a subsequent PointerMoved would mutate (and dirty) the wrong
                // trigger — a silent lost-edit.
                _selectedKeyframe  = null;
                _draggingKeyframe  = null;
                _keyframeDragDirty = false;
                SyncTriggerTabSelection(vm);
                RebuildGraphView(vm);
                UpdateTimelineHeader(vm);
                RedrawTimeline();
                // Switching triggers resets playback to the new trigger's t=0 so
                // the preview shows the new trigger at its start.
                _playback?.Stop();
                SyncPlaybackTimeline();
                // Track C — tell the embedded in-widget preview which trigger is
                // now being edited so it renders THAT trigger (not the static
                // onStartup its ?widget= page loads with) at t=0. The panel tracks
                // its own widget id, so only the trigger name is passed; it posts
                // SET_ACTIVE_TRIGGER and re-renders the new trigger's start frame.
                EmbeddedPreview?.SetActiveTrigger(ActiveTriggerName());
                break;
            case nameof(VisualistViewModel.ActiveTriggerDurationLabel):
                UpdateTimelineHeader(vm);
                RedrawTimeline();
                SyncPlaybackTimeline();
                break;
            case nameof(VisualistViewModel.PlayheadMs):
                // Scrub-driven; the scrubber repositions its own line + halo for
                // smoothness, so a full redraw isn't needed. Keep the playback
                // engine in step when the scrub came from the user (not from a
                // playback tick — guarded inside SyncPlaybackToPlayhead).
                SyncPlaybackToPlayhead();
                break;
        }
    }

    // ─── trigger tabs ───────────────────────────────────────────────────

    private void RebuildTriggerTabs(VisualistViewModel vm)
    {
        TriggerTabStrip.Children.Clear();

        // R13 — leading "← Back" returns to the Layer Canvas, matching the
        // manifesto §4.5 "[← Back] [onStartup] … [+]" strip and the pre-WinUI
        // WidgetEditorForm "Back to Layout" button. Under the 2-tab shell the
        // Layer Canvas tab is also always visible, but Back reinforces the
        // "entered a widget" model and is the gesture authors expect.
        var back = new Button
        {
            Content          = "← Back",
            Padding          = new Thickness(8, 0, 8, 0),
            Background       = ResolveBrushTinted("CoalCardBrush", 0xFF, 0x2A, 0x26, 0x20),
            BorderThickness  = new Thickness(0),
            Foreground       = ResolveBrushTinted("CoalBodyTextBrush", 0xFF, 0xC9, 0xC2, 0xB6),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin           = new Thickness(0, 0, 6, 0),
        };
        ToolTipService.SetToolTip(back, "Back to the Layer Canvas");
        back.Click += (s, e) => FindPillarMainView()?.ShowLayerCanvas();
        TriggerTabStrip.Children.Add(back);

        foreach (string name in vm.TriggerNames)
        {
            string capture = name;
            var tab = new ToggleButton
            {
                Content   = capture,
                IsChecked = string.Equals(capture, vm.ActiveTrigger, StringComparison.OrdinalIgnoreCase),
                Style     = (Style)Application.Current.Resources["TriggerTabButtonStyle"],
                ContextFlyout = BuildTriggerContextFlyout(vm, capture),
            };
            tab.Click += (s, e) => vm.ActiveTrigger = capture;

            // B34 — drag-to-reorder. WinUI's StackPanel doesn't ship a reorder
            // facility, so we wire pointer-based detection per tab: a press
            // captures the source index + pointer, a move past a horizontal
            // threshold opens the drag (the tab's Click handler still fires if
            // the move is too small), and release calls vm.ReorderTrigger.
            tab.Tag = capture;
            tab.PointerPressed  += OnTriggerTabPointerPressed;
            tab.PointerMoved    += OnTriggerTabPointerMoved;
            tab.PointerReleased += OnTriggerTabPointerReleased;
            tab.PointerCaptureLost += OnTriggerTabPointerCaptureLost;

            TriggerTabStrip.Children.Add(tab);
        }

        // Trailing "+" — opens a TextBox dialog to create a new trigger.
        var add = new ToggleButton
        {
            Content = "+",
            Style   = (Style)Application.Current.Resources["TriggerTabButtonStyle"],
        };
        add.Click += async (s, e) =>
        {
            add.IsChecked = false;
            await PromptAddTrigger(vm);
        };
        // Disable the "+" if no widget is selected — adding triggers without a
        // host widget is meaningless.
        add.IsEnabled = vm.SelectedWidget is not null;
        TriggerTabStrip.Children.Add(add);
    }

    // ─── B34 trigger tab drag-to-reorder ────────────────────────────────
    //
    // Plain ToggleButton drag (no WinUI TabView reorder facility available
    // here — the tab strip is a custom StackPanel). Track the source tab on
    // press; once the pointer crosses _dragThresholdPx horizontally, we're in
    // a drag and the release maps the drop X back to a tab index via the
    // children's bounds. The undo / dirty discipline lives in
    // VisualistViewModel.ReorderTrigger.

    private const double _dragThresholdPx = 6.0;
    private ToggleButton? _dragSourceTab;
    private string?       _dragSourceName;
    private double        _dragStartX;
    private bool          _dragArmed;

    private void OnTriggerTabPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ToggleButton tab) return;
        if (tab.Tag is not string name) return;
        // Only left-button drags. Right-click opens the context flyout via the
        // ToggleButton's ContextFlyout property, so we can't shadow that here.
        var pp = e.GetCurrentPoint(TriggerTabStrip);
        if (!pp.Properties.IsLeftButtonPressed) return;
        _dragSourceTab  = tab;
        _dragSourceName = name;
        _dragStartX     = pp.Position.X;
        _dragArmed      = false;
    }

    private void OnTriggerTabPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSourceTab is null || _dragSourceName is null) return;
        var pp = e.GetCurrentPoint(TriggerTabStrip);
        if (!pp.Properties.IsLeftButtonPressed) return;
        double dx = pp.Position.X - _dragStartX;
        if (!_dragArmed && Math.Abs(dx) >= _dragThresholdPx)
        {
            _dragArmed = true;
            // Visual cue — dim the source tab while a drag is in flight.
            _dragSourceTab.Opacity = 0.45;
            try { _dragSourceTab.CapturePointer(e.Pointer); } catch { /* best-effort */ }
        }
        // Area 1 P2 — once armed, render an insertion marker at the computed
        // drop slot so the user can see where the tab will land. The marker is
        // an absolutely-positioned line in TabDropMarkerLayer (a Canvas sibling
        // of the StackPanel) so it doesn't disturb the tab layout.
        if (_dragArmed)
        {
            UpdateTabDropMarker(pp.Position.X);
        }
    }

    // Compute the drop index the same way OnTriggerTabPointerReleased does, then
    // place a thin vertical marker at the left edge of that slot's tab (or the
    // right edge of the last tab for an append). Cleared by ResetDragState.
    private void UpdateTabDropMarker(double dropX)
    {
        if (TabDropMarkerLayer is null) return;

        double markerX = 0;
        bool placed = false;
        for (int i = 0; i < TriggerTabStrip.Children.Count; i++)
        {
            if (TriggerTabStrip.Children[i] is not ToggleButton tb) continue;
            if (tb.Tag is not string) continue; // skip the "+" sentinel
            var transform = tb.TransformToVisual(TriggerTabStrip);
            var origin = transform.TransformPoint(new Point(0, 0));
            double center = origin.X + tb.ActualWidth / 2.0;
            if (dropX < center)
            {
                markerX = origin.X;
                placed = true;
                break;
            }
            // Track the right edge so a drop past the last tab marks the append slot.
            markerX = origin.X + tb.ActualWidth;
        }
        // (placed == false → markerX already holds the last tab's right edge.)
        _ = placed;

        if (_tabDropMarker is null)
        {
            _tabDropMarker = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width  = 2,
                Fill   = (Brush)Application.Current.Resources["EmberPrimaryBrush"],
                IsHitTestVisible = false,
            };
            TabDropMarkerLayer.Children.Add(_tabDropMarker);
        }
        _tabDropMarker.Height = Math.Max(2, TriggerTabStrip.ActualHeight > 0 ? TriggerTabStrip.ActualHeight : 24);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_tabDropMarker, Math.Max(0, markerX - 1));
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_tabDropMarker, 0);
    }

    private void ClearTabDropMarker()
    {
        if (_tabDropMarker is null) return;
        try { TabDropMarkerLayer?.Children.Remove(_tabDropMarker); } catch { }
        _tabDropMarker = null;
    }

    private void OnTriggerTabPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSourceTab is null || _dragSourceName is null)
        {
            ResetDragState();
            return;
        }
        try
        {
            if (sender is ToggleButton src)
            {
                try { src.ReleasePointerCapture(e.Pointer); } catch { /* best-effort */ }
            }
            if (!_dragArmed || _vm is null)
            {
                return;
            }
            // Resolve drop index: walk the trigger tabs (skip the trailing "+")
            // and find the slot whose center lies just to the right of the drop
            // X. A drop past the last tab clamps to the last reorderable slot.
            double dropX = e.GetCurrentPoint(TriggerTabStrip).Position.X;
            int srcIdx = -1;
            int dstIdx = -1;
            int tabIndex = 0;
            for (int i = 0; i < TriggerTabStrip.Children.Count; i++)
            {
                if (TriggerTabStrip.Children[i] is not ToggleButton tb) continue;
                if (tb.Tag is not string) continue; // skip the "+" sentinel
                if (ReferenceEquals(tb, _dragSourceTab)) srcIdx = tabIndex;
                var transform = tb.TransformToVisual(TriggerTabStrip);
                var origin = transform.TransformPoint(new Point(0, 0));
                double center = origin.X + tb.ActualWidth / 2.0;
                if (dstIdx < 0 && dropX < center) dstIdx = tabIndex;
                tabIndex++;
            }
            int triggerCount = _vm.SelectedWidget?.Triggers.Count ?? 0;
            if (dstIdx < 0) dstIdx = Math.Max(0, triggerCount - 1);
            if (srcIdx >= 0 && dstIdx >= 0 && srcIdx != dstIdx)
            {
                // When dragging right-ward past intermediate tabs, the visible
                // drop slot is one past the source index — RemoveAt+Insert
                // already accounts for the shift, so pass dstIdx as-is.
                _vm.ReorderTrigger(srcIdx, dstIdx);
            }
        }
        finally
        {
            ResetDragState();
        }
    }

    private void OnTriggerTabPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetDragState();
    }

    private void ResetDragState()
    {
        if (_dragSourceTab is not null)
        {
            _dragSourceTab.Opacity = 1.0;
        }
        // Area 1 P2 — drop the insertion marker on release / cancel.
        ClearTabDropMarker();
        _dragSourceTab  = null;
        _dragSourceName = null;
        _dragArmed      = false;
        _dragStartX     = 0;
    }

    private MenuFlyout BuildTriggerContextFlyout(VisualistViewModel vm, string triggerName)
    {
        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Localizer.T("common.context.rename", "Rename…") };
        rename.Click += async (s, e) => await PromptRenameTrigger(vm, triggerName);
        flyout.Items.Add(rename);

        var delete = new MenuFlyoutItem { Text = Localizer.T("common.context.delete", "Delete") };
        delete.Click += async (s, e) => await ConfirmDeleteTrigger(vm, triggerName);
        flyout.Items.Add(delete);

        // B34 (audit 2026-05-24) — Duplicate / Move Left / Move Right. Duplicate
        // deep-clones the trigger including graph + timeline with an _copy
        // suffix (underscore — the hyphen the audit specced fails the
        // WidgetTrigger.Name regex; see VisualistViewModel.DuplicateTrigger).
        // Move Left/Right shift the trigger in its parent collection; both
        // grey out at the strip edges via IsEnabled below.
        flyout.Items.Add(new MenuFlyoutSeparator());

        int idx = vm.SelectedWidget?.Triggers.FindIndex(t =>
            string.Equals(t.Name, triggerName, StringComparison.OrdinalIgnoreCase)) ?? -1;
        int count = vm.SelectedWidget?.Triggers.Count ?? 0;

        var duplicate = new MenuFlyoutItem
        {
            Text = Localizer.T("common.context.duplicate", "Duplicate"),
        };
        duplicate.Click += (s, e) => vm.DuplicateTrigger(triggerName);
        flyout.Items.Add(duplicate);

        var moveLeft = new MenuFlyoutItem
        {
            Text     = Localizer.T("visualist.context.move_left",  "Move Left"),
            IsEnabled = idx > 0,
        };
        moveLeft.Click += (s, e) => vm.MoveTriggerLeft(triggerName);
        flyout.Items.Add(moveLeft);

        var moveRight = new MenuFlyoutItem
        {
            Text     = Localizer.T("visualist.context.move_right", "Move Right"),
            IsEnabled = idx >= 0 && idx < count - 1,
        };
        moveRight.Click += (s, e) => vm.MoveTriggerRight(triggerName);
        flyout.Items.Add(moveRight);

        // Sprint B — cross-pillar snippet producer. Right-clicking a trigger
        // tab and choosing this menu item writes a VisualTriggerSnippet payload
        // to the system clipboard; the Architect canvas's Ctrl+V handler
        // (LogicCanvasView.TryPasteVisualTriggerSnippetAsync) spawns a fully-
        // attributed Visual.Trigger node referencing this layer/widget/trigger.
        // Separator keeps the rename/delete pair visually grouped above the
        // less-frequently-used clipboard action.
        flyout.Items.Add(new MenuFlyoutSeparator());
        var copySnippet = new MenuFlyoutItem
        {
            Text = Localizer.T(
                "visualist.context.copy_architect_snippet",
                "Copy as Architect snippet"),
        };
        // [LANE-C P3] onStartup is semantically auto-fired — it cannot be called
        // from Architect via a Visual.Trigger node (which is exactly what the
        // snippet produces), so copying a snippet for it would author a node that
        // never matches a callable trigger. Disable the item for onStartup and
        // explain why via tooltip, mirroring how the old InspectorPanel hid
        // Copy/Test for onStartup. (Per feedback_no_modal_dialogs_for_repeatable_
        // rejections we disable rather than pop a rejection dialog.)
        bool isStartup = string.Equals(triggerName, "onStartup", StringComparison.OrdinalIgnoreCase);
        if (isStartup)
        {
            copySnippet.IsEnabled = false;
            ToolTipService.SetToolTip(copySnippet, Localizer.T(
                "visualist.context.copy_architect_snippet.startup_na",
                "onStartup fires automatically and cannot be called from an Architect Visual.Trigger node."));
        }
        else
        {
            copySnippet.Click += (s, e) => OnCopyArchitectSnippetClicked(vm, triggerName);
        }
        flyout.Items.Add(copySnippet);

        return flyout;
    }

    /// <summary>
    /// Snippet-producer dispatch. Resolves the named trigger on the currently-
    /// selected widget (the context flyout binds the trigger name at build
    /// time, not the live ActiveTrigger, so a right-click on a non-active tab
    /// still copies the snippet for THAT tab's trigger) and routes through
    /// <see cref="VisualTriggerSnippetProducer.CopyToClipboard"/>. All failure
    /// paths log via GlobalLogger — no modal dialogs, per
    /// feedback_no_modal_dialogs_for_repeatable_rejections.
    /// </summary>
    private void OnCopyArchitectSnippetClicked(VisualistViewModel vm, string triggerName)
    {
        LayerWidget? widget = vm.SelectedWidget;
        WidgetTrigger? trigger = widget?.Triggers.FirstOrDefault(t =>
            string.Equals(t.Name, triggerName, StringComparison.OrdinalIgnoreCase));
        string? layerPath = vm.Document?.FilePath;
        VisualTriggerSnippetProducer.CopyToClipboard(layerPath, widget, trigger);
    }

    private async System.Threading.Tasks.Task PromptAddTrigger(VisualistViewModel vm)
    {
        if (vm.SelectedWidget is null)
        {
            // R8/R1 — the "+" is disabled without a selected widget, but guard
            // anyway and tell the user why instead of silently no-oping.
            SetStatus("Select or enter a widget before adding a trigger.");
            return;
        }
        string? name = await PromptForName("New Trigger", "onTrigger:new", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        // R15 — vm.AddTrigger returns null on empty / duplicate / invalid name.
        // The pre-fix code discarded the result, so a rejected add looked like a
        // dead "+". A bare name like "raid" is now auto-prefixed to onTrigger:raid,
        // so only genuinely-bad input fails — and we surface the SPECIFIC reason.
        if (vm.AddTrigger(name, out var status) is null)
        {
            SetStatus(status == VisualistViewModel.AddTriggerStatus.Duplicate
                ? $"A trigger named \"{VisualistViewModel.NormalizeTriggerName(name)}\" already exists."
                : $"\"{name.Trim()}\" isn't a valid trigger name — use letters, digits and underscores (e.g. onTrigger:raid).");
        }
    }

    private async System.Threading.Tasks.Task PromptRenameTrigger(VisualistViewModel vm, string oldName)
    {
        string? name = await PromptForName("Rename Trigger", oldName, oldName);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, oldName, StringComparison.Ordinal)) return;
        if (!vm.RenameTrigger(oldName, name))
        {
            SetStatus("Rename failed — name is invalid or in use.");
            return;
        }
        // [LANE-C P2] Zoom-state key follows the rename. _zoomByTrigger is keyed
        // on "{widget.Id}|{trigger.Name}" (a USER-EDITABLE name, not a stable id).
        // A rename leaves the old key orphaned AND seeds a fresh fit-to-width zoom
        // under the new key — the user's zoom level silently resets on rename.
        // Migrating the entry preserves the zoom and prunes the stale key.
        MigrateZoomKeyForRename(vm.SelectedWidget, oldName, name.Trim());
    }

    // [LANE-C P2/P3] Move the zoom entry from the old trigger-name key to the new
    // one after a rename, so a rename keeps the user's zoom and doesn't strand a
    // stale dictionary entry. No-op when the old key has no stored state.
    private void MigrateZoomKeyForRename(LayerWidget? widget, string oldName, string newName)
    {
        if (widget is null) return;
        string oldKey = $"{widget.Id}|{oldName}";
        string newKey = $"{widget.Id}|{newName}";
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;
        if (_zoomByTrigger.TryGetValue(oldKey, out var state))
        {
            _zoomByTrigger.Remove(oldKey);
            // The new active trigger will resolve against newKey on the next
            // RedrawTimeline; pre-seed it so the existing zoom carries over.
            _zoomByTrigger[newKey] = state;
        }
    }

    private async System.Threading.Tasks.Task ConfirmDeleteTrigger(VisualistViewModel vm, string name)
    {
        if (XamlRoot is null) return;
        var dlg = new ContentDialog
        {
            XamlRoot           = XamlRoot,
            Title              = "Delete trigger?",
            Content            = $"This deletes trigger \"{name}\" — its graph and timeline are removed. Undo restores them.",
            PrimaryButtonText  = "Delete",
            CloseButtonText    = "Cancel",
            DefaultButton      = ContentDialogButton.Close,
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary) vm.RemoveTrigger(name);
    }

    private async System.Threading.Tasks.Task<string?> PromptForName(string title, string placeholder, string initial)
    {
        if (XamlRoot is null) return null;
        var input = new TextBox
        {
            PlaceholderText = placeholder,
            Text            = initial,
            FontFamily      = new Microsoft.UI.Xaml.Media.FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws
        };
        var dlg = new ContentDialog
        {
            XamlRoot           = XamlRoot,
            Title              = title,
            Content            = input,
            PrimaryButtonText  = "OK",
            CloseButtonText    = "Cancel",
            DefaultButton      = ContentDialogButton.Primary,
        };
        var res = await dlg.ShowAsync();
        return res == ContentDialogResult.Primary ? input.Text : null;
    }

    private void SyncTriggerTabSelection(VisualistViewModel vm)
    {
        foreach (UIElement child in TriggerTabStrip.Children)
        {
            if (child is not ToggleButton tab) continue;
            if (tab.Content is string text && !string.Equals(text, "+", StringComparison.Ordinal))
                tab.IsChecked = string.Equals(text, vm.ActiveTrigger, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─── graph view ─────────────────────────────────────────────────────
    //
    // Sprint A swapped the read-only stack-of-cards for an interactive
    // [WidgetGraphCanvas](WidgetGraphCanvas.xaml.cs) — pan / zoom / node
    // drag against absolute Location. The canvas owns its own empty-state
    // hint, so this method just hands it the active VM + trigger; the
    // canvas re-renders nodes whenever Bind is called.
    //
    // Sprints B–D layer wire-drop, palette, lasso, context menus, live
    // preview thumbnails, and timeline scrubbing on top — see the parity
    // plan §T13b for cadence and the canvas's own header for the seam map.

    private void RebuildGraphView(VisualistViewModel vm)
    {
        GraphCanvas.Bind(vm, vm.SelectedWidget is null ? null : vm.ActiveTriggerObject);
    }

    // ─── timeline header ────────────────────────────────────────────────

    private void UpdateTimelineHeader(VisualistViewModel vm)
    {
        TimelineHeaderLabel.Text = vm.ActiveTriggerDurationLabel;
        SyncDurationBox();
    }

    // ─── R5 — trigger duration editor + Fit ──────────────────────────────
    //
    // Restores the pre-WinUI TimelinePanel _numDuration NumericUpDown +
    // _btnFitDuration button. Writes Timeline.DurationMs through PushUndo +
    // MarkDirty + NotifyActiveTriggerChanged so the header label, the playable
    // range, and the ruler all refresh together.

    // Guards the programmatic DurationBox.Value write in SyncDurationBox from
    // bouncing back through OnDurationChanged as a phantom user edit.
    private bool _suppressDurationEcho;

    private void SyncDurationBox()
    {
        if (DurationBox is null) return;
        var tl = _vm?.ActiveTriggerObject?.Timeline;
        _suppressDurationEcho = true;
        try { DurationBox.Value = tl is null ? double.NaN : tl.DurationMs; }
        finally { _suppressDurationEcho = false; }
        DurationBox.IsEnabled = tl is not null;
        if (FitButton is not null) FitButton.IsEnabled = tl is not null;
    }

    private void OnDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_suppressDurationEcho) return;
        if (_vm?.ActiveTriggerObject?.Timeline is not WidgetTimeline tl) return;
        double v = double.IsNaN(e.NewValue) ? 0 : Math.Clamp(e.NewValue, 0, 600000);
        double ms = Math.Round(v);
        if (Math.Abs(ms - tl.DurationMs) < 0.5) return;
        _vm.Document?.PushUndo();
        tl.DurationMs = ms;
        _vm.Document?.MarkDirty();
        _vm.NotifyActiveTriggerChanged();   // refreshes header label + ActiveTriggerObject
        RedrawTimeline();
    }

    private void OnFitDurationClicked(object sender, RoutedEventArgs e)
    {
        if (_vm?.ActiveTriggerObject?.Timeline is not WidgetTimeline tl)
        {
            SetStatus("No trigger selected.");
            return;
        }
        double maxKf = tl.SortedKeyframes.Count > 0 ? tl.SortedKeyframes.Max(k => k.TimeMs) : 0;
        // Last keyframe + 200ms tail, rounded up to the next 100ms; floor 1000ms
        // (mirrors the pre-WinUI FitDurationToKeyframes math).
        double fit = Math.Ceiling((maxKf + 200) / 100.0) * 100.0;
        if (fit < 1000) fit = 1000;
        if (Math.Abs(fit - tl.DurationMs) < 0.5) { SetStatus("Duration already fits."); return; }
        _vm.Document?.PushUndo();
        tl.DurationMs = fit;
        _vm.Document?.MarkDirty();
        _vm.NotifyActiveTriggerChanged();
        SyncDurationBox();
        RedrawTimeline();
        SetStatus($"Duration fit to {fit:0} ms.");
    }

    // ─── R8 — empty-state + R13 Back navigation ──────────────────────────

    /// <summary>R8 — show the "no widget selected" hint over the graph when
    /// there's no widget to edit; otherwise hide it.</summary>
    private void ApplyEmptyState()
    {
        if (NoWidgetEmptyState is null) return;
        NoWidgetEmptyState.Visibility =
            _vm?.SelectedWidget is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEmptyStateBackClicked(object sender, RoutedEventArgs e)
        => FindPillarMainView()?.ShowLayerCanvas();

    // R13 — Visualist-local copy of the visual-tree walk to the embedding
    // MainView (per feedback_visualist_architect_chrome_independence — copied,
    // not lifted to Shared). Mirrors LayerCanvasView.FindPillarMainView.
    private Phoenix.Controls.Visualist.WinUI.MainView? FindPillarMainView()
    {
        DependencyObject? cur = this;
        while (cur is not null)
        {
            cur = VisualTreeHelper.GetParent(cur);
            if (cur is Phoenix.Controls.Visualist.WinUI.MainView mv) return mv;
        }
        return null;
    }

    // ─── timeline scrubber (sprint D) ───────────────────────────────────
    //
    // Replaces the Track 7 mock bars with a real time-axis + keyframe
    // markers + draggable playhead bound to VisualistViewModel.PlayheadMs.
    // The trigger's WidgetTimeline.DurationMs sets the right edge; tick
    // marks land on a human-friendly ramp picked from the active
    // pixels-per-second zoom (see QC52-03 zoom section below).

    private double _playheadMsCache;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _playheadLine;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _playheadHalo;
    private bool _isScrubbing;

    // Keyframe-marker drag state (QC52-02). _selectedKeyframe is the
    // currently-highlighted keyframe (rendered with the Ember200 fill); set
    // on PointerPressed, persists until a different marker is pressed or the
    // timeline is rebuilt with a now-removed keyframe (auto-cleared below).
    private Keyframe? _selectedKeyframe;
    private Keyframe? _draggingKeyframe;
    private bool _keyframeDragDirty;

    // R27/R38 — per-ParameterPath track rows. Rebuilt each RedrawTimeline from
    // the active trigger's keyframe set (insertion order = stable track order);
    // double-click-add (R28) maps the cursor Y back to a row → ParameterPath.
    private sealed class TimelineTrackRow
    {
        public string Path = "";
        public double CenterY;
        public double Top;
        public double Height;
    }
    private readonly List<TimelineTrackRow> _trackRows = new();
    private readonly Dictionary<string, int> _trackRowByPath = new(StringComparer.Ordinal);
    private const double TrackAreaTop   = 16;   // clear the tick-label band
    private const double TrackMinHeight = 10;
    private const double TrackMaxHeight = 26;

    // [LANE-C P3] Explicit timeline Z-order. Pre-fix the layering was implicit in
    // the Children.Add() call sequence (ticks → baselines → markers → playhead),
    // so a future refactor that batched or reordered the Add() calls could
    // silently put the playhead behind the markers. These structural Z bands make
    // the order intent explicit and refactor-proof: ticks/baselines at the back,
    // keyframe markers above them, the playhead always on top.
    private const int ZTick     = 0;
    private const int ZBaseline = 0;
    private const int ZMarker   = 10;
    private const int ZPlayhead = 20;

    // ─── timeline zoom (QC52-03) ────────────────────────────────────────
    //
    // Pre-fix the timeline was permanently fit-to-width with a hardcoded
    // 0.5s/1s/2s/5s/10s/30s/60s tick ramp — multi-minute durations capped at
    // a 60s ramp produced thousands of ticks, and authors couldn't zoom in
    // to fine-tune ms-level keyframes either. This block replaces the
    // px-derived-from-duration math with an explicit pixels-per-second
    // zoom whose tick interval is picked off a wider ramp under a 40px
    // minimum-spacing constraint.
    //
    // Persistence (point 4 in the QC fix shape) is *session-local* per
    // (widget.Id, trigger.Name) — same scope as PlayheadMs, which is also
    // not written to .phxlayer. The zoom level survives panel close inside
    // a single Visualist process; closing and re-opening the layer resets
    // to the fit-to-width default. Persisting to disk would require a
    // .phxlayer schema bump that's out of scope for this QC slice.

    private const double MinPxPerSec      = 5.0;      // multi-minute fit
    private const double MaxPxPerSec      = 2000.0;   // ms-level authoring
    private const double MinTickSpacingPx = 40.0;     // no two ticks closer than this
    private const double ZoomStepFactor   = 1.15;     // per wheel notch

    // Tick interval ramp, in seconds. Covers µs… 1h. The picker walks the
    // ramp and returns the first interval whose *pixel* width exceeds the
    // min-spacing constraint, so the actual chosen interval scales with
    // pxPerSec rather than a fixed cap.
    private static readonly double[] s_tickIntervalSeconds =
    {
        0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5,
        1, 2, 5, 10, 15, 30,
        60, 120, 300, 600, 900, 1800,
        3600,
    };

    private sealed class TimelineZoomState
    {
        public double PxPerSec      = 100.0;
        public double ScrollOffsetMs = 0.0;
    }

    // Keyed by "{widget.Id}|{trigger.Name}". Survives trigger / widget
    // switches inside the editor's lifetime; cleared on Unloaded.
    private readonly Dictionary<string, TimelineZoomState> _zoomByTrigger = new();
    private TimelineZoomState? _zoom;  // active state for the current redraw
    private double _fitPxPerSec = 100.0; // fit-to-width baseline for the chip

    private static string ZoomKey(LayerWidget? w, WidgetTrigger? t)
        => w is null || t is null ? "" : $"{w.Id}|{t.Name}";

    // Compute fit-to-width px/sec for a given duration and surface width.
    // Used both as the seed for an uninitialised zoom state and as the
    // baseline the visible "x1.0" chip multiplier is computed against.
    private static double FitPxPerSec(double durationMs, double surfaceW)
    {
        double seconds = durationMs / 1000.0;
        if (seconds <= 0 || surfaceW <= 0) return 100.0;
        return Math.Clamp(surfaceW / seconds, MinPxPerSec, MaxPxPerSec);
    }

    // Resolve (or seed) the zoom state for the current widget+trigger.
    // Called from RedrawTimeline so a brand-new (widget, trigger) opens
    // pre-fit and an existing one restores the user's last zoom.
    private TimelineZoomState ResolveZoomState(LayerWidget? widget, WidgetTrigger? trigger, double durationMs, double surfaceW)
    {
        _fitPxPerSec = FitPxPerSec(durationMs, surfaceW);
        string key = ZoomKey(widget, trigger);
        if (string.IsNullOrEmpty(key))
        {
            // Unbound (no widget/trigger) — return a transient state with
            // fit-to-width, no scroll. Don't store it.
            return new TimelineZoomState { PxPerSec = _fitPxPerSec, ScrollOffsetMs = 0 };
        }
        if (!_zoomByTrigger.TryGetValue(key, out var state))
        {
            state = new TimelineZoomState
            {
                PxPerSec       = _fitPxPerSec,
                ScrollOffsetMs = 0,
            };
            _zoomByTrigger[key] = state;
        }
        // Clamp on every resolve so a window resize that shrinks the surface
        // past the saved zoom's effective px/sec doesn't strand the user
        // at an out-of-bounds value.
        state.PxPerSec = Math.Clamp(state.PxPerSec, MinPxPerSec, MaxPxPerSec);
        // Clamp scroll so we never expose negative time or scroll past
        // duration. Re-clamped on every redraw because durationMs or surface
        // width may have changed since the last interaction.
        double maxOffset = Math.Max(0, durationMs - (surfaceW / state.PxPerSec) * 1000.0);
        state.ScrollOffsetMs = Math.Clamp(state.ScrollOffsetMs, 0, maxOffset);
        return state;
    }

    // Walk the tick ramp and return the smallest interval (seconds) that
    // produces ticks no closer than MinTickSpacingPx at the active zoom.
    private static double PickTickIntervalSec(double pxPerSec)
    {
        foreach (double sec in s_tickIntervalSeconds)
        {
            if (sec * pxPerSec >= MinTickSpacingPx) return sec;
        }
        return s_tickIntervalSeconds[s_tickIntervalSeconds.Length - 1];
    }

    // Format a time label in human-friendly units based on the interval
    // size: sub-second intervals render as "Xms", second-range as "X.Xs"
    // (and minute-range as "Xm Ys"). Keeps the tick row scannable across
    // the full 5–2000 px/s range.
    private static string FormatTickLabel(double seconds, double intervalSec)
    {
        if (intervalSec < 1)
        {
            double ms = seconds * 1000.0;
            // Two decimals for sub-ms intervals; integer ms otherwise.
            return intervalSec < 0.01
                ? $"{ms:0.##}ms"
                : $"{ms:0.#}ms";
        }
        if (intervalSec < 60) return $"{seconds:0.#}s";
        // Minute-and-up: show "Xm" or "Xm Ys".
        int mins = (int)(seconds / 60);
        double rem = seconds - mins * 60;
        return rem < 0.5 ? $"{mins}m" : $"{mins}m {rem:0}s";
    }

    // World-time (ms) → pixel-X within TimelineSurface.
    private double TimeToX(double timeMs, double pxPerSec, double scrollOffsetMs)
        => ((timeMs - scrollOffsetMs) / 1000.0) * pxPerSec;

    // Pixel-X within TimelineSurface → world-time (ms).
    private double XToTime(double xPx, double pxPerSec, double scrollOffsetMs)
        => scrollOffsetMs + (xPx / pxPerSec) * 1000.0;

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only rebuild the timeline when the surface dimensions actually moved
        // by more than a pixel — coalesces the burst of SizeChanged events a
        // single window resize produces into one redraw.
        double w = TimelineSurface.ActualWidth;
        double h = TimelineSurface.ActualHeight;
        if (!double.IsNaN(_lastTimelineSurfaceW)
            && Math.Abs(w - _lastTimelineSurfaceW) <= 1.0
            && Math.Abs(h - _lastTimelineSurfaceH) <= 1.0)
        {
            return;
        }
        _lastTimelineSurfaceW = w;
        _lastTimelineSurfaceH = h;
        RedrawTimeline();
    }

    private void RedrawTimeline()
    {
        double w = TimelineSurface.ActualWidth;
        double h = TimelineSurface.ActualHeight;
        // Bug #3 (timeline ruler ticks vanish on re-enter) — guard the zero-size
        // case BEFORE clearing. Re-entering the widget editor recycles this
        // singleton view; the re-entry relayout can fire a transient SizeChanged
        // with a not-yet-measured (0×0) surface. If we Clear()'d first and then
        // bailed on the zero-size guard, the ruler ticks were wiped and never
        // repainted — re-entering the SAME widget doesn't change the DataContext,
        // so OnDataContextChanged's RedrawTimeline never runs to rebuild them.
        // Leaving the existing children in place until we can actually draw makes
        // a transient 0-size pass a no-op instead of a destructive wipe.
        if (w <= 0 || h <= 0) { UpdateZoomChip(); return; }
        TimelineSurface.Children.Clear();
        _playheadLine = null;
        _playheadHalo = null;

        if (_vm is not { } vm) { UpdateZoomChip(); return; }
        var trigger = vm.ActiveTriggerObject;
        double durationMs = trigger?.Timeline?.DurationMs ?? 0.0;
        if (durationMs <= 0) durationMs = 5000; // default 5s if untimed

        // Resolve (or seed) the zoom state for this (widget, trigger). The
        // resolver also re-clamps PxPerSec / ScrollOffsetMs against the
        // current surface width so a window resize never strands the saved
        // state at an out-of-bounds value.
        _zoom = ResolveZoomState(vm.SelectedWidget, trigger, durationMs, w);
        double pxPerSec      = _zoom.PxPerSec;
        double scrollOffsetMs = _zoom.ScrollOffsetMs;

        // QC52-03 — tick density now derives from pixels-per-second under a
        // 40px minimum-spacing constraint, so the ramp scales smoothly from
        // 1ms (deep zoom) to 30min (multi-minute fit-to-width) instead of
        // capping at 60s. Compute the visible window in ms once and emit
        // only the ticks inside it (no more "thousands of ticks" on long
        // durations).
        double tickSec      = PickTickIntervalSec(pxPerSec);
        double tickMs       = tickSec * 1000.0;
        double visibleSpanMs = (w / pxPerSec) * 1000.0;
        double leftMs       = scrollOffsetMs;
        double rightMs      = Math.Min(durationMs, scrollOffsetMs + visibleSpanMs);

        // First tick at or above leftMs that lands on a tickMs boundary.
        double firstTickMs = Math.Ceiling(leftMs / tickMs) * tickMs;
        // Cap the loop to a hard ceiling so a programming-error pxPerSec
        // never explodes the visual tree. With MinTickSpacingPx=40 and a
        // typical surface ≤ 4000px, we're nowhere near this in practice.
        const int maxTicksPerFrame = 256;
        int ticksDrawn = 0;
        for (double t = firstTickMs; t <= rightMs + 0.0001 && ticksDrawn < maxTicksPerFrame; t += tickMs, ticksDrawn++)
        {
            double x = TimeToX(t, pxPerSec, scrollOffsetMs);
            if (x < -0.5 || x > w + 0.5) continue; // off-screen safety
            var line = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width  = 1,
                Height = h,
                Fill   = ResolveBrush("CoalCardBrush", 0x22, 0x1C, 0x16), // [LANE-C P2] safe resolve
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Stretch,
                Margin = new Thickness(x, 0, 0, 0),
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(line, ZTick); // [LANE-C P3] explicit Z-order
            TimelineSurface.Children.Add(line);

            var label = new TextBlock
            {
                Text       = FormatTickLabel(t / 1000.0, tickSec),
                FontFamily = new FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws
                FontSize   = 9,
                Foreground = ResolveBrush("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72), // [LANE-C P2] safe resolve
                Margin     = new Thickness(x + 2, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(label, ZTick); // [LANE-C P3] explicit Z-order
            TimelineSurface.Children.Add(label);
        }

        // Keyframes for the active trigger render as small ember diamonds
        // along a baseline track. Multiple keyframes at the same TimeMs (one
        // per ParameterPath) overlap; sprint future-polish can stagger.
        //
        // QC52-02 — diamonds are now hit-testable. PointerPressed on a marker
        // captures + snaps the playhead to the keyframe's time (so the value
        // displays in any keyframe inspector); PointerMoved drags the keyframe
        // along the timeline (mutating Keyframe.TimeMs + marking the document
        // dirty); PointerReleased commits. Per-marker e.Handled stops the
        // surface-level scrub handler from also firing.
        //
        // QC52-03 — keyframes outside the visible scroll window are skipped
        // entirely so a 1ms-zoom on a 10min trigger doesn't allocate markers
        // for the thousands of keyframes off-screen.
        // QC52-NaN — render from the NaN/Infinity-filtered, sorted projection
        // (WidgetTimeline.SortedKeyframes) rather than the raw authoring list.
        // A NaN TimeMs would produce a NaN x here: TimeToX(NaN) → NaN, the
        // off-screen bounds check below stays false for NaN, and a NaN Thickness
        // margin then corrupts the WinUI layout engine. SortedKeyframes drops
        // those poisoned values before they reach the markers.
        // R27 / R38 — one track ROW per ParameterPath, each curve a distinct
        // colour. Pre-fix every keyframe collapsed onto a single centered
        // baseline, so two parameters animating at the same time rendered as one
        // overlapping diamond and you couldn't tell which curve a marker
        // belonged to. Build the row map first (insertion order = stable track
        // order), then place each keyframe on its parameter's row.
        BuildTrackRows(trigger, h);
        if (trigger?.Timeline?.SortedKeyframes is { Count: > 0 } kfs && _trackRows.Count > 0)
        {
            // Faint per-row baseline + left label so the tracks read.
            var baselineBrush = ResolveBrushTinted("CoalCardBrush", 0x40, 0x2A, 0x26, 0x20);
            foreach (var row in _trackRows)
            {
                var line = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = w, Height = 1,
                    Fill = baselineBrush,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                    Margin = new Thickness(0, row.CenterY, 0, 0),
                    IsHitTestVisible = false,
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(line, ZBaseline); // [LANE-C P3] explicit Z-order
                TimelineSurface.Children.Add(line);

                var lbl = new TextBlock
                {
                    Text       = PrettyParamLabel(row.Path, trigger?.Graph),
                    FontFamily = new FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"),
                    FontSize   = 8,
                    Foreground = ResolveBrush("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72), // [LANE-C P2] safe resolve
                    Opacity    = 0.65,
                    Margin     = new Thickness(3, Math.Max(0, row.CenterY - 11), 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140,
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(lbl, ZBaseline); // [LANE-C P3] explicit Z-order
                TimelineSurface.Children.Add(lbl);
            }

            var goldStroke   = ResolveBrushTinted("SelectionBrush", 0xFF, 0xFF, 0xD7, 0x00);
            var normalStroke = ResolveBrush("Ember700Brush", 0x4A, 0x2A, 0x08); // [LANE-C P2] safe resolve

            // [LANE-C P3] Off-screen cull in TIME space, not a fixed pixel margin.
            // A rotated 9px diamond has a ~6.4px half-diagonal, so the old ±8px
            // pixel margin was borderline; more importantly, a pixel margin makes
            // the visible-edge tail shrink (in ms) as you zoom in, which is the
            // wrong direction. Cull markers whose TimeMs falls outside the visible
            // window [leftMs, rightMs] expanded by a small px tail converted to ms
            // at the current zoom, so the cull tail is a constant ~10px regardless
            // of zoom and a marker straddling the edge is never clipped.
            const double cullTailPx = 10.0;
            double cullTailMs  = (cullTailPx / pxPerSec) * 1000.0;
            double cullLeftMs  = scrollOffsetMs - cullTailMs;
            double cullRightMs = scrollOffsetMs + visibleSpanMs + cullTailMs;

            foreach (var kf in kfs)
            {
                var captureKf = kf;
                // Time-window cull (NaN-safe: SortedKeyframes already drops NaN/Inf,
                // but the comparison below is false for NaN anyway, which would
                // KEEP it — guard explicitly so a poisoned value can't slip a NaN
                // margin into the layout engine).
                if (double.IsNaN(kf.TimeMs) || double.IsInfinity(kf.TimeMs)) continue;
                if (kf.TimeMs < cullLeftMs || kf.TimeMs > cullRightMs) continue; // off-screen — skip
                double x = TimeToX(kf.TimeMs, pxPerSec, scrollOffsetMs);
                if (!_trackRowByPath.TryGetValue(kf.ParameterPath ?? "", out int rowIdx)) continue;
                var trackRow = _trackRows[rowIdx];
                bool selected = ReferenceEquals(_selectedKeyframe, kf);
                var marker = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width  = 9,
                    Height = 9,
                    // R38 — fill is the per-curve colour; selection moves to the
                    // STROKE (gold, heavier) so the curve identity is never lost
                    // when a keyframe is selected.
                    Fill   = CurveBrush(rowIdx),
                    Stroke = selected ? goldStroke : normalStroke,
                    StrokeThickness = selected ? 1.75 : 0.75,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                    Margin = new Thickness(x - 4.5, trackRow.CenterY - 4.5, 0, 0),
                    RenderTransform = new RotateTransform { Angle = 45 },
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    IsHitTestVisible = true,
                };
                marker.PointerPressed  += (s, args) => OnKeyframeMarkerPressed(s, args, captureKf);
                // [LANE-C P2] PointerMoved now captures the keyframe identity in
                // its closure (parity with Pressed/Released/RightTapped) instead
                // of relying solely on the _draggingKeyframe field, so a stray
                // PointerMoved on the wrong marker (before CapturePointer fully
                // isolates events) can't mutate a different keyframe.
                marker.PointerMoved    += (s, args) => OnKeyframeMarkerMoved(s, args, captureKf);
                marker.PointerReleased += OnKeyframeMarkerReleased;
                // [LANE-C P2] Hover cursor affordance — restores the WinForms
                // baseline's "hover to discover drag" hint lost in the per-marker
                // port. ProtectedCursor is protected and only settable from this
                // derived UserControl (see WidgetGraphCanvas / WidgetSinglePreview
                // — both set it on `this`, never on children), so we drive the
                // editor's own cursor while the pointer is over a marker and clear
                // it on exit. Wrapped because the cursor API can be unavailable in
                // designer / pre-app hosts.
                marker.PointerEntered  += OnKeyframeMarkerPointerEntered;
                marker.PointerExited   += OnKeyframeMarkerPointerExited;
                // B27 — right-click context menu (Delete / Edit Curve… /
                // Cycle Curve). Attached per-marker so the menu carries the
                // keyframe-under-cursor identity. RightTapped is the WinUI
                // event for right-click — PointerPressed sees both buttons
                // but RightTapped only fires for the right press, which is
                // what we want here.
                marker.RightTapped += (s, args) => OnKeyframeMarkerRightTapped(s, args, captureKf);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(marker, ZMarker); // [LANE-C P3] explicit Z-order — markers above ticks/baselines
                TimelineSurface.Children.Add(marker);
            }
        }

        // Playhead — vertical line + soft halo. Draggable: PointerPressed
        // captures, PointerMoved updates VM.PlayheadMs, PointerReleased
        // commits. With zoom the playhead may sit off-screen; we still emit
        // the elements so the cached references stay non-null for smooth
        // scrub, just clamp their X into the visible band.
        //
        // [LANE-C P3] Playhead-skip fix: a full redraw re-allocates the playhead
        // element, so it must rebuild at the LIVE time, not a possibly-stale
        // cache. _vm.PlayheadMs is the source of truth (advanced by playback ticks
        // + scrub). The scrub/drag paths maintain _playheadMsCache + reposition
        // the element directly for smoothness, so during those we trust the cache;
        // for every other redraw (zoom, resize, selection/trigger switch) we
        // re-sync the cache from the VM so the rebuilt playhead doesn't visually
        // jump back to where it sat at the previous redraw.
        if (!_isScrubbing && _draggingKeyframe is null && _vm is not null)
            _playheadMsCache = _vm.PlayheadMs;
        double playheadMs = Math.Clamp(_playheadMsCache, 0, durationMs);
        double phXRaw = TimeToX(playheadMs, pxPerSec, scrollOffsetMs);
        double phX    = Math.Clamp(phXRaw, -1000, w + 1000); // huge clamp keeps WinUI happy

        _playheadHalo = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width  = 8,
            // EmberPrimaryBrush at α=0x55 — same colour the pre-fix literal
            // produced, routed through the theme token via the local
            // ResolveBrushTinted helper so a future palette tweak ripples here
            // without code edits.
            Fill   = ResolveBrushTinted("EmberPrimaryBrush", 0x55, 0xE5, 0xA2, 0x4E),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Margin = new Thickness(phX - 3, 0, 0, 0),
            IsHitTestVisible = false,
            Visibility = (phXRaw < -4 || phXRaw > w + 4) ? Visibility.Collapsed : Visibility.Visible,
        };
        _playheadLine = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width  = 2,
            Fill   = ResolveBrush("Ember200Brush", 0xF2, 0xC7, 0x7F), // [LANE-C P2] safe resolve
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Margin = new Thickness(phX, 0, 0, 0),
            IsHitTestVisible = false,
            Visibility = (phXRaw < -1 || phXRaw > w + 1) ? Visibility.Collapsed : Visibility.Visible,
        };
        // [LANE-C P3] Playhead always renders on top of markers/ticks via an
        // explicit Z band, not the Add() order, so a future redraw refactor can't
        // accidentally bury it behind the keyframe markers.
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_playheadHalo, ZPlayhead);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_playheadLine, ZPlayhead);
        TimelineSurface.Children.Add(_playheadHalo);
        TimelineSurface.Children.Add(_playheadLine);

        // QC52-04: scrub handlers are now subscribed once on Loaded and
        // unsubscribed on Unloaded — see SubscribeTimelineHandlers /
        // UnsubscribeTimelineHandlers up top. Redraw no longer touches them.
        // If the timeline ever needs to swap surfaces dynamically, route the
        // swap through Unsubscribe → repoint → Subscribe rather than reviving
        // the per-redraw detach/reattach dance.
        SubscribeTimelineHandlers();

        UpdateZoomChip();
    }

    // Refresh the "x1.0" chip in the timeline header. Hidden when there's
    // no active zoom state (no widget / no trigger); otherwise shows the
    // multiplier vs the fit-to-width baseline at the current surface size.
    private void UpdateZoomChip()
    {
        if (_zoom is null || _fitPxPerSec <= 0)
        {
            TimelineZoomChip.Visibility = Visibility.Collapsed;
            return;
        }
        double mult = _zoom.PxPerSec / _fitPxPerSec;
        // Hide the chip when we're within ±5% of fit-to-width — keeps the
        // header chrome quiet at the default state and surfaces only when
        // the user has actively zoomed.
        if (mult >= 0.95 && mult <= 1.05)
        {
            TimelineZoomChip.Visibility = Visibility.Collapsed;
            return;
        }
        TimelineZoomChip.Visibility = Visibility.Visible;
        TimelineZoomChipLabel.Text  = mult >= 100
            ? $"x{mult:0}"
            : mult >= 10
                ? $"x{mult:0.0}"
                : $"x{mult:0.00}";
    }

    // QC52-03 — Ctrl+wheel zooms (cursor-anchored), plain wheel pans the
    // visible window when we're zoomed past fit-to-width. Pointer position
    // determines both the anchor for zoom and the pan direction's sign-of-
    // life when wheeling without Ctrl.
    private void OnTimelineWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        var trigger = _vm.ActiveTriggerObject;
        double durationMs = trigger?.Timeline?.DurationMs ?? 0.0;
        if (durationMs <= 0) durationMs = 5000;
        double w = TimelineSurface.ActualWidth;
        if (w <= 0) return;

        var state = ResolveZoomState(_vm.SelectedWidget, trigger, durationMs, w);
        var pp    = e.GetCurrentPoint(TimelineSurface);
        int delta = pp.Properties.MouseWheelDelta;
        if (delta == 0) return;

        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                     & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        if (ctrl)
        {
            // Cursor-anchored zoom — preserve the world-time under the pointer.
            double cursorX = pp.Position.X;
            double cursorMsBefore = XToTime(cursorX, state.PxPerSec, state.ScrollOffsetMs);

            double factor = delta > 0 ? ZoomStepFactor : 1.0 / ZoomStepFactor;
            double newPx  = Math.Clamp(state.PxPerSec * factor, MinPxPerSec, MaxPxPerSec);
            if (Math.Abs(newPx - state.PxPerSec) < 0.001) { e.Handled = true; return; }
            state.PxPerSec = newPx;

            // Solve for the scroll offset that keeps cursorMsBefore at cursorX
            // under the new px/sec: cursorX = (cursorMsBefore - offset) * px/sec / 1000
            // ⇒ offset = cursorMsBefore - (cursorX * 1000 / px/sec).
            double newOffset = cursorMsBefore - (cursorX * 1000.0 / newPx);
            double maxOffset = Math.Max(0, durationMs - (w / newPx) * 1000.0);
            state.ScrollOffsetMs = Math.Clamp(newOffset, 0, maxOffset);
        }
        else
        {
            // Plain wheel: horizontal pan. One notch = 15% of the visible
            // window; positive delta scrolls left (back in time), matching
            // the After Effects / DaVinci Resolve idiom.
            double visibleMs = (w / state.PxPerSec) * 1000.0;
            double stepMs    = visibleMs * 0.15 * (delta > 0 ? -1 : 1);
            double maxOffset = Math.Max(0, durationMs - visibleMs);
            // If we're fully fit-to-width (no scroll headroom), don't bother
            // — let the parent handle the wheel for whatever scroll context
            // wraps the editor.
            if (maxOffset <= 0.0001) return;
            state.ScrollOffsetMs = Math.Clamp(state.ScrollOffsetMs + stepMs, 0, maxOffset);
        }

        e.Handled = true;
        // [LANE-C P3] RedrawTimeline now re-syncs _playheadMsCache from the live
        // _vm.PlayheadMs (when not mid-scrub/drag) before rebuilding the playhead,
        // so a zoom mid-playback rebuilds it at the authoritative time instead of a
        // stale cache — no visible jump.
        RedrawTimeline();
    }

    private void OnTimelineScrubPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm?.ActiveTriggerObject?.Timeline is null) return;
        _isScrubbing = true;
        TimelineSurface.CapturePointer(e.Pointer);
        // R28 — focus the editor so a subsequent Delete targets the timeline
        // (clears any stale graph focus too).
        try { Focus(FocusState.Programmatic); } catch { /* pre-realised */ }
        UpdatePlayheadFromPointer(e.GetCurrentPoint(TimelineSurface).Position.X);
        e.Handled = true;
    }

    private void OnTimelineScrubMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing) return;
        UpdatePlayheadFromPointer(e.GetCurrentPoint(TimelineSurface).Position.X);
        e.Handled = true;
    }

    private void OnTimelineScrubReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        TimelineSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // ─── keyframe-marker drag (QC52-02) ─────────────────────────────────

    private void OnKeyframeMarkerPressed(object sender, PointerRoutedEventArgs e, Keyframe kf)
    {
        if (sender is not FrameworkElement marker) return;
        // Capture pointer on the marker (not the timeline surface) so the
        // PointerMoved events bound to the marker keep firing even when the
        // pointer leaves the small 9×9 rect during drag.
        try { marker.CapturePointer(e.Pointer); } catch { /* best-effort */ }
        // R28 — take keyboard focus so Delete removes this keyframe.
        try { Focus(FocusState.Programmatic); } catch { /* pre-realised */ }
        _selectedKeyframe  = kf;
        _draggingKeyframe  = kf;
        _keyframeDragDirty = false;

        // Snap playhead to the selected keyframe's time as a side-effect of
        // selection (mirrors the After Effects "click keyframe to set time").
        if (_vm is not null) _vm.PlayheadMs = kf.TimeMs;

        e.Handled = true;
        // The timeline rebuild repaints the markers with the new selection
        // fill — RedrawTimeline is called from OnVmPropertyChanged when
        // PlayheadMs changes? It isn't, so trigger a redraw explicitly.
        RedrawTimeline();
    }

    private void OnKeyframeMarkerMoved(object sender, PointerRoutedEventArgs e, Keyframe captureKf)
    {
        // [LANE-C P2] Identity guard — only act when the keyframe this handler
        // was wired for is the one actually being dragged. A PointerMoved that
        // arrives on a non-source marker (before CapturePointer isolates the
        // stream) is ignored rather than mutating _draggingKeyframe's keyframe by
        // accident.
        if (_draggingKeyframe is not Keyframe kf) return;
        if (!ReferenceEquals(kf, captureKf)) return;
        if (_vm?.ActiveTriggerObject?.Timeline is not WidgetTimeline tl) return;

        double durationMs = tl.DurationMs > 0 ? tl.DurationMs : 5000;
        double w = TimelineSurface.ActualWidth;
        if (w <= 0) return;

        // QC52-03 — drag math now goes through the zoom transform so dragging
        // at any zoom level produces a millisecond delta proportional to the
        // pointer's pixel delta, not to the surface width.
        var zoom = _zoom ?? ResolveZoomState(_vm.SelectedWidget, _vm.ActiveTriggerObject, durationMs, w);
        double xInTimeline = e.GetCurrentPoint(TimelineSurface).Position.X;
        double newMs = XToTime(Math.Clamp(xInTimeline, 0, w), zoom.PxPerSec, zoom.ScrollOffsetMs);
        newMs = Math.Clamp(newMs, 0, durationMs);
        if (Math.Abs(newMs - kf.TimeMs) < 0.5) return; // sub-frame jitter → ignore

        if (!_keyframeDragDirty)
        {
            _keyframeDragDirty = true;
            if (_vm.Document is { } doc) doc.PushUndo();
        }
        kf.TimeMs = newMs;
        if (_vm is not null) _vm.PlayheadMs = newMs;
        e.Handled = true;
        RedrawTimeline();
    }

    private void OnKeyframeMarkerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement marker)
        {
            try { marker.ReleasePointerCapture(e.Pointer); } catch { /* best-effort */ }
        }
        if (_keyframeDragDirty && _vm?.Document is { } doc)
        {
            doc.MarkDirty();
        }
        _draggingKeyframe  = null;
        _keyframeDragDirty = false;
        e.Handled = true;
    }

    // ─── [LANE-C P2] keyframe hover cursor affordance ────────────────────
    //
    // The pre-T15 WinForms timeline ran a centralized mouse-tracking loop that
    // swapped the cursor to a move glyph when hovering a keyframe. The WinUI
    // per-marker port dropped it. ProtectedCursor is protected on UIElement and
    // can only be set from within a derived type, so (matching WidgetGraphCanvas
    // / WidgetSinglePreviewPanel, which both set it on `this`) we drive the
    // editor's OWN cursor while the pointer is over a marker and reset on exit.

    private void OnKeyframeMarkerPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll); }
        catch { /* cursor API unavailable in designer / pre-app — ignore */ }
    }

    private void OnKeyframeMarkerPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Don't clear mid-drag — CapturePointer keeps the marker "hovered" in
        // spirit while the pointer roams; the release path doesn't touch the
        // cursor, so clearing here on the natural exit at drag-end is correct.
        try { ProtectedCursor = null; }
        catch { /* cursor API unavailable in designer / pre-app — ignore */ }
    }

    // ─── R27/R38 — per-parameter track rows + per-curve colours ──────────

    private void BuildTrackRows(WidgetTrigger? trigger, double h)
    {
        _trackRows.Clear();
        _trackRowByPath.Clear();
        var kfList = trigger?.Timeline?.Keyframes;
        if (kfList is null || kfList.Count == 0) return;

        // Distinct ParameterPaths in insertion order — stable track ordering
        // that doesn't reshuffle as the user scrubs.
        foreach (var k in kfList)
        {
            string p = k?.ParameterPath ?? "";
            if (p.Length == 0) continue;
            if (!_trackRowByPath.ContainsKey(p))
            {
                _trackRowByPath[p] = _trackRows.Count;
                _trackRows.Add(new TimelineTrackRow { Path = p });
            }
        }
        if (_trackRows.Count == 0) return;

        double availH   = Math.Max(0, h - TrackAreaTop - 2);
        double rowH     = Math.Clamp(availH / _trackRows.Count, TrackMinHeight, TrackMaxHeight);
        for (int i = 0; i < _trackRows.Count; i++)
        {
            double top = TrackAreaTop + i * rowH;
            _trackRows[i].Top     = top;
            _trackRows[i].Height  = rowH;
            _trackRows[i].CenterY = top + rowH / 2.0;
        }
    }

    // R38 — distinct per-curve colour. Hue rotates by the golden angle so any
    // number of tracks stay visually separated; functional track-tinting (à la
    // DaVinci / After Effects), not a brand palette.
    private static Brush CurveBrush(int index)
    {
        double hue = (index * 137.508) % 360.0;
        return new SolidColorBrush(HsvToColor(hue, 0.62, 0.96));
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if      (h < 60)  { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else              { r = c; g = 0; b = x; }
        byte R = (byte)Math.Round((r + m) * 255);
        byte G = (byte)Math.Round((g + m) * 255);
        byte B = (byte)Math.Round((b + m) * 255);
        return Color.FromArgb(0xFF, R, G, B);
    }

    // "<short node title>.<component>" for the row label; falls back to the raw
    // path when the node id can't be resolved (e.g. shape-vertex paths).
    private static string PrettyParamLabel(string path, Graph? graph)
    {
        if (string.IsNullOrEmpty(path)) return "—";
        int dot = path.IndexOf('.');
        if (dot > 0 && graph is not null)
        {
            string maybeId = path.Substring(0, dot);
            var node = graph.Nodes?.FirstOrDefault(n => string.Equals(n.Id, maybeId, StringComparison.Ordinal));
            if (node is not null)
            {
                string comp  = path.Substring(dot + 1);
                string title = string.IsNullOrEmpty(node.Title) ? "?" : node.Title;
                int t = title.LastIndexOf('.');
                if (t >= 0 && t < title.Length - 1) title = title.Substring(t + 1);
                return $"{title}.{comp}";
            }
        }
        return path;
    }

    // ─── R28 — double-click-to-add + Delete-key removal ──────────────────

    private void OnTimelineDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        var trigger = _vm.ActiveTriggerObject;
        if (trigger?.Timeline is not WidgetTimeline tl) return;
        if (_trackRows.Count == 0) return;   // no tracks → nothing to add to

        var pos = e.GetPosition(TimelineSurface);
        // Resolve the row under the cursor (the track whose band contains Y).
        // [LANE-C P1] Hit zone now maps 1:1 to the row's visual band (no ±2px
        // slop). The old ±2px margin was ambiguous at small track heights
        // (TrackMinHeight=10 → 20% of the row), letting a click on the boundary
        // resolve to the wrong row. Half-open interval [Top, Top+Height) so two
        // adjacent rows never both claim the seam pixel.
        TimelineTrackRow? hit = null;
        foreach (var row in _trackRows)
        {
            if (pos.Y >= row.Top && pos.Y < row.Top + row.Height) { hit = row; break; }
        }
        if (hit is null) return;             // double-clicked outside any track row

        double w = TimelineSurface.ActualWidth;
        if (w <= 0) return;
        double durationMs = tl.DurationMs > 0 ? tl.DurationMs : 5000;
        var zoom = _zoom ?? ResolveZoomState(_vm.SelectedWidget, trigger, durationMs, w);
        double t = Math.Clamp(XToTime(Math.Clamp(pos.X, 0, w), zoom.PxPerSec, zoom.ScrollOffsetMs), 0, durationMs);

        // Don't stack a second keyframe right on top of an existing one.
        if (tl.Keyframes.Any(k => k != null
                && string.Equals(k.ParameterPath, hit.Path, StringComparison.Ordinal)
                && Math.Abs(k.TimeMs - t) < 0.5))
        {
            e.Handled = true;
            return;
        }

        double seedVal = ResolveParamSeedValue(hit.Path, trigger);
        _vm.Document?.PushUndo();
        var kf = new Keyframe
        {
            ParameterPath = hit.Path,
            TimeMs        = t,
            Value         = System.Text.Json.JsonSerializer.SerializeToElement(seedVal),
            Curve         = KeyframeCurve.Linear,
        };
        tl.Keyframes.Add(kf);
        _selectedKeyframe = kf;
        _vm.Document?.MarkDirty();
        _vm.NotifyActiveTriggerChanged();
        RedrawTimeline();
        SetStatus($"Keyframe added at {t:0} ms.");
        e.Handled = true;
    }

    // Best-effort current value for a freshly-added keyframe: the node's literal
    // attribute for "<nodeId>.<component>" parameters; 0 otherwise.
    private static double ResolveParamSeedValue(string path, WidgetTrigger trigger)
    {
        int dot = path.IndexOf('.');
        if (dot <= 0 || trigger.Graph is null) return 0;
        string id   = path.Substring(0, dot);
        string comp = path.Substring(dot + 1);
        var node = trigger.Graph.Nodes?.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
        return node is null
            ? 0
            : Phoenix.Controls.Visualist.Core.AnimatedPinRegistry.ReadComponentLiteral(node, comp);
    }

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // R28 — Delete / Backspace removes the selected keyframe. Guarded on a
        // live selection so the key bubbling up from the graph canvas (node
        // delete) isn't shadowed when no keyframe is active.
        if ((e.Key == Windows.System.VirtualKey.Delete || e.Key == Windows.System.VirtualKey.Back)
            && _selectedKeyframe is { } kf)
        {
            DeleteKeyframe(kf);
            e.Handled = true;
        }
    }

    // ─── R39 — prev/next keyframe transport ──────────────────────────────

    private void OnPrevKeyframeClicked(object sender, RoutedEventArgs e) => StepToKeyframe(-1);
    private void OnNextKeyframeClicked(object sender, RoutedEventArgs e) => StepToKeyframe(+1);

    // Move the playhead to the previous / next keyframe time across the whole
    // active timeline (any parameter). Shares intent with the per-pill ◀▶ seek
    // but is timeline-global rather than parameter-scoped.
    private void StepToKeyframe(int dir)
    {
        if (_vm?.ActiveTriggerObject?.Timeline is not WidgetTimeline tl) return;
        double cur  = _vm.PlayheadMs;
        double best = double.NaN;
        foreach (var k in tl.Keyframes)
        {
            if (k is null) continue;
            double t = k.TimeMs;
            if (double.IsNaN(t) || double.IsInfinity(t)) continue;
            if (dir < 0)
            {
                if (t < cur - 0.5 && (double.IsNaN(best) || t > best)) best = t;
            }
            else
            {
                if (t > cur + 0.5 && (double.IsNaN(best) || t < best)) best = t;
            }
        }
        if (double.IsNaN(best))
        {
            SetStatus(dir < 0 ? "No earlier keyframe." : "No later keyframe.");
            return;
        }
        _playheadMsCache = best;
        _vm.PlayheadMs = best;
        RepositionPlayheadForTime(best);
    }

    private void UpdatePlayheadFromPointer(double xInTimeline)
    {
        var trigger = _vm?.ActiveTriggerObject;
        if (trigger?.Timeline is null) return;
        double durationMs = trigger.Timeline.DurationMs;
        if (durationMs <= 0) durationMs = 5000;
        double w = TimelineSurface.ActualWidth;
        if (w <= 0) return;

        // QC52-03 — scrub math now goes through the zoom transform so the
        // pointer X maps to the visible-window-relative time, not duration *
        // (x/w). Without this the playhead would jump to the full-duration
        // position regardless of how far we'd zoomed in.
        var zoom = _zoom ?? ResolveZoomState(_vm!.SelectedWidget, trigger, durationMs, w);
        double clampedX = Math.Clamp(xInTimeline, 0, w);
        double newMs = XToTime(clampedX, zoom.PxPerSec, zoom.ScrollOffsetMs);
        newMs = Math.Clamp(newMs, 0, durationMs);
        _playheadMsCache = newMs;

        if (_vm is not null) _vm.PlayheadMs = newMs;

        // Drive the previews live while the user drags the playhead. The playback
        // tick path (OnPlaybackTimeChanged) posts SCRUB, but a manual drag doesn't
        // go through it — without this the preview only updated during PLAY, so
        // scrubbing showed nothing. compositor.js samples keyframes (incl. colour)
        // at this time, so the interpolated state shows as you scrub.
        string trig = ActiveTriggerName();
        string? widgetId = _vm?.SelectedWidget?.Id;
        if (!string.IsNullOrEmpty(widgetId))
        {
            FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview?.PostScrub(widgetId!, trig, newMs);
            EmbeddedPreview?.PostScrub(newMs);
        }

        // Reposition the playhead line + halo without a full RedrawTimeline
        // (smooth scrubbing — no re-allocation of every tick mark per move).
        if (_playheadLine is not null)
            _playheadLine.Margin = new Thickness(clampedX, 0, 0, 0);
        if (_playheadHalo is not null)
            _playheadHalo.Margin = new Thickness(clampedX - 3, 0, 0, 0);
    }

    // Lightweight playhead reposition for automated playback (Task 6). Maps the
    // playback time → X via the active zoom transform and slides the cached line
    // + halo, clamping into the visible band. Re-uses the same TimeToX math
    // RedrawTimeline uses so a zoomed/scrolled timeline tracks correctly without
    // re-allocating the tick marks on every 30Hz tick.
    private void RepositionPlayheadForTime(double timeMs)
    {
        if (_playheadLine is null && _playheadHalo is null) return;
        var trigger = _vm?.ActiveTriggerObject;
        double durationMs = trigger?.Timeline?.DurationMs ?? 0.0;
        if (durationMs <= 0) durationMs = 5000;
        double w = TimelineSurface.ActualWidth;
        if (w <= 0) return;

        var zoom = _zoom ?? ResolveZoomState(_vm?.SelectedWidget, trigger, durationMs, w);
        double clamped = Math.Clamp(timeMs, 0, durationMs);
        double phXRaw = TimeToX(clamped, zoom.PxPerSec, zoom.ScrollOffsetMs);
        double phX = Math.Clamp(phXRaw, -1000, w + 1000);

        if (_playheadLine is not null)
        {
            _playheadLine.Margin = new Thickness(phX, 0, 0, 0);
            _playheadLine.Visibility = (phXRaw < -1 || phXRaw > w + 1) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_playheadHalo is not null)
        {
            _playheadHalo.Margin = new Thickness(phX - 3, 0, 0, 0);
            _playheadHalo.Visibility = (phXRaw < -4 || phXRaw > w + 4) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ─── B27 keyframe right-click context menu ──────────────────────────
    //
    // Three items per the audit:
    //   - Delete           → removes the keyframe; pushes undo + marks dirty.
    //   - Edit Curve…      → opens CurveEditorDialog for fine-grained editing.
    //   - Cycle Curve      → walks Linear → EaseIn → EaseOut → Bezier → Step
    //                        → Linear, mutating Keyframe.Curve in place.
    // The dialog and the cycle path both round-trip through PushUndo +
    // MarkDirty so the document treats the curve edit like any other
    // mutation (undo restores the previous curve + handles).

    private void OnKeyframeMarkerRightTapped(object sender, RightTappedRoutedEventArgs args, Keyframe kf)
    {
        if (sender is not FrameworkElement marker) return;
        _selectedKeyframe = kf;
        var flyout = new MenuFlyout();

        var delete = new MenuFlyoutItem
        {
            Text = Localizer.T("common.context.delete", "Delete"),
        };
        delete.Click += (s, e) => DeleteKeyframe(kf);
        flyout.Items.Add(delete);

        var edit = new MenuFlyoutItem
        {
            Text = Localizer.T("visualist.context.edit_curve", "Edit Curve…"),
        };
        edit.Click += async (s, e) => await EditKeyframeCurveAsync(kf);
        flyout.Items.Add(edit);

        var cycle = new MenuFlyoutItem
        {
            Text = Localizer.T("visualist.context.cycle_curve", "Cycle Curve"),
        };
        cycle.Click += (s, e) => CycleKeyframeCurve(kf);
        flyout.Items.Add(cycle);

        var editValue = new MenuFlyoutItem
        {
            Text = Localizer.T("visualist.context.edit_value", "Edit Value…"),
        };
        editValue.Click += async (s, e) => await EditKeyframeValueAsync(kf);
        flyout.Items.Add(editValue);

        try
        {
            flyout.ShowAt(marker, args.GetPosition(marker));
        }
        catch
        {
            // Fall back to a no-position show — should not normally fail but
            // RightTapped can fire from input devices the position resolver
            // doesn't model (eg. touch + long-press) and we'd rather surface
            // the menu unanchored than crash.
            try { flyout.ShowAt(marker); } catch { /* designer / pre-app */ }
        }
        args.Handled = true;
    }

    // [LANE-C P1] Trigger-switch race guard. A keyframe captured in a context-
    // menu / drag closure can be acted on AFTER a trigger switch (e.g. the user
    // right-clicks a marker, then clicks a different trigger tab, then the menu
    // item fires). At that point ActiveTriggerObject is a DIFFERENT trigger, and
    // mutating the captured (now-orphaned) keyframe would silently dirty the
    // wrong timeline or no timeline at all. This validates the captured keyframe
    // still lives in the CURRENT active trigger's timeline before any mutation.
    // Returns the live timeline when valid, null (with a System log) otherwise.
    private WidgetTimeline? ValidateKeyframeOnActiveTimeline(Keyframe? kf, string op)
    {
        if (kf is null) return null;
        if (_vm?.ActiveTriggerObject?.Timeline is not WidgetTimeline tl) return null;
        // Identity check — the keyframe must still belong to the active trigger's
        // timeline. A trigger switch between capture and action replaces the
        // timeline, so the captured keyframe won't be found here.
        if (!tl.Keyframes.Contains(kf))
        {
            GlobalLogger.Log(
                $"{op} ignored — the keyframe no longer belongs to the active trigger " +
                "(a trigger switch invalidated the captured keyframe).",
                "WidgetEditorView",
                LogLevel.System);
            return null;
        }
        return tl;
    }

    private void DeleteKeyframe(Keyframe kf)
    {
        // [LANE-C P1] Race guard — confirm the captured keyframe is still on the
        // active timeline before deleting (a trigger switch could have replaced it).
        if (ValidateKeyframeOnActiveTimeline(kf, "Delete keyframe") is not WidgetTimeline tl) return;
        if (_vm?.Document is { } doc) doc.PushUndo();
        tl.Keyframes.Remove(kf);
        if (ReferenceEquals(_selectedKeyframe, kf)) _selectedKeyframe = null;
        if (ReferenceEquals(_draggingKeyframe, kf)) _draggingKeyframe = null;
        if (_vm?.Document is { } d2) d2.MarkDirty();
        RedrawTimeline();
    }

    private async System.Threading.Tasks.Task EditKeyframeCurveAsync(Keyframe kf)
    {
        if (XamlRoot is null) return;
        // Snapshot the curve + handles BEFORE showing the dialog so undo can
        // round-trip even when the user mutates via the dialog. The dialog
        // writes back on Save; on Cancel the snapshot stays the source of
        // truth and we don't push undo (no mutation happened).
        var before = (kf.Curve, kf.BezierP1X, kf.BezierP1Y, kf.BezierP2X, kf.BezierP2Y);
        var dlg = new Dialogs.CurveEditorDialog(kf) { XamlRoot = XamlRoot };
        try
        {
            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;
            // [LANE-C P1] Trigger-switch race — the dialog is modal-async, so the
            // user could have switched triggers (or deleted this keyframe) while
            // it was open. The dialog already wrote into `kf`, but if `kf` no
            // longer belongs to the active timeline we must NOT push undo / mark
            // the (wrong) document dirty on its behalf. Revert the dialog's write
            // and bail. The orphaned timeline keeps the value the dialog set on
            // its own keyframe object; the active document is left untouched.
            if (ValidateKeyframeOnActiveTimeline(kf, "Edit curve") is null)
            {
                kf.Curve = before.Curve;
                kf.BezierP1X = before.BezierP1X; kf.BezierP1Y = before.BezierP1Y;
                kf.BezierP2X = before.BezierP2X; kf.BezierP2Y = before.BezierP2Y;
                return;
            }
            var after = (kf.Curve, kf.BezierP1X, kf.BezierP1Y, kf.BezierP2X, kf.BezierP2Y);
            if (Equals(after, before)) return;
            // The dialog wrote DIRECTLY to the keyframe before PushUndo could
            // capture the snapshot, so the undo step we'd want is the BEFORE
            // state. Round-trip: revert → PushUndo → re-apply. This matches
            // how the inspector edits its TextBoxes (refresh-suppressed echo).
            kf.Curve = before.Curve;
            kf.BezierP1X = before.BezierP1X; kf.BezierP1Y = before.BezierP1Y;
            kf.BezierP2X = before.BezierP2X; kf.BezierP2Y = before.BezierP2Y;
            if (_vm?.Document is { } doc) doc.PushUndo();
            kf.Curve = after.Curve;
            kf.BezierP1X = after.BezierP1X; kf.BezierP1Y = after.BezierP1Y;
            kf.BezierP2X = after.BezierP2X; kf.BezierP2Y = after.BezierP2Y;
            if (_vm?.Document is { } d2) d2.MarkDirty();
            RedrawTimeline();
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "WidgetEditorView", "EditKeyframeCurveAsync", ex);
        }
    }

    // Edit a single keyframe's numeric value directly (right-click → Edit Value…).
    // Works for any scalar track — a plain Scalar/Float/Int keyframe, one vector
    // component, or one colour channel (0–255). The typed editors in the Inspector
    // are the richer path (colour picker etc.); this is the precise per-keyframe
    // poke for a value you can't reach without scrubbing exactly onto it.
    private async System.Threading.Tasks.Task EditKeyframeValueAsync(Keyframe kf)
    {
        if (XamlRoot is null) return;
        if (ValidateKeyframeOnActiveTimeline(kf, "Edit value") is null) return;
        double cur = ReadKeyframeNumber(kf);
        var box = new NumberBox
        {
            Value                   = cur,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Header                  = Localizer.T("visualist.keyframe.value", "Value"),
        };
        var dlg = new ContentDialog
        {
            Title             = Localizer.T("visualist.context.edit_value_title", "Edit keyframe value"),
            Content           = box,
            PrimaryButtonText = Localizer.T("common.ok", "OK"),
            CloseButtonText   = Localizer.T("common.cancel", "Cancel"),
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot,
        };
        try
        {
            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;
            double nv = box.Value;
            if (double.IsNaN(nv) || double.IsInfinity(nv)) return;
            // Async race guard — the user could have switched triggers / deleted
            // the keyframe while the dialog was open (mirrors EditKeyframeCurveAsync).
            if (ValidateKeyframeOnActiveTimeline(kf, "Edit value") is null) return;
            if (Math.Abs(ReadKeyframeNumber(kf) - nv) < 1e-9) return;
            if (_vm?.Document is { } doc) doc.PushUndo();
            kf.Value = System.Text.Json.JsonSerializer.SerializeToElement(nv);
            if (_vm?.Document is { } d2) d2.MarkDirty();
            RedrawTimeline();
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "WidgetEditorView", "EditKeyframeValueAsync", ex);
        }
    }

    private static double ReadKeyframeNumber(Keyframe kf)
    {
        try
        {
            if (kf.Value.ValueKind == System.Text.Json.JsonValueKind.Number) return kf.Value.GetDouble();
            if (kf.Value.ValueKind == System.Text.Json.JsonValueKind.String
                && double.TryParse(kf.Value.GetString(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double d))
                return d;
        }
        catch { /* malformed value — fall through to 0 */ }
        return 0;
    }

    private void CycleKeyframeCurve(Keyframe kf)
    {
        // [LANE-C P1] Race guard — the context menu captured this keyframe; a
        // trigger switch between right-click and click would orphan it. Validate
        // it still belongs to the active timeline before mutating + dirtying.
        if (ValidateKeyframeOnActiveTimeline(kf, "Cycle curve") is null) return;
        // Cycle ramp per audit B27 — Linear → EaseIn → EaseOut → EaseInOut →
        // Bezier → Step → Linear. [LANE-C P3 R61] EaseInOut is now included in
        // the cycle so right-click discoverability reaches every preset (it was
        // previously dialog-only, which was undiscoverable from the menu).
        KeyframeCurve next = kf.Curve switch
        {
            KeyframeCurve.Linear    => KeyframeCurve.EaseIn,
            KeyframeCurve.EaseIn    => KeyframeCurve.EaseOut,
            KeyframeCurve.EaseOut   => KeyframeCurve.EaseInOut,
            KeyframeCurve.EaseInOut => KeyframeCurve.Bezier,
            KeyframeCurve.Bezier    => KeyframeCurve.Step,
            KeyframeCurve.Step      => KeyframeCurve.Linear,
            _                       => KeyframeCurve.Linear,
        };
        if (kf.Curve == next) return;
        if (_vm?.Document is { } doc) doc.PushUndo();
        kf.Curve = next;
        // Strip the bezier handles when leaving Bezier so the JSON stays
        // clean — symmetrical to CurveEditorDialog.OnSaveClicked.
        if (next != KeyframeCurve.Bezier)
        {
            kf.BezierP1X = kf.BezierP1Y = null;
            kf.BezierP2X = kf.BezierP2Y = null;
        }
        if (_vm?.Document is { } d2) d2.MarkDirty();
        RedrawTimeline();
    }

    // ─── Lane B: layer id + active trigger helpers ───────────────────────

    // Hub serves overlays under /layer/<file-stem>; LayerRegistry keys each
    // entry by the same stem. Layer.Name is the DISPLAY name and is NOT what Hub
    // looks up — so derive the id from the saved file path's stem (matching the
    // WinForms baseline's GetLayerIdForPreview). Null for an unsaved document.
    private string? ResolveLayerId()
    {
        string? path = _vm?.Document?.FilePath;
        if (string.IsNullOrEmpty(path)) return null;
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private string ActiveTriggerName()
        => string.IsNullOrEmpty(_vm?.ActiveTrigger) ? "onStartup" : _vm!.ActiveTrigger;

    // ─── Task 3: Test Run ────────────────────────────────────────────────
    //
    // Fire-and-forget VISUAL_TRIGGER for the active trigger over the bus so the
    // live OBS source executes it. Disabled when the bus is offline so an offline
    // click can't pile into the bounded outbound queue. Feedback goes to the
    // status bar — no modal (feedback_no_modal_dialogs_for_repeatable_rejections).
    private async void OnTestRunClicked(object sender, RoutedEventArgs e)
    {
        if (!VisualistBusClient.Instance.IsConnected)
        {
            SetStatus("Test Run unavailable — Hub bus is offline.");
            return;
        }
        string? layerId = ResolveLayerId();
        if (layerId is null)
        {
            SetStatus("Test Run unavailable — save the layer first.");
            return;
        }
        LayerWidget? widget = _vm?.SelectedWidget;
        if (widget is null || string.IsNullOrEmpty(widget.Id))
        {
            SetStatus("Test Run unavailable — no widget selected.");
            return;
        }
        string triggerName = ActiveTriggerName();

        TestRunButton.IsEnabled = false;
        try
        {
            await VisualistBusClient.Instance.SendVisualTriggerAsync(layerId, widget.Id, triggerName);
            SetStatus($"Test Run sent: {widget.Name} · {triggerName}");
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetEditorView", "Test Run", ex);
            SetStatus($"Test Run failed: {ex.Message}");
        }
        finally
        {
            // Re-gate against live bus state (it may have dropped during the send).
            ApplyButtonGating();
        }
    }

    // ─── Bus connection indicator ────────────────────────────────────────

    // Process-wide guard so multiple editor instances (or repeated Loaded
    // cycles) don't each launch a fresh VisualistBusClient connect loop. See
    // the note in InitPreviewAndTransport — this is a stop-gap until the bus
    // is started from the Visualist pillar bootstrap (reported hook).
    private static bool _busStartRequested;
    private static readonly object _busStartGate = new();
    private static void EnsureBusStarted()
    {
        if (_busStartRequested) return;
        lock (_busStartGate)
        {
            if (_busStartRequested) return;
            _busStartRequested = true;
            try { VisualistBusClient.Instance.Start(); }
            catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "EnsureBusStarted", ex); }
        }
    }

    private void OnBusConnectionChanged(bool connected)
    {
        // OnConnectionStatusChanged fires from the bus receive/connect loop, NOT
        // the UI thread — marshal before touching XAML.
        try { DispatcherQueue?.TryEnqueue(() => ApplyBusConnectionState(connected)); }
        catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "OnBusConnectionChanged", ex); }
    }

    private void ApplyBusConnectionState(bool connected)
    {
        if (BusStatusDot is not null)
        {
            // Resource key may be missing or hold a non-Brush during designer /
            // pre-app load — a raw (Brush) cast would throw InvalidCastException
            // and this method runs outside the OnBusConnectionChanged try-catch
            // (that one only wraps the TryEnqueue marshal). Fall back to gray.
            try
            {
                BusStatusDot.Fill = connected
                    ? (Brush)Application.Current.Resources["OkBrush"]
                    : (Brush)Application.Current.Resources["ErrBrush"];
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WidgetEditorView", "ApplyBusConnectionState", ex);
                BusStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            }
            ToolTipService.SetToolTip(BusStatusDot, connected ? "Hub bus connected" : "Hub bus offline");
        }
        ApplyButtonGating();
    }

    // Enable Test Run only when the bus is online AND the layer is saved AND a
    // widget is selected. Preview-popout buttons gate on the config toggle.
    private void ApplyButtonGating()
    {
        bool canTestRun = VisualistBusClient.Instance.IsConnected
                       && ResolveLayerId() is not null
                       && _vm?.SelectedWidget is not null;
        if (TestRunButton is not null) TestRunButton.IsEnabled = canTestRun;

        bool popoutEnabled = VisualistUserConfig.Instance.EditorPopoutPreviewEnabled;
        if (PreviewLayerButton  is not null) PreviewLayerButton.IsEnabled  = popoutEnabled;
        if (PreviewWidgetButton is not null) PreviewWidgetButton.IsEnabled = popoutEnabled && _vm?.SelectedWidget is not null;
    }

    // ─── Task 7: embedded floating preview ───────────────────────────────

    private void OnPreviewConfigChanged()
    {
        try { DispatcherQueue?.TryEnqueue(() =>
        {
            ApplyEmbeddedPreviewVisibility();
            ApplyButtonGating();
            ReloadEmbeddedPreview();
        }); }
        catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "OnPreviewConfigChanged", ex); }
    }

    private void ApplyEmbeddedPreviewVisibility()
    {
        if (EmbeddedPreview is null) return;
        EmbeddedPreview.Visibility = VisualistUserConfig.Instance.EditorEmbeddedPreviewEnabled
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReloadEmbeddedPreview()
    {
        if (EmbeddedPreview is null) return;
        if (!VisualistUserConfig.Instance.EditorEmbeddedPreviewEnabled) return;
        LayerWidget? widget = _vm?.SelectedWidget;
        // Feed the widget aspect so the pane height tracks the widget ratio.
        if (widget is not null && widget.Rect.Height > 0)
            EmbeddedPreview.SetWidgetAspect(widget.Rect.Width / (double)widget.Rect.Height);
        _ = EmbeddedPreviewLoadSafeAsync(ResolveLayerId(), widget?.Id);
    }

    private async System.Threading.Tasks.Task EmbeddedPreviewLoadSafeAsync(string? layerId, string? widgetId)
    {
        if (EmbeddedPreview is null) return;
        try
        {
            await EmbeddedPreview.LoadAsync(layerId, widgetId);
            // Track C — the ?widget= page loads on onStartup; once the widget is
            // (re)pointed, tell the preview which trigger the editor is actually
            // editing so the in-widget preview renders THAT sequence. This also
            // arms the trigger name the timeline-transport bridge (PostScrub /
            // PostPlay) reuses, so scrubbing/playing animates the right trigger.
            //
            // #5 — LoadAsync only fires Navigate(); the page (compositor.js) hasn't
            // loaded yet, so this synchronous SET_ACTIVE_TRIGGER races the page and
            // would be dropped, leaving the first renderAll() on the onStartup
            // fallback. The panel now ARMS the requested trigger and RE-SENDS it
            // (plus a scrub to the current playhead) on its own NavigationCompleted,
            // so the call below is the "arm" half — the panel closes the race after
            // the page is live. Seed the panel's last-scrub with the editor's
            // current playhead so the post-navigation re-send restores that frame
            // instead of snapping to t=0.
            EmbeddedPreview.SetActiveTrigger(ActiveTriggerName());
            double playheadMs = _vm?.PlayheadMs ?? 0;
            if (playheadMs > 0) EmbeddedPreview.PostScrub(playheadMs);
        }
        catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "EmbeddedPreviewLoad", ex); }
    }

    private void OnEmbeddedPreviewResized(int newWidth)
    {
        VisualistUserConfig.Instance.Update(c => c.EditorPreviewWidth = Math.Clamp(
            newWidth,
            Controls.WidgetSinglePreviewPanel.MinPreviewWidth,
            Controls.WidgetSinglePreviewPanel.MaxPreviewWidth));
        SetStatus($"Preview width: {newWidth}px");
    }

    // R6 — a node was selected/deselected in the graph; light (or clear) the
    // matching manipulator in the embedded preview. SET_MANIPULATOR/CLEAR_MANIPULATOR
    // is posted to compositor.js by WidgetSinglePreviewPanel.SetActiveNode. The
    // layer/widget popouts host a LayerPreviewPanel (no per-node manipulator), so
    // only the embedded single-widget preview takes the active-node forward.
    //
    // Track A — the same selection also drives the Inspector's typed per-node
    // NODE section. Setting VM.SelectedNode (null on deselect) rebuilds
    // SelectedNodeParams; InspectorPanel reacts to the SelectedNode
    // PropertyChanged and shows the NODE section in widget-editor mode (where
    // SetEditorContext already collapses the layer/geometry mirrors). The VM
    // set is independent of the preview path, so we run it FIRST (and outside
    // the EmbeddedPreview-null early-out) — a missing/teared-down preview
    // surface must not suppress the Inspector update.
    private void OnGraphNodeSelectionChanged(Node? node)
    {
        // Inspector routing (Track A) — always runs, even when there's no
        // embedded preview surface or no selected widget.
        if (_vm is not null) _vm.SelectedNode = node;

        try
        {
            if (EmbeddedPreview is null) return;
            string? widgetId = _vm?.SelectedWidget?.Id;
            if (string.IsNullOrEmpty(widgetId)) return;
            EmbeddedPreview.SetActiveNode(widgetId!, ActiveTriggerName(), node);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetEditorView", "OnGraphNodeSelectionChanged", ex);
        }
    }

    // Bug #5 — Ctrl+Z / Ctrl+Y forwarded from the embedded preview (its WebView2
    // had focus after a manipulator drag, so the canvas hotkey couldn't catch the
    // keypress). Route to the same pillar-level document undo/redo the graph
    // canvas's Ctrl+Z chord uses, so transform edits are undoable from the preview.
    private void OnPreviewUndoRequested()
    {
        try { FindPillarMainView()?.Undo(); }
        catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "OnPreviewUndoRequested", ex); }
    }

    private void OnPreviewRedoRequested()
    {
        try { FindPillarMainView()?.Redo(); }
        catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "OnPreviewRedoRequested", ex); }
    }

    // Apply a manipulator drag's final attribute set into the in-memory node.
    // Mirrors WidgetEditorForm.OnManipulatorAttrChanged: PushUndo, merge only the
    // changed keys, MarkDirty, repaint the graph so inline pills catch up.
    private void OnManipulatorAttrChanged(ManipulatorAttrChange change)
    {
        if (change is null || string.IsNullOrEmpty(change.NodeId)) return;
        LayerWidget? widget = _vm?.SelectedWidget;
        WidgetTrigger? trig = widget?.Triggers.FirstOrDefault(t => t.Name == change.TriggerName)
                              ?? _vm?.ActiveTriggerObject;
        Node? node = trig?.Graph?.Nodes.FirstOrDefault(n => n.Id == change.NodeId);
        if (node is null) return;

        _vm?.Document?.PushUndo();
        node.Attributes ??= new Dictionary<string, string>();
        foreach (var kv in change.Attrs) node.Attributes[kv.Key] = kv.Value;
        _vm?.Document?.MarkDirty();
        // Bug #4 — refresh ONLY this node's inline pills in place. The old path
        // (NotifyActiveTriggerChanged → ActiveTriggerObject handler + an explicit
        // RebuildGraphView) rebuilt the WHOLE graph, which dropped the node
        // selection on mouse-up — so the preview manipulator vanished and every
        // further drag needed a re-click — and needlessly reset playback to t=0.
        // The compositor already mutated the live preview during the drag; here we
        // only need the node-body TranslateX/Y / Scale / Rotation pills to catch up,
        // which RefreshNode does without touching selection or the manipulator.
        GraphCanvas.RefreshNode(change.NodeId);
        SetStatus("Applied manipulator change.");
    }

    // Bug #3 — a right-pane Inspector param edit writes node.Attributes via
    // VisualistViewModel.CommitNodeAttribute, which raises NodeParamCommitted.
    // Mirror that onto the node's inline body pills so the inspector and the node
    // never show different values. RefreshNode re-reads just this node in place,
    // keeping the selection + the active preview manipulator.
    private void OnNodeParamCommitted(Node node)
    {
        try
        {
            GraphCanvas.RefreshNode(node?.Id);
            PushNodeToPreview(node);
        }
        catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "OnNodeParamCommitted", ex); }
    }

    // Inline body-pill edit (NodeBodyCommitted). The Inspector echoes it for the
    // right-pane fields; here we forward it to the embedded preview so a typed
    // TranslateX/Y / Scale / Rotation (or any node attribute) re-renders the live
    // widget — closing the "pill handlers do nothing in the preview" gap. The pill
    // already updated itself, so no RefreshNode is needed on this path.
    private void OnNodeBodyCommitted(Node node)
    {
        try { PushNodeToPreview(node); }
        catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "OnNodeBodyCommitted", ex); }
    }

    // Push a node's CURRENT attributes to the embedded preview so compositor.js
    // mirrors them into its in-memory layer and re-renders the widget. Re-uses the
    // SET_MANIPULATOR channel (SetActiveNode) — compositor.js's setManipulator now
    // applies the attrs to the active node + requestRerenderActiveWidget, so this
    // single call updates BOTH the handle overlay and the rendered content. No-op
    // when there's no preview surface or no selected widget.
    private void PushNodeToPreview(Node? node)
    {
        if (node is null || EmbeddedPreview is null) return;
        string? widgetId = _vm?.SelectedWidget?.Id;
        if (string.IsNullOrEmpty(widgetId)) return;
        EmbeddedPreview.SetActiveNode(widgetId!, ActiveTriggerName(), node);
    }

    // ─── Task 8: Preview Layer / Preview Widget popouts ──────────────────

    private void OnPreviewLayerClicked(object sender, RoutedEventArgs e)
    {
        if (!VisualistUserConfig.Instance.EditorPopoutPreviewEnabled) return;
        if (ResolveLayerId() is null) { SetStatus("Preview unavailable — save the layer first."); return; }
        // R22 — route through the shared pillar-owned popout so the editor and
        // the layer-canvas command bar use one window.
        bool ok = FindPillarMainView()?.PreviewLayer() == true;
        SetStatus(ok ? "Opened layer preview." : "Could not open layer preview.");
    }

    private void OnPreviewWidgetClicked(object sender, RoutedEventArgs e)
    {
        if (!VisualistUserConfig.Instance.EditorPopoutPreviewEnabled) return;
        string? layerId = ResolveLayerId();
        if (layerId is null) { SetStatus("Preview unavailable — save the layer first."); return; }
        LayerWidget? widget = _vm?.SelectedWidget;
        if (widget is null || string.IsNullOrEmpty(widget.Id)) { SetStatus("Select a widget to preview."); return; }

        if (_widgetPreviewWindow is not null)
        {
            try { _widgetPreviewWindow.Activate(); return; }
            catch { _widgetPreviewWindow = null; }
        }

        try
        {
            int w = Math.Max(1, widget.Rect.Width);
            int h = Math.Max(1, widget.Rect.Height);
            var win = new Phoenix.Controls.Visualist.WinUI.Hosting.LayerPreviewWindow(
                widget.Name, layerId, widget.Id, w, h);
            win.Closed += (_, _) => _widgetPreviewWindow = null;
            _widgetPreviewWindow = win;
            win.Activate();
            SetStatus("Opened widget preview.");
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetEditorView", "OnPreviewWidgetClicked", ex);
            SetStatus("Could not open widget preview.");
        }
    }

    // ─── Task 6: timeline transport (TimelinePlayback) ───────────────────

    private void SyncPlaybackTimeline()
    {
        if (_playback is null) return;
        _playback.Timeline = _vm?.ActiveTriggerObject?.Timeline;
        UpdateTransportGlyph(_playback.IsPlaying);
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_playback is null) return;
        SyncPlaybackTimeline();
        if (_playback.IsPlaying)
        {
            _playback.Pause();
            SetStatus("Paused.");
        }
        else
        {
            var tl = _vm?.ActiveTriggerObject?.Timeline;
            if (tl is null || tl.DurationMs <= 0)
            {
                SetStatus("Nothing to play — this trigger has no timeline duration.");
                return;
            }
            _playback.Play();
            SetStatus("Playing.");
        }
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _playback?.Stop();
        SetStatus("Stopped.");
    }

    private void OnLoopToggled(object sender, RoutedEventArgs e)
    {
        if (_playback is null) return;
        _playback.Loop = LoopToggle?.IsChecked == true;
        SetStatus(_playback.Loop ? "Loop on." : "Loop off.");
    }

    // Guard so a playback-driven PlayheadMs write doesn't recurse back into the
    // engine via OnVmPropertyChanged → SyncPlaybackToPlayhead → ScrubTo.
    private bool _applyingPlaybackTick;

    private void OnPlaybackTimeChanged(double timeMs)
    {
        // TimelinePlayback fires on the UI thread (DispatcherTimer), so we can
        // touch the VM directly. Set the guard so the resulting PlayheadMs change
        // doesn't bounce back into ScrubTo.
        _applyingPlaybackTick = true;
        try
        {
            if (_vm is not null) _vm.PlayheadMs = timeMs;
            // Move the visible playhead line/halo to track automated playback
            // (the scrub path only repositions on pointer move). Mirror the
            // lightweight reposition UpdatePlayheadFromPointer uses so we don't
            // re-allocate every tick mark at 30Hz.
            _playheadMsCache = timeMs;
            RepositionPlayheadForTime(timeMs);
        }
        finally { _applyingPlaybackTick = false; }

        // Bridge the scrub into the open full-layer popout so its rendered frame
        // tracks the playhead. compositor.js applies SCRUB directly via the
        // LayerPreviewPanel message bridge.
        //
        // Track C — the EMBEDDED single-widget preview also takes the scrub now.
        // Its ?widget= page renders the active trigger (SET_ACTIVE_TRIGGER, see
        // the ActiveTrigger PropertyChanged case), and SCRUB samples that
        // trigger's graph at timeMs — so dragging the playhead animates the
        // in-widget preview and keyframing is visible. The embedded panel tracks
        // its own widget id + active trigger, so its PostScrub takes only timeMs.
        string trig = ActiveTriggerName();
        string? widgetId = _vm?.SelectedWidget?.Id;
        if (!string.IsNullOrEmpty(widgetId))
        {
            // R22 — bridge into the shared pillar-owned layer preview window.
            FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview?.PostScrub(widgetId!, trig, timeMs);
            // Track C — embedded in-widget preview follows the playhead too. It
            // tracks its own widget id + active trigger (set on LoadAsync /
            // SetActiveTrigger), so the scrub call only carries the time.
            EmbeddedPreview?.PostScrub(timeMs);
        }
    }

    private void OnPlaybackPlayingChanged(bool playing)
    {
        UpdateTransportGlyph(playing);
        string trig = ActiveTriggerName();
        string? widgetId = _vm?.SelectedWidget?.Id;
        var tl = _vm?.ActiveTriggerObject?.Timeline;
        if (string.IsNullOrEmpty(widgetId)) return;
        // R22 — bridge into the shared pillar-owned layer preview window.
        var preview = FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview;
        if (playing)
        {
            preview?.PostPlay(widgetId!, trig, tl?.DurationMs ?? 0, _playback?.Loop ?? false);
            // Track C — animate the embedded in-widget preview in lockstep. It
            // already knows its widget + active trigger, so only duration + loop
            // are passed.
            EmbeddedPreview?.PostPlay(tl?.DurationMs ?? 0, _playback?.Loop ?? false);
        }
        else
        {
            preview?.PostStop();
            EmbeddedPreview?.PostStop();
        }
    }

    private void UpdateTransportGlyph(bool playing)
    {
        if (PlayPauseGlyph is not null)
            PlayPauseGlyph.Text = playing ? "⏸" /* pause */ : "▶" /* play */;
    }

    // User scrubbed the timeline → keep the playback engine's cursor in step so
    // a subsequent Play resumes from the scrubbed position. Skipped while a
    // playback tick is the source of the change (the guard above).
    private void SyncPlaybackToPlayhead()
    {
        if (_playback is null || _applyingPlaybackTick) return;
        if (_isScrubbing || _draggingKeyframe is not null)
        {
            _playback.Timeline = _vm?.ActiveTriggerObject?.Timeline;
            _playback.ScrubTo(_vm?.PlayheadMs ?? 0);
        }
    }

    // ─── Task 4: status bar ──────────────────────────────────────────────

    /// <summary>Surface a transient status message in the bottom strip and
    /// auto-clear it to "Ready" after ~5s. Public so other surfaces can feed
    /// it. Safe to call off the UI thread (marshals).</summary>
    public void SetStatus(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        void Apply()
        {
            if (StatusLabel is null) return;
            StatusLabel.Text = message;
            StatusLabel.Foreground = (Brush)Application.Current.Resources["CoalBodyTextBrush"];

            _statusClearTimer ??= CreateStatusClearTimer();
            _statusClearTimer.Stop();
            _statusClearTimer.Start();
        }
        if (DispatcherQueue is null || DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private DispatcherTimer CreateStatusClearTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (StatusLabel is null) return;
            StatusLabel.Text = "Ready";
            StatusLabel.Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"];
        };
        return t;
    }

    // R16 — show/hide the docked media library rail (column 1). Default visible
    // for discoverability; the toggle persists only for the session.
    private void OnMediaToggleClick(object sender, RoutedEventArgs e)
    {
        bool show = MediaToggle?.IsChecked == true;
        if (MediaColumn is not null) MediaColumn.Width = show ? new GridLength(240) : new GridLength(0);
        if (MediaPanel is not null)  MediaPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Track E — simple per-trigger audio mixer ────────────────────────
    //
    // A deliberately-minimal mixer: a MASTER fader bound to the active
    // WidgetTrigger.Volume (compositor.js multiplies every Audio.Play node's
    // own Volume by it) plus one fader per Audio.Play node in the active
    // trigger, each editing that node's existing "Volume" attribute. No meters,
    // no routing — just levels.
    //
    // Both fader kinds reuse the editor's existing undo/dirty/preview-refresh
    // discipline:
    //   * node faders  → VM.CommitNodeAttribute(node, "Volume", value) (Track A
    //     chokepoint: PushUndo → write → MarkDirty → visual refresh).
    //   * master fader → the same PushUndo → set → MarkDirty → NotifyActive-
    //     TriggerChanged → ReloadEmbeddedPreview path OnDurationChanged uses,
    //     with one undo entry per drag gesture (_masterVolumeGestureDirty).

    // True once a master-fader drag/edit gesture has spent its single undo entry.
    // Reset when the flyout (re)opens and on the slider's PointerCaptureLost so a
    // fresh gesture pushes a new undo state.
    private bool _masterVolumeGestureDirty;
    // Guards the programmatic slider/readout writes (seed + cross-update) from
    // bouncing back through ValueChanged as a phantom user edit.
    private bool _suppressMixerEcho;

    private void OnAudioMixerOpening(object? sender, object e) => RebuildAudioMixer();

    private void RebuildAudioMixer()
    {
        if (AudioMixerRoot is null) return;
        AudioMixerRoot.Children.Clear();
        _masterVolumeGestureDirty = false;

        WidgetTrigger? trigger = _vm?.ActiveTriggerObject;
        if (_vm is null || trigger is null)
        {
            AudioMixerRoot.Children.Add(MixerHint("No trigger selected."));
            return;
        }

        // Header.
        AudioMixerRoot.Children.Add(new TextBlock
        {
            Text       = "AUDIO MIXER",
            Style      = (Style)Application.Current.Resources["EyebrowTextStyle"],
        });

        // ── Master fader ──────────────────────────────────────────────────
        AudioMixerRoot.Children.Add(BuildMasterRow(trigger));

        // ── Per-Audio.Play faders ────────────────────────────────────────
        var audioNodes = trigger.Graph?.Nodes
            .Where(n => n is not null && string.Equals(n.Title, "Audio.Play", StringComparison.Ordinal))
            .ToList() ?? new List<Node>();

        if (audioNodes.Count == 0)
        {
            AudioMixerRoot.Children.Add(MixerHint("No audio sources in this trigger."));
            return;
        }

        AudioMixerRoot.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 2, 0, 2),
            Background = (Brush)Application.Current.Resources["CoalCardBrush"],
        });

        int i = 1;
        foreach (Node node in audioNodes)
            AudioMixerRoot.Children.Add(BuildNodeRow(node, LabelForAudioNode(node, i++)));
    }

    // Master row — writes WidgetTrigger.Volume via the editor's own undo/dirty/
    // preview-refresh path, one undo entry per drag. Unlike OnDurationChanged we
    // deliberately do NOT call NotifyActiveTriggerChanged: Volume changes neither
    // the graph nor the timeline, and that notify would rebuild the graph + stop
    // playback on every slider tick. ReloadEmbeddedPreview alone re-renders the
    // embedded preview (where the new audio level takes effect).
    private FrameworkElement BuildMasterRow(WidgetTrigger trigger)
    {
        double cur = Math.Clamp(trigger.Volume, 0, 1);
        var (row, slider, readout) = BuildFaderRow("Master", cur);

        slider.ValueChanged += (_, args) =>
        {
            if (_suppressMixerEcho || _vm is null) return;
            double v = Math.Clamp(args.NewValue, 0, 1);
            _suppressMixerEcho = true;
            try { readout.Text = FormatVolume(v); } finally { _suppressMixerEcho = false; }
            if (Math.Abs(v - Math.Clamp(trigger.Volume, 0, 1)) < 0.0005) return;
            if (!_masterVolumeGestureDirty)
            {
                _masterVolumeGestureDirty = true;
                _vm.Document?.PushUndo();
            }
            trigger.Volume = v;
            _vm.Document?.MarkDirty();
            ReloadEmbeddedPreview();
        };
        // Each fresh drag/keyboard gesture earns its own undo entry.
        slider.PointerCaptureLost += (_, _) => _masterVolumeGestureDirty = false;

        return row;
    }

    // Per-node row — edits the node's "Volume" attribute through the Track A
    // chokepoint (VM.CommitNodeAttribute), which owns PushUndo/MarkDirty/refresh.
    private FrameworkElement BuildNodeRow(Node node, string label)
    {
        double cur = ReadNodeVolume(node);
        var (row, slider, readout) = BuildFaderRow(label, cur);

        slider.ValueChanged += (_, args) =>
        {
            if (_suppressMixerEcho || _vm is null) return;
            double v = Math.Clamp(args.NewValue, 0, 1);
            _suppressMixerEcho = true;
            try { readout.Text = FormatVolume(v); } finally { _suppressMixerEcho = false; }
            // CommitNodeAttribute no-ops + skips the undo slot when unchanged, so
            // a per-increment ValueChanged storm collapses to one entry per value.
            _vm.CommitNodeAttribute(node, "Volume",
                VisualistViewModel.PublicFormatScalar(v));
        };

        return row;
    }

    // Shared fader-row layout: [label] [slider 0..1] [percentage readout].
    private (FrameworkElement Row, Slider Slider, TextBlock Readout) BuildFaderRow(string label, double value)
    {
        double v = Math.Clamp(value, 0, 1);
        var grid = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        var name = new TextBlock
        {
            Text       = label,
            FontSize   = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["CoalBodyTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(name, label);

        var slider = new Slider
        {
            Minimum       = 0,
            Maximum       = 1,
            Value         = v,
            StepFrequency = 0.01,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var readout = new TextBlock
        {
            Text       = FormatVolume(v),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"),
            FontSize   = 11,
            TextAlignment = TextAlignment.Right,
            Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        return (grid, slider, readout);
    }

    private TextBlock MixerHint(string text) => new TextBlock
    {
        Text       = text,
        FontSize   = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
    };

    // 0..1 → "100%" readout (dB-free per the SPEC's "keep it simple").
    private static string FormatVolume(double v)
        => $"{Math.Clamp(v, 0, 1) * 100:0}%";

    // Audio.Play stores "Volume" as a BARE scalar (matching the Track A scalar
    // commit convention); default 1.0 when absent or unparsable.
    private static double ReadNodeVolume(Node node)
    {
        if (node.Attributes is { } attrs
            && attrs.TryGetValue("Volume", out var raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            return Math.Clamp(v, 0, 1);
        }
        return 1.0;
    }

    // Prefer the upstream source filename if one is trivially readable from a
    // Path/File/Source/Url attribute on the node; otherwise fall back to
    // "Audio N" by graph order.
    private static string LabelForAudioNode(Node node, int ordinal)
    {
        if (node.Attributes is { } attrs)
        {
            foreach (string key in new[] { "Path", "Source", "File", "Url" })
            {
                if (attrs.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
                {
                    string s = raw.Trim().Trim('"');
                    if (s.Length == 0) continue;
                    s = s.Replace('\\', '/');
                    int slash = s.LastIndexOf('/');
                    string file = slash >= 0 ? s[(slash + 1)..] : s;
                    if (!string.IsNullOrWhiteSpace(file)) return file;
                }
            }
        }
        return $"Audio {ordinal}";
    }
}
