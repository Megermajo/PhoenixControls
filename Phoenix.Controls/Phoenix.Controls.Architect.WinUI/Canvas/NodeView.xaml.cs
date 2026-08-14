using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Phoenix.Controls.Shared.Localization;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Visual for one NodeViewModel. Layout split into header band + title row +
// two-column socket grid. Selection halo is applied imperatively so the
// LogicCanvasView can re-style without forcing an extra DataTemplate trigger
// path through XAML.
public sealed partial class NodeView : UserControl
{
    // Resolved from PhoenixDark.xaml at first construction so the three
    // node-state brushes track the theme dictionary instead of being frozen
    // ARGB literals. Falls back to literal coal/ember
    // if the resource lookup fails — should never happen in production but
    // keeps designers running from raw XBF reloads sane.
    // These were `static readonly`, resolved once at
    // first construction and never refreshed — a runtime OS light↔dark / high-
    // contrast switch left every node painting stale colours until restart
    // (nothing subscribed to ActualThemeChanged). Now they're refreshable via
    // RefreshThemeBrushes(), called from each NodeView's ActualThemeChanged
    // (guarded to re-resolve once per actual transition); every live instance
    // then repaints its border. Mirrors the Hub SystemLogView ActualThemeChanged
    // fix.
    private static SolidColorBrush s_selectionBrush = ResolveBrush("EmberPrimaryBrush", 0xFF, 0xE5, 0xA2, 0x4E);
    private static SolidColorBrush s_dividerBrush   = ResolveBrush("CoalDividerBrush", 0xFF, 0x3A, 0x31, 0x27);
    // s_flashBrush retired — the flash is now a
    // saturated-amber FlashOverlay body-tint (NodeView.xaml), not a border brush.
    // Error state — tries the project's ErrBrush key (set by the canvas
    // wire-drop preview path) and falls back to a saturated rust red.
    private static SolidColorBrush s_errorBrush     = ResolveBrush("ErrBrush", 0xFF, 0xCB, 0x4D, 0x3F);
    // Var-chain hover halos — cyan (writers) + amber (readers).
    // Resolved from PhoenixDark.xaml so a future palette retune doesn't
    // require editing this code-behind. Fallbacks preserve pre-T15 ARGB
    // values for designer-time / pre-app-construction lookups.
    private static SolidColorBrush s_varWriterBrush = ResolveBrush("VarChainWriterBrush", 0xFF, 0x78, 0xC8, 0xFF);
    private static SolidColorBrush s_varReaderBrush = ResolveBrush("VarChainReaderBrush", 0xFF, 0xFF, 0xB4, 0x50);
    // Compact-mode border tint (soft teal, distinct from the cyan
    // var-chain writer halo and the gold selection halo). Lowest-priority
    // branch in ApplyBorder: a compact node that is NOT selected / error /
    // var-chain gets this tint so the compact state is visible at a glance,
    // pairing with the centred CompactSymbol glyph in NodeView.xaml.
    private static SolidColorBrush s_compactBrush   = ResolveBrush("InfoBrush", 0xFF, 0x5F, 0xB8, 0xA6);

    // Last ElementTheme the static brushes were
    // resolved for. ActualThemeChanged fires per live NodeView, so guard the
    // (shared) re-resolve to run once per actual transition.
    private static ElementTheme s_lastResolvedTheme = ElementTheme.Default;
    private static readonly object s_themeRefreshGate = new();

    /// <summary>
    /// Re-resolve the shared node-state brushes from
    /// the (now theme-/HC-current) resource dictionary. Idempotent and cheap;
    /// guarded so only the first NodeView to observe a given transition pays the
    /// re-resolve. Also invalidates the wire selection-brush cache so selected
    /// wires repaint in the new theme on their next read.
    /// </summary>
    private static void RefreshThemeBrushes(ElementTheme nowTheme)
    {
        lock (s_themeRefreshGate)
        {
            if (s_lastResolvedTheme == nowTheme) return;
            s_lastResolvedTheme = nowTheme;
            s_selectionBrush = ResolveBrush("EmberPrimaryBrush", 0xFF, 0xE5, 0xA2, 0x4E);
            s_dividerBrush   = ResolveBrush("CoalDividerBrush", 0xFF, 0x3A, 0x31, 0x27);
            s_errorBrush     = ResolveBrush("ErrBrush", 0xFF, 0xCB, 0x4D, 0x3F);
            s_varWriterBrush = ResolveBrush("VarChainWriterBrush", 0xFF, 0x78, 0xC8, 0xFF);
            s_varReaderBrush = ResolveBrush("VarChainReaderBrush", 0xFF, 0xFF, 0xB4, 0x50);
            s_compactBrush   = ResolveBrush("InfoBrush", 0xFF, 0x5F, 0xB8, 0xA6);
        }
        LinkViewModel.InvalidateThemeBrushCache();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Re-resolve shared brushes for the new theme (once), then repaint this
        // node's border so the swap is immediate rather than restart-gated.
        RefreshThemeBrushes(ActualTheme);
        if (DataContext is NodeViewModel vm) ApplyBorder(vm);
    }

    // {varname} token extractor — runs from OnPillPointerEntered (i.e. mouse
    // tracking frequency over inline-attr pills). Cache compiled because the
    // pre-cache version re-parsed the pattern on every PointerEntered event,
    // which hits at 60+ Hz when scanning across the canvas.
    private static readonly Regex s_varTokenPattern =
        new(@"\{([A-Za-z_][A-Za-z0-9_\.]*)\}", RegexOptions.Compiled);

