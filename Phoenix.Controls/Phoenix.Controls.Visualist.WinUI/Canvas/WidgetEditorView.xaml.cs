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

    // ─── Live-preview / bus / transport wiring ──────
    //
    // The pre-T15 WinForms WidgetEditorForm carried: a green Test Run button →
    // VisualistBusClient.SendVisualTriggerAsync; Preview Layer / Preview Widget
    // popouts; a floating WidgetSinglePreviewPanel; a TimelinePlayback transport;
    // and a bottom StatusStrip. This restores all of them on WinUI. The chrome
    // stays Visualist-local; failures + offline rejections route to the status
    // bar / GlobalLogger, not modal dialogs.

    // Design-time playback engine for the active trigger's timeline. Drives
    // VM.PlayheadMs per tick + bridges PLAY/SCRUB/STOP into the open preview
    // surfaces. Created lazily on first use; disposed on Unloaded.
    private TimelinePlayback? _playback;

    // Popout windows. The LAYER preview window is now owned by the pillar
    // shell (MainView.PreviewLayer / ActiveLayerPreviewWindow) so the editor
    // toolbar and the layer-canvas command bar share one instance; the editor
    // still owns the single-WIDGET preview popout.
    private Phoenix.Controls.Visualist.WinUI.Hosting.LayerPreviewWindow? _widgetPreviewWindow;

    // Bus + config subscriptions — detached on Unloaded so a recycled editor
    // doesn't keep responding to a disposed surface's events.
    private Action<bool>?   _onBusConnChanged;
    private Action?         _onConfigChanged;

    // Status-bar auto-clear timer — resets the label to "Ready"
    // ~5s after the last SetStatus call.
    private DispatcherTimer? _statusClearTimer;

    // Trigger-tab drop insertion marker — a thin vertical line added
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
        // Restore the user's persisted manual timeline height (null = auto). The
        // resize handle writes it back on release; ComputeDesiredTimelineHeight
        // clamps it to [Min, ResizeMax] so a stored value stays sane across
        // widgets with differing track counts.
        try { _userTimelineHeight = VisualistUserConfig.Instance.TimelineHeight; } catch { /* config best-effort */ }
        DataContextChanged += OnDataContextChanged;
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
        // Delete-key keyframe removal. The view is IsTabStop so a timeline
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

    // ─── init / teardown ──────────────────────────────────────────

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
        // The VisualistBusClient connects to Hub's bus
        // (port 18081). Nothing in the WinUI assembly starts it today, so we
        // ensure it's running here — guarded process-wide so multiple editor
        // instances don't spawn duplicate connect loops. The CANONICAL start
        // belongs in the Visualist pillar bootstrap (MainView). Starting it
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

        // Forward graph-canvas node selection into the embedded preview's
        // manipulator overlay, so selecting a spatial node lights its drag handles
        // (parity with the WinForms WidgetEditorForm._canvas.OnSelectedNodeChanged
        // → _widgetPreview.SetActiveNode wire). WidgetGraphCanvas now exposes
        // OnSelectedNodeChanged. Idempotent across Loaded cycles. The inbound
        // ATTR_CHANGED → node merge (OnManipulatorAttrChanged) was already wired;
        // this closes the outbound select → manipulator direction.
        GraphCanvas.OnSelectedNodeChanged -= OnGraphNodeSelectionChanged;
        GraphCanvas.OnSelectedNodeChanged += OnGraphNodeSelectionChanged;

        // V6 — Test Run's target selector. Built once (idempotent across Loaded cycles)
        // and BEFORE the first ApplyButtonGating, because the gate now reads _testTarget.
        InitTestTargetAffordance();

        ApplyEmbeddedPreviewVisibility();
        ApplyButtonGating();

        // Rebuild the audio-mixer rows each time its flyout opens so it
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
        // Ctrl+wheel zoom anchored on the pointer, plain wheel pans
        // the visible window when zoomed in past fit-to-width.
        TimelineSurface.PointerWheelChanged += OnTimelineWheelChanged;
        // Double-click an empty track adds a keyframe on that row.
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
    // of WidgetGraphCanvas.ResolveBrush (Visualist owns its own paint helpers,
    // never lifted to Shared). Theme lookup wins; the literal ARGB triple is the
    // designer / pre-app / missing-key fallback.
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

    // Safe theme-brush resolve with a literal-ARGB fallback. The
    // RedrawTimeline path previously mixed safe (ResolveBrushTinted) and UNSAFE
    // direct `(Brush)Application.Current.Resources[key]` casts — a missing key or
    // a non-Brush value (theme swap, designer load, resource-injection test)
    // throws InvalidCastException / KeyNotFoundException and aborts the whole
    // redraw. This mirrors CurveEditorDialog.ResolveBrush: theme lookup wins, the
    // ARGB triple is the fallback. This is a per-pillar copy, never lifted to Shared.
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

        // ─── teardown ──────────────────────────────────────────────
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
        // Drop the canvas node-selection forward.
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

        // V11 — stop the preview debounce so a tick armed by the last playhead
        // write can't fire after the view is detached (it would touch node views
        // on a recycled canvas). The Tick subscription stays wired: this view is
        // a reused singleton that goes Unloaded → Loaded on every re-enter, and
        // RequestPreviewRefreshAtPlayhead re-uses the same timer instance.
        try { _previewRefreshDebounce?.Stop(); } catch { }

        // Drop the audio-mixer flyout subscription.
        if (AudioMixerFlyout is not null)
        {
            try { AudioMixerFlyout.Opening -= OnAudioMixerOpening; } catch { }
        }

        // Close the editor-owned widget popout so its WebView2 process tears down
        // with the editor. The shared LAYER preview window is owned by the
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
                // Tell the embedded in-widget preview which trigger is
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
                // V11 — node-body preview thumbnails follow the playhead. Debounced
                // + gesture-gated (see RequestPreviewRefreshAtPlayhead); this arm only
                // arms the timer, it never walks the graph inline.
                RequestPreviewRefreshAtPlayhead();
                break;
        }
    }

    // ─── trigger tabs ───────────────────────────────────────────────────

    private void RebuildTriggerTabs(VisualistViewModel vm)
    {
        TriggerTabStrip.Children.Clear();

        // Leading "← Back" returns to the Layer Canvas, matching the
        // manifesto §4.5 "[← Back] [onStartup] … [+]" strip and the pre-WinUI
        // WidgetEditorForm "Back to Layout" button. Under the 2-tab shell the
        // Layer Canvas tab is also always visible, but Back reinforces the
        // "entered a widget" model and is the gesture authors expect.
        var back = new Button
        {
            Content          = Localizer.T("visualist.widget.tabs.back", "← Back"),
            Padding          = new Thickness(8, 0, 8, 0),
            Background       = ResolveBrushTinted("CoalCardBrush", 0xFF, 0x2A, 0x26, 0x20),
            BorderThickness  = new Thickness(0),
            Foreground       = ResolveBrushTinted("CoalBodyTextBrush", 0xFF, 0xC9, 0xC2, 0xB6),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin           = new Thickness(0, 0, 6, 0),
        };
        ToolTipService.SetToolTip(back, Localizer.T("visualist.widget.tabs.back.tip", "Back to the Layer Canvas"));
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

            // Drag-to-reorder. WinUI's StackPanel doesn't ship a reorder
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

    // ─── trigger tab drag-to-reorder ────────────────────────────────
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
        // Once armed, render an insertion marker at the computed
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
        // Drop the insertion marker on release / cancel.
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

        // Duplicate / Move Left / Move Right. Duplicate
        // deep-clones the trigger including graph + timeline with an _copy
        // suffix (underscore — a hyphen fails the WidgetTrigger.Name regex;
        // see VisualistViewModel.DuplicateTrigger).
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

        // Cross-pillar snippet producer. Right-clicking a trigger
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
        // onStartup is semantically auto-fired — it cannot be called
        // from Architect via a Visual.Trigger node (which is exactly what the
        // snippet produces), so copying a snippet for it would author a node that
        // never matches a callable trigger. Disable the item for onStartup and
        // explain why via tooltip, mirroring how the old InspectorPanel hid
        // Copy/Test for onStartup. (This is a repeatable rejection, so we disable
        // rather than pop a rejection dialog.)
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
    /// paths log via GlobalLogger — no modal dialogs.
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
            // The "+" is disabled without a selected widget, but guard
            // anyway and tell the user why instead of silently no-oping.
            SetStatus(Localizer.T("visualist.widget.status.select_widget_first",
                "Select or enter a widget before adding a trigger."));
            return;
        }
        // "onTrigger:new" is the persisted trigger-name grammar, not prose — it
        // stays English in every language (see the trigger-name rule).
        string? name = await PromptForName(
            Localizer.T("visualist.widget.trigger.add.title", "New Trigger"), "onTrigger:new", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        // vm.AddTrigger returns null on empty / duplicate / invalid name.
        // The pre-fix code discarded the result, so a rejected add looked like a
        // dead "+". A bare name like "raid" is now auto-prefixed to onTrigger:raid,
        // so only genuinely-bad input fails — and we surface the SPECIFIC reason.
        if (vm.AddTrigger(name, out var status) is null)
        {
            SetStatus(status == VisualistViewModel.AddTriggerStatus.Duplicate
                ? string.Format(
                    Localizer.T("visualist.widget.trigger.duplicate_format",
                        "A trigger named \"{0}\" already exists."),
                    VisualistViewModel.NormalizeTriggerName(name))
                : string.Format(
                    Localizer.T("visualist.widget.trigger.invalid_format",
                        "\"{0}\" isn't a valid trigger name — use letters, digits and underscores (e.g. onTrigger:raid)."),
                    name.Trim()));
        }
    }

    private async System.Threading.Tasks.Task PromptRenameTrigger(VisualistViewModel vm, string oldName)
    {
        string? name = await PromptForName(
            Localizer.T("visualist.widget.trigger.rename.title", "Rename Trigger"), oldName, oldName);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, oldName, StringComparison.Ordinal)) return;
        if (!vm.RenameTrigger(oldName, name))
        {
            SetStatus(Localizer.T("visualist.widget.status.rename_failed",
                "Rename failed — name is invalid or in use."));
            return;
        }
        // Zoom-state key follows the rename. _zoomByTrigger is keyed
        // on "{widget.Id}|{trigger.Name}" (a USER-EDITABLE name, not a stable id).
        // A rename leaves the old key orphaned AND seeds a fresh fit-to-width zoom
        // under the new key — the user's zoom level silently resets on rename.
        // Migrating the entry preserves the zoom and prunes the stale key.
        MigrateZoomKeyForRename(vm.SelectedWidget, oldName, name.Trim());
    }

    // Move the zoom entry from the old trigger-name key to the new
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
            Title              = Localizer.T("visualist.widget.trigger.delete.title", "Delete trigger?"),
            Content            = string.Format(
                Localizer.T("visualist.widget.trigger.delete.body_format",
                    "This deletes trigger \"{0}\" — its graph and timeline are removed. Undo restores them."),
                name),
            PrimaryButtonText  = Localizer.T("common.button.delete", "Delete"),
            CloseButtonText    = Localizer.T("common.cancel", "Cancel"),
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
            PrimaryButtonText  = Localizer.T("common.ok", "OK"),
            CloseButtonText    = Localizer.T("common.cancel", "Cancel"),
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
    // The read-only stack-of-cards was swapped for an interactive
    // [WidgetGraphCanvas](WidgetGraphCanvas.xaml.cs) — pan / zoom / node
    // drag against absolute Location. The canvas owns its own empty-state
    // hint, so this method just hands it the active VM + trigger; the
    // canvas re-renders nodes whenever Bind is called.
    //
    // Wire-drop, palette, lasso, context menus, live preview thumbnails, and
    // timeline scrubbing layer on top — see the canvas's own header for the
    // seam map.

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

    // ─── trigger duration editor + Fit ──────────────────────────────
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
            SetStatus(Localizer.T("visualist.widget.status.no_trigger", "No trigger selected."));
            return;
        }
        double maxKf = tl.SortedKeyframes.Count > 0 ? tl.SortedKeyframes.Max(k => k.TimeMs) : 0;
        // Last keyframe + 200ms tail, rounded up to the next 100ms; floor 1000ms
        // (mirrors the pre-WinUI FitDurationToKeyframes math).
        double fit = Math.Ceiling((maxKf + 200) / 100.0) * 100.0;
        if (fit < 1000) fit = 1000;
        if (Math.Abs(fit - tl.DurationMs) < 0.5) { SetStatus(Localizer.T("visualist.widget.status.duration_fits", "Duration already fits.")); return; }
        _vm.Document?.PushUndo();
        tl.DurationMs = fit;
        _vm.Document?.MarkDirty();
        _vm.NotifyActiveTriggerChanged();
        SyncDurationBox();
        RedrawTimeline();
        SetStatus(string.Format(
            Localizer.T("visualist.widget.status.duration_fit_format", "Duration fit to {0} ms."),
            fit.ToString("0")));
    }

    // ─── empty-state + Back navigation ──────────────────────────

    /// <summary>Show the "no widget selected" hint over the graph when
    /// there's no widget to edit; otherwise hide it.</summary>
    private void ApplyEmptyState()
    {
        if (NoWidgetEmptyState is null) return;
        NoWidgetEmptyState.Visibility =
            _vm?.SelectedWidget is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEmptyStateBackClicked(object sender, RoutedEventArgs e)
        => FindPillarMainView()?.ShowLayerCanvas();

    // Visualist-local copy of the visual-tree walk to the embedding
    // MainView (copied, not lifted to Shared). Mirrors
    // LayerCanvasView.FindPillarMainView.
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

    // ─── timeline scrubber ───────────────────────────────────
    //
    // Replaces the earlier mock bars with a real time-axis + keyframe
    // markers + draggable playhead bound to VisualistViewModel.PlayheadMs.
    // The trigger's WidgetTimeline.DurationMs sets the right edge; tick
    // marks land on a human-friendly ramp picked from the active
    // pixels-per-second zoom (see the zoom section below).

    private double _playheadMsCache;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _playheadLine;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _playheadHalo;
    private bool _isScrubbing;

    // Keyframe-marker drag state. _selectedKeyframe is the
    // currently-highlighted keyframe (rendered with the Ember200 fill); set
    // on PointerPressed, persists until a different marker is pressed or the
    // timeline is rebuilt with a now-removed keyframe (auto-cleared below).
    private Keyframe? _selectedKeyframe;
    private Keyframe? _draggingKeyframe;
    private bool _keyframeDragDirty;

    // Direct handle to the dragged keyframe's live marker Rectangle (same
    // idea as _playheadLine): the drag fast path slides only this marker's
    // Margin plus the playhead per pointer-move instead of a full
    // RedrawTimeline; the full rebuild happens once on release. Captured by
    // RedrawTimeline's marker loop (the press-time rebuild replaces the rect
    // the pointer was captured on, so the sender element is NOT the live one).
    private Microsoft.UI.Xaml.Shapes.Rectangle? _draggingMarkerRect;
    private double _draggingMarkerCenterY;

    // Per-ParameterPath track rows. Rebuilt each RedrawTimeline from
    // the active trigger's keyframe set (insertion order = stable track order);
    // double-click-add maps the cursor Y back to a row → ParameterPath.
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

    // ─── dynamic timeline height (Change 1 + 2) ────────────────────
    //
    // Pre-fix the timeline track area was height-locked at the row's
    // MinHeight (~48px): a single animated Color pin = 4 tracks, and the
    // lower tracks were squeezed into an unreachable sliver. The surface now
    // GROWS with the track count — desired height = TrackAreaTop + n *
    // per-track band + bottom pad — clamped to an auto ceiling of
    // TimelineMaxVisibleTracks. This changes ONLY the vertical (Y) layout;
    // the time↔X math (TimeToX / XToTime) still derives purely from
    // ActualWidth, so every scrub / keyframe-drag coordinate stays identical.
    //
    // A hand-rolled N/S resize handle lets the user override the auto height
    // by dragging the top edge (Change 2). The chosen height is held in
    // _userTimelineHeight and persisted to VisualistUserConfig.TimelineHeight —
    // restored on init (ctor) and written on drag-release (EndTimelineResize).
    private const double TimelineMinHeight        = 48;   // historical compact floor
    private const double TimelinePerTrackHeight   = 24;   // comfortable per-track band
    private const double TimelineBottomPad        = 8;
    private const int    TimelineMaxVisibleTracks = 7;    // auto-grow ceiling
    private const double TimelineResizeMaxHeight  = 420;  // manual drag ceiling

    // User-dragged timeline height override (null = follow the auto-grow
    // formula). Session-local; the resize handle writes it.
    private double? _userTimelineHeight;

    // Rotated diamond marker size (Change 3) — enlarged 9→13 so a keyframe is
    // easy to grab at low zoom. Half is the center offset used both when the
    // marker is created and in the drag fast path, kept as one constant so the
    // two never drift.
    private const double KeyframeMarkerSize = 13.0;
    private const double KeyframeMarkerHalf = KeyframeMarkerSize / 2.0;

    // Explicit timeline Z-order. Pre-fix the layering was implicit in
    // the Children.Add() call sequence (ticks → baselines → markers → playhead),
    // so a future refactor that batched or reordered the Add() calls could
    // silently put the playhead behind the markers. These structural Z bands make
    // the order intent explicit and refactor-proof: ticks/baselines at the back,
    // keyframe markers above them, the playhead always on top.
    private const int ZTick     = 0;
    private const int ZBaseline = 0;
    private const int ZMarker   = 10;
    private const int ZPlayhead = 20;

    // ─── timeline zoom ────────────────────────────────────────
    //
    // Pre-fix the timeline was permanently fit-to-width with a hardcoded
    // 0.5s/1s/2s/5s/10s/30s/60s tick ramp — multi-minute durations capped at
    // a 60s ramp produced thousands of ticks, and authors couldn't zoom in
    // to fine-tune ms-level keyframes either. This block replaces the
    // px-derived-from-duration math with an explicit pixels-per-second
    // zoom whose tick interval is picked off a wider ramp under a 40px
    // minimum-spacing constraint.
    //
    // Persistence is *session-local* per
    // (widget.Id, trigger.Name) — same scope as PlayheadMs, which is also
    // not written to .phxlayer. The zoom level survives panel close inside
    // a single Visualist process; closing and re-opening the layer resets
    // to the fit-to-width default. Persisting to disk would require a
    // .phxlayer schema bump that's out of scope here.

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
        // Change 1 — the track area height now GROWS with the track count (or
        // honours the user's resize-handle override) instead of being locked to
        // the row MinHeight. Drive the surface Height from the computed value so
        // the Auto-sized timeline row grows with it, and lay out against that
        // same value (not the not-yet-updated ActualHeight) so the first pass
        // paints at the final size. This is a pure Y-axis change — the X/time
        // math below is untouched.
        double h = ComputeDesiredTimelineHeight();
        ApplyTimelineSurfaceHeight(h);
        // Bug #3 (timeline ruler ticks vanish on re-enter) — guard the not-yet-
        // measured case BEFORE clearing. Re-entering the widget editor recycles
        // this singleton view; the re-entry relayout can fire a transient
        // SizeChanged with a 0-width surface. If we Clear()'d first and then
        // bailed, the ruler ticks were wiped and never repainted — re-entering
        // the SAME widget doesn't change the DataContext, so OnDataContextChanged's
        // RedrawTimeline never runs to rebuild them. Leaving the existing children
        // in place until we can actually draw makes a transient pass a no-op
        // instead of a destructive wipe. (h is always a positive computed value
        // now, so the width check is the meaningful guard.)
        if (w <= 0) { UpdateZoomChip(); return; }
        TimelineSurface.Children.Clear();
        _playheadLine = null;
        _playheadHalo = null;
        _draggingMarkerRect = null;   // re-captured in the marker loop below

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

        // Tick density now derives from pixels-per-second under a
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
                Fill   = ResolveBrush("CoalCardBrush", 0x22, 0x1C, 0x16), // safe resolve
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Stretch,
                Margin = new Thickness(x, 0, 0, 0),
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(line, ZTick); // explicit Z-order
            TimelineSurface.Children.Add(line);

            var label = new TextBlock
            {
                Text       = FormatTickLabel(t / 1000.0, tickSec),
                FontFamily = new FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws
                FontSize   = 9,
                Foreground = ResolveBrush("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72), // safe resolve
                Margin     = new Thickness(x + 2, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(label, ZTick); // explicit Z-order
            TimelineSurface.Children.Add(label);
        }

        // Keyframes for the active trigger render as small ember diamonds
        // along a baseline track. Multiple keyframes at the same TimeMs (one
        // per ParameterPath) overlap; sprint future-polish can stagger.
        //
        // Diamonds are now hit-testable. PointerPressed on a marker
        // captures + snaps the playhead to the keyframe's time (so the value
        // displays in any keyframe inspector); PointerMoved drags the keyframe
        // along the timeline (mutating Keyframe.TimeMs + marking the document
        // dirty); PointerReleased commits. Per-marker e.Handled stops the
        // surface-level scrub handler from also firing.
        //
        // Keyframes outside the visible scroll window are skipped
        // entirely so a 1ms-zoom on a 10min trigger doesn't allocate markers
        // for the thousands of keyframes off-screen.
        // Render from the NaN/Infinity-filtered, sorted projection
        // (WidgetTimeline.SortedKeyframes) rather than the raw authoring list.
        // A NaN TimeMs would produce a NaN x here: TimeToX(NaN) → NaN, the
        // off-screen bounds check below stays false for NaN, and a NaN Thickness
        // margin then corrupts the WinUI layout engine. SortedKeyframes drops
        // those poisoned values before they reach the markers.
        // One track ROW per ParameterPath, each curve a distinct
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
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(line, ZBaseline); // explicit Z-order
                TimelineSurface.Children.Add(line);

                var lbl = new TextBlock
                {
                    Text       = PrettyParamLabel(row.Path, trigger?.Graph),
                    FontFamily = new FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"),
                    FontSize   = 8,
                    Foreground = ResolveBrush("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72), // safe resolve
                    Opacity    = 0.65,
                    Margin     = new Thickness(3, Math.Max(0, row.CenterY - 11), 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140,
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(lbl, ZBaseline); // explicit Z-order
                TimelineSurface.Children.Add(lbl);
            }

            var goldStroke   = ResolveBrushTinted("SelectionBrush", 0xFF, 0xFF, 0xD7, 0x00);
            var normalStroke = ResolveBrush("Ember700Brush", 0x4A, 0x2A, 0x08); // safe resolve

            // Off-screen cull in TIME space, not a fixed pixel margin.
            // A rotated 13px diamond (Change 3) has a ~9.2px half-diagonal, so
            // the cull tail must exceed that or a marker straddling the edge gets
            // clipped; a pixel margin also makes the visible-edge tail shrink (in
            // ms) as you zoom in, which is the wrong direction. Cull markers whose
            // TimeMs falls outside the visible window [leftMs, rightMs] expanded
            // by a small px tail converted to ms at the current zoom, so the cull
            // tail is a constant ~12px regardless of zoom and a marker straddling
            // the edge is never clipped.
            const double cullTailPx = 12.0;
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
                    // Change 3 — 13×13 (was 9×9) so the diamond is an easy grab
                    // target at low zoom; still a rotated square, so the look is
                    // unchanged bar the size. The hit area is the marker itself,
                    // so the larger rect enlarges the grab zone directly.
                    Width  = KeyframeMarkerSize,
                    Height = KeyframeMarkerSize,
                    // Fill is the per-curve colour; selection moves to the
                    // STROKE (gold, heavier) so the curve identity is never lost
                    // when a keyframe is selected.
                    Fill   = CurveBrush(rowIdx),
                    Stroke = selected ? goldStroke : normalStroke,
                    StrokeThickness = selected ? 1.75 : 0.75,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                    Margin = new Thickness(x - KeyframeMarkerHalf, trackRow.CenterY - KeyframeMarkerHalf, 0, 0),
                    RenderTransform = new RotateTransform { Angle = 45 },
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    IsHitTestVisible = true,
                };
                marker.PointerPressed  += (s, args) => OnKeyframeMarkerPressed(s, args, captureKf);
                // PointerMoved now captures the keyframe identity in
                // its closure (parity with Pressed/Released/RightTapped) instead
                // of relying solely on the _draggingKeyframe field, so a stray
                // PointerMoved on the wrong marker (before CapturePointer fully
                // isolates events) can't mutate a different keyframe.
                marker.PointerMoved    += (s, args) => OnKeyframeMarkerMoved(s, args, captureKf);
                marker.PointerReleased += OnKeyframeMarkerReleased;
                // Capture-lost / canceled recovery — a swallowed release after a
                // window re-activation (alt-tab back in) would otherwise leave
                // _draggingKeyframe armed so the keyframe tracks the cursor. Same
                // drag-stick class the node/pan/lasso gestures guard against.
                marker.PointerCaptureLost += OnKeyframeMarkerCaptureLost;
                marker.PointerCanceled    += OnKeyframeMarkerCaptureLost;
                // Hover cursor affordance — restores the WinForms
                // baseline's "hover to discover drag" hint lost in the per-marker
                // port. ProtectedCursor is protected and only settable from this
                // derived UserControl (see WidgetGraphCanvas / WidgetSinglePreview
                // — both set it on `this`, never on children), so we drive the
                // editor's own cursor while the pointer is over a marker and clear
                // it on exit. Wrapped because the cursor API can be unavailable in
                // designer / pre-app hosts.
                marker.PointerEntered  += OnKeyframeMarkerPointerEntered;
                marker.PointerExited   += OnKeyframeMarkerPointerExited;
                // Right-click context menu (Delete / Edit Curve… /
                // Cycle Curve). Attached per-marker so the menu carries the
                // keyframe-under-cursor identity. RightTapped is the WinUI
                // event for right-click — PointerPressed sees both buttons
                // but RightTapped only fires for the right press, which is
                // what we want here.
                marker.RightTapped += (s, args) => OnKeyframeMarkerRightTapped(s, args, captureKf);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(marker, ZMarker); // explicit Z-order — markers above ticks/baselines
                // Keep the drag fast path's handle on the LIVE rect for the
                // keyframe being dragged (the press-time rebuild replaced the
                // rect the pointer capture actually sits on).
                if (ReferenceEquals(_draggingKeyframe, kf))
                {
                    _draggingMarkerRect    = marker;
                    _draggingMarkerCenterY = trackRow.CenterY;
                }
                TimelineSurface.Children.Add(marker);
            }
        }

        // Playhead — vertical line + soft halo. Draggable: PointerPressed
        // captures, PointerMoved updates VM.PlayheadMs, PointerReleased
        // commits. With zoom the playhead may sit off-screen; we still emit
        // the elements so the cached references stay non-null for smooth
        // scrub, just clamp their X into the visible band.
        //
        // Playhead-skip fix: a full redraw re-allocates the playhead
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
            Fill   = ResolveBrush("Ember200Brush", 0xF2, 0xC7, 0x7F), // safe resolve
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Margin = new Thickness(phX, 0, 0, 0),
            IsHitTestVisible = false,
            Visibility = (phXRaw < -1 || phXRaw > w + 1) ? Visibility.Collapsed : Visibility.Visible,
        };
        // Playhead always renders on top of markers/ticks via an
        // explicit Z band, not the Add() order, so a future redraw refactor can't
        // accidentally bury it behind the keyframe markers.
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_playheadHalo, ZPlayhead);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_playheadLine, ZPlayhead);
        TimelineSurface.Children.Add(_playheadHalo);
        TimelineSurface.Children.Add(_playheadLine);

        // Scrub handlers are now subscribed once on Loaded and
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

    // Ctrl+wheel zooms (cursor-anchored), plain wheel pans the
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
        // RedrawTimeline now re-syncs _playheadMsCache from the live
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
        // Focus the editor so a subsequent Delete targets the timeline
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

    // ─── keyframe-marker drag ─────────────────────────────────

    private void OnKeyframeMarkerPressed(object sender, PointerRoutedEventArgs e, Keyframe kf)
    {
        if (sender is not FrameworkElement marker) return;
        // Capture pointer on the marker (not the timeline surface) so the
        // PointerMoved events bound to the marker keep firing even when the
        // pointer leaves the small 9×9 rect during drag.
        try { marker.CapturePointer(e.Pointer); } catch { /* best-effort */ }
        // Take keyboard focus so Delete removes this keyframe.
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
        // Identity guard — only act when the keyframe this handler
        // was wired for is the one actually being dragged. A PointerMoved that
        // arrives on a non-source marker (before CapturePointer isolates the
        // stream) is ignored rather than mutating _draggingKeyframe's keyframe by
        // accident.
        if (_draggingKeyframe is not Keyframe kf) return;
        if (!ReferenceEquals(kf, captureKf)) return;

        // Stale-gesture guard — a keyframe drag only continues while the left
        // button is held. A swallowed activation release (alt-tab back in) would
        // otherwise leave the marker tracking the cursor. Settle like a release.
        if (!e.GetCurrentPoint(TimelineSurface).Properties.IsLeftButtonPressed)
        {
            EndKeyframeDrag(sender as FrameworkElement, e.Pointer);
            e.Handled = true;
            return;
        }

        if (_vm?.ActiveTriggerObject?.Timeline is not WidgetTimeline tl) return;

        double durationMs = tl.DurationMs > 0 ? tl.DurationMs : 5000;
        double w = TimelineSurface.ActualWidth;
        if (w <= 0) return;

        // Drag math now goes through the zoom transform so dragging
        // at any zoom level produces a millisecond delta proportional to the
        // pointer's pixel delta, not to the surface width.
        var zoom = _zoom ?? ResolveZoomState(_vm.SelectedWidget, _vm.ActiveTriggerObject, durationMs, w);
        double xInTimeline = e.GetCurrentPoint(TimelineSurface).Position.X;
        double newMs = XToTime(Math.Clamp(xInTimeline, 0, w), zoom.PxPerSec, zoom.ScrollOffsetMs);
        newMs = Math.Clamp(newMs, 0, durationMs);
        // Change 4 — snap (AFTER the clamp) to the 100ms grid + sibling
        // keyframes on the SAME track, unless Alt is held (momentary precise
        // drop, matching the WidgetGraphCanvas Alt-suppress convention). The
        // playhead is NOT a snap target here — it is slaved to this drag
        // (_vm.PlayheadMs = newMs below), so snapping to it would be degenerate.
        // Re-clamp defensively; a snap target is in-range by construction.
        newMs = Math.Clamp(
            SnapTimelineMs(newMs, zoom.PxPerSec, kf.ParameterPath, includePlayhead: false),
            0, durationMs);
        if (Math.Abs(newMs - kf.TimeMs) < 0.5) return; // sub-frame jitter → ignore

        if (!_keyframeDragDirty)
        {
            _keyframeDragDirty = true;
            if (_vm.Document is { } doc) doc.PushUndo();
        }
        kf.TimeMs = newMs;
        if (_vm is not null) _vm.PlayheadMs = newMs;
        e.Handled = true;

        // Lightweight per-move repaint (the scrub path's pattern): slide only
        // the dragged marker's Margin and the playhead line/halo instead of
        // re-emitting every tick, label, baseline and marker per pointer-move.
        // The marker already carries its selection styling from the press-time
        // rebuild; the full RedrawTimeline reconciliation runs once on release.
        if (_draggingMarkerRect is { } markerRect)
        {
            double x = TimeToX(newMs, zoom.PxPerSec, zoom.ScrollOffsetMs);
            markerRect.Margin = new Thickness(x - KeyframeMarkerHalf, _draggingMarkerCenterY - KeyframeMarkerHalf, 0, 0);
            _playheadMsCache = newMs;
            RepositionPlayheadForTime(newMs);
        }
        else
        {
            // No live marker handle (zero-size redraw skipped the rebuild) —
            // fall back to the full pass rather than dropping the frame.
            RedrawTimeline();
        }
    }

    private void OnKeyframeMarkerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndKeyframeDrag(sender as FrameworkElement, e.Pointer);
        e.Handled = true;
    }

    private void OnKeyframeMarkerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingKeyframe is null) return; // not an armed keyframe gesture
        EndKeyframeDrag(sender as FrameworkElement, e.Pointer);
    }

    /// <summary>
    /// Idempotent teardown for a keyframe-marker drag, shared by
    /// <see cref="OnKeyframeMarkerReleased"/>, the capture-lost handler, and the
    /// stale-gesture guard in <see cref="OnKeyframeMarkerMoved"/>. State is cleared
    /// BEFORE releasing capture so the synchronous PointerCaptureLost this raises
    /// re-enters as a no-op instead of double-committing.
    /// </summary>
    private void EndKeyframeDrag(FrameworkElement? marker, Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        bool dragged = _keyframeDragDirty;
        _draggingKeyframe   = null;
        _keyframeDragDirty  = false;
        _draggingMarkerRect = null;
        if (marker is not null && pointer is not null)
        {
            try { marker.ReleasePointerCapture(pointer); } catch { /* already lost — harmless */ }
        }
        // One full reconciliation pass for a drag that actually moved — the
        // per-move fast path only slid the dragged marker + playhead, so the
        // release rebuild settles everything (marker child order, playhead cache
        // re-sync) in a single redraw. A plain click (no move) changed nothing the
        // press-time redraw didn't already paint.
        if (dragged && _vm?.Document is { } doc) doc.MarkDirty();
        if (dragged) RedrawTimeline();
    }

    // ─── keyframe hover cursor affordance ────────────────────
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

    // ─── per-parameter track rows + per-curve colours ──────────

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

    // ─── dynamic timeline height + resize + snap helpers ──────────

    // Distinct animated-parameter (track) count for the active trigger — the
    // number of dope-sheet rows. Mirrors BuildTrackRows' distinct-ParameterPath
    // pass without needing the surface height, so the desired height can be
    // computed BEFORE the track rows are laid out.
    private int CountDistinctTrackPaths()
    {
        var kfs = _vm?.ActiveTriggerObject?.Timeline?.Keyframes;
        if (kfs is null || kfs.Count == 0) return 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in kfs)
        {
            string p = k?.ParameterPath ?? "";
            if (p.Length == 0) continue;
            seen.Add(p);
        }
        return seen.Count;
    }

    // Desired timeline surface height. Honours the user's resize-handle
    // override when set; otherwise grows with the track count up to the
    // TimelineMaxVisibleTracks ceiling (beyond which BuildTrackRows compresses
    // rows inside the existing TrackMin/Max clamp). Never below TimelineMinHeight.
    private double ComputeDesiredTimelineHeight()
    {
        double pad = TrackAreaTop + TimelineBottomPad;
        if (_userTimelineHeight is double uh)
            return Math.Clamp(uh, TimelineMinHeight, TimelineResizeMaxHeight);
        int n = Math.Max(1, CountDistinctTrackPaths());
        double autoH    = pad + n * TimelinePerTrackHeight;
        double maxAutoH = pad + TimelineMaxVisibleTracks * TimelinePerTrackHeight;
        return Math.Clamp(autoH, TimelineMinHeight, maxAutoH);
    }

    // Drive the surface Height from the computed/overridden value. Only writes
    // when it actually changes (Height defaults to NaN = Auto) so a redraw that
    // didn't move the height doesn't kick off a redundant SizeChanged →
    // RedrawTimeline round-trip. The timeline row is Auto, so growing the
    // surface grows the row.
    private void ApplyTimelineSurfaceHeight(double desiredH)
    {
        if (TimelineSurface is null) return;
        double cur = TimelineSurface.Height;
        if (double.IsNaN(cur) || Math.Abs(cur - desiredH) > 0.5)
        {
            TimelineSurface.Height    = desiredH;
            TimelineSurface.MinHeight = desiredH;
        }
    }

    // ─── timeline resize handle (Change 2) ─────────────────────────
    //
    // Hand-rolled N/S splitter on the timeline's top edge — WinUI 3 ships no
    // GridSplitter. Button-held guard + capture-lost teardown mirror the
    // MainView rail-splitter pattern. Dragging UP grows the track area (and,
    // because Row 3 is Auto and the graph row is star-sized, shrinks the graph);
    // dragging DOWN shrinks it. Pointer Y is read against `this` — a stable
    // frame that does NOT move as the row resizes — to avoid drag feedback.
    private bool   _timelineResizeDrag;
    private double _resizeDragStartY;
    private double _resizeStartHeight;

    private void OnTimelineResizeEntered(object sender, PointerRoutedEventArgs e)
    {
        try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth); }
        catch { /* cursor API unavailable in designer / pre-app — ignore */ }
        // T11-9 — ember hover accent on the grabber pill (skip if a drag is live).
        if (!_timelineResizeDrag) SetTimelinePill("EmberDeepBrush");
    }

    private void OnTimelineResizeExited(object sender, PointerRoutedEventArgs e)
    {
        if (_timelineResizeDrag) return; // keep the resize cursor + highlight through the drag
        try { ProtectedCursor = null; } catch { /* best-effort */ }
        SetTimelinePill("CoalDividerBrush");
    }

    // T11-9 — paint the timeline N/S handle's grabber pill for hover/drag
    // affordance, matching the MainView side-splitter treatment. The pill is the
    // handle Border's single child; a missing token degrades to a no-op.
    private void SetTimelinePill(string brushKey)
    {
        if (TimelineResizeHandle?.Child is Border pill
            && Application.Current?.Resources is { } res
            && res.TryGetValue(brushKey, out var v) && v is Brush b)
            pill.Background = b;
    }

    private void OnTimelineResizePressed(object sender, PointerRoutedEventArgs e)
    {
        _timelineResizeDrag = true;
        _resizeDragStartY   = e.GetCurrentPoint(this).Position.Y;
        _resizeStartHeight  = _userTimelineHeight ?? ComputeDesiredTimelineHeight();
        SetTimelinePill("EmberPrimaryBrush");
        if (sender is UIElement el) { try { el.CapturePointer(e.Pointer); } catch { /* best-effort */ } }
        e.Handled = true;
    }

    private void OnTimelineResizeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelineResizeDrag) return;
        var pp = e.GetCurrentPoint(this);
        // Stale-gesture guard — settle like a release if the button was let go
        // during a swallowed release (alt-tab back in).
        if (!pp.Properties.IsLeftButtonPressed) { EndTimelineResize(sender as UIElement, e.Pointer); return; }
        // Dragging the top handle UP (smaller Y) grows the track area.
        double dy   = _resizeDragStartY - pp.Position.Y;
        double newH = Math.Clamp(_resizeStartHeight + dy, TimelineMinHeight, TimelineResizeMaxHeight);
        _userTimelineHeight = newH;
        RedrawTimeline(); // applies the new height + relays out the track rows
        e.Handled = true;
    }

    private void OnTimelineResizeReleased(object sender, PointerRoutedEventArgs e)
        => EndTimelineResize(sender as UIElement, e.Pointer);

    private void OnTimelineResizeCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndTimelineResize(sender as UIElement, e.Pointer);

    private void EndTimelineResize(UIElement? handle, Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        _timelineResizeDrag = false;
        if (handle is not null && pointer is not null)
        { try { handle.ReleasePointerCapture(pointer); } catch { /* already lost — harmless */ } }
        try { ProtectedCursor = null; } catch { /* best-effort */ }
        SetTimelinePill("CoalDividerBrush");   // T11-9 — back to resting pill

        // Persist the chosen height so it survives across sessions. Guarded so a
        // config-write fault can't break the resize interaction.
        try { VisualistUserConfig.Instance.Update(c => c.TimelineHeight = _userTimelineHeight); }
        catch (Exception ex) { GlobalLogger.Error("Visualist.WinUI", "persist timeline height", ex); }
    }

    // ─── timeline snapping (Change 4) ──────────────────────────────
    //
    // Snap a proposed time to strong targets — the 100ms grid, and keyframes
    // (same-track for a keyframe drag, any-track for a scrub) — within a small
    // PIXEL threshold at the current zoom (so the grab radius feels the same at
    // every zoom level). Alt held suspends snap for a precise drop: the modifier
    // disables the active constraint rather than adding one, matching the
    // WidgetGraphCanvas Alt-suppress convention. Callers apply this AFTER their
    // own clamp — a snap target is always in-range by construction.
    private const double SnapThresholdPx = 8.0;   // grab radius, in pixels
    private const double SnapGridMs      = 100.0; // hard 100ms grid

    // VirtualKey.Menu == Alt. Local mirror of WidgetGraphCanvas.IsAltDown so the
    // hold-to-suspend-snap gesture reads identically across the two surfaces.
    private static bool IsAltDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private double SnapTimelineMs(double proposedMs, double pxPerSec,
                                  string? sameTrackPath, bool includePlayhead)
    {
        if (IsAltDown()) return proposedMs;        // power-user precise drop
        if (pxPerSec <= 0) return proposedMs;
        double thresholdMs = (SnapThresholdPx / pxPerSec) * 1000.0;
        if (thresholdMs <= 0) return proposedMs;

        double bestMs   = proposedMs;
        double bestDist = thresholdMs;             // only snap within threshold
        void Consider(double target)
        {
            if (double.IsNaN(target) || double.IsInfinity(target)) return;
            double d = Math.Abs(target - proposedMs);
            if (d < bestDist) { bestDist = d; bestMs = target; }
        }

        // Nearest 100ms gridline.
        Consider(Math.Round(proposedMs / SnapGridMs) * SnapGridMs);

        // Playhead — only meaningful when it is NOT slaved to the caller's drag
        // (scrub passes false; keyframe drag passes false because the playhead
        // follows the dragged keyframe there).
        if (includePlayhead && _vm is not null) Consider(_vm.PlayheadMs);

        // Keyframe times. A keyframe drag restricts to the SAME track and skips
        // the keyframe being dragged; a scrub (sameTrackPath == null) snaps to
        // any keyframe.
        var kfs = _vm?.ActiveTriggerObject?.Timeline?.SortedKeyframes;
        if (kfs is not null)
        {
            foreach (var k in kfs)
            {
                if (k is null) continue;
                if (ReferenceEquals(k, _draggingKeyframe)) continue;
                if (sameTrackPath is not null
                    && !string.Equals(k.ParameterPath, sameTrackPath, StringComparison.Ordinal))
                    continue;
                Consider(k.TimeMs);
            }
        }
        return bestMs;
    }

    // Distinct per-curve colour. Hue rotates by the golden angle so any
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

    // ─── double-click-to-add + Delete-key removal ──────────────────

    private void OnTimelineDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        var trigger = _vm.ActiveTriggerObject;
        if (trigger?.Timeline is not WidgetTimeline tl) return;
        if (_trackRows.Count == 0) return;   // no tracks → nothing to add to

        var pos = e.GetPosition(TimelineSurface);
        // Resolve the row under the cursor (the track whose band contains Y).
        // Hit zone now maps 1:1 to the row's visual band (no ±2px
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
        SetStatus(string.Format(
            Localizer.T("visualist.widget.status.keyframe_added_format", "Keyframe added at {0} ms."),
            t.ToString("0")));
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
        // Delete / Backspace removes the selected keyframe. Guarded on a
        // live selection so the key bubbling up from the graph canvas (node
        // delete) isn't shadowed when no keyframe is active.
        if ((e.Key == Windows.System.VirtualKey.Delete || e.Key == Windows.System.VirtualKey.Back)
            && _selectedKeyframe is { } kf)
        {
            DeleteKeyframe(kf);
            e.Handled = true;
        }
    }

    // ─── prev/next keyframe transport ──────────────────────────────

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
            SetStatus(dir < 0
                ? Localizer.T("visualist.widget.status.no_earlier_keyframe", "No earlier keyframe.")
                : Localizer.T("visualist.widget.status.no_later_keyframe", "No later keyframe."));
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

        // Scrub math now goes through the zoom transform so the
        // pointer X maps to the visible-window-relative time, not duration *
        // (x/w). Without this the playhead would jump to the full-duration
        // position regardless of how far we'd zoomed in.
        var zoom = _zoom ?? ResolveZoomState(_vm!.SelectedWidget, trigger, durationMs, w);
        double clampedX = Math.Clamp(xInTimeline, 0, w);
        double newMs = XToTime(clampedX, zoom.PxPerSec, zoom.ScrollOffsetMs);
        newMs = Math.Clamp(newMs, 0, durationMs);
        // Change 4 — snap (AFTER the clamp) to the 100ms grid + keyframe times
        // (any track), unless Alt is held. Reciprocal of keyframe drag:
        // scrubbing near a keyframe drops the playhead onto it. No "snap to
        // playhead" — the playhead is what's moving.
        newMs = Math.Clamp(
            SnapTimelineMs(newMs, zoom.PxPerSec, sameTrackPath: null, includePlayhead: false),
            0, durationMs);
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
        // (smooth scrubbing — no re-allocation of every tick mark per move). Use
        // the SNAPPED time's X (not the raw cursor X) so the visible playhead
        // lands on the snap target rather than trailing behind it.
        double snappedX = TimeToX(newMs, zoom.PxPerSec, zoom.ScrollOffsetMs);
        if (_playheadLine is not null)
            _playheadLine.Margin = new Thickness(snappedX, 0, 0, 0);
        if (_playheadHalo is not null)
            _playheadHalo.Margin = new Thickness(snappedX - 3, 0, 0, 0);
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

    // ─── keyframe right-click context menu ──────────────────────────
    //
    // Three items:
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

    // Trigger-switch race guard. A keyframe captured in a context-
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
        // Race guard — confirm the captured keyframe is still on the
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
            // Trigger-switch race — the dialog is modal-async, so the
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
        // Race guard — the context menu captured this keyframe; a
        // trigger switch between right-click and click would orphan it. Validate
        // it still belongs to the active timeline before mutating + dirtying.
        if (ValidateKeyframeOnActiveTimeline(kf, "Cycle curve") is null) return;
        // Cycle ramp — Linear → EaseIn → EaseOut → EaseInOut →
        // Bezier → Step → Linear. EaseInOut is included in
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

    // ─── layer id + active trigger helpers ───────────────────────

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

    // ─── Test Run + Test Target (V6) ─────────────────────────────
    //
    // Fire the active trigger. Historically this ONLY sent a fire-and-forget
    // VISUAL_TRIGGER over the bus, i.e. it only ever addressed the live OBS source.
    //
    // V6 made that a dead end for the most common authoring loop. Hub now excludes
    // Visualist preview sockets from layer presence (an editor pane is no longer
    // indistinguishable from an OBS Browser Source), so with only a preview open
    // LayerRuntime.EnqueueTriggerAsync takes its inactive-layer fast-succeed: no
    // RUN_TRIGGER is produced, nothing renders, and because the Test Run payload
    // carries an empty WaitId nothing surfaces the swallow — the status bar still
    // said "Test Run sent". Correct presence, useless button.
    //
    // So Test Run gained a TARGET. The default addresses BOTH the open design
    // surfaces and the bus, which is what makes "fire a test with only a preview
    // open" render something again without needing the author to discover anything.
    // The two isolating targets exist for when they want to be sure which surface
    // they are looking at. Every path reports the target it actually reached, so the
    // author is never guessing whose pixels those are.
    //
    // ── Three invariants this region must keep (each one is a fixed bug) ──────────
    //
    // (1) "live OBS (bus)" is claimed ONLY when the layer has real PRODUCTION presence.
    //     A successful SendVisualTriggerAsync proves the BUS took the envelope, which is
    //     precisely the signal the doc-comment above says must not be trusted: Hub's
    //     LayerRuntime then discards it on the inactive-layer fast-succeed. With OBS
    //     closed the author was told the OBS target was reached while zero pixels moved —
    //     a false confirmation from the very affordance that exists to remove them. See
    //     HasProductionPresence + BuildTestRunStatus.
    //
    // (2) The two transports must never BOTH drive the same pane. LayerRegistry's
    //     GetConnections is deliberately kind-blind (a preview pane is a real socket that
    //     must keep receiving frames), so a bus RUN_TRIGGER fans out to the open preview
    //     sockets as well. Doing the local SET_ACTIVE_TRIGGER/SCRUB/PLAY on top of that
    //     ran every pane TWICE: both passes bump the audio activation, so a one-shot alert
    //     sound played twice in the pane, and the two transports fought over the same
    //     widget's timeline. Local dispatch is therefore the FALLBACK, taken only when the
    //     wire cannot reach those sockets. See busDrivesPreviews in OnTestRunClicked.
    //
    // (3) A surface counts as reached only when its post actually went out. The WebView2
    //     bridge no-ops while CoreWebView2 is uninitialised (the documented intermittent
    //     detached-tree case), so a non-null reference plus a Visibility check proved
    //     nothing. The panels' PostScrub/PostPlay now return bool. See
    //     DispatchTestToPreviewSurfaces.
    private enum TestTarget
    {
        /// <summary>Default — every open Visualist preview surface AND the bus (live OBS).</summary>
        PreviewAndObs = 0,

        /// <summary>Bus only. The pre-V6 behaviour, kept for "prove it works on stream".</summary>
        ObsOnly = 1,

        /// <summary>Open preview surfaces only. Never touches the bus.</summary>
        PreviewOnly = 2,
    }

    private TestTarget _testTarget = TestTarget.PreviewAndObs;

    // Target-name label inside the Test Run button's content, and the flyout items,
    // built in code — see InitTestTargetAffordance for why this is not XAML.
    private TextBlock? _testTargetLabel;
    private bool _testTargetAffordanceReady;
    private RadioMenuFlyoutItem? _targetItemBoth;
    private RadioMenuFlyoutItem? _targetItemObs;
    private RadioMenuFlyoutItem? _targetItemPreview;

    /// <summary>
    /// Builds the Test Target selector: the current target is written into the Test Run
    /// button's own label, and a three-item radio <see cref="MenuFlyout"/> is attached as
    /// the button's context flyout so the author can pin a target.
    ///
    /// <para>WHY CODE-BEHIND and not a <c>SplitButton</c> in WidgetEditorView.xaml: the XAML
    /// is outside this sprint's edit scope (V6 owns this code-behind for the Test Target work
    /// only). A SplitButton with the same MenuFlyout is the better shape and the toolbar
    /// already has a Flyout precedent next door on the audio-mixer button — promote it when
    /// the XAML is free. Nothing else has to change: the target state, the dispatch and the
    /// reporting all live in this region.</para>
    ///
    /// <para>Discovery is deliberately cheap because it does not have to carry the feature:
    /// the DEFAULT target already drives the previews, so an author who never finds the
    /// flyout still gets a rendering Test Run. The label states the current target and the
    /// tooltip states how to change it.</para>
    ///
    /// <para>★ THE SELECTOR MUST NOT RIDE A GATED CONTROL. This flyout hangs off the Test
    /// Run button's <c>ContextFlyout</c>, and WinUI routes no input to a disabled control.
    /// While <see cref="ApplyButtonGating"/> still disabled the button whenever the pinned
    /// target needed the bus and the bus was down, the target selector became unreachable
    /// at exactly the moment the author needed to leave the target that had stopped
    /// working — and the field is not persisted, so the only recovery was restarting the
    /// pillar. ApplyButtonGating no longer consults bus state for that reason; the
    /// bus-offline rejection is REPORTED from <see cref="OnTestRunClicked"/> instead. If
    /// this flyout is ever moved back onto a conditionally-enabled part, that trap comes
    /// straight back.</para>
    /// </summary>
    private void InitTestTargetAffordance()
    {
        if (_testTargetAffordanceReady || TestRunButton is null) return;
        _testTargetAffordanceReady = true;

        try
        {
            // Rebuild the button content so the target label has a stable identity
            // (reaching into the XAML-declared StackPanel by child index would break the
            // moment anyone reorders it).
            _testTargetLabel = new TextBlock
            {
                FontSize            = 11,
                VerticalAlignment   = VerticalAlignment.Center,
                Opacity             = 0.75,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new TextBlock { Text = "▶", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = Localizer.T("visualist.widget.toolbar.test_run.label", "Test Run"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(_testTargetLabel);
            TestRunButton.Content = content;

            _targetItemBoth = new RadioMenuFlyoutItem
            {
                Text      = Localizer.T("visualist.widget.test_target.both", "Preview + live OBS"),
                GroupName = "WidgetEditorTestTarget",
                IsChecked = true,
            };
            _targetItemObs = new RadioMenuFlyoutItem
            {
                Text      = Localizer.T("visualist.widget.test_target.obs", "Live OBS only"),
                GroupName = "WidgetEditorTestTarget",
            };
            _targetItemPreview = new RadioMenuFlyoutItem
            {
                Text      = Localizer.T("visualist.widget.test_target.preview", "This preview only"),
                GroupName = "WidgetEditorTestTarget",
            };
            _targetItemBoth.Click    += (_, _) => SetTestTarget(TestTarget.PreviewAndObs);
            _targetItemObs.Click     += (_, _) => SetTestTarget(TestTarget.ObsOnly);
            _targetItemPreview.Click += (_, _) => SetTestTarget(TestTarget.PreviewOnly);

            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
            flyout.Items.Add(_targetItemBoth);
            flyout.Items.Add(_targetItemObs);
            flyout.Items.Add(_targetItemPreview);
            TestRunButton.ContextFlyout = flyout;

            ApplyTestTargetLabel();
        }
        catch (Exception ex)
        {
            // A failed affordance must not cost the author the button itself: Test Run keeps
            // working at its default target with the XAML label it was declared with.
            GlobalLogger.Error("WidgetEditorView", "InitTestTargetAffordance", ex);
        }
    }

    private void SetTestTarget(TestTarget target)
    {
        _testTarget = target;
        ApplyTestTargetLabel();
        // Re-poll the gate. The target no longer influences it (see ApplyButtonGating —
        // bus state is deliberately not a gate, so the flyout on the button can never be
        // locked away), but the layer-saved / widget-selected conditions may have moved
        // since the last poll and the label change is a natural refresh point.
        ApplyButtonGating();
        SetStatus(string.Format(
            Localizer.T("visualist.widget.status.test_target_format", "Test Run target: {0}."),
            DescribeTarget(target)));
    }

    private static string DescribeTarget(TestTarget target) => target switch
    {
        TestTarget.ObsOnly     => Localizer.T("visualist.widget.test_target.describe.obs", "live OBS only"),
        TestTarget.PreviewOnly => Localizer.T("visualist.widget.test_target.describe.preview", "this preview only"),
        _                      => Localizer.T("visualist.widget.test_target.describe.both", "preview + live OBS"),
    };

    private void ApplyTestTargetLabel()
    {
        if (_testTargetLabel is not null)
        {
            _testTargetLabel.Text = _testTarget switch
            {
                TestTarget.ObsOnly     => Localizer.T("visualist.widget.test_target.chip.obs", "· OBS"),
                TestTarget.PreviewOnly => Localizer.T("visualist.widget.test_target.chip.preview", "· Preview"),
                _                      => Localizer.T("visualist.widget.test_target.chip.both", "· Preview + OBS"),
            };
        }
        if (_targetItemBoth    is not null) _targetItemBoth.IsChecked    = _testTarget == TestTarget.PreviewAndObs;
        if (_targetItemObs     is not null) _targetItemObs.IsChecked     = _testTarget == TestTarget.ObsOnly;
        if (_targetItemPreview is not null) _targetItemPreview.IsChecked = _testTarget == TestTarget.PreviewOnly;
        if (TestRunButton is not null)
        {
            ToolTipService.SetToolTip(TestRunButton, string.Format(
                Localizer.T("visualist.widget.toolbar.test_run.target_tip_format",
                    "Run this trigger — target: {0}. Right-click to change the target."),
                DescribeTarget(_testTarget)));
        }
    }

    /// <summary>
    /// Does this layer have real PRODUCTION presence — i.e. is an OBS Browser Source
    /// attached right now?
    ///
    /// <para>WHY THIS EXISTS: a successful <c>SendVisualTriggerAsync</c> only proves the BUS
    /// accepted the envelope. Hub's <c>LayerRuntime.EnqueueTriggerAsync</c> then consults
    /// <c>LayerRegistry.IsLayerActive</c> and takes its inactive-layer fast-succeed when the
    /// layer has no production socket — the trigger is discarded, and because Test Run's
    /// payload carries an empty <c>WaitId</c> nothing surfaces the swallow. Claiming the OBS
    /// target from the send alone told the author their overlay had fired while OBS was
    /// closed.</para>
    ///
    /// <para>HOW IT RESOLVES: through the rail rows the VM already maintains from
    /// <c>ILayerRegistrySource</c> — <c>SeedActiveFromSource</c> writes
    /// <c>row.Active = IsLayerActive(id)</c> and <c>LiveLayerChanged</c> keeps it current.
    /// Post-V6 both of those are PRODUCTION-scoped (an editor socket is neither presence nor
    /// a transition), so the row is exactly the signal wanted, and Visualist gets it without
    /// referencing Hub — the pillar-isolation rule means <c>LayerRegistry</c> itself is
    /// unreachable from here. It also guarantees this status line can never contradict the
    /// presence dot the author is looking at in the rail.</para>
    ///
    /// <para>SIBLING WINDOWS (was a known limitation, now closed): siblings used to be
    /// constructed with <c>layerSource: null</c>, so no rail row was ever Active there and
    /// this returned false even with an OBS source attached. That was reasoned about as
    /// cosmetic — a status line that under-claims is the safe direction — but the reasoning
    /// only covered the STATUS TEXT. The same flag also gates DISPATCH one method down:
    /// <c>busDrivesPreviews</c> went false, so <c>OnTestRunClicked</c> ran the preview
    /// dispatch AND the bus trigger, and Hub's fan-out is kind-blind, so the pane was driven
    /// twice — the double-dispatch invariant (2) of this region explicitly forbids. Siblings
    /// now receive <c>VisualistWindowRegistry.AmbientLayerSource</c>, published by Hub, so
    /// they answer the same as the embedded surface.
    ///
    /// <b>The transferable lesson:</b> when a flag is documented as affecting only a
    /// cosmetic surface, check every OTHER read of it before believing that.</para>
    /// </summary>
    private bool HasProductionPresence(string layerId)
    {
        var rows = _vm?.Layers;
        if (rows is null || string.IsNullOrEmpty(layerId)) return false;
        try
        {
            foreach (var row in rows)
            {
                // Skip the synthetic "(unsaved)" row — it has no file, so no HUD presence.
                if (row.IsUnsaved) continue;
                // Same keying as VisualistViewModel.LayerIdFor / Hub's LayerRegistry: the
                // filename stem, NOT Layer.Name.
                string id = System.IO.Path.GetFileNameWithoutExtension(row.FileName);
                if (string.Equals(id, layerId, StringComparison.OrdinalIgnoreCase)) return row.Active;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetEditorView", $"HasProductionPresence('{layerId}')", ex);
        }
        return false;
    }

    private async void OnTestRunClicked(object sender, RoutedEventArgs e)
    {
        bool wantsBus = _testTarget != TestTarget.PreviewOnly;
        bool wantsPreview = _testTarget != TestTarget.ObsOnly;

        // Only the OBS-only target is dead in the water without the bus; the other two
        // degrade to their preview half (see the send-time IsConnected re-check below, which
        // is what keeps an offline click off the bounded outbound queue).
        //
        // This rejection is REPORTED here rather than pre-empted by disabling the button:
        // the Test Target flyout hangs off this button's ContextFlyout, and a disabled
        // control receives no input, so gating it locked the author into the target that
        // had just stopped working. See ApplyButtonGating + InitTestTargetAffordance.
        if (wantsBus && !wantsPreview && !VisualistBusClient.Instance.IsConnected)
        {
            SetStatus(Localizer.T("visualist.widget.status.test_run.bus_offline",
                "Test Run unavailable — Hub bus is offline. Right-click Test Run to pick a preview target."));
            return;
        }
        string? layerId = ResolveLayerId();
        if (layerId is null)
        {
            SetStatus(Localizer.T("visualist.widget.status.test_run.unsaved",
                "Test Run unavailable — save the layer first."));
            return;
        }
        LayerWidget? widget = _vm?.SelectedWidget;
        if (widget is null || string.IsNullOrEmpty(widget.Id))
        {
            SetStatus(Localizer.T("visualist.widget.status.test_run.no_widget",
                "Test Run unavailable — no widget selected."));
            return;
        }
        string triggerName = ActiveTriggerName();

        TestRunButton.IsEnabled = false;
        try
        {
            bool hasProduction = HasProductionPresence(layerId);
            bool busReachable  = wantsBus && VisualistBusClient.Instance.IsConnected;

            // ★ ONE transport per pane. Hub fans a bus RUN_TRIGGER out over
            // LayerRegistry.GetConnections, which is deliberately kind-blind — so when the
            // wire can reach the layer at all, the open preview panes are ALREADY driven by
            // it. Adding the local SET_ACTIVE_TRIGGER/SCRUB/PLAY on top ran each pane twice
            // (double audio activation → a one-shot alert sound playing twice; two
            // transports racing the same widget's timeline). So: wire when the wire can
            // reach them (this is exactly the pre-V6 behaviour), local ONLY as the fallback
            // for a layer with no production socket, where Hub's inactive-layer
            // fast-succeed means no RUN_TRIGGER is produced at all.
            bool busDrivesPreviews = wantsPreview && busReachable && hasProduction;

            int previewSurfaces = 0;
            bool busSent = false;
            string? busError = null;

            if (busDrivesPreviews)
            {
                // Bus first here (the usual preview-first ordering is inverted on purpose):
                // the local path is the fallback, and we can only know whether it is needed
                // after the send either succeeds or throws.
                (busSent, busError) = await TrySendVisualTriggerAsync(layerId, widget.Id, triggerName);
                if (!busSent) previewSurfaces = DispatchTestToPreviewSurfaces(widget, triggerName);
            }
            else
            {
                // Previews first: the local WebView2 bridge is synchronous, so the author
                // sees the frame move before the bus round-trip resolves.
                if (wantsPreview) previewSurfaces = DispatchTestToPreviewSurfaces(widget, triggerName);
                if (busReachable)
                    (busSent, busError) = await TrySendVisualTriggerAsync(layerId, widget.Id, triggerName);
            }

            SetStatus(BuildTestRunStatus(widget, triggerName, layerId, wantsBus, wantsPreview,
                                         previewSurfaces, busSent, busError,
                                         hasProduction, busDrivesPreviews && busSent));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetEditorView", "Test Run", ex);
            SetStatus(string.Format(
                Localizer.T("visualist.widget.status.test_run.failed_format", "Test Run failed: {0}"),
                ex.Message));
        }
        finally
        {
            // Re-gate (layer-saved / widget-selected may have changed under a long send).
            ApplyButtonGating();
        }
    }

    /// <summary>
    /// Fire-and-forget VISUAL_TRIGGER over the bus, reporting (sent, error) instead of
    /// throwing. Extracted so <see cref="OnTestRunClicked"/> can order the bus send before
    /// or after the local preview dispatch without duplicating the try/catch.
    /// </summary>
    private static async System.Threading.Tasks.Task<(bool Sent, string? Error)> TrySendVisualTriggerAsync(
        string layerId, string widgetId, string triggerName)
    {
        try
        {
            await VisualistBusClient.Instance.SendVisualTriggerAsync(layerId, widgetId, triggerName);
            return (true, null);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetEditorView", "Test Run", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Composes the one line the author reads to know WHERE the trigger went. Naming the
    /// reached surfaces is the point of the whole affordance — a generic "Test Run sent" is
    /// exactly what let the pre-V6 swallow go unnoticed, since the bus accepted a
    /// VISUAL_TRIGGER that LayerRuntime then discarded.
    ///
    /// <para>★ <paramref name="busSent"/> alone must NEVER print as a reached OBS target.
    /// It means "the bus accepted the envelope", which is the exact signal the swallow hides
    /// behind: without production presence Hub discards the trigger. So the OBS claim is
    /// gated on <paramref name="hasProductionPresence"/>, and the ungated case says so
    /// plainly instead of quietly dropping the target from the list — the author needs to
    /// know their overlay is not attached, not merely that OBS went unmentioned.</para>
    ///
    /// <para><paramref name="previewsDrivenByBus"/> covers the default target on a live
    /// layer: the panes are driven by the same RUN_TRIGGER fan-out that reaches OBS, so
    /// <paramref name="previewSurfaces"/> is 0 by design and reporting "hit nothing" would
    /// be wrong.</para>
    /// </summary>
    private static string BuildTestRunStatus(LayerWidget widget, string triggerName, string layerId,
        bool wantsBus, bool wantsPreview, int previewSurfaces, bool busSent, string? busError,
        bool hasProductionPresence, bool previewsDrivenByBus)
    {
        // Each reached-surface fragment is its own key: the line is assembled from
        // them, so a translator needs the pieces AND the frames. widget.Name and
        // triggerName are user/persisted data and are interpolated, never translated.
        var reached = new List<string>();
        if (previewsDrivenByBus)
        {
            reached.Add(Localizer.T("visualist.widget.test_run.reached.bus_and_previews",
                "live OBS (bus) + every open preview"));
        }
        else
        {
            if (previewSurfaces == 1) reached.Add(Localizer.T("visualist.widget.test_run.reached.preview", "preview"));
            else if (previewSurfaces > 1) reached.Add(string.Format(
                Localizer.T("visualist.widget.test_run.reached.previews_format", "{0} previews"),
                previewSurfaces));
            if (busSent && hasProductionPresence) reached.Add(Localizer.T("visualist.widget.test_run.reached.bus", "live OBS (bus)"));
        }

        string head = $"{widget.Name} · {triggerName}";
        string busNote = busError is not null
            ? string.Format(
                Localizer.T("visualist.widget.test_run.note.bus_failed_format", " (bus failed: {0})"),
                busError)
            : (busSent && !hasProductionPresence
                ? string.Format(
                    Localizer.T("visualist.widget.test_run.note.no_obs_format",
                        " (bus accepted, but no OBS source is attached to '{0}' — nothing rendered on stream)"),
                    layerId)
                : "");

        if (reached.Count > 0) return string.Format(
            Localizer.T("visualist.widget.test_run.summary_format", "Test Run → {0}: {1}{2}"),
            string.Join(" + ", reached), head, busNote);

        if (busError is not null) return string.Format(
            Localizer.T("visualist.widget.status.test_run.failed_format", "Test Run failed: {0}"),
            busError);
        if (busSent)
        {
            // Implies !hasProductionPresence — the only way the bus can be the sole
            // transport and still reach nobody.
            string previewHalf = wantsPreview
                ? Localizer.T("visualist.widget.test_run.note.no_preview_took", ", and no preview surface took it")
                : "";
            return string.Format(
                Localizer.T("visualist.widget.test_run.nothing.bus_only_format",
                    "Test Run hit nothing — bus accepted, but no OBS source is attached to '{0}'{1}: nothing rendered on stream."),
                layerId, previewHalf);
        }
        if (wantsBus && !VisualistBusClient.Instance.IsConnected && !wantsPreview)
            return Localizer.T("visualist.widget.test_run.unavailable.bus_offline",
                "Test Run unavailable — Hub bus is offline.");
        if (wantsPreview && !wantsBus)
            return Localizer.T("visualist.widget.test_run.nothing.no_preview",
                "Test Run hit nothing — no preview surface is open (open the embedded preview or a popout).");
        return Localizer.T("visualist.widget.test_run.nothing.none",
            "Test Run hit nothing — no preview is open and the Hub bus is offline.");
    }

    /// <summary>
    /// Renders the active trigger in every Visualist design surface this editor can reach,
    /// returning how many took it.
    ///
    /// <para>This bypasses the bus entirely and uses the existing design-time WebView2
    /// bridge (the same messages the timeline transport sends), so it is unaffected by the
    /// V6 presence rules by construction: no RUN_TRIGGER, no VISUAL_COMPLETE, nothing that
    /// could resolve a script's wait. A preview must never be able to answer for a
    /// production overlay — that is the bug V6 fixed, and this affordance must not
    /// reintroduce it through the front door.</para>
    ///
    /// <para>Per surface it SCRUBS to 0 and then PLAYS. Both halves are needed:
    /// compositor.js's handlePlay starts from the CURRENT cursor
    /// (<c>startMs: triggerContext.timeMs</c>), so without the scrub a second Test Run would
    /// resume from wherever the last one stopped; and a trigger with no authored timeline
    /// duration is a no-op for handlePlay, so the scrub is what makes an untimed trigger
    /// render at all rather than reporting a target it never painted.</para>
    ///
    /// <para>★ A SURFACE COUNTS ONLY WHEN THE POST ACTUALLY WENT OUT. The count used to come
    /// from a non-null reference plus a Visibility check, but the panels' post methods
    /// silently return when their WebView2 bridge is not initialised — the documented
    /// intermittent detached-tree case this editor carries a retry for. The status line then
    /// claimed a pane that took nothing: the same false-confirmation class this affordance
    /// exists to remove. <c>PostScrub</c> reports delivery (the SCRUB is the one message
    /// every surface always gets), so it is the gate for both the PLAY and the tally.</para>
    /// </summary>
    private int DispatchTestToPreviewSurfaces(LayerWidget widget, string triggerName)
    {
        double durationMs = _vm?.ActiveTriggerObject?.Timeline?.DurationMs ?? 0;
        if (!double.IsFinite(durationMs) || durationMs < 0) durationMs = 0;
        int hit = 0;

        // 1) The embedded single-widget pane. The Visibility + config checks stay as cheap
        //    pre-filters (a collapsed pane, or preview turned off in settings, has no live
        //    WebView2 at all) — but delivery is what decides the tally.
        if (EmbeddedPreview is not null
            && EmbeddedPreview.Visibility == Visibility.Visible
            && VisualistUserConfig.Instance.EditorEmbeddedPreviewEnabled)
        {
            try
            {
                EmbeddedPreview.SetActiveTrigger(triggerName);
                if (EmbeddedPreview.PostScrub(0))
                {
                    if (durationMs > 0) EmbeddedPreview.PostPlay(durationMs, loop: false);
                    hit++;
                }
            }
            catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "TestRun → embedded preview", ex); }
        }

        // 2) The single-WIDGET popout this editor owns.
        var widgetPane = _widgetPreviewWindow?.WidgetPreview;
        if (widgetPane is not null)
        {
            try
            {
                widgetPane.SetActiveTrigger(triggerName);
                if (widgetPane.PostScrub(0))
                {
                    if (durationMs > 0) widgetPane.PostPlay(durationMs, loop: false);
                    hit++;
                }
            }
            catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "TestRun → widget popout", ex); }
        }

        // 3) The pillar-owned full-LAYER popout. It hosts a whole layer rather than one
        //    widget, so its bridge is addressed per call (no SetActiveTrigger); the scrub
        //    carries the widget + trigger it should sample.
        var layerPane = FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview;
        if (layerPane is not null)
        {
            try
            {
                if (layerPane.PostScrub(widget.Id, triggerName, 0))
                {
                    if (durationMs > 0) layerPane.PostPlay(widget.Id, triggerName, durationMs, loop: false);
                    hit++;
                }
            }
            catch (Exception ex) { GlobalLogger.Error("WidgetEditorView", "TestRun → layer popout", ex); }
        }

        return hit;
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
            ToolTipService.SetToolTip(BusStatusDot, connected
                ? Localizer.T("visualist.widget.toolbar.bus.connected.tip", "Hub bus connected")
                : Localizer.T("visualist.widget.toolbar.bus.offline.tip", "Hub bus offline"));
        }
        ApplyButtonGating();
    }

    // Enable Test Run when the layer is saved AND a widget is selected. Preview-popout
    // buttons gate on the config toggle.
    //
    // ★ BUS STATE IS DELIBERATELY NOT A GATE HERE. V6 first made the bus requirement
    // target-scoped (only "live OBS only" needs it), but even that was wrong for a reason
    // that has nothing to do with the send: the Test Target flyout is attached to this
    // button's ContextFlyout, and WinUI routes no input to a disabled control. Disabling
    // the button for the bus-needing target made the target SELECTOR unreachable at exactly
    // the moment the author needed to leave the target that had stopped working — and the
    // target is not persisted, so the only recovery was restarting the pillar.
    //
    // Nothing is lost by dropping it. The original gate existed so an offline click
    // couldn't pile into the bounded outbound queue, and OnTestRunClicked still guarantees
    // that: the OBS-only target returns early with "Hub bus is offline" before any send,
    // and the other two re-check IsConnected before touching the client. So the offline
    // click is now REPORTED rather than pre-empted, which is also the more honest UX — a
    // disabled button with a tooltip is not an explanation.
    private void ApplyButtonGating()
    {
        bool canTestRun = ResolveLayerId() is not null
                       && _vm?.SelectedWidget is not null;
        if (TestRunButton is not null) TestRunButton.IsEnabled = canTestRun;

        bool popoutEnabled = VisualistUserConfig.Instance.EditorPopoutPreviewEnabled;
        if (PreviewLayerButton  is not null) PreviewLayerButton.IsEnabled  = popoutEnabled;
        if (PreviewWidgetButton is not null) PreviewWidgetButton.IsEnabled = popoutEnabled && _vm?.SelectedWidget is not null;
    }

    // ─── embedded floating preview ───────────────────────────────

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
            // The ?widget= page loads on onStartup; once the widget is
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
        SetStatus(string.Format(
            Localizer.T("visualist.widget.status.preview_width_format", "Preview width: {0}px"),
            newWidth));
    }

    // A node was selected/deselected in the graph; light (or clear) the
    // matching manipulator in the embedded preview. SET_MANIPULATOR/CLEAR_MANIPULATOR
    // is posted to compositor.js by WidgetSinglePreviewPanel.SetActiveNode. The
    // layer/widget popouts host a LayerPreviewPanel (no per-node manipulator), so
    // only the embedded single-widget preview takes the active-node forward.
    //
    // The same selection also drives the Inspector's typed per-node
    // NODE section. Setting VM.SelectedNode (null on deselect) rebuilds
    // SelectedNodeParams; InspectorPanel reacts to the SelectedNode
    // PropertyChanged and shows the NODE section in widget-editor mode (where
    // SetEditorContext already collapses the layer/geometry mirrors). The VM
    // set is independent of the preview path, so we run it FIRST (and outside
    // the EmbeddedPreview-null early-out) — a missing/teared-down preview
    // surface must not suppress the Inspector update.
    private void OnGraphNodeSelectionChanged(Node? node)
    {
        // Inspector routing — always runs, even when there's no
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
        SetStatus(Localizer.T("visualist.widget.status.manipulator_applied", "Applied manipulator change."));
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

    // ─── Preview Layer / Preview Widget popouts ──────────────────

    private void OnPreviewLayerClicked(object sender, RoutedEventArgs e)
    {
        if (!VisualistUserConfig.Instance.EditorPopoutPreviewEnabled) return;
        if (ResolveLayerId() is null) { SetStatus(Localizer.T("visualist.widget.status.preview_unsaved", "Preview unavailable — save the layer first.")); return; }
        // Route through the shared pillar-owned popout so the editor and
        // the layer-canvas command bar use one window.
        bool ok = FindPillarMainView()?.PreviewLayer() == true;
        SetStatus(ok
            ? Localizer.T("visualist.widget.status.preview_layer_opened", "Opened layer preview.")
            : Localizer.T("visualist.widget.status.preview_layer_failed", "Could not open layer preview."));
    }

    private void OnPreviewWidgetClicked(object sender, RoutedEventArgs e)
    {
        if (!VisualistUserConfig.Instance.EditorPopoutPreviewEnabled) return;
        string? layerId = ResolveLayerId();
        if (layerId is null) { SetStatus(Localizer.T("visualist.widget.status.preview_unsaved", "Preview unavailable — save the layer first.")); return; }
        LayerWidget? widget = _vm?.SelectedWidget;
        if (widget is null || string.IsNullOrEmpty(widget.Id)) { SetStatus(Localizer.T("visualist.widget.status.preview_no_widget", "Select a widget to preview.")); return; }

        if (_widgetPreviewWindow is not null)
        {
            try { Phoenix.Controls.Visualist.WinUI.Hosting.WindowFront.Show(_widgetPreviewWindow); return; }
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
            Phoenix.Controls.Visualist.WinUI.Hosting.WindowFront.Show(win);
            SetStatus(Localizer.T("visualist.widget.status.preview_widget_opened", "Opened widget preview."));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetEditorView", "OnPreviewWidgetClicked", ex);
            SetStatus(Localizer.T("visualist.widget.status.preview_widget_failed", "Could not open widget preview."));
        }
    }

    // ─── timeline transport (TimelinePlayback) ───────────────────

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
            SetStatus(Localizer.T("visualist.widget.status.paused", "Paused."));
        }
        else
        {
            var tl = _vm?.ActiveTriggerObject?.Timeline;
            if (tl is null || tl.DurationMs <= 0)
            {
                SetStatus(Localizer.T("visualist.widget.status.nothing_to_play",
                    "Nothing to play — this trigger has no timeline duration."));
                return;
            }
            _playback.Play();
            SetStatus(Localizer.T("visualist.widget.status.playing", "Playing."));
        }
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _playback?.Stop();

        // ★ AFTER Stop(), never before. Stop() sets IsPlaying=false (which posts STOP_PLAY
        // when it was playing) and then TimeMs=0, and that time write posts a SCRUB that
        // RE-PINS the widget's design-time clock at frame 0. Releasing first would be
        // undone by that scrub a moment later.
        //
        // Pause holds the frame it stopped on — the author's playhead still describes what
        // is on screen. STOP is the gesture that says otherwise, so it is the one that
        // hands every widget back to the ambient clock. Without this the whole-layer
        // preview had no reachable release: a bare playhead drag pinned a widget for the
        // page's life and _seedWidgetAnimator refuses a pinned widget, so its ambient
        // animation was dead until the page reloaded.
        var preview = FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview;
        preview?.PostReleaseTimeCursor();
        EmbeddedPreview?.PostReleaseTimeCursor();

        SetStatus(Localizer.T("visualist.widget.status.stopped", "Stopped."));
    }

    private void OnLoopToggled(object sender, RoutedEventArgs e)
    {
        if (_playback is null) return;
        _playback.Loop = LoopToggle?.IsChecked == true;
        SetStatus(_playback.Loop
            ? Localizer.T("visualist.widget.status.loop_on", "Loop on.")
            : Localizer.T("visualist.widget.status.loop_off", "Loop off."));
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
        // The EMBEDDED single-widget preview also takes the scrub now.
        // Its ?widget= page renders the active trigger (SET_ACTIVE_TRIGGER, see
        // the ActiveTrigger PropertyChanged case), and SCRUB samples that
        // trigger's graph at timeMs — so dragging the playhead animates the
        // in-widget preview and keyframing is visible. The embedded panel tracks
        // its own widget id + active trigger, so its PostScrub takes only timeMs.
        string trig = ActiveTriggerName();
        string? widgetId = _vm?.SelectedWidget?.Id;
        if (!string.IsNullOrEmpty(widgetId))
        {
            // Bridge into the shared pillar-owned layer preview window.
            FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview?.PostScrub(widgetId!, trig, timeMs);
            // Embedded in-widget preview follows the playhead too. It
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
        // Bridge into the shared pillar-owned layer preview window.
        var preview = FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview;
        if (playing)
        {
            preview?.PostPlay(widgetId!, trig, tl?.DurationMs ?? 0, _playback?.Loop ?? false);
            // Animate the embedded in-widget preview in lockstep. It
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

    // ─── V11: playhead-following node-body previews ──────────────────────

    // UI-thread debounce for the node-body preview refresh. Same idiom (and the
    // same 150 ms) as HexPatternOverlay's resize debounce, for the same reason:
    // PlayheadMs is written per pointer-move during a scrub and ~30× a second
    // during playback, and NodeEvaluator.EvaluatePreviews is a synchronous
    // whole-graph walk — running it per write would put exactly the kind of
    // per-frame work back on the UI thread that the perf pass removed.
    //
    // Because Stop()+Start() restarts the interval, a write storm never reaches
    // Tick at all: the timer only elapses ~150 ms after the LAST write, i.e. on
    // scrub-settle / playback-stop. DispatcherTimer (not Threading.Timer) so the
    // Tick lands on the UI thread — the refresh touches node views.
    private DispatcherTimer? _previewRefreshDebounce;

    // Arm (or re-arm) the debounce. Cheap by design: every PlayheadMs write lands
    // here, and all it does is restart a timer. PlayheadMs's setter already drops
    // idempotent writes, so a scrub that doesn't move costs nothing at all.
    private void RequestPreviewRefreshAtPlayhead()
    {
        if (_previewRefreshDebounce is null)
        {
            _previewRefreshDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _previewRefreshDebounce.Tick += OnPreviewRefreshDebounceTick;
        }
        _previewRefreshDebounce.Stop();
        _previewRefreshDebounce.Start();
    }

    private void OnPreviewRefreshDebounceTick(object? sender, object e)
    {
        _previewRefreshDebounce?.Stop();

        // Gesture gate — mirrors the precedent in SyncPlaybackToPlayhead above:
        // an in-flight scrub or keyframe drag means the user is still moving, so
        // don't spend a graph walk on a position they are about to leave.
        // IsPlaying joins them because playback is a continuous position stream,
        // not a settled one. A gesture that holds the pointer still for >150 ms
        // would otherwise let the tick slip through mid-drag, so instead of
        // dropping the request we re-arm: the refresh then lands ~150 ms after
        // the gesture actually settles (pointer release / stop), which is the
        // V11 contract.
        if (_isScrubbing || _draggingKeyframe is not null || _playback?.IsPlaying == true)
        {
            _previewRefreshDebounce?.Start();
            return;
        }

        // The canvas owns its own gesture state (node-drag / pan / lasso /
        // group-drag are four independent flags in there). It returns false when
        // it declines for that reason — re-arm so the refresh isn't simply lost.
        if (!GraphCanvas.RefreshPreviewsAtTime(_vm?.PlayheadMs ?? 0))
            _previewRefreshDebounce?.Start();
    }

    // ─── status bar ──────────────────────────────────────────────

    /// <summary>Optional host sink for transient status messages. MainView sets
    /// this (T11-9) so the editor's feedback surfaces in Hub's single canonical
    /// status strip — this view's own bottom strip is collapsed to avoid a double
    /// strip. Null when hosted without a status host.</summary>
    public Action<string>? ExternalStatusSink { get; set; }

    /// <summary>Surface a transient status message in the bottom strip and
    /// auto-clear it to "Ready" after ~5s. Public so other surfaces can feed
    /// it. Safe to call off the UI thread (marshals).</summary>
    public void SetStatus(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        void Apply()
        {
            // T11-9 — surface in the host's canonical strip first (this view's own
            // strip is collapsed). Runs on the UI thread (Apply is marshalled).
            ExternalStatusSink?.Invoke(message);

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
            StatusLabel.Text = Localizer.T("visualist.widget.status.ready", "Ready");
            StatusLabel.Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"];
        };
        return t;
    }

    // Show/hide the docked media library rail (column 1). Default visible
    // for discoverability; the toggle persists only for the session.
    private void OnMediaToggleClick(object sender, RoutedEventArgs e)
    {
        bool show = MediaToggle?.IsChecked == true;
        if (MediaColumn is not null) MediaColumn.Width = show ? new GridLength(240) : new GridLength(0);
        if (MediaPanel is not null)  MediaPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── simple per-trigger audio mixer ────────────────────────
    //
    // A deliberately-minimal mixer: a MASTER fader bound to the active
    // WidgetTrigger.Volume (compositor.js multiplies every Audio.Play node's
    // own Volume by it) plus one fader per Audio.Play node in the active
    // trigger, each editing that node's existing "Volume" attribute. No meters,
    // no routing — just levels.
    //
    // Both fader kinds reuse the editor's existing undo/dirty/preview-refresh
    // discipline:
    //   * node faders  → VM.CommitNodeAttribute(node, "Volume", value) (the
    //     attribute-commit chokepoint: PushUndo → write → MarkDirty → visual refresh).
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
            AudioMixerRoot.Children.Add(MixerHint(
                Localizer.T("visualist.widget.mixer.no_trigger", "No trigger selected.")));
            return;
        }

        // Header.
        AudioMixerRoot.Children.Add(new TextBlock
        {
            Text       = Localizer.T("visualist.widget.mixer.title", "AUDIO MIXER"),
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
            AudioMixerRoot.Children.Add(MixerHint(
                Localizer.T("visualist.widget.mixer.no_audio", "No audio sources in this trigger.")));
            return;
        }

        AudioMixerRoot.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 2, 0, 2),
            Background = (Brush)Application.Current.Resources["CoalDividerBrush"],
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
        var (row, slider, readout) = BuildFaderRow(Localizer.T("visualist.widget.mixer.master", "Master"), cur);

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

    // Per-node row — edits the node's "Volume" attribute through the
    // attribute-commit chokepoint (VM.CommitNodeAttribute), which owns
    // PushUndo/MarkDirty/refresh.
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

    // Audio.Play stores "Volume" as a BARE scalar (matching the scalar
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
        return string.Format(
            Localizer.T("visualist.widget.mixer.audio_row_format", "Audio {0}"), ordinal);
    }
}
