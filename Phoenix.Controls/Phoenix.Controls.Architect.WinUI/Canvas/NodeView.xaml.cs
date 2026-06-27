using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    // ARGB literals (TODO 2026-05-07 P1). Falls back to literal coal/ember
    // if the resource lookup fails — should never happen in production but
    // keeps designers running from raw XBF reloads sane.
    //  These were `static readonly`, resolved once at
    // first construction and never refreshed — a runtime OS light↔dark / high-
    // contrast switch left every node painting stale colours until restart
    // (nothing subscribed to ActualThemeChanged). Now they're refreshable via
    // RefreshThemeBrushes(), called from each NodeView's ActualThemeChanged
    // (guarded to re-resolve once per actual transition); every live instance
    // then repaints its border. Mirrors the Hub SystemLogView ActualThemeChanged
    // fix (commit 04d56a89).
    private static SolidColorBrush s_selectionBrush = ResolveBrush("EmberPrimaryBrush", 0xFF, 0xE5, 0xA2, 0x4E);
    private static SolidColorBrush s_dividerBrush   = ResolveBrush("CoalDividerBrush", 0xFF, 0x3A, 0x31, 0x27);
    //  s_flashBrush retired — the flash is now a
    // saturated-amber FlashOverlay body-tint (NodeView.xaml), not a border brush.
    // Error state — tries the project's ErrBrush key (set by the canvas
    // wire-drop preview path) and falls back to a saturated rust red.
    private static SolidColorBrush s_errorBrush     = ResolveBrush("ErrBrush", 0xFF, 0xCB, 0x4D, 0x3F);
    // Var-chain hover halos — cyan (writers) + amber (readers). 0.10.0 theme P2:
    // resolved from PhoenixDark.xaml so a future palette retune doesn't
    // require editing this code-behind. Fallbacks preserve pre-T15 ARGB
    // values for designer-time / pre-app-construction lookups.
    private static SolidColorBrush s_varWriterBrush = ResolveBrush("VarChainWriterBrush", 0xFF, 0x78, 0xC8, 0xFF);
    private static SolidColorBrush s_varReaderBrush = ResolveBrush("VarChainReaderBrush", 0xFF, 0xFF, 0xB4, 0x50);
    // S4-fix — compact-mode border tint (soft teal, distinct from the cyan
    // var-chain writer halo and the gold selection halo). Lowest-priority
    // branch in ApplyBorder: a compact node that is NOT selected / error /
    // var-chain gets this tint so the compact state is visible at a glance,
    // pairing with the centred CompactSymbol glyph in NodeView.xaml.
    private static SolidColorBrush s_compactBrush   = ResolveBrush("InfoBrush", 0xFF, 0x5F, 0xB8, 0xA6);

    //  Last ElementTheme the static brushes were
    // resolved for. ActualThemeChanged fires per live NodeView, so guard the
    // (shared) re-resolve to run once per actual transition.
    private static ElementTheme s_lastResolvedTheme = ElementTheme.Default;
    private static readonly object s_themeRefreshGate = new();

    /// <summary>
    ///  Re-resolve the shared node-state brushes from
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

    // S29 (P1-A21): the last socket-label the pointer entered. F2 fired on the
    // NodeView routes to this socket when present, otherwise to the node title
    // (P0-A5). PointerExited clears it so a stale hover after the cursor leaves
    // the node body doesn't capture an unintended F2. Per-NodeView state — the
    // hover signal doesn't cross nodes because PointerExited fires before the
    // next node's PointerEntered when crossing the boundary.
    private SocketViewModel? _hoveredSocketVm;

    // PERF (perf/architect-blockers, HIGH): cached so OnPillPointerEntered /
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

    // Per-node flash storyboard cache (Architect UI WIP). Re-flashing a node
    // that's already flashing must Stop() the prior storyboard before
    // starting a new one; otherwise WinUI keeps the previous
    // DoubleAnimationUsingKeyFrames in its active-animation pool and the
    // next flash's opacity ramps are layered on top of the stale ones —
    // visually shows as the flash brush "sticking" at full opacity until
    // both timelines complete.
    private Storyboard? _currentFlashStoryboard;

    public NodeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        //  2026-06-08 — when an inline pill TextBox grows (lots of
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
        //  React to runtime theme / high-contrast
        // switches — re-resolve the shared node brushes and repaint.
        ActualThemeChanged += OnActualThemeChanged;
        // S4-fix — keep the var-chain glow sized to the body as it
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
            //  P2-A3 — honor the persisted __disabled attribute on
            // first paint so reloaded graphs reflect the disabled state
            // immediately rather than waiting for a property-changed bump.
            ApplyDisabledOpacity(vm);
            // 0.11.5 canvas-polish r3 — pin overlay binding. See
            // NodeView.Pins.cs for the rebuild + reposition logic.
            HookPinOverlay(vm);
        }
        else
        {
            UnhookPinOverlay();
        }
    }

    /// <summary>
    ///  P2-A3 — fade the node body when <see cref="NodeViewModel.IsDisabled"/>
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

    // [Tranche-2 virtualization] When true, this view was removed from
    // NodeLayer.Children by the viewport cull (it scrolled off-screen) and will be
    // remounted UNCHANGED when it scrolls back in. Set by the canvas immediately
    // before the cull-driven Children.Remove; cleared on remount / real removal.
    // Without this guard, Children.Remove fires OnUnloaded which tears the view
    // down (VM unbind, pin overlay, tooltip timer, flash) — defeating keep-alive.
    internal bool _isCulling;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // [Tranche-2] Cull-unmount: skip the full teardown so the VM binding, pin
        // overlay, tooltip timer and flash storyboard survive for a clean remount.
        // A REAL removal (node deleted) leaves _isCulling=false → full teardown.
        if (_isCulling) return;
        RunUnloadTeardown();
    }

    /// <summary>
    /// [Tranche-2] The per-view teardown (detach VM binding, unhook the pin
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
            // S4-fix — compact-mode toggle re-runs the border cascade so the
            // teal compact tint paints / clears (RaiseHeaderChanged nudges this).
            e.PropertyName == nameof(NodeViewModel.IsCompactMode))
            ApplyBorder(vm);
        //  P2-A3 — IsDisabled toggles the node-body opacity so the
        // authoring surface reads the node as inert. Border state stays
        // untouched (selection / error halos still paint at full opacity so
        // disabled nodes can still be inspected / re-enabled).
        if (e.PropertyName == nameof(NodeViewModel.IsDisabled))
            ApplyDisabledOpacity(vm);
    }

    /// <summary>
    ///  Atomically snapshot the six shared node-state
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
        //  Flash is no longer part of this border
        // cascade — it's a separate body-tint overlay (UpdateFlashOverlay) so a
        // selected / error / var-chain node keeps its border colour while the
        // execution pulse plays over the body.
        //  Snapshot the shared brushes under the gate
        // so an in-flight theme refresh can't be observed half-applied.
        var br = SnapshotThemeBrushes();
        // arch-perf P1-1 (Fix 3) — NodeRoot stays a CONSTANT 1px divider body
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
            // S4-fix — compact mode reads as a distinct teal-tinted ring so it's
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
    /// S4-fix (BOTH-RUNS) — paint the var-chain outer glow halo behind
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
        //  Snapshot under the gate so a concurrent
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
    /// S4-fix — size the glow to the body + 8px (4px each side, paired with
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
    ///  Drive the FlashOverlay body-tint pulse,
    /// independent of the border cascade so the selection / error halo stays
    /// visible while a node executes. Saturated amber, ~420ms ramp
    /// (120 ease-out fade-in, 180 hold, 120 ease-in fade-out). Re-firing while a
    /// pulse is mid-ramp (tight loop) Stop()s the prior storyboard first so the
    /// opacity timelines don't stack.
    /// </summary>
    private const double FlashPeakOpacity = 0.42;
    private void UpdateFlashOverlay(NodeViewModel vm)
    {
        if (FlashOverlay is null) return;
        if (vm.IsExecutingFlash)
        {
            _currentFlashStoryboard?.Stop();
            FlashOverlay.Opacity = 0;
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
            try { sb.Begin(); } catch { /* design-time / pre-realised tree */ }
        }
        else
        {
            if (_currentFlashStoryboard is not null)
            {
                try { _currentFlashStoryboard.Stop(); } catch { /* unloaded tree */ }
                _currentFlashStoryboard = null;
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
            //  Snapshot under the gate so a concurrent
            // theme refresh can't be observed half-applied mid-paint.
            var br = SnapshotThemeBrushes();
            // arch-perf P1-1 (Fix 3) — non-VM fallback: NodeRoot stays a constant
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

    //  The inline-edit TextBoxes (value pill, socket-
    // label rename, node-title rename, middle-attr) toggle Visibility on
    // IsEditing / IsRenaming / IsTitleRenaming — they stay in the visual tree,
    // so a one-shot Loaded never re-fires per edit. Register a visibility watcher
    // so each time an editor becomes visible it grabs focus + selects-all,
    // honouring the "tap → type replaces" contract (pre-fix the box showed
    // unfocused: the first keypress inserted instead of replacing, and a second
    // click was needed to place the caret).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, object> s_inlineEditorWatched = new();
    private static readonly object s_inlineEditorSentinel = new();

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

    private static void FocusInlineEditor(TextBox tb)
    {
        // Defer to let the visibility change + layout settle before focusing,
        // otherwise Focus on a just-shown (previously-collapsed) element can
        // no-op. SelectAll so typing replaces the existing value.
        //
        //  On a pointer-captured canvas (the pill is
        // tapped while the canvas still owns pointer capture) Focus() can silently
        // return false; SelectAll() on an unfocused box then no-ops and the first
        // keystroke inserts instead of replacing. Honour the Focus() result: only
        // SelectAll() when it succeeded, and on failure re-enqueue ONE guarded
        // retry so capture has a frame to release. The retry flag prevents a loop.
        void Apply(bool retried)
        {
            try
            {
                if (tb.Visibility != Visibility.Visible) return;
                if (tb.Focus(FocusState.Programmatic))
                {
                    tb.SelectAll();
                }
                else if (!retried)
                {
                    var rq = tb.DispatcherQueue;
                    if (rq is not null) rq.TryEnqueue(() => Apply(retried: true));
                }
            }
            catch { /* designer-time / detached */ }
        }
        var dq = tb.DispatcherQueue;
        if (dq is null) Apply(retried: false); else dq.TryEnqueue(() => Apply(retried: false));
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
    /// [perf/win2d-immediate-canvas] Realize + focus the inline editor (value pill or
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
            // rewound past the edit (Architect UX review P0-1).
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
                    if (tables.Count == 0)
                    {
                        flyout.Items.Add(new MenuFlyoutItem
                        {
                            Text = "(no tables in databank)",
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
                            Text = "(set TableName first)",
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
                        var rowidItem = new MenuFlyoutItem { Text = "rowid (row id)" };
                        rowidItem.Click += (_, _) => s.ValuePill = "rowid";
                        flyout.Items.Add(rowidItem);
                    }
                    if (schema.Count == 0)
                    {
                        flyout.Items.Add(new MenuFlyoutItem
                        {
                            Text = $"(no columns in {tableName})",
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
                    Text = "(databank unavailable)",
                    IsEnabled = false,
                });
            }
            catch { /* flyout already disposed */ }
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "NodeView", "PopulateDatabankPickerAsync", ex);
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
        //  Skip the VM write (and its var-chain
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
                        FindAncestor<LogicCanvasView>(sender as DependencyObject)?.PushUndoForInlineEdit();
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
        // B19 (audit/winui-regressions-2026-05-24) — capture the pin's
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

        // B19 — apply type filter when we know the pin's DataType. Non-
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

        // B20 (audit/winui-regressions-2026-05-24) — pre-select the first
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

        // B20 — handle Enter/Escape on the ListView. WinUI's ListView
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
            // B20 — focus the ListView on open so arrow keys + Enter land
            // there. Pre-fix the picker opened with keyboard focus still
            // on the TextBox and Up/Down moved the caret instead of
            // walking the picker rows.
            try { list.Focus(FocusState.Programmatic); }
            catch { /* best-effort */ }
        };

        flyout.ShowAt(host);
    }

    /// <summary>
    /// B19 (audit/winui-regressions-2026-05-24) — filter the picker pool
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
    /// B19 — map the <see cref="Phoenix.Controls.Shared.Models.VariableDefinition.Type"/>
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
        Add("global.", "namespace");
        Add("user.",   "namespace");
        Add("state.",  "namespace");
        foreach (var sys in new[] { "time","hours","minutes","seconds","unix","date","day","month","monthname","year" })
            Add("system." + sys, "system");

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
                        Add(vn.Trim(), "var");
                    break;
                case "Public.Get":
                case "Public.Set":
                    if (n.Attributes.TryGetValue("KeyName", out var kn) && !string.IsNullOrWhiteSpace(kn))
                        Add("public." + kn.Trim(), "public");
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
                foreach (var t in scoped) Add(t, "scope");
            }
            catch { /* scope builder is best-effort — never break the picker on fault */ }
        }

        // 4) Graph-level Variables list (still surfaced — power users define vars
        //    here and refer to them in pill content).
        foreach (var v in graph.Variables)
            Add(v.Name, $"graph · {v.Type}");

        return pool;
    }

    /// <summary>Two-column row template: token name + dim source tag.</summary>
    ///
    /// PERF (perf/architect-blockers, HIGH): cached as a static readonly. Pre-cache
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

    /// <summary>One-line: variable name + tiny type tag. Built in code-behind to
    /// avoid a XAML resource dependency on the canvas namespace from NodeView's scope.</summary>
    private static readonly DataTemplate s_varPickerTemplate
        = LoadVarPickerTemplate();
    private static DataTemplate BuildVarPickerTemplate() => s_varPickerTemplate;

    private static DataTemplate LoadVarPickerTemplate()
    {
        // x:DataType stripped for the same reason as the autocomplete
        // template above — XamlReader.Load doesn't accept compile-time
        // attributes and the {Binding} expressions below don't need it.
        var xaml = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Grid Padding=""6,4"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""*"" />
      <ColumnDefinition Width=""Auto"" />
    </Grid.ColumnDefinitions>
    <TextBlock Text=""{Binding Name}"" FontFamily=""{StaticResource MonoFont}"" FontSize=""11"" />
    <TextBlock Grid.Column=""1"" Text=""{Binding Type}"" Margin=""8,0,0,0""
               Foreground=""{StaticResource CoalSecondaryTextBrush}""
               FontFamily=""{StaticResource MonoFont}"" FontSize=""9"" />
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
                FindAncestor<LogicCanvasView>(sender as DependencyObject)?.PushUndoForInlineEdit();
        }
    }

    // ─── Inline socket-name rename ───────────────────────────────────────
    // Per feedback_node_ui_inline_sockets — UE-Blueprints style, sockets are
    // renamed on the node body. Double-tap label → TextBox, Enter/Esc/blur
    // commits via TwoWay binding back into SocketViewModel.Label which writes
    // through to Socket.Name.

    private void OnLabelDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SocketViewModel s)
        {
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
                    var canvas = FindAncestor<LogicCanvasView>(sender as DependencyObject);
                    canvas?.PushUndoForInlineEdit();
                    // S29 (P1-A6): payload-socket rename on a paired-event host
                    // must propagate to sibling .phxg files. Names of the
                    // hosting node stay decoupled (per feedback_event_pair_socket_sync.md)
                    // — the cross-file sync only reshapes payload bubbles on
                    // matching peers; the host's own "EventName" is untouched.
                    MaybeScheduleCrossFileEventPairSync(canvas, s);
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
                var canvas = FindAncestor<LogicCanvasView>(sender as DependencyObject);
                canvas?.PushUndoForInlineEdit();
                // S29 (P1-A6): same cross-file propagation as the Enter-commit
                // path — focus-loss is the second commit edge that has to
                // honour the payload-sync contract.
                MaybeScheduleCrossFileEventPairSync(canvas, s);
            }
        }
    }

    /// <summary>
    /// S29 (P1-A6): when a socket-label rename commits AND the hosting node
    /// is an Event-pair role (<c>Event.Trigger</c> / <c>Event.Executor</c>),
    /// fire the canvas's debounced cross-file sync so every sibling .phxg's
    /// matching peer ends up with the new payload-socket name. The sync is
    /// already a no-op for non-event hosts upstream, but gating here keeps
    /// the call site honest about which renames need to cross file boundaries.
    /// </summary>
    private static void MaybeScheduleCrossFileEventPairSync(LogicCanvasView? canvas, SocketViewModel s)
    {
        if (canvas is null) return;
        // The hosting node-view-model is reachable via the parent on
        // SocketViewModel — but the VM only exposes the raw Node. Re-derive
        // the role from Node.Title to keep this check at the model edge,
        // matching LogicCanvasView's own LinkTouchesEventPair tests.
        var hostTitle = s.ParentNode?.Title;
        if (hostTitle is "Event.Trigger" or "Event.Executor")
            canvas.RequestCrossFileEventPairSyncFromNodeView();
    }

    // ─── Socket-label hover tracking (S29 P1-A21) ───────────────────────
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

    // ─── Inline node-title rename (S29 P0-A5) ───────────────────────────
    // Same baseline-snapshot pattern as the socket-label rename above —
    // BeginTitleEdit() captures the rollback baseline on the VM, Esc
    // restores it, Enter / focus-loss commits, empty-title commits are
    // logged + rolled back (no modal per feedback_no_modal_dialogs_for_repeatable_rejections.md).

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
                    FindAncestor<LogicCanvasView>(sender as DependencyObject)?.PushUndoForInlineEdit();
                e.Handled = true;
            }
        }
    }

    private void OnTitleEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is NodeViewModel vm)
        {
            if (vm.EndTitleEdit(commit: true))
                FindAncestor<LogicCanvasView>(sender as DependencyObject)?.PushUndoForInlineEdit();
        }
    }

    /// <summary>
    /// S29 (P0-A5 / P1-A21): UserControl-level F2 KeyboardAccelerator. Routes
    /// to the hovered socket label (P1-A21) when one is tracked, otherwise
    /// enters title rename on the host node (P0-A5). Double-tap is the mouse
    /// entry; F2 is the keyboard parity affordance — the two paths share the
    /// VM's BeginLabelEdit / BeginTitleEdit so commit / rollback semantics
    /// stay identical regardless of which trigger started the edit.
    /// </summary>
    private void OnF2RenameAccelerator(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        // Prefer the hovered socket — that's the surface the user is
        // looking at, mirroring the double-tap target.
        if (_hoveredSocketVm is { } socket)
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
    // inline-only state was inspector-only pre-QC45 get authorable on the
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
                        FindAncestor<LogicCanvasView>(sender as DependencyObject)?.PushUndoForInlineEdit();
                    e.Handled = true;
                    return;
                }
                // Multi-line + bare Enter: fall through so AcceptsReturn inserts \n.
            }
        }

        // S4-fix — Ctrl+Space opens the variable-picker flyout, matching the
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
                FindAncestor<LogicCanvasView>(sender as DependencyObject)?.PushUndoForInlineEdit();
        }
    }

    /// <summary>
    /// Bool middle-attr glyph tap — flip the stored "true" / "false" value
    /// through MiddleAttributeViewModel.ToggleBool and push undo. Mirrors
    /// the InspectorField.OnBoolHitTapped shape (consumed pattern, NOT lifted
    /// file — a parallel agent owns InspectorField.xaml.cs per QC19).
    /// Per feedback_no_modal_dialogs_for_repeatable_rejections.md: no dialogs
    /// for repeatable interactions; the toggle commits silently.
    /// </summary>
    private void OnMiddleAttrBoolTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MiddleAttributeViewModel m)
        {
            if (m.ToggleBool())
                FindAncestor<LogicCanvasView>(sender as DependencyObject)?.PushUndoForInlineEdit();
            e.Handled = true;
        }
    }
}