    private static SolidColorBrush ResolveBrush(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush brush) return brush;
        }
        catch { /* designer-time / pre-app construction — fall through */ }
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    // Track the VM we're currently subscribed to so a DataContext swap can
    // detach the OLD VM's PropertyChanged. Without this the previous VM keeps
    // a handler pointing at this NodeView even after the view recycled to a
    // new VM — a small but real listener leak under reload paths.
    private NodeViewModel? _boundVm;

    // The last socket-label the pointer entered. F2 fired on the
    // NodeView routes to this socket when present, otherwise to the node title.
    // PointerExited clears it so a stale hover after the cursor leaves
    // the node body doesn't capture an unintended F2. Per-NodeView state — the
    // hover signal doesn't cross nodes because PointerExited fires before the
    // next node's PointerEntered when crossing the boundary.
    private SocketViewModel? _hoveredSocketVm;

    // PERF: cached so OnPillPointerEntered /
    // OnPillPointerExited / ShowVarPicker don't walk the visual tree on every
    // pointer event. The canvas reference is stable across the NodeView's
    // lifetime in the same logical tree; we invalidate on DataContext swap
    // (which is what canvas-recycle implies) and on Unloaded.
    private LogicCanvasView? _cachedCanvas;
    private LogicCanvasView? GetCanvasCached(DependencyObject? from)
    {
        if (_cachedCanvas is not null) return _cachedCanvas;
        if (from is null) return null;
        _cachedCanvas = FindAncestor<LogicCanvasView>(from);
        return _cachedCanvas;
    }

    /// <summary>
    /// Deterministically stamp the owning canvas into the cached-resolver slot.
    /// Called by the canvas at every mount (retained <c>MountNodeView</c> and the
    /// GPU path's <c>EnterImmediateEdit</c>) so the commit-time handlers can still
    /// reach the canvas AFTER this view is detached: on the Win2D canvas a
    /// click-away exits edit mode by REMOVING the view from NodeLayer, and the
    /// editor's LostFocus then fires with the visual chain severed — a
    /// FindAncestor walk from the sender returns null there, which silently
    /// dropped the whole commit tail (undo push, the EventName adopt/pair-sync
    /// notify, the socket-rename cross-file sync) while the raw value still
    /// committed. The cache survives cull/edit unmounts by design —
    /// <c>RunUnloadTeardown</c> clears it only on a REAL removal.
    /// </summary>
    internal void StampOwnerCanvas(LogicCanvasView canvas) => _cachedCanvas = canvas;

    // Per-node flash storyboard, built once (EnsureFlashStoryboard) and
    // replayed per pulse. Re-flashing a node that's already flashing must
    // Stop() the running timeline before Begin(); otherwise WinUI keeps the
    // previous DoubleAnimationUsingKeyFrames in its active-animation pool and
    // the next flash's opacity ramps are layered on top of the stale ones —
    // visually shows as the flash brush "sticking" at full opacity until
    // both timelines complete.
    private Storyboard? _currentFlashStoryboard;

    public NodeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        // 2026-06-08 — when an inline pill TextBox grows (lots of
        // typed text wrapping onto new lines) WinUI raises BringIntoViewRequested to
        // pull the now-taller focused control into view; a host ScrollViewer up the
        // tree (Hub shell) honours it and yanks/zooms the canvas onto the node (Majo:
        // "the camera does a sudden zoom-in onto the node"). The canvas owns its own
        // pan/zoom via the ViewTransform and never wants the framework to move the
        // viewport on focus/resize. NodeView is the guaranteed ancestor of its own
        // editors, so handling here reliably stops the bubble before any ScrollViewer
        // sees it — unlike the canvas-level VisualTreeHelper walk in
        // LogicCanvasView.OnBringIntoViewRequested, which could miss the NodeView
        // boundary. The TextBox's own internal caret scroll is unaffected (handled
        // inside the TextBox template, below this node).
        BringIntoViewRequested += (_, e) => e.Handled = true;
        // React to runtime theme / high-contrast
        // switches — re-resolve the shared node brushes and repaint.
        ActualThemeChanged += OnActualThemeChanged;
        // Keep the var-chain glow sized to the body as it
        // intrinsic-grows (multi-line pill edits, placeholder activation) while
        // a chain highlight is active. No-op when the glow is collapsed.
        if (NodeRoot is not null)
            NodeRoot.SizeChanged += OnNodeRootSizeChangedForGlow;
    }

    private void OnNodeRootSizeChangedForGlow(object sender, SizeChangedEventArgs e)
    {
        if (VarChainGlow is { Visibility: Visibility.Visible })
            ShowVarChainGlow();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_boundVm is not null && !ReferenceEquals(_boundVm, args.NewValue))
        {
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            _boundVm = null;
        }
        // DataContext swap implies a view-recycle; the next pointer event will
        // refresh the canvas reference via GetCanvasCached.
        _cachedCanvas = null;

        if (args.NewValue is NodeViewModel vm)
        {
            if (!ReferenceEquals(_boundVm, vm))
            {
                vm.PropertyChanged += OnVmPropertyChanged;
                _boundVm = vm;
            }
            ApplySelection(vm.IsSelected);
            // Honor the persisted __disabled attribute on
            // first paint so reloaded graphs reflect the disabled state
            // immediately rather than waiting for a property-changed bump.
            ApplyDisabledOpacity(vm);
            // 0.11.5 — pin overlay binding. See
            // NodeView.Pins.cs for the rebuild + reposition logic.
            HookPinOverlay(vm);
        }
        else
        {
            UnhookPinOverlay();
        }
    }

    /// <summary>
    /// Fade the node body when <see cref="NodeViewModel.IsDisabled"/>
    /// is true so the canvas reads it as inert. Selection / error / flash
    /// borders stay at full opacity (they're on NodeRoot.BorderBrush which the
    /// Opacity setter still affects — but the priority comment in ApplyBorder
    /// keeps the visual hierarchy intact: disabled is a state, not a
    /// replacement for selection or error). 0.45 mirrors the pre-T15 WinForms
    /// disabled-node fade.
    /// </summary>
    private void ApplyDisabledOpacity(NodeViewModel vm)
    {
        NodeRoot.Opacity = vm.IsDisabled ? 0.45 : 1.0;
    }

    // When true, this view was removed from
    // NodeLayer.Children by the viewport cull (it scrolled off-screen) and will be
    // remounted UNCHANGED when it scrolls back in. Set by the canvas immediately
    // before the cull-driven Children.Remove; cleared on remount / real removal.
    // Without this guard, Children.Remove fires OnUnloaded which tears the view
    // down (VM unbind, pin overlay, tooltip timer, flash) — defeating keep-alive.
    internal bool _isCulling;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Cull-unmount: skip the full teardown so the VM binding, pin
        // overlay, tooltip timer and flash storyboard survive for a clean remount.
        // A REAL removal (node deleted) leaves _isCulling=false → full teardown.
        if (_isCulling) return;
        RunUnloadTeardown();
    }

    /// <summary>
    /// The per-view teardown (detach VM binding, unhook the pin
    /// overlay + its static-event listeners, stop the flash storyboard). Run from
    /// <see cref="OnUnloaded"/> on a real unmount, AND called directly by the
    /// canvas when a node is deleted while culled off-screen — that view isn't in
    /// the visual tree so no Unloaded fires, and skipping this would leak its
    /// static selection / animated-var listeners.
    /// </summary>
    internal void RunUnloadTeardown()
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            _boundVm = null;
        }
        // ActualThemeChanged stays subscribed across load/unload recycles: the
        // handler is an instance method on this FrameworkElement (self-cycle, so
        // GC reclaims it with the element) and a detached element doesn't raise
        // the event — unsubscribing here would silently kill theme-reactivity
        // after the first virtualization recycle.
        UnhookPinOverlay();
        _cachedCanvas = null;
        // Stop the cached flash storyboard so the timeline doesn't keep running
        // against the unloaded brush — small but real animation-leak avoidance
        // (Architect UI WIP).
        if (_currentFlashStoryboard is not null)
        {
            try { _currentFlashStoryboard.Stop(); } catch { /* unloaded tree */ }
            _currentFlashStoryboard = null;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NodeViewModel vm) return;
        if (e.PropertyName == nameof(NodeViewModel.IsSelected) ||
            e.PropertyName == nameof(NodeViewModel.IsExecutingFlash) ||
            e.PropertyName == nameof(NodeViewModel.FlashTick) ||
            e.PropertyName == nameof(NodeViewModel.IsErrorState) ||
            e.PropertyName == nameof(NodeViewModel.IsVarChainWriter) ||
            e.PropertyName == nameof(NodeViewModel.IsVarChainReader) ||
            e.PropertyName == nameof(NodeViewModel.IsDimmedByPicker) ||
            // Compact-mode toggle re-runs the border cascade so the
            // teal compact tint paints / clears (RaiseHeaderChanged nudges this).
            e.PropertyName == nameof(NodeViewModel.IsCompactMode))
            ApplyBorder(vm);
        // IsDisabled toggles the node-body opacity so the
        // authoring surface reads the node as inert. Border state stays
        // untouched (selection / error halos still paint at full opacity so
        // disabled nodes can still be inspected / re-enabled).
        if (e.PropertyName == nameof(NodeViewModel.IsDisabled))
            ApplyDisabledOpacity(vm);
    }

    /// <summary>
    /// Atomically snapshot the six shared node-state
    /// brushes under <see cref="s_themeRefreshGate"/> so a concurrent
    /// <see cref="RefreshThemeBrushes"/> can't be read half-applied mid-paint
    /// (e.g. a new-theme selection brush paired with an old-theme divider
    /// brush). Callers paint from the returned locals, not the static fields.
    /// </summary>
    private static (SolidColorBrush Selection, SolidColorBrush Divider, SolidColorBrush Error,
        SolidColorBrush VarWriter, SolidColorBrush VarReader, SolidColorBrush Compact) SnapshotThemeBrushes()
    {
        lock (s_themeRefreshGate)
        {
            return (s_selectionBrush, s_dividerBrush, s_errorBrush,
                s_varWriterBrush, s_varReaderBrush, s_compactBrush);
        }
    }

    private void ApplyBorder(NodeViewModel vm)
    {
        // Border priority (matches pre-T15 Canvas.PaintNodes):
        //   var-chain (writer/reader) > selection > error > default.
        // Var-chain wins over selection because the user explicitly summoned
        // the halo (right-click → Trace / Pin) and wants the chain visible
        // even when a chain member is also the selected node. Picker dim
        // overlay is layered on top of the body via DimOverlay below.
        // Flash is no longer part of this border
        // cascade — it's a separate body-tint overlay (UpdateFlashOverlay) so a
        // selected / error / var-chain node keeps its border colour while the
        // execution pulse plays over the body.
        // Snapshot the shared brushes under the gate
        // so an in-flight theme refresh can't be observed half-applied.
        var br = SnapshotThemeBrushes();
        // NodeRoot stays a CONSTANT 1px divider body
        // border (set the brush only; never touch its Thickness, so the body is
        // never re-measured on a state toggle). The 2px accent emphasis is painted
        // on the no-content AccentRing sibling instead. Same priority cascade as
        // before: var-chain (writer/reader) > selection > error > compact > none.
        NodeRoot.BorderBrush = br.Divider;
        if (vm.IsVarChainWriter)
        {
            AccentRing.BorderBrush = br.VarWriter;
            AccentRing.Visibility  = Microsoft.UI.Xaml.Visibility.Visible;
        }
        else if (vm.IsVarChainReader)
        {
            AccentRing.BorderBrush = br.VarReader;
            AccentRing.Visibility  = Microsoft.UI.Xaml.Visibility.Visible;
        }
        // Selection ahead of error: a selected error-state node must still show
        // the gold halo — without this the user can click an error node and get
        // no visual feedback that the click registered.
        else if (vm.IsSelected)
        {
            AccentRing.BorderBrush = br.Selection;
            AccentRing.Visibility  = Microsoft.UI.Xaml.Visibility.Visible;
        }
        else if (vm.IsErrorState)
        {
            AccentRing.BorderBrush = br.Error;
            AccentRing.Visibility  = Microsoft.UI.Xaml.Visibility.Visible;
        }
        else if (vm.IsCompactMode)
        {
            // Compact mode reads as a distinct teal-tinted ring so it's
            // not mistaken for a manually-shrunk node. Lowest priority: selection
            // / error / var-chain all win above.
            AccentRing.BorderBrush = br.Compact;
            AccentRing.Visibility  = Microsoft.UI.Xaml.Visibility.Visible;
        }
        else
        {
            AccentRing.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        ApplyDim(vm);
        ApplyVarChainGlow(vm);
        UpdateFlashOverlay(vm);
    }

    /// <summary>
    /// Paint the var-chain outer glow halo behind
    /// NodeRoot when this node writes (cyan) or reads (amber) the currently
    /// hovered / picked variable. NodeViewModel documents "cyan border + outer
    /// glow halo" / "amber border + outer glow halo"; the border is set in
    /// <see cref="ApplyBorder"/> and the glow is the sibling
    /// <c>VarChainGlow</c> Border (NodeView.xaml) inflated 4px around the body.
    /// Writer wins over reader (a node can technically be both for a self-
    /// referential chain; cyan is the "this is the source" signal). Collapsed
    /// + 0 opacity when neither flag is set.
    /// </summary>
    private void ApplyVarChainGlow(NodeViewModel vm)
    {
        if (VarChainGlow is null) return;
        // Snapshot under the gate so a concurrent
        // theme refresh can't be read half-applied while painting the halo.
        var br = SnapshotThemeBrushes();
        if (vm.IsVarChainWriter)
        {
            VarChainGlow.Background = br.VarWriter;
            ShowVarChainGlow();
        }
        else if (vm.IsVarChainReader)
        {
            VarChainGlow.Background = br.VarReader;
            ShowVarChainGlow();
        }
        else
        {
            VarChainGlow.Opacity    = 0;
            VarChainGlow.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Size the glow to the body + 8px (4px each side, paired with
    /// the XAML Margin="-4") and reveal it at the pre-T15 ~0.43 halo opacity.
    /// Re-sizing here (rather than via an ElementName binding) lets the glow
    /// track NodeRoot's live intrinsic-grow size without XAML arithmetic.
    /// </summary>
    private void ShowVarChainGlow()
    {
        if (VarChainGlow is null || NodeRoot is null) return;
        double w = NodeRoot.ActualWidth;
        double h = NodeRoot.ActualHeight;
        if (w > 0) VarChainGlow.Width  = w + 8.0;
        if (h > 0) VarChainGlow.Height = h + 8.0;
        VarChainGlow.Opacity    = 0.43;
        VarChainGlow.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Drive the FlashOverlay body-tint pulse,
    /// independent of the border cascade so the selection / error halo stays
    /// visible while a node executes. Saturated amber, ~420ms ramp
    /// (120 ease-out fade-in, 180 hold, 120 ease-in fade-out). Re-firing while a
    /// pulse is mid-ramp (tight loop) Stop()s the prior storyboard first so the
    /// opacity timelines don't stack.
    /// </summary>
    private const double FlashPeakOpacity = 0.42;

    // Build the flash storyboard ONCE per NodeView and re-Begin it per pulse.
    // Allocating a fresh Storyboard + 4 keyframes + 2 easing functions per
    // DEBUG_NODE_EXEC flash churned the animation pool under a busy debug
    // session; the timeline is a constant, so Stop()+Begin() on a cached
    // instance replays the identical pulse. Targets FlashOverlay, which lives
    // for this view's lifetime (cull unmounts keep the element; a real
    // teardown discards the whole view, storyboard included).
    private Storyboard EnsureFlashStoryboard()
    {
        if (_currentFlashStoryboard is not null) return _currentFlashStoryboard;
        var anim = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTarget(anim, FlashOverlay);
        Storyboard.SetTargetProperty(anim, "Opacity");
        anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame {
            KeyTime = TimeSpan.FromMilliseconds(120), Value = FlashPeakOpacity,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(300), Value = FlashPeakOpacity });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame {
            KeyTime = TimeSpan.FromMilliseconds(420), Value = 0.0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        var sb = new Storyboard();
        sb.Children.Add(anim);
        _currentFlashStoryboard = sb;
        return sb;
    }

    private void UpdateFlashOverlay(NodeViewModel vm)
    {
        if (FlashOverlay is null) return;
        if (vm.IsExecutingFlash)
        {
            var sb = EnsureFlashStoryboard();
            // Stop first so a re-fire mid-ramp restarts cleanly instead of
            // stacking opacity timelines (the "sticking at full opacity" bug).
            try { sb.Stop(); } catch { /* unloaded tree */ }
            FlashOverlay.Opacity = 0;
            try { sb.Begin(); } catch { /* design-time / pre-realised tree */ }
        }
        else
        {
            if (_currentFlashStoryboard is not null)
            {
                try { _currentFlashStoryboard.Stop(); } catch { /* unloaded tree */ }
            }
            FlashOverlay.Opacity = 0;
        }
    }

    /// <summary>
    /// Painted last so it sits above sockets / pills / middle-attrs — pre-T15
    /// picker-mode dim. The Border named <c>DimOverlay</c> in NodeView.xaml
    /// is set Visible / Collapsed based on <see cref="NodeViewModel.IsDimmedByPicker"/>.
    /// </summary>
    private void ApplyDim(NodeViewModel vm)
    {
        if (DimOverlay is not null)
            DimOverlay.Visibility = vm.IsDimmedByPicker
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void ApplySelection(bool selected)
    {
        if (DataContext is NodeViewModel vm) ApplyBorder(vm);
        else
        {
            // Snapshot under the gate so a concurrent
            // theme refresh can't be observed half-applied mid-paint.
            var br = SnapshotThemeBrushes();
            // Non-VM fallback: NodeRoot stays a constant
            // 1px divider; selection paints the AccentRing (paint-only, no body
            // re-measure), matching the ApplyBorder path above.
            NodeRoot.BorderBrush   = br.Divider;
            AccentRing.BorderBrush = br.Selection;
            AccentRing.Visibility  = selected
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    // ─── Inline pill editing ─────────────────────────────────────────────
    // Click pill → IsEditing flips on, TextBox swaps in.
    // Enter / Esc / focus-loss → IsEditing flips off; Mode=TwoWay binding
    // already wrote the typed text back into ValuePill via SocketViewModel.

    // The inline-edit TextBoxes (value pill, socket-
    // label rename, node-title rename, middle-attr) toggle Visibility on
    // IsEditing / IsRenaming / IsTitleRenaming — they stay in the visual tree,
    // so a one-shot Loaded never re-fires per edit. Register a visibility watcher
    // so each time an editor becomes visible it grabs focus + selects-all,
    // honouring the "tap → type replaces" contract (pre-fix the box showed
    // unfocused: the first keypress inserted instead of replacing, and a second
    // click was needed to place the caret).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, object> s_inlineEditorWatched = new();
    private static readonly object s_inlineEditorSentinel = new();

    /// <summary>
    /// Set by the owning canvas (see <c>LogicCanvasView.EnsureFullView</c>): returns
    /// true while a pan / wire-drop / node-drag gesture owns pointer capture. During
    /// that window a programmatic inline-editor <see cref="Control.Focus(FocusState)"/>
    /// must NOT run — on WinAppSDK 1.5 that focus is delivered through
    /// <c>Microsoft.UI.Input!WindowsMessageDeliveryAdapter::ProcessWindowMessage_NoLock</c>
    /// and, mid-input-dispatch with capture active, spins a nested
    /// <c>user32!GetMessageW</c> pump that wedges the UI thread 8–15s (the confirmed
    /// "Architect freeze while editing a node under pan/zoom" hang, native minidumps
    /// 2026-07-19). When set and true, <see cref="FocusInlineEditor"/> re-defers the
    /// focus one dispatcher turn at a time until the gesture releases capture, then
    /// focuses cleanly. Null (no owner wired) ⇒ never blocked ⇒ original behaviour.
    /// Per-instance (not static) so it stays correct across multiple Architect windows.
    /// </summary>
    internal System.Func<bool>? InlineFocusBlocked { get; set; }

    // Cap on how many dispatcher turns the inline-editor focus waits for a gesture to
    // release capture before giving up (a very long drag simply forgoes the auto-focus;
    // the user can click the pill again). Bounded so a stuck predicate can't loop forever.
    private const int InlineFocusMaxDeferTurns = 240;

    private void OnInlineEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        // Focus now if it loaded already-visible (edit began before realization).
        if (tb.Visibility == Visibility.Visible) FocusInlineEditor(tb);
        if (s_inlineEditorWatched.TryGetValue(tb, out _)) return; // already watching this instance
        s_inlineEditorWatched.Add(tb, s_inlineEditorSentinel);
        tb.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (d, _) =>
        {
            if (d is TextBox t && t.Visibility == Visibility.Visible) FocusInlineEditor(t);
        });
    }

    private void FocusInlineEditor(TextBox tb)
    {
        // Defer to let the visibility change + layout settle before focusing,
        // otherwise Focus on a just-shown (previously-collapsed) element can
        // no-op. SelectAll so typing replaces the existing value.
        //
        // CAPTURE GATE (freeze fix): never run the programmatic Focus while the
        // owning canvas holds pointer capture for a pan / wire-drop / node-drag
        // gesture. TextBox.Focus() executed mid-gesture is delivered through the
        // WinAppSDK 1.5 Microsoft.UI.Input message adapter and spins a nested
        // GetMessage pump that wedges the UI thread 8–15s (the confirmed edit-
        // under-pan/zoom freeze; see InlineFocusBlocked). Re-defer one dispatcher
        // turn at a time until the gesture releases capture, then focus cleanly —
        // bounded by InlineFocusMaxDeferTurns so a stuck gesture can't loop forever.
        //
        // On a pointer-captured canvas Focus() can also silently return false;
        // keep honouring that (SelectAll only on success, re-defer on failure) so
        // the first keystroke still replaces the value.
        void Apply(int attempt)
        {
            try
            {
                if (tb.Visibility != Visibility.Visible) return;
                if (InlineFocusBlocked?.Invoke() == true)
                {
                    if (attempt < InlineFocusMaxDeferTurns)
                        tb.DispatcherQueue?.TryEnqueue(() => Apply(attempt + 1));
                    return;
                }
                if (tb.Focus(FocusState.Programmatic))
                {
                    tb.SelectAll();
                }
                else if (attempt < InlineFocusMaxDeferTurns)
                {
                    var rq = tb.DispatcherQueue;
                    if (rq is not null) rq.TryEnqueue(() => Apply(attempt + 1));
                }
            }
            catch { /* designer-time / detached */ }
        }
        var dq = tb.DispatcherQueue;
        if (dq is null) Apply(0); else dq.TryEnqueue(() => Apply(0));
    }

    /// <summary>
    /// Close the click→type focus-timing race for an inline value pill. The read-only
    /// pill TextBlock and its inline editor TextBox are siblings in the same Grid, and the
    /// TextBox's Visibility binding flips synchronously the instant IsEditing is set (by the
    /// caller, just above). Realize it via UpdateLayout and focus it inside THIS input event
    /// so a fast first keystroke — notably '/' (= Shift+7 = VirtualKey.Number7 on a German
    /// QWERTZ layout, which the canvas quick-key map would otherwise arm into a node-spawn
    /// and swallow) — lands in the field instead of on the still-unfocused, pointer-captured
    /// canvas. The <see cref="OnInlineEditorLoaded"/> deferred-retry path stays as the
    /// fallback for when the box is not yet realizable.
    /// </summary>
    private static void TryFocusInlineEditorNow(object? tappedPill)
    {
        if (tappedPill is not DependencyObject d) return;
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d) is not Panel row) return;
        TextBox? box = null;
        foreach (var child in row.Children)
            if (child is TextBox t) { box = t; break; }
        if (box is null) return;
        try
        {
            box.UpdateLayout();
            if (box.Visibility == Visibility.Visible && box.Focus(FocusState.Programmatic))
                box.SelectAll();
        }
        catch { /* not realizable yet — the deferred OnInlineEditorLoaded retry handles it */ }
    }

    /// <summary>
    /// Realize + focus the inline editor (value pill or
    /// middle-attr) whose DataContext is <paramref name="editTarget"/>, for the GPU
    /// canvas's single-click pill-edit path. The caller has already flipped the
    /// target's IsEditing (so the editor TextBox's Visibility binding is Visible), but
    /// on a node JUST materialized over the GPU canvas the row hasn't been laid out
    /// yet — so without this the editor is neither realized nor focused on the first
    /// click and the user had to click again (perceived "double-click"). Mirrors the
    /// retained <see cref="OnPillTapped"/> path: force <see cref="UIElement.UpdateLayout"/>
    /// to realize the now-Visible TextBox in THIS input event, then focus + select-all.
    /// The editor is matched by its DataContext (the socket / middle-attr VM) and its
    /// Visible state (the read-only pill + the label-rename box stay Collapsed under
    /// IsEditing), so the right box is found without an ItemsControl container lookup.
    /// </summary>
    public bool FocusInlineEditorFor(object editTarget)
    {
        if (editTarget is null) return false;
        try
        {
            UpdateLayout();   // realize the row → the IsEditing-Visible editor TextBox now exists
            var box = FindVisibleEditorFor(this, editTarget);
            if (box is null) return false;
            FocusInlineEditor(box);   // deferred Focus + SelectAll, with the capture-release retry
            return true;
        }
        catch { return false; }
    }

    // Depth-first walk for the realized, Visible editor TextBox bound to editTarget.
    private static TextBox? FindVisibleEditorFor(DependencyObject root, object editTarget)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb
                && tb.Visibility == Visibility.Visible
                && ReferenceEquals(tb.DataContext, editTarget))
                return tb;
            var found = FindVisibleEditorFor(child, editTarget);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnPillTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
        {
            // 0.10.0 — snapshot the baseline (not the undo stack) so Esc
            // can roll back and a no-op commit (old == new) skips pushing
            // an undo entry entirely. Pre-0.10.0 the push fired here
            // eagerly and Esc didn't rewind the TwoWay-bound value;
            // typing-then-Esc lost the original and Ctrl+Z silently
            // rewound past the edit.
            s.BeginValuePillEdit();
            TryFocusInlineEditorNow(sender);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 0.11.x polish — Databank picker chevron. Opens a MenuFlyout anchored
    /// to the ▾ button with items pulled from the live SQLite databank
    /// (tables for the TableName socket, columns for the Column socket).
    /// Clicking an item writes the picked value through SocketViewModel.ValuePill
    /// which already persists into Node.Attributes and re-fires Width/Height
    /// so the body resizes to fit the new (possibly longer) label.
    /// <para>
    /// 0.11.x freeze fix — pre-fix the handler was <c>async void</c> and
    /// blocked on <c>await db.GetAllTableNamesAsync().ConfigureAwait(true)</c>
    /// BEFORE calling <c>flyout.ShowAt(fe)</c>. Two failure modes followed:
    /// (1) the WinUI Button captures the pointer for the duration of its
    /// Click handler's synchronous prefix; if the awaited DB lock is held
    /// by another caller (Hub's script engine running a `db.*` command,
    /// or the WAL-checkpoint timer), the chevron click "freezes" the
    /// canvas with no visible flyout while the user sits at a non-
    /// responsive ▾. (2) When the await DID eventually resume, the Click
    /// handler had returned the routed event up the visual tree and the
    /// XamlRoot anchored on <c>fe</c> could be stale (canvas re-rendered
    /// the row during the await — e.g. cross-file event-pair sync rebuilt
    /// the InputsItemsControl), so <c>ShowAt</c> silently failed or hit
    /// a layout deadlock the watchdog reported as "UI thread unresponsive."
    /// </para>
    /// <para>
    /// Fix: open the flyout IMMEDIATELY against the click-time XamlRoot
    /// with a "Loading…" placeholder, then refresh items from the async
    /// DB load. The user sees the picker open instantly; if the DB is
    /// slow, the spinner stays visible (instead of the canvas freezing).
    /// XamlRoot is captured before the await so a re-render of the
    /// source button during loading doesn't strand <c>ShowAt</c>.
    /// </para>
    /// </summary>
    private async void OnDatabankPickerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not SocketViewModel s) return;
        if (s.DatabankPickerKind == SocketViewModel.DatabankPickerKindValue.None) return;

        try
        {
            var flyout = new MenuFlyout();
            // Explicit XamlRoot — required when the Click originates from a
            // Button inside a heavily-transformed Canvas (the Architect canvas
            // applies a zoom/pan RenderTransform). Without it ShowAt picks the
            // wrong root for popup placement when the DataContext-bound row
            // gets rebuilt by an interleaved RebuildSockets.
            try { flyout.XamlRoot = fe.XamlRoot; }
            catch { /* designer-time, no XamlRoot */ }

            // Build the flyout FULLY POPULATED before ShowAt. Mutating a
            // MenuFlyout.Items collection while the popup is already on-screen
            // silently dismisses it in WinUI 3 — that is the regression Majo
            // flagged as "DB picker does not pop up". The prior fix opened the
            // flyout with a "Loading…" item and replaced it from the Opened
            // hook, but that Clear()+Add() still ran while the popup was
            // visible, so WinUI tore the popup down the instant the DB query
            // returned (the picker "flashed and vanished"). The DB queries are
            // genuinely async (DB.cs awaits the connection lock + reader with
            // ConfigureAwait(false)), so awaiting here yields the UI thread
            // instead of freezing it; the picker appears the moment the list
            // is ready and its items never mutate mid-show.
            await PopulateDatabankPickerAsync(s, flyout).ConfigureAwait(true);

            // The source row can be torn down by an interleaved RebuildSockets
            // while the await was in flight — bail if the anchor lost its
            // XamlRoot rather than throwing inside ShowAt.
            if (fe.XamlRoot is null) return;
            flyout.ShowAt(fe);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "NodeView", "OnDatabankPickerClicked", ex);
        }
    }

    /// <summary>
    /// Async population of the databank picker into a not-yet-shown flyout.
    /// The flyout starts empty, so items are only ever ADDED here (never
    /// cleared mid-show); the caller awaits this before calling ShowAt.
    /// Catches its own exceptions and surfaces them as a single disabled
    /// placeholder item so the user gets feedback instead of a silent empty
    /// popup.
    /// </summary>
    private static async System.Threading.Tasks.Task PopulateDatabankPickerAsync(
        SocketViewModel s, MenuFlyout flyout)
    {
        try
        {
            var db = Phoenix.Controls.Shared.Services.DB.Instance;
            switch (s.DatabankPickerKind)
            {
                case SocketViewModel.DatabankPickerKindValue.Tables:
                {
                    var tables = await db.GetAllTableNamesAsync().ConfigureAwait(true);
                    // Giveaway.Ticket's PriceTable is a convenience surface — the
                    // picker offers the standard currency table as a one-click
                    // create (columns name/currency) when it doesn't exist yet.
                    bool offerCreate = s.ParentNode.Title == "Giveaway.Ticket"
                        && !tables.Contains(ChannelPointsTableName, System.StringComparer.OrdinalIgnoreCase);
                    if (tables.Count == 0 && !offerCreate)
                    {
                        flyout.Items.Add(new MenuFlyoutItem
                        {
                            Text = Localizer.T("architect.canvas.databank_picker.no_tables", "(no tables in databank)"),
                            IsEnabled = false,
                        });
                    }
                    else
                    {
                        foreach (var t in tables)
                        {
                            var name = t;
                            var item = new MenuFlyoutItem { Text = name };
                            item.Click += (_, _) => s.ValuePill = name;
                            flyout.Items.Add(item);
                        }
                        if (offerCreate)
                        {
                            if (tables.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
                            var create = new MenuFlyoutItem
                            {
                                Text = string.Format(
                                    Localizer.T("architect.canvas.databank_picker.create_table", "Create '{0}' table"),
                                    ChannelPointsTableName),
                                Icon = new FontIcon { Glyph = "" },   // Segoe MDL2 "Add"
                            };
                            ToolTipService.SetToolTip(create, string.Format(
                                Localizer.T("architect.canvas.databank_picker.create_table.tip",
                                    "Creates the standard currency table '{0}' with the columns " +
                                    "name + currency — the shape this node charges channel points from — and selects it here."),
                                ChannelPointsTableName));
                            create.Click += async (_, _) => await CreateChannelPointsTableAsync(s);
                            flyout.Items.Add(create);
                        }
                    }
                    break;
                }
                case SocketViewModel.DatabankPickerKindValue.Giveaways:
                {
                    // Giveaway.IsActive's selector drop-down. First entry clears
                    // the pill (empty selector = follow the app-wide default
                    // giveaway); the rest list every giveaway in the databank —
                    // the pill is set to the KEY, which ResolveTargetAsync
                    // matches unambiguously (titles may repeat).
                    var defaultItem = new MenuFlyoutItem
                    {
                        Text = Localizer.T("architect.canvas.databank_picker.default_giveaway", "(default giveaway)"),
                    };
                    ToolTipService.SetToolTip(defaultItem,
                        Localizer.T("architect.canvas.databank_picker.default_giveaway.tip",
                            "Clears the selector — the node then follows the app-wide default giveaway from the Hub Giveaway page."));
                    defaultItem.Click += (_, _) => s.ValuePill = "";
                    flyout.Items.Add(defaultItem);

                    var giveaways = await db.GetGiveawaysAsync().ConfigureAwait(true);
                    if (giveaways.Count > 0)
                    {
                        flyout.Items.Add(new MenuFlyoutSeparator());
                        foreach (var g in giveaways)
                        {
                            var key = g.Key;
                            string title = string.IsNullOrWhiteSpace(g.Title) ? key : g.Title;
                            var item = new MenuFlyoutItem { Text = $"{title} — {key} · {g.Status}" };
                            item.Click += (_, _) => s.ValuePill = key;
                            flyout.Items.Add(item);
                        }
                    }
                    break;
                }
                case SocketViewModel.DatabankPickerKindValue.Columns:
                {
                    var tableName = s.ParentTableNameAttr;
                    if (string.IsNullOrEmpty(tableName))
                    {
                        flyout.Items.Add(new MenuFlyoutItem
                        {
                            Text = Localizer.T("architect.canvas.databank_picker.set_table_first", "(set TableName first)"),
                            IsEnabled = false,
                        });
                        break;
                    }
                    var schema = await db.GetSchemaAsync(tableName!).ConfigureAwait(true);
                    // rowid-as-source: GetColumn can read the row-id "column" so a script
                    // can obtain the (ordered) list of row ids to drive per-row operations
                    // (the GetColumn('rowid') → ForEach → GetCell/SetCell/DeleteRow idiom).
                    // GetSchemaAsync filters rowid out, so surface it here. Scoped to
                    // DB.GetColumn — offering rowid on GetCell/SetCell/Increment would
                    // invite reading, or worse WRITING, the id itself.
                    if (s.ParentNode.Title is { } pt
                        && pt.EndsWith("GetColumn", System.StringComparison.Ordinal))
                    {
                        var rowidItem = new MenuFlyoutItem
                        {
                            Text = Localizer.T("architect.canvas.databank_picker.rowid", "rowid (row id)"),
                        };
                        rowidItem.Click += (_, _) => s.ValuePill = "rowid";
                        flyout.Items.Add(rowidItem);
                    }
                    if (schema.Count == 0)
                    {
                        flyout.Items.Add(new MenuFlyoutItem
                        {
                            Text = string.Format(
                                Localizer.T("architect.canvas.databank_picker.no_columns", "(no columns in {0})"),
                                tableName),
                            IsEnabled = false,
                        });
                    }
                    else
                    {
                        foreach (var col in schema)
                        {
                            var name = col.Name;
                            var item = new MenuFlyoutItem { Text = name };
                            item.Click += (_, _) => s.ValuePill = name;
                            flyout.Items.Add(item);
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = Localizer.T("architect.canvas.databank_picker.unavailable", "(databank unavailable)"),
                    IsEnabled = false,
                });
            }
            catch { /* flyout already disposed */ }
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "NodeView", "PopulateDatabankPickerAsync", ex);
        }
    }

    /// <summary>
    /// The standard currency table Giveaway.Ticket charges channel points
    /// from. Fixed name + shape (columns <c>name</c> / <c>currency</c>) so the
    /// node, the Hub-side purchase SQL, and user banking scripts all agree.
    /// </summary>
    internal const string ChannelPointsTableName = "ChannelPoints";

    /// <summary>
    /// One-click create for the standard currency table (picker item on
    /// Giveaway.Ticket's PriceTable ▾). CREATE IF NOT EXISTS underneath, so a
    /// concurrent/repeat click is harmless; on success the pill is set to the
    /// table name. Failures land in the system log — the guardrail-style
    /// no-modal rule applies (repeat-fire surface).
    /// </summary>
    private static async System.Threading.Tasks.Task CreateChannelPointsTableAsync(SocketViewModel s)
    {
        try
        {
            await Phoenix.Controls.Shared.Services.DB.Instance.CreateUserTableAsync(
                ChannelPointsTableName,
                new System.Collections.Generic.List<(string, string)>
                {
                    ("name", "TEXT"),
                    ("currency", "INTEGER"),
                }).ConfigureAwait(true);
            s.ValuePill = ChannelPointsTableName;
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                $"Databank: created table '{ChannelPointsTableName}' (name, currency) for Giveaway.Ticket.",
                "Architect", Phoenix.Controls.Shared.Models.LogLevel.System);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "NodeView", "CreateChannelPointsTableAsync", ex);
        }
    }

    /// <summary>
    /// 0.13.x inline TARGET picker — Process.Spawn (process) and Macro.Call
    /// (macro) middle-attribute rows. Mirrors <see cref="OnDatabankPickerClicked"/>
    /// (the DB.* TableName/Column picker): the ▾ chevron opens a MenuFlyout of
    /// the graph's processes / macros so the user binds by PICKING. Picking sets
    /// the ProcessId / MacroId the exporter binds on (free-text entry only ever
    /// set the display name, so the spawn exported "not found") AND re-syncs the
    /// call node's sockets, via the canvas <c>Bind*Node</c> helpers. Reads the
    /// MiddleAttributeViewModel off the Button's DataContext (NOT Tag — HitTagFrom
    /// would mis-route the click as a node hit, the same trap the DB picker note
    /// documents).
    /// </summary>
    private void OnMiddleAttrPickerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not MiddleAttributeViewModel m || !m.HasOptionsPicker) return;
        var canvas = GetCanvasCached(sender as DependencyObject);
        var graph = canvas?.ViewModel?.Graph;
        if (canvas is null || graph is null) return;
        var node = m.ParentNode;

        try
        {
            var flyout = new MenuFlyout();
            // Explicit XamlRoot — the click originates from a Button inside the
            // zoom/pan-transformed canvas; without it ShowAt can pick the wrong
            // popup root (mirrors OnDatabankPickerClicked).
            try { flyout.XamlRoot = fe.XamlRoot; } catch { /* designer — no XamlRoot */ }

            bool isProcess = string.Equals(node.Title, "Process.Start", System.StringComparison.Ordinal);
            if (isProcess)
            {
                if (graph.Processes.Count == 0)
                    flyout.Items.Add(new MenuFlyoutItem { Text = Localizer.T("architect.canvas.target_picker.no_processes", "(no processes — add one in the left rail)"), IsEnabled = false });
                foreach (var p in graph.Processes)
                {
                    var proc = p;
                    var item = new MenuFlyoutItem { Text = string.IsNullOrEmpty(proc.Name) ? Localizer.T("architect.canvas.target_picker.unnamed_process", "(unnamed process)") : proc.Name };
                    item.Click += (_, _) => canvas.BindProcessSpawnNode(node, proc);
                    flyout.Items.Add(item);
                }
            }
            else // Macro.Call
            {
                if (graph.Macros.Count == 0)
                    flyout.Items.Add(new MenuFlyoutItem { Text = Localizer.T("architect.canvas.target_picker.no_macros", "(no macros — add one in the left rail)"), IsEnabled = false });
                foreach (var mac in graph.Macros)
                {
                    var macro = mac;
                    var item = new MenuFlyoutItem { Text = string.IsNullOrEmpty(macro.Name) ? Localizer.T("architect.canvas.target_picker.unnamed_macro", "(unnamed macro)") : macro.Name };
                    item.Click += (_, _) => canvas.BindMacroCallNode(node, macro);
                    flyout.Items.Add(item);
                }
            }

            if (fe.XamlRoot is null) return;
            flyout.ShowAt(fe);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("NodeView", "OnMiddleAttrPickerClicked", ex);
        }
    }

    /// <summary>
    /// PointerEntered on the value pill — if the pill content carries a
    /// <c>{var}</c> token, set the canvas VM's <c>HoveredVarChainName</c>
    /// so writers / readers light up across the canvas. Mirrors pre-T15
    /// UpdateVarChainHover.
    /// </summary>
    private void OnPillPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SocketViewModel s) return;
        var v = s.ValuePill;
        if (string.IsNullOrEmpty(v)) return;
        var m = s_varTokenPattern.Match(v);
        if (!m.Success) return;
        var canvas = GetCanvasCached(sender as DependencyObject);
        // Skip the VM write (and its var-chain
        // highlight recompute) when the hovered var is unchanged. The setter
        // already short-circuits internally, but this avoids even the call on a
        // re-enter of the same pill.
        if (canvas?.ViewModel is { } vm)
        {
            string name = m.Groups[1].Value;
            if (!string.Equals(vm.HoveredVarChainName, name, StringComparison.OrdinalIgnoreCase))
                vm.HoveredVarChainName = name;
        }
    }

    private void OnPillPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var canvas = GetCanvasCached(sender as DependencyObject);
        if (canvas?.ViewModel is not null)
            canvas.ViewModel.HoveredVarChainName = null;
    }

    private void OnPillEditKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
        {
            // Esc rolls back to the baseline captured at edit-start
            // (UE-Blueprints idiom). Ctrl+Enter always commits;
            // bare Enter commits ONLY for single-line attrs — multi-line
            // attrs let AcceptsReturn pass the keystroke through to insert
            // a newline.
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                s.EndValuePillEdit(commit: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                bool ctrl = IsCtrlDown();
                if (ctrl || !s.IsMultilineAttr)
                {
                    if (s.EndValuePillEdit(commit: true))
                    {
                        var canvas = GetCanvasCached(sender as DependencyObject);
                        canvas?.PushUndoForInlineEdit();
                        MaybeNotifyEventNameChangedFromValuePill(canvas, s);
                    }
                    e.Handled = true;
                    return;
                }
                // Multi-line + bare Enter: fall through so AcceptsReturn inserts \n.
            }
        }

        // Ctrl+Space → var-picker flyout. Shows the loaded graph's Variables
        // list; pick one to insert "{name}" at the cursor.
        if (e.Key == Windows.System.VirtualKey.Space && IsCtrlDown() && sender is TextBox tb)
        {
            ShowVarPicker(tb);
            e.Handled = true;
        }
    }

    private static bool IsCtrlDown()
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    private void ShowVarPicker(TextBox host)
    {
        // Walk the visual tree up to LogicCanvasView for access to the Graph
        // (cached after first hit; see GetCanvasCached).
        var canvas = GetCanvasCached(host);
        if (canvas?.ViewModel is null) return;
        var graph = canvas.ViewModel.Graph;

        // Resolve the current node so AutocompleteScopeBuilder.Build can walk
        // upstream flow ancestors and surface event.* / loop.* / result.* tokens.
        Phoenix.Controls.Shared.Models.Node? currentNode = null;
        // Capture the pin's
        // DataType so the picker can hide variables whose persisted type
        // doesn't match. The picker was opened from a value-pill keystroke
        // (Ctrl+Space inside the inline TextBox); the host TextBox's
        // DataContext is the SocketViewModel that owns the pill. Falls
        // back to Any when no SVM is reachable (e.g. a future caller that
        // opens the picker from a non-pin host), which preserves the
        // pre-fix "all variables shown" behaviour.
        var pinDataType = Phoenix.Controls.Shared.Models.SocketDataType.Any;
        if ((host as FrameworkElement)?.DataContext is SocketViewModel svm)
        {
            currentNode = svm.ParentNode;
            pinDataType = svm.DataType;
        }

        var pool = BuildAutocompletePool(graph, currentNode);

        // Apply type filter when we know the pin's DataType. Non-
        // variable entries (system tokens, namespace bookends, scope-
        // builder output, public.* keys) stay in the pool — they have no
        // declared type and resolve at runtime; suppressing them would
        // shrink the picker below what users expect from "Ctrl+Space".
        // Graph-variable entries surface with a "graph · <Type>" Source
        // string built by BuildAutocompletePool; we parse the type token
        // back out, map String/Number/Bool to SocketDataType, and run
        // AreCompatible against the pin so Int↔Float widening + Any
        // wildcard match the same way wire-drop's compatibility gate does.
        if (pinDataType != Phoenix.Controls.Shared.Models.SocketDataType.Any
            && pinDataType != Phoenix.Controls.Shared.Models.SocketDataType.Flow)
        {
            pool = FilterPoolByPinDataType(pool, pinDataType);
        }

        if (pool.Count == 0) return;

        var list = new ListView
        {
            ItemsSource = pool,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 320,
        };
        list.ItemTemplate = BuildAutocompleteRowTemplate();

        // Pre-select the first
        // row so a user who already knows the variable name can hit Enter
        // alone (and so the selection-highlight reads as a "focus" cue
        // before they touch the arrow keys).
        if (pool.Count > 0) list.SelectedIndex = 0;

        var flyout = new Flyout
        {
            Content = list,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
        };

        // Shared commit path used by both mouse-click and Enter-key.
        void CommitEntry(AutocompleteEntry entry)
        {
            string ins = entry.IsToken ? entry.Token : "{" + entry.Token + "}";
            int caret = host.SelectionStart;
            host.Text = host.Text.Insert(caret, ins);
            host.SelectionStart = caret + ins.Length;
            flyout.Hide();
        }

        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is AutocompleteEntry entry)
                CommitEntry(entry);
            else
                flyout.Hide();
        };

        // Handle Enter/Escape on the ListView. WinUI's ListView
        // routes Up/Down internally when SelectionMode=Single, so we let
        // it handle navigation; Enter to commit and Escape to dismiss
        // are what need explicit wiring.
        list.KeyDown += (_, ke) =>
        {
            switch (ke.Key)
            {
                case Windows.System.VirtualKey.Enter:
                    if (list.SelectedItem is AutocompleteEntry sel)
                    {
                        CommitEntry(sel);
                        ke.Handled = true;
                    }
                    break;
                case Windows.System.VirtualKey.Escape:
                    flyout.Hide();
                    ke.Handled = true;
                    break;
            }
        };

        flyout.Opened += (_, _) =>
        {
            // Focus the ListView on open so arrow keys + Enter land
            // there. Pre-fix the picker opened with keyboard focus still
            // on the TextBox and Up/Down moved the caret instead of
            // walking the picker rows.
            try { list.Focus(FocusState.Programmatic); }
            catch { /* best-effort */ }
        };

        // Defer the popup's ShowAt off this synchronous KeyDown dispatch. Opening
        // a windowed Flyout (its own top-level HWND + message delivery) from inside
        // the live input event, while the value-pill TextBox still holds keyboard
        // focus, can enter the WinAppSDK Microsoft.UI.Input nested GetMessage pump
        // that wedges the UI thread (same freeze class as the inline-editor focus).
        // One dispatcher turn later the input event has unwound; behaviour is
        // otherwise identical.
        if (host.DispatcherQueue is { } dq) dq.TryEnqueue(() => flyout.ShowAt(host));
        else flyout.ShowAt(host);
    }

    /// <summary>
    /// Filter the picker pool
    /// against the pin's DataType. Keeps non-variable entries (system
    /// tokens, namespaces, scope-builder output, public.*) because their
    /// runtime type isn't declared in the graph; rejects graph variables
    /// whose <see cref="Phoenix.Controls.Shared.Models.VariableDefinition.Type"/>
    /// doesn't satisfy
    /// <see cref="Phoenix.Controls.Architect.Core.NodeRegistry.AreCompatible"/>.
    /// </summary>
    private static System.Collections.Generic.List<AutocompleteEntry> FilterPoolByPinDataType(
        System.Collections.Generic.List<AutocompleteEntry> pool,
        Phoenix.Controls.Shared.Models.SocketDataType pinType)
    {
        var filtered = new System.Collections.Generic.List<AutocompleteEntry>(pool.Count);
        foreach (var entry in pool)
        {
            // Source tag is "graph · <Type>" for graph variables; anything
            // else is a passthrough (system tokens, scope, public.* keys).
            if (entry.Source != null && entry.Source.StartsWith("graph · ", StringComparison.Ordinal))
            {
                string typeToken = entry.Source.Substring("graph · ".Length).Trim();
                var varType = MapVariableTypeTokenToSocketDataType(typeToken);
                if (Phoenix.Controls.Architect.Core.NodeRegistry.AreCompatible(pinType, varType))
                    filtered.Add(entry);
            }
            else
            {
                filtered.Add(entry);
            }
        }
        return filtered;
    }

    /// <summary>
    /// Map the <see cref="Phoenix.Controls.Shared.Models.VariableDefinition.Type"/>
    /// token ("String" / "Number" / "Bool") onto a
    /// <see cref="Phoenix.Controls.Shared.Models.SocketDataType"/>. Number
    /// maps to Float so Int↔Float widening in AreCompatible accepts either
    /// side; an unrecognised token (future schema addition) maps to Any so
    /// the entry passes through rather than getting silently dropped by a
    /// stale picker.
    /// </summary>
    private static Phoenix.Controls.Shared.Models.SocketDataType MapVariableTypeTokenToSocketDataType(string typeToken)
        => typeToken switch
        {
            "String" => Phoenix.Controls.Shared.Models.SocketDataType.String,
            "Number" => Phoenix.Controls.Shared.Models.SocketDataType.Float,
            "Bool"   => Phoenix.Controls.Shared.Models.SocketDataType.Bool,
            _        => Phoenix.Controls.Shared.Models.SocketDataType.Any,
        };

    /// <summary>One row in the autocomplete flyout — Name plus optional dim subtitle for the source.</summary>
    private sealed record AutocompleteEntry(string Token, string Source, bool IsToken);

    /// <summary>
    /// Builds the multi-source pool the pre-T15 BuildAutocompleteSuggestions
    /// surfaced inside the inline pill editor. Static catalogue (10 system
    /// tokens + global. / user. / state. namespace bookends) plus local-scope
    /// (Var.* + Public.* names) plus the scope walker
    /// (<see cref="AutocompleteScopeBuilder"/>) for event.* / loop.* / result.*.
    /// </summary>
    private static System.Collections.Generic.List<AutocompleteEntry> BuildAutocompletePool(
        Phoenix.Controls.Shared.Models.Graph graph,
        Phoenix.Controls.Shared.Models.Node? currentNode)
    {
        var pool = new System.Collections.Generic.List<AutocompleteEntry>();
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        void Add(string token, string source)
        {
            if (string.IsNullOrEmpty(token) || !seen.Add(token)) return;
            pool.Add(new AutocompleteEntry(token, source, IsToken: false));
        }

        // 1) Static catalogue — namespace bookends + 10 system tokens.
        // The TOKENS are engine identifiers and stay English; the dim
        // right-hand SOURCE tag beside each one is prose.
        string srcNamespace = Localizer.T("architect.canvas.autocomplete.source.namespace", "namespace");
        Add("global.", srcNamespace);
        Add("user.",   srcNamespace);
        Add("state.",  srcNamespace);
        string srcSystem = Localizer.T("architect.canvas.autocomplete.source.system", "system");
        foreach (var sys in new[] { "time","hours","minutes","seconds","unix","date","day","month","monthname","year" })
            Add("system." + sys, srcSystem);

        // 2) Local-scope variables sweep — every Var.Get/Set/Inc/Toggle.VariableName +
        //    every Public.Get/Set.KeyName (engine-key prefixed "public.").
        foreach (var n in graph.Nodes)
        {
            if (n.Attributes == null) continue;
            switch (n.Title)
            {
                case "Var.Get":
                case "Var.Set":
                case "Var.Inc":
                case "Var.Toggle":
                    if (n.Attributes.TryGetValue("VariableName", out var vn) && !string.IsNullOrWhiteSpace(vn))
                        Add(vn.Trim(), Localizer.T("architect.canvas.autocomplete.source.var", "var"));
                    break;
                case "Public.Get":
                case "Public.Set":
                    if (n.Attributes.TryGetValue("KeyName", out var kn) && !string.IsNullOrWhiteSpace(kn))
                        Add("public." + kn.Trim(), Localizer.T("architect.canvas.autocomplete.source.public", "public"));
                    break;
            }
        }

        // 3) Scoped-token walker — upstream flow ancestors contribute event.* /
        //    loop.* / result.* / per-event arg/ret tokens.
        if (currentNode is not null)
        {
            try
            {
                var scoped = Phoenix.Controls.Architect.Core.AutocompleteScopeBuilder.Build(graph, currentNode);
                string srcScope = Localizer.T("architect.canvas.autocomplete.source.scope", "scope");
                foreach (var t in scoped) Add(t, srcScope);
            }
            catch { /* scope builder is best-effort — never break the picker on fault */ }
        }

        // 4) Graph-level Variables list (still surfaced — power users define vars
        //    here and refer to them in pill content).
        foreach (var v in graph.Variables)
            Add(v.Name, string.Format(
                Localizer.T("architect.canvas.autocomplete.source.graph", "graph · {0}"), v.Type));

        return pool;
    }

    /// <summary>Two-column row template: token name + dim source tag.</summary>
    ///
    /// PERF: cached as a static readonly. Previously
    /// every Ctrl+Space invocation re-parsed the XAML string via
    /// <see cref="Microsoft.UI.Xaml.Markup.XamlReader.Load"/> (tokenise,
    /// namespace resolve, type lookup, object construction). One template is
    /// enough — ItemTemplate is shared across rows.
    private static readonly DataTemplate s_autocompleteRowTemplate
        = LoadAutocompleteRowTemplate();
    private static DataTemplate BuildAutocompleteRowTemplate() => s_autocompleteRowTemplate;

    private static DataTemplate LoadAutocompleteRowTemplate()
    {
        // XamlReader.Load is the runtime XAML parser — it does NOT support
        // x:DataType. That attribute is compile-time-only (emitted by the
        // XAML compiler for {x:Bind} optimisation). The bindings below use
        // classic {Binding ...}, so x:DataType is both unnecessary and
        // actively harmful: pre-fix the parser failed with "The property
        // 'DataType' was not found in type 'DataTemplate'", which threw
        // out of NodeView's static cctor, made every TypeInitializer
        // reference of NodeView throw, and broke .phxg load entirely
        // (RefreshEventPairErrorState → IsErrorState setter → ApplyBorder
        // is the first NodeView touch on load).
        var xaml = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Grid Padding=""6,4"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""*"" />
      <ColumnDefinition Width=""Auto"" />
    </Grid.ColumnDefinitions>
    <TextBlock Text=""{Binding Token}"" FontFamily=""{StaticResource MonoFont}"" FontSize=""11"" />
    <TextBlock Grid.Column=""1"" Text=""{Binding Source}"" Margin=""8,0,0,0""
               Foreground=""{StaticResource CoalSecondaryTextBrush}""
               FontFamily=""{StaticResource SansFont}"" FontSize=""9"" />
  </Grid>
</DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        // Nullable parameter so call sites can pass `sender as DependencyObject`
        // (which is nullable) without each caller having to null-check first.
        // Walking the tree is a no-op when seed is null; the while loop just
        // returns null. Pre-fix every call site produced a CS8604 warning.
        while (node is not null)
        {
            if (node is T t) return t;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OnPillEditLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
        {
            // Focus-loss commits (same semantics as Enter on a single-line
            // pill). No-op when EndValuePillEdit was already called by the
            // Esc / Enter path.
            if (s.EndValuePillEdit(commit: true))
            {
                var canvas = GetCanvasCached(sender as DependencyObject);
                canvas?.PushUndoForInlineEdit();
                MaybeNotifyEventNameChangedFromValuePill(canvas, s);
            }
        }
    }

    /// <summary>
    /// When a socket VALUE-pill commit writes the <c>EventName</c> on an
    /// Event-pair node, run the canvas's EventName tail (adopt-on-join +
    /// pair sync + cross-file sync + unpaired refresh). Event.Trigger is the
    /// case that needs this: its EventName is an INPUT SOCKET whose unwired
    /// value pill writes <c>Attributes["EventName"]</c> — so naming a Trigger
    /// went through THIS commit path, which (pre-fix) ended at the undo push.
    /// <see cref="MaybeNotifyEventNameChanged"/> only covers the
    /// middle-attribute pill (Event.Executor / Event.Return, which have no
    /// EventName socket), so a freshly placed Trigger never adopted the
    /// executor's bubble shape from the sibling file ("trigger does not sync
    /// up with executors when placed and name given").
    /// </summary>
    private static void MaybeNotifyEventNameChangedFromValuePill(LogicCanvasView? canvas, SocketViewModel s)
    {
        if (canvas is null) return;
        if (s.Model.Name != "EventName") return;
        var host = s.ParentNode;
        if (host?.Title is "Event.Trigger" or "Event.Executor" or "Event.Return")
            canvas.NotifyEventNameChangedFromNodeView(host);
    }

    // ─── Inline socket-name rename ───────────────────────────────────────
    // UE-Blueprints style, sockets are
    // renamed on the node body. Double-tap label → TextBox, Enter/Esc/blur
    // commits via TwoWay binding back into SocketViewModel.Label which writes
    // through to Socket.Name.

    private void OnLabelDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
        {
            // A managed "+ variable" / "+ return" / "+ input" / "+ output"
            // slot is an ADD affordance, not a socket the user names —
            // renaming its label leaves IsPlaceholder=true under a
            // non-placeholder name, which no longer matches
            // IsManagedPlaceholder, so the slot can never be activated again
            // (a zombie row that presents as "the + slot is dead"). The name
            // is assigned by PlaceholderActivator.Activate at activation;
            // rename the ACTIVATED bubble instead.
            if (s.Model.IsPlaceholder)
            {
                e.Handled = true;
                return;
            }
            // 0.10.0 — baseline snapshot (not undo push). Same Esc-rollback /
            // no-op-skip contract as the inline pill above.
            s.BeginLabelEdit();
            e.Handled = true;
        }
    }

    private void OnLabelEditKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                s.EndLabelEdit(commit: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (s.EndLabelEdit(commit: true))
                {
                    var canvas = GetCanvasCached(sender as DependencyObject);
                    canvas?.PushUndoForInlineEdit();
                    // Payload-socket rename on a paired-event host must reach
                    // the in-graph peers AND sibling .phxg files. Names of the
                    // hosting node stay decoupled — the sync only reshapes
                    // payload bubbles on matching peers; the host's own
                    // "EventName" is untouched.
                    MaybeNotifyEventSocketRenamed(canvas, s);
                }
                e.Handled = true;
            }
        }
    }

    private void OnLabelEditLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
        {
            if (s.EndLabelEdit(commit: true))
            {
                var canvas = GetCanvasCached(sender as DependencyObject);
                canvas?.PushUndoForInlineEdit();
                // Same propagation as the Enter-commit path —
                // focus-loss is the second commit edge that has to
                // honour the payload-sync contract.
                MaybeNotifyEventSocketRenamed(canvas, s);
            }
        }
    }

    /// <summary>
    /// When a socket-label rename commits AND the hosting node is an
    /// Event-pair role (<c>Event.Trigger</c> / <c>Event.Executor</c> /
    /// <c>Event.Return</c>), run the canvas's full rename tail: IN-GRAPH
    /// pair sync + peer view rebuild + debounced cross-file sync. Pre-fix
    /// only the cross-file half fired — the in-graph peers kept the old
    /// bubble name and the next pair-sync from any of them stomped the
    /// rename right back ("bubble names are constantly resetted"). The sync
    /// is already a no-op for non-event hosts upstream, but gating here
    /// keeps the call site honest about which renames need to propagate.
    /// </summary>
    private static void MaybeNotifyEventSocketRenamed(LogicCanvasView? canvas, SocketViewModel s)
    {
        if (canvas is null) return;
        // The hosting node-view-model is reachable via the parent on
        // SocketViewModel — but the VM only exposes the raw Node. Re-derive
        // the role from Node.Title to keep this check at the model edge,
        // matching LogicCanvasView's own LinkTouchesEventPair tests.
        var host = s.ParentNode;
        if (host?.Title is "Event.Trigger" or "Event.Executor" or "Event.Return")
            canvas.NotifyEventSocketRenamedFromNodeView(host);
    }

    /// <summary>
    /// When a middle-attribute pill commit changes the <c>EventName</c> definer on
    /// an <c>Event.Trigger</c> / <c>Event.Executor</c> / <c>Event.Return</c> node,
    /// renaming the event changes its cross-file pairing identity — so the node must
    /// re-sync its payload-socket shape to the (newly matching) peers in-graph, in
    /// other open windows, and on disk, and refresh the unpaired red-border state.
    /// The socket-label rename path has
    /// <see cref="MaybeNotifyEventSocketRenamed"/>; the EventName pill had no
    /// equivalent, so renaming an event silently stopped Event-pair sync from firing
    /// (the old peer kept mismatched sockets until an unrelated edit). Names stay
    /// decoupled (signal/slot) — only socket SHAPE follows the pairing.
    /// </summary>
    private static void MaybeNotifyEventNameChanged(LogicCanvasView? canvas, MiddleAttributeViewModel m)
    {
        if (canvas is null) return;
        if (m.Key != "EventName") return;
        if (m.ParentNode?.Title is "Event.Trigger" or "Event.Executor" or "Event.Return")
            canvas.NotifyEventNameChangedFromNodeView(m.ParentNode);
    }

    // ─── Socket-label hover tracking ───────────────────────
    // Pointer-driven targeting for the UserControl-level F2 accelerator: the
    // most recently entered socket-label captures F2 (alongside double-tap)
    // and falls through to title rename when no socket is hovered.

    private void OnSocketLabelPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
            _hoveredSocketVm = s;
    }

    private void OnSocketLabelPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s
            && ReferenceEquals(_hoveredSocketVm, s))
            _hoveredSocketVm = null;
    }

    // ─── Inline node-title rename ───────────────────────────
    // Same baseline-snapshot pattern as the socket-label rename above —
    // BeginTitleEdit() captures the rollback baseline on the VM, Esc
    // restores it, Enter / focus-loss commits, empty-title commits are
    // logged + rolled back (no modal for repeatable interactions).

    private void OnTitleDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (DataContext is NodeViewModel vm)
        {
            vm.BeginTitleEdit();
            e.Handled = true;
        }
    }

    private void OnTitleEditKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (DataContext is NodeViewModel vm)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                vm.EndTitleEdit(commit: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (vm.EndTitleEdit(commit: true))
                    GetCanvasCached(sender as DependencyObject)?.PushUndoForInlineEdit();
                e.Handled = true;
            }
        }
    }

    private void OnTitleEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is NodeViewModel vm)
        {
            if (vm.EndTitleEdit(commit: true))
                GetCanvasCached(sender as DependencyObject)?.PushUndoForInlineEdit();
        }
    }

    /// <summary>
    /// UserControl-level F2 KeyboardAccelerator. Routes
    /// to the hovered socket label when one is tracked, otherwise
    /// enters title rename on the host node. Double-tap is the mouse
    /// entry; F2 is the keyboard parity affordance — the two paths share the
    /// VM's BeginLabelEdit / BeginTitleEdit so commit / rollback semantics
    /// stay identical regardless of which trigger started the edit.
    /// </summary>
    private void OnF2RenameAccelerator(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        // Accelerators are window-scoped and fire regardless of keyboard
        // focus — F2 while the user is typing in a pill / rename box /
        // inspector field must not yank the session into a different rename
        // (same focus-gate contract as the chrome menu accelerators). Also gate
        // on InlineEditGate: FocusManager doesn't reliably report the canvas
        // pill's TextBox under XAML-Islands hosting, so focus alone let F2 leak.
        if (TextInputFocusGuard.IsTextInputFocused(XamlRoot) || InlineEditGate.IsActive)
        {
            args.Handled = true;
            return;
        }
        // Prefer the hovered socket — that's the surface the user is
        // looking at, mirroring the double-tap target. Managed "+" slots are
        // excluded for the same reason as the double-tap path: renaming a
        // placeholder label zombifies the slot (IsPlaceholder stays true
        // under a non-placeholder name → IsManagedPlaceholder never matches
        // → the slot can't be activated again).
        if (_hoveredSocketVm is { } socket && !socket.Model.IsPlaceholder)
        {
            socket.BeginLabelEdit();
            args.Handled = true;
            return;
        }
        // Fall-through: rename the title.
        if (DataContext is NodeViewModel vm)
        {
            vm.BeginTitleEdit();
            args.Handled = true;
        }
    }

    // ─── Middle-attribute row editing ────────────────────────────────────
    // Mirrors the socket-pill edit/commit cycle for `DefaultProperties` keys
    // that have NO matching input socket on the node (Design_Orders §5.1 —
    // Key …… [pill] / Key …… ☑/☐). Required so the ~20 templates whose
    // inline-only state was previously inspector-only get authorable on the
    // node body.

    private void OnMiddleAttrPillTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MiddleAttributeViewModel m)
        {
            m.BeginEdit();
            TryFocusInlineEditorNow(sender);
            e.Handled = true;
        }
    }

    private void OnMiddleAttrPillEditKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MiddleAttributeViewModel m)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                m.EndEdit(commit: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                bool ctrl = IsCtrlDown();
                if (ctrl || !m.IsMultiline)
                {
                    if (m.EndEdit(commit: true))
                    {
                        var canvas = GetCanvasCached(sender as DependencyObject);
                        canvas?.PushUndoForInlineEdit();
                        MaybeNotifyEventNameChanged(canvas, m);
                    }
                    e.Handled = true;
                    return;
                }
                // Multi-line + bare Enter: fall through so AcceptsReturn inserts \n.
            }
        }

        // Ctrl+Space opens the variable-picker flyout, matching the
        // socket-pill path (OnPillEditKeyDown). Middle-attribute pills
        // (Template / Script / Payload, …) are commonly multi-line and carry
        // {variable} tokens, so authors expect the same insert-a-{name}
        // affordance. ShowVarPicker is re-entrant and resolves the picker pool
        // from the loaded graph; the host TextBox is the insertion target.
        if (e.Key == Windows.System.VirtualKey.Space && IsCtrlDown() && sender is TextBox tb)
        {
            ShowVarPicker(tb);
            e.Handled = true;
        }
    }

    private void OnMiddleAttrPillEditLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MiddleAttributeViewModel m)
        {
            if (m.EndEdit(commit: true))
            {
                var canvas = GetCanvasCached(sender as DependencyObject);
                canvas?.PushUndoForInlineEdit();
                MaybeNotifyEventNameChanged(canvas, m);
            }
        }
    }

    /// <summary>
    /// Bool middle-attr glyph tap — flip the stored "true" / "false" value
    /// through MiddleAttributeViewModel.ToggleBool and push undo. Mirrors
    /// the InspectorField.OnBoolHitTapped shape (consumed pattern, NOT lifted
    /// file — a parallel agent owns InspectorField.xaml.cs).
    /// No dialogs
    /// for repeatable interactions; the toggle commits silently.
    /// </summary>
    private void OnMiddleAttrBoolTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MiddleAttributeViewModel m)
        {
            if (m.ToggleBool())
            {
                var canvas = GetCanvasCached(sender as DependencyObject);
                canvas?.PushUndoForInlineEdit();
                // Bool attrs can be semantically load-bearing (the Chat.Message
                // Twitch / YouTube / Kick checkmarks change the exported
                // `on_chat` header), so a toggle must flag the graph dirty —
                // OnGraphMutated raises GraphMutatedAny → ArchitectViewModel
                // IsDirty=true → save/autosave re-exports the .phx. Without it
                // a toggle-only session presented as clean and never saved.
                canvas?.ViewModel?.OnGraphMutated();
            }
            e.Handled = true;
        }
    }
}
