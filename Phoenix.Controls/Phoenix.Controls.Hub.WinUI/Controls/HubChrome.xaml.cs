// 0.10.0 redesign R2 — `winui::` disambiguates Windows.UI.Text.TextDecorations
// between Microsoft.WinUI.dll and Microsoft.Windows.SDK.NET.dll (CS0433).
// Both assemblies project the same enum, and the unqualified reference is
// ambiguous; the project file tags Microsoft.WinUI's resolved reference with
// `global,winui` so the rest of this file's WinUI types keep working
// unaliased while the access-key underline below pulls the enum value
// explicitly from Microsoft.WinUI.
extern alias winui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using System;
using System.Collections.Generic;
using System.Reflection;
using Windows.System;

namespace Phoenix.Controls.Hub.WinUI.Controls;

public sealed partial class HubChrome : UserControl
{
    public event EventHandler<MenuItemInvokedEventArgs>? MenuItemInvoked;
    public event EventHandler<PillarKind>? PillarTabClicked;
    // 4th tab — distinct from the PillarKind surface-swap tabs. Clicking it
    // opens the Giveaway page in an independent pop-out window (MainWindow
    // owns the single-instance window lifecycle). Kept off PillarKind so the
    // enum's load-bearing ordinals stay limited to the three swap surfaces.
    public event EventHandler? GiveawayTabClicked;
    // Custom traffic-light clicks — surfaced as plain events so MainWindow
    // can wire each one to the active AppWindow's presenter without the
    // UserControl needing a Window reference.
    public event EventHandler? MinimizeClicked;
    public event EventHandler? MaximizeRestoreClicked;
    public event EventHandler? CloseClicked;

    private PillarKind _activePillar = PillarKind.Hub;

    // Tracked Undo/Redo MenuFlyoutItems for the active pillar's Edit menu.
    // Rebuilt every BuildMenuBar so the references match the live UI tree
    // (MenuBar is rebuilt wholesale on pillar swap). Null when the active
    // pillar's menu has no Undo/Redo (i.e. Hub itself).
    private MenuFlyoutItem? _undoMenuItem;
    private MenuFlyoutItem? _redoMenuItem;

    // M25 (2026-05-14): pre-built per-pillar menu strips. The original code
    // rebuilt the whole MenuBar (Items.Clear + new MenuBarItem + new
    // MenuFlyoutItem * N + Style + Localizer.T + AccessKey hook) on every
    // pillar swap. Pre-building once in the ctor (or first SetPillar) and
    // swapping by reference drops a pillar switch from O(items) allocations
    // to a single Items.Clear + N Add of cached references.
    private sealed class MenuStripBundle
    {
        public IReadOnlyList<MenuBarItem> Items { get; }
        public MenuFlyoutItem? UndoItem { get; }
        public MenuFlyoutItem? RedoItem { get; }
        // 2026-05-22 (arch-bug #ctrl-z-noop) — full accelerator manifest for
        // the bundle. The pre-fix code registered KeyboardAccelerators only
        // on the leaf MenuFlyoutItems, but WinUI 3 doesn't realize those
        // items until the parent flyout is opened at least once — so the
        // accelerator chord registry stayed empty and Ctrl+Z (and friends)
        // did nothing if the user never popped the Edit menu. Mirroring the
        // same accelerators onto a window-wide scope owner (typically the
        // MainWindow's root content) fixes the regression: AcceleratorEntries
        // holds the (chord, menuToken, itemToken) tuples so
        // <see cref="MirrorAcceleratorsTo"/> can rebuild the mirror after
        // every pillar swap.
        public IReadOnlyList<AcceleratorEntry> AcceleratorEntries { get; }
        public MenuStripBundle(IReadOnlyList<MenuBarItem> items,
                               MenuFlyoutItem? undo,
                               MenuFlyoutItem? redo,
                               IReadOnlyList<AcceleratorEntry> accelerators)
        {
            Items = items;
            UndoItem = undo;
            RedoItem = redo;
            AcceleratorEntries = accelerators;
        }
    }

    private sealed record AcceleratorEntry(string Chord, string MenuToken, string ItemToken);

    private MenuStripBundle? _hubMenuBundle;
    private MenuStripBundle? _architectMenuBundle;
    private MenuStripBundle? _visualistMenuBundle;

    // The active pillar's undo/redo source — Architect.MainView /
    // Visualist.MainView are ICanExecuteUndoRedo today. Set by SetPillar
    // *before* BuildMenuBar runs so the initial IsEnabled reflects the
    // live stacks instead of the always-true default. Detached on pillar
    // swap so the chrome doesn't keep a stale subscription pinning the
    // previous pillar's view.
    private ICanExecuteUndoRedo? _undoRedoSource;

    public HubChrome()
    {
        InitializeComponent();

        VersionStamp.Text = ResolveVersionString();

        // M25 (2026-05-14): pre-build all three pillar menu strips up front
        // so SetPillar can swap by reference instead of paying the
        // BuildMenuBar allocation/styling cost on every pillar click. Initial
        // mount uses the Hub bundle by default — _activePillar starts at Hub.
        _hubMenuBundle       = BuildMenuStripBundle(MenuDefinition.Hub);
        _architectMenuBundle = BuildMenuStripBundle(MenuDefinition.Architect);
        _visualistMenuBundle = BuildMenuStripBundle(MenuDefinition.Visualist);
        ApplyMenuBundle(_hubMenuBundle);

        ArchitectTab.Clicked += (_, _) => PillarTabClicked?.Invoke(this, PillarKind.Architect);
        VisualistTab.Clicked += (_, _) => PillarTabClicked?.Invoke(this, PillarKind.Visualist);
        HubTab.Clicked       += (_, _) => PillarTabClicked?.Invoke(this, PillarKind.Hub);

        MinButton.Clicked   += (_, _) => MinimizeClicked?.Invoke(this, EventArgs.Empty);
        MaxButton.Clicked   += (_, _) => MaximizeRestoreClicked?.Invoke(this, EventArgs.Empty);
        CloseButton.Clicked += (_, _) => CloseClicked?.Invoke(this, EventArgs.Empty);

        // Localized label for the 4th (Giveaway) tab — matches the PillarTab
        // localization posture (visible label + screen-reader name follow the
        // active language; the "G" sigil glyph is a typographic affordance,
        // not a translation target).
        GiveawayTabLabel.Text = Localizer.T("pillartab.giveaway.label", "GIVEAWAY");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GiveawayTab,
            string.Format(Localizer.T("pillartab.aria.name_format", "{0} pillar tab"), GiveawayTabLabel.Text));
        ToolTipService.SetToolTip(GiveawayTab,
            Localizer.T("pillartab.giveaway.tooltip", "Giveaway — opens in its own window"));
    }

    // ── Giveaway tab (4th tab) ───────────────────────────────────────────
    // The tab never becomes "active" (it opens a window rather than swapping
    // a surface), so it lives permanently in the inactive PillarTab visual
    // state with a simple hover fill / brighter-label affordance, mirroring
    // PillarTab.OnPointerEntered/Exited's non-active hover. Colors are set
    // directly (no ColorAnimation) — the hover is a binary affordance here.
    private static readonly global::Windows.UI.Color GvColorTransparent = global::Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
    private static readonly global::Windows.UI.Color GvColorCoalHover    = global::Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x25, 0x1D);
    private static readonly global::Windows.UI.Color GvColorCoalBody     = global::Windows.UI.Color.FromArgb(0xFF, 0xA8, 0x96, 0x83);
    private static readonly global::Windows.UI.Color GvColorCoalSecondary = global::Windows.UI.Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72);

    private void OnGiveawayTabPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        GiveawayTab.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(GvColorCoalHover);
        GiveawayTabLabelBrush.Color = GvColorCoalBody;
    }

    private void OnGiveawayTabPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        GiveawayTab.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(GvColorTransparent);
        GiveawayTabLabelBrush.Color = GvColorCoalSecondary;
    }

    private void OnGiveawayTabPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => GiveawayTabClicked?.Invoke(this, EventArgs.Empty);

    private void OnGiveawayTabKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter || e.Key == global::Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            GiveawayTabClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Swap the menu strip to the active pillar's menu set AND update the
    /// pillar-tab IsActive flags so the chrome strip's "raised" tab matches
    /// the visible workspace. Called by MainWindow.OnPillarTabClicked after
    /// the MainPaneRegion content swap. Tokens stay pillar-prefixed
    /// (architect.file.new, visualist.file.openLayer, …) so MainWindow's
    /// dispatch can fan out to the right pillar's MainView.
    ///
    /// <paramref name="undoRedoSource"/> is the active pillar's
    /// <see cref="ICanExecuteUndoRedo"/> handle (typically the pillar's
    /// MainView itself). Pass null for pillars without an undo stack (Hub).
    /// The chrome subscribes to its <c>CanExecuteChanged</c> and re-evaluates
    /// the Edit-menu Undo / Redo IsEnabled state push-style, plus an initial
    /// poll right after BuildMenuBar so the items reflect the current
    /// stacks before the user opens the menu.
    /// </summary>
    public void SetPillar(PillarKind kind, ICanExecuteUndoRedo? undoRedoSource = null)
    {
        // Tab IsActive sync runs unconditionally — tab-strip selection drift
        // was the user-visible regression in TODO 2026-05-07 P0 #3 (the menu
        // bar swapped fine but the Hub tab stayed "raised" forever).
        UpdateTabSelection(kind);

        // Detach the previous undo source first so the swap doesn't fire a
        // CanExecuteChanged into the outgoing bundle's MenuFlyoutItem refs
        // (which RefreshUndoRedoEnabled then resolves against the new
        // bundle's items after ApplyMenuBundle below).
        DetachUndoRedoSource();

        if (_activePillar != kind || HubMenuBar.Items.Count == 0)
        {
            _activePillar = kind;
            // M25 — swap the pre-built MenuBarItems by reference rather than
            // reconstructing the bundle. The first swap after ctor still hits
            // a fresh bundle; subsequent swaps cost only Items.Clear + N Add.
            var bundle = kind switch
            {
                PillarKind.Architect => _architectMenuBundle,
                PillarKind.Visualist => _visualistMenuBundle,
                _                    => _hubMenuBundle,
            };
            if (bundle is not null) ApplyMenuBundle(bundle);
        }

        AttachUndoRedoSource(undoRedoSource);
    }

    private void UpdateTabSelection(PillarKind kind)
    {
        HubTab.IsActive       = kind == PillarKind.Hub;
        ArchitectTab.IsActive = kind == PillarKind.Architect;
        VisualistTab.IsActive = kind == PillarKind.Visualist;
    }

    public string FileName
    {
        get => FilenameText.Text;
        set => FilenameText.Text = value ?? string.Empty;
    }

    /// <summary>
    /// 0.11.x polish — fired when the user clicks the × glyph next to the
    /// filename. MainWindow subscribes and routes the request to the
    /// active pillar (Architect: clear graph back to Welcome; Visualist:
    /// prompt save + start a new layer). EventArgs.Empty so the event
    /// stays cheap; the dispatch site already knows which pillar is
    /// active and what "close" means for it.
    /// </summary>
    public event EventHandler? FileCloseRequested;

    /// <summary>
    /// 0.11.x polish — explicit visibility gate for the close-X glyph. Used
    /// by MainWindow.UpdateChromeFilename so the affordance hides when the
    /// active pillar has no real document loaded (Architect on the Welcome
    /// card, Visualist before its first layer). Mirrors ArchitectChrome's
    /// same-named helper so MainWindow can call into either chrome with
    /// identical semantics.
    /// </summary>
    public void SetCloseFileButtonVisible(bool visible)
    {
        if (CloseFileButton is null) return;
        CloseFileButton.Visibility = visible
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void OnCloseFileClicked(object sender, RoutedEventArgs e)
        => FileCloseRequested?.Invoke(this, EventArgs.Empty);

    private static string ResolveVersionString()
    {
        string version = Phoenix.Controls.Shared.Services.GitInfoService.GetAssemblyVersion();
        var build = string.Equals(
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Release", StringComparison.OrdinalIgnoreCase) ? "RELEASE" : "DEV";
        return $"v{version} — {build}";
    }

    private MenuStripBundle BuildMenuStripBundle(IReadOnlyList<MenuSection> menus)
    {
        // M25 — produces an out-of-tree MenuBarItem list + the
        // undo/redo refs for the bundle. ApplyMenuBundle is responsible for
        // attaching the items to HubMenuBar.Items. Keeping construction and
        // attachment apart is what lets us cache the bundles in the ctor.

        // Branded menu chrome (redesign R2 / chrome.jsx — MenuItem). The
        // styles live in Shared.WinUI/Themes/PhoenixDark.xaml; resolve once
        // outside the hot loop. The MenuFlyoutPresenter style is applied as
        // an implicit subtree style in HubChrome.xaml so the generated
        // submenu picks it up without an explicit per-item assignment
        // (MenuFlyoutItem doesn't expose MenuFlyoutPresenterStyle).
        var barItemStyle    = (Style?)Application.Current.Resources["ChromeMenuBarItemStyle"];
        var flyoutItemStyle = (Style?)Application.Current.Resources["ChromeMenuFlyoutItemStyle"];

        // Ember access-key underline (redesign R2 / chrome.jsx — first
        // character of each menu title rendered with an ember underline).
        // MenuBarItem.Title is a plain string, so the rendering is done
        // by walking the realized visual tree on Loaded and replacing
        // the title TextBlock's Text with a two-Run InlineCollection.
        // The Title property stays set so AccessKey + Automation still
        // pick up the canonical label.
        var emberBrush = (Brush?)Application.Current.Resources["EmberPrimaryBrush"];

        var items = new List<MenuBarItem>(menus.Count);
        MenuFlyoutItem? undo = null;
        MenuFlyoutItem? redo = null;
        var accelEntries = new List<AcceleratorEntry>();

        foreach (var menu in menus)
        {
            var localizedTitle = Localizer.T(menu.LabelKey, menu.MenuToken);
            var bar = new MenuBarItem { Title = localizedTitle };
            if (barItemStyle is not null) bar.Style = barItemStyle;
            if (emberBrush is not null) HookAccessKeyUnderline(bar, localizedTitle, emberBrush);
            foreach (var entry in menu.Items)
            {
                if (entry.IsSeparator)
                {
                    bar.Items.Add(new MenuFlyoutSeparator());
                    continue;
                }
                var item = new MenuFlyoutItem { Text = Localizer.T(entry.LabelKey, entry.ItemToken) };
                if (flyoutItemStyle is not null) item.Style = flyoutItemStyle;
                if (!string.IsNullOrEmpty(entry.Accelerator))
                {
                    // Visible hint stays cosmetic (mirrors what the user sees
                    // on the right edge of the MenuFlyoutItem).  — also
                    // register a real Microsoft.UI.Xaml.Input.KeyboardAccelerator
                    // so the chord actually fires the menu item when the chrome
                    // window has focus. Pre-fix every advertised accelerator
                    // was decorative — Ctrl+S in the menu strip rendered the
                    // text but did nothing on press.
                    item.KeyboardAcceleratorTextOverride = entry.Accelerator;
                    //  Suppress WinUI's auto-generated
                    // accelerator tooltip on the item — the visible chord hint
                    // is the right-aligned KeyboardAcceleratorTextOverride above,
                    // not a floating popup. Default (Auto) pops a chip on
                    // pointer-over/focus; Hidden matches NodeView.xaml's F2.
                    item.KeyboardAcceleratorPlacementMode =
                        Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
                    var accel = TryParseAccelerator(entry.Accelerator);
                    if (accel is not null)
                    {
                        item.KeyboardAccelerators.Add(accel);
                        // 2026-05-22 — capture the accelerator for the
                        // window-wide mirror that MirrorAcceleratorsTo will
                        // attach to the chrome's parent root, so Ctrl+Z (and
                        // friends) fire even when the Edit menu has never
                        // been opened. The per-item accelerator stays as
                        // belt-and-braces in case the mirror's scope owner
                        // is never resolved (headless / detached tests).
                        accelEntries.Add(new AcceleratorEntry(
                            entry.Accelerator, menu.MenuToken, entry.ItemToken));
                    }
                }

                var captured = entry;
                var capturedMenu = menu;
                item.Click += (_, _) => MenuItemInvoked?.Invoke(this,
                    new MenuItemInvokedEventArgs(capturedMenu.MenuToken, captured.ItemToken));
                bar.Items.Add(item);

                // Track undo/redo items by stable token suffix so the IsEnabled
                // refresh path stays language-independent (the visible label
                // ran through Localizer.T above). Pillar-prefixed tokens vary
                // (architect.edit.undo / visualist.edit.undo) — match on the
                // suffix so both are picked up by the same chrome.
                if (captured.ItemToken.EndsWith(".edit.undo", StringComparison.Ordinal))
                    undo = item;
                else if (captured.ItemToken.EndsWith(".edit.redo", StringComparison.Ordinal))
                    redo = item;
            }
            items.Add(bar);
        }

        return new MenuStripBundle(items, undo, redo, accelEntries);
    }

    private void ApplyMenuBundle(MenuStripBundle bundle)
    {
        HubMenuBar.Items.Clear();
        foreach (var bar in bundle.Items) HubMenuBar.Items.Add(bar);
        // Point the live undo/redo refs at the active bundle so
        // RefreshUndoRedoEnabled hits the visible MenuFlyoutItems.
        _undoMenuItem = bundle.UndoItem;
        _redoMenuItem = bundle.RedoItem;
        // 2026-05-22 — refresh the window-wide accelerator mirror so the
        // active pillar's chords (architect.edit.undo vs visualist.edit.undo)
        // resolve correctly. The mirror lives on whichever scope owner
        // MirrorAcceleratorsTo last registered; on first SetPillar before
        // MainWindow wires the mirror this no-ops safely.
        RefreshAcceleratorMirror(bundle);
    }

    // 2026-05-22 (arch-bug #ctrl-z-noop) — window-wide accelerator mirror.
    //
    // Pre-fix: KeyboardAccelerators registered on a MenuFlyoutItem only fire
    // window-wide AFTER the parent MenuFlyout has been opened at least once
    // (WinUI 3 doesn't realize the items until the flyout opens, so the
    // accelerator chord registry stays empty before then). Result: Ctrl+Z
    // was a no-op on a fresh launch until the user clicked Edit menu.
    //
    // Fix: re-attach the same KeyboardAccelerators to a top-level UIElement
    // (the MainWindow's root content). UIElement-level accelerators fire
    // window-wide regardless of focus, so the chord resolves whether the
    // user is in the canvas, in the chrome menu strip, or in a textbox
    // somewhere in the workspace. We rebuild on every ApplyMenuBundle so
    // Architect ↔ Visualist ↔ Hub swaps point the mirror at the right
    // pillar's tokens.
    private UIElement? _acceleratorMirrorScope;
    private readonly List<KeyboardAccelerator> _mirroredAccelerators = new();

    /// <summary>
    /// Register the chrome's menu accelerators on a window-wide scope owner.
    /// MainWindow calls this once at startup with its root Grid so chord
    /// invocations (Ctrl+Z, Ctrl+S, Ctrl+O, etc.) fire from anywhere in the
    /// window. Idempotent — calling again clears the previous mirror and
    /// re-attaches against the new scope.
    /// </summary>
    public void MirrorAcceleratorsTo(UIElement scopeRoot)
    {
        if (scopeRoot is null) return;
        // Detach any previous mirror first so a re-call doesn't leak
        // accelerators on the old scope.
        ClearAcceleratorMirror();
        _acceleratorMirrorScope = scopeRoot;
        //  The mirror scope is the window-root UIElement, so
        // its accelerators fire window-wide — but WinUI's default placement also
        // pops the chord's tooltip ("Ctrl+L", …) on pointer-over / focus ANYWHERE
        // in the window, which is the stray chip Majo saw drifting over the canvas
        // while moving the mouse / panning. Hidden suppresses that surface; the
        // chords still fire and the in-menu hints are unaffected. This is the
        // load-bearing half of the fix — the per-item PlacementMode above is
        // belt-and-braces for the realized-menu path.
        scopeRoot.KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        // Re-apply the currently-active bundle so the first MirrorAcceleratorsTo
        // call doesn't have to wait for the next pillar swap.
        var bundle = _activePillar switch
        {
            PillarKind.Architect => _architectMenuBundle,
            PillarKind.Visualist => _visualistMenuBundle,
            _                    => _hubMenuBundle,
        };
        if (bundle is not null) RefreshAcceleratorMirror(bundle);
    }

    private void RefreshAcceleratorMirror(MenuStripBundle bundle)
    {
        if (_acceleratorMirrorScope is null) return;
        ClearAcceleratorMirror();
        foreach (var entry in bundle.AcceleratorEntries)
        {
            var accel = TryParseAccelerator(entry.Chord);
            if (accel is null) continue;
            var captured = entry;
            accel.Invoked += (_, args) =>
            {
                args.Handled = true;
                MenuItemInvoked?.Invoke(this,
                    new MenuItemInvokedEventArgs(captured.MenuToken, captured.ItemToken));
            };
            _acceleratorMirrorScope.KeyboardAccelerators.Add(accel);
            _mirroredAccelerators.Add(accel);
        }
    }

    private void ClearAcceleratorMirror()
    {
        if (_acceleratorMirrorScope is null || _mirroredAccelerators.Count == 0)
        {
            _mirroredAccelerators.Clear();
            return;
        }
        foreach (var accel in _mirroredAccelerators)
        {
            try { _acceleratorMirrorScope.KeyboardAccelerators.Remove(accel); }
            catch { /* scope torn down — best effort */ }
        }
        _mirroredAccelerators.Clear();
    }

    /// <summary>
    /// Inject the ember access-key underline on the MenuBarItem's title once
    /// it's realized. WinUI 3's default MenuBarItem template renders Title
    /// through an internal TextBlock — we walk the visual tree after Loaded,
    /// locate the first descendant whose Text matches the localized title,
    /// and swap its Text for a two-Run InlineCollection (first char
    /// underlined + ember-coloured, rest in inherited colour). If the
    /// TextBlock isn't found (future template changes) the menu falls back
    /// to the plain Title rendering — no functional regression.
    /// </summary>
    private static void HookAccessKeyUnderline(MenuBarItem bar, string title, Brush emberBrush)
    {
        if (string.IsNullOrEmpty(title)) return;

        // M25 (2026-05-14): kept subscribed for the bar's lifetime instead
        // of auto-removing after the first fire. Pre-built bundles get
        // detached from HubMenuBar and re-attached on pillar swap; WinUI
        // may re-realize the template on re-attach, which discards the
        // injected Inlines. Re-applying on every Loaded keeps the ember
        // first-character emphasis intact across swaps.
        void Apply(object? _, RoutedEventArgs __)
        {
            try
            {
                var tb = FindTitleTextBlock(bar, title);
                if (tb is null) return;
                // No-op if the Inlines already match — first Run already
                // ember-coloured. Cheap to verify, cheap to skip.
                if (tb.Inlines.Count == 2
                    && tb.Inlines[0] is Run first
                    && ReferenceEquals(first.Foreground, emberBrush)) return;
                tb.Inlines.Clear();
                // Ember-coloured + ember-underlined first character. Mirrors
                // chrome.jsx's CSS `text-decoration: underline;
                // text-decoration-color: var(--ember-300)`. The `winui::`
                // prefix forces the Microsoft.WinUI projection of
                // Windows.UI.Text.TextDecorations so the value is type-
                // compatible with Run.TextDecorations (also from
                // Microsoft.WinUI). The duplicate definition in
                // Microsoft.Windows.SDK.NET trips CS0433 without the alias.
                var accent = new Run
                {
                    Text = title.Substring(0, 1),
                    Foreground = emberBrush,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
                accent.TextDecorations = winui::Windows.UI.Text.TextDecorations.Underline;
                tb.Inlines.Add(accent);
                if (title.Length > 1)
                    tb.Inlines.Add(new Run { Text = title.Substring(1) });
            }
            catch
            {
                // Visual-tree shape is implementation-defined; if anything
                // throws, fall back to the plain title rendering and move on.
            }
        }
        bar.Loaded += Apply;
    }

    private static TextBlock? FindTitleTextBlock(DependencyObject root, string title)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && string.Equals(tb.Text, title, StringComparison.Ordinal))
                return tb;
            var deeper = FindTitleTextBlock(child, title);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private void AttachUndoRedoSource(ICanExecuteUndoRedo? source)
    {
        _undoRedoSource = source;
        if (source is not null)
            source.CanExecuteChanged += OnSourceCanExecuteChanged;
        // Initial sync so the menu items match the live stacks the moment
        // the user opens the Edit menu — without it, undo on a freshly-loaded
        // graph would render enabled until the first Push fired the event.
        RefreshUndoRedoEnabled();
    }

    private void DetachUndoRedoSource()
    {
        if (_undoRedoSource is not null)
            _undoRedoSource.CanExecuteChanged -= OnSourceCanExecuteChanged;
        _undoRedoSource = null;
    }

    private void OnSourceCanExecuteChanged(object? sender, EventArgs e)
    {
        // Marshal to UI thread — the source may raise on whichever thread
        // mutated the stack (e.g. graph load runs off the UI thread for the
        // initial decode). Perf-review H1: HasThreadAccess fast-path skips
        // the dispatcher hop on UI-thread-originated changes.
        if (DispatcherQueue is null) { RefreshUndoRedoEnabled(); return; }
        if (DispatcherQueue.HasThreadAccess) RefreshUndoRedoEnabled();
        else DispatcherQueue.TryEnqueue(RefreshUndoRedoEnabled);
    }

    private void RefreshUndoRedoEnabled()
    {
        bool canUndo = _undoRedoSource?.CanUndo ?? false;
        bool canRedo = _undoRedoSource?.CanRedo ?? false;
        if (_undoMenuItem is not null) _undoMenuItem.IsEnabled = canUndo;
        if (_redoMenuItem is not null) _redoMenuItem.IsEnabled = canRedo;
    }

    /// <summary>
    ///  — parse a MenuDefinition accelerator string (e.g. "Ctrl+S",
    /// "Ctrl+Shift+S", "Ctrl+,", "F", "F1") into a real
    /// <see cref="KeyboardAccelerator"/>. Returns null when the spec is
    /// empty, malformed, or intentionally skipped (Alt+F4 — WinUI handles
    /// system close natively, so registering a duplicate accelerator would
    /// double-fire / fight the native chord).
    /// </summary>
    /// <remarks>
    /// Last token is the key, preceding tokens are modifiers. Letter / digit /
    /// F-key / named tokens go through <see cref="Enum.TryParse{TEnum}(string,bool,out TEnum)"/>
    /// for VirtualKey resolution; the literal "," falls back to VK_OEM_COMMA
    /// (188) because it isn't a named member of VirtualKey. Parse failures
    /// log at Debug level and return null — a typo in MenuDefinition reduces
    /// to a missing chord, never a crash.
    /// </remarks>
    private static KeyboardAccelerator? TryParseAccelerator(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        try
        {
            var tokens = spec.Split('+', StringSplitOptions.RemoveEmptyEntries
                                       | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) return null;

            var keyToken = tokens[^1];
            var modifiers = VirtualKeyModifiers.None;
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                switch (tokens[i].ToLowerInvariant())
                {
                    case "ctrl":  modifiers |= VirtualKeyModifiers.Control;  break;
                    case "shift": modifiers |= VirtualKeyModifiers.Shift;    break;
                    case "alt":   modifiers |= VirtualKeyModifiers.Menu;     break;
                    case "win":   modifiers |= VirtualKeyModifiers.Windows;  break;
                    default:
                        GlobalLogger.Log(
                            $"HubChrome accelerator '{spec}' — unknown modifier '{tokens[i]}'; skipped",
                            "HubChrome", LogLevel.Debug);
                        return null;
                }
            }

            VirtualKey key;
            if (keyToken == ",")
            {
                //  VK_OEM_COMMA (0xBC = 188) has no named VirtualKey
                // member. A KeyboardAccelerator { Key = (VirtualKey)188 } is accepted at
                // build time, but WinUI's NATIVE accelerator validation rejects the
                // undefined-enum key when the owning MenuFlyoutItem is REALIZED (the
                // first time the menu opens) — a 0xc000027b / E_FAIL fail-fast inside
                // microsoft.ui.xaml.dll that bypasses App.UnhandledException and every
                // log sink, hard-crashing Hub the instant the Tools menu is opened
                // (Settings is the only menu item in the whole suite carrying an
                // OEM/punctuation chord, which is why the crash was Tools-specific and
                // deterministic — confirmed from the WER crash dumps). Follow the Alt+F4
                // precedent below: keep the visible "Ctrl+," hint (the caller sets
                // KeyboardAcceleratorTextOverride regardless of this return) but DO NOT
                // wire a real accelerator. The menu item still opens Settings on click.
                // Re-enabling the chord would need a key WinUI's validator accepts, not
                // an undefined OEM cast.
                return null;
            }
            else if (!Enum.TryParse<VirtualKey>(keyToken, ignoreCase: true, out key))
            {
                GlobalLogger.Log(
                    $"HubChrome accelerator '{spec}' — could not resolve key token '{keyToken}'; skipped",
                    "HubChrome", LogLevel.Debug);
                return null;
            }

            // Alt+F4 — WinUI / the OS already handles system close on this
            // chord. Registering our own KeyboardAccelerator would either
            // double-invoke (menu item + native close) or fight the native
            // chord depending on focus. The parser must still tolerate the
            // spec (MenuDefinition.Hub advertises it for the visible hint),
            // so we accept it but return null to skip wiring.
            if (modifiers == VirtualKeyModifiers.Menu && key == VirtualKey.F4)
                return null;

            return new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"HubChrome accelerator '{spec}' — parse failed: {ex.Message}",
                "HubChrome", LogLevel.Debug);
            return null;
        }
    }
}

/// <summary>
/// Menu invocation event — payload carries stable language-independent
/// tokens (e.g. "file"/"openLogicFolder") so MainWindow's dispatch
/// switches don't break when Localizer hands the chrome a translated
/// label string.
/// </summary>
public sealed record MenuItemInvokedEventArgs(string Menu, string Item);

internal sealed record MenuEntry(string ItemToken, string LabelKey, string? Accelerator = null, bool IsSeparator = false)
{
    public static MenuEntry Sep { get; } = new(string.Empty, string.Empty, IsSeparator: true);
}

internal sealed record MenuSection(string MenuToken, string LabelKey, IReadOnlyList<MenuEntry> Items);

internal static class MenuDefinition
{
    // Mirrors redesign-plan/design/project/chrome.jsx APP_MENUS["hub"].
    // MenuToken / ItemToken are stable English-language IDs that survive
    // localization (MainWindow.OnMenuItemInvoked dispatches on them);
    // LabelKey points into Phoenix.Controls.Shared/lang/{en,de,fr,es}.json
    // for the user-visible text.
    public static IReadOnlyList<MenuSection> Hub { get; } = new[]
    {
        new MenuSection("file", "chrome.menu.file", new MenuEntry[]
        {
            new("openLogicFolder", "chrome.hub.file.openLogicFolder", "Ctrl+L"),
            new("openAssetsFolder", "chrome.hub.file.openAssetsFolder", "Ctrl+K"),
            new("openActionPackFolder", "chrome.hub.file.openActionPackFolder"),
            MenuEntry.Sep,
            new("exit", "chrome.hub.file.exit", "Alt+F4"),
        }),
        new MenuSection("view", "chrome.menu.view", new MenuEntry[]
        {
            new("liveFeed",  "chrome.hub.view.liveFeed"),
            new("chat",      "chrome.hub.view.chat"),
            new("script",    "chrome.hub.view.script"),
            new("systemLog", "chrome.hub.view.systemLog", "Ctrl+Shift+L"),
            // B43 (audit/winui-regressions-2026-05-24) — opens the EventLog
            // tail panel in a new window. Not toggleable like the four
            // workspace panels because the EventLog has no slot in the
            // 4-pane grid; clicking it spawns a fresh pop-out window via
            // HubWorkspaceView.OpenEventLogPopOut.
            new("eventLog",  "chrome.hub.view.eventLog"),
            // C16 (audit/winui-regressions-2026-05-24) — opens the
            // "Recent Webhooks" activity tail panel in a new window.
            // Same out-of-band pop-out shape as eventLog above; the
            // panel subscribes to HUDServer.OnWebhookFired for live
            // updates.
            new("webhook",   "chrome.hub.view.webhook"),
        }),
        new MenuSection("tools", "chrome.menu.tools", new MenuEntry[]
        {
            new("settings",      "chrome.hub.tools.settings", "Ctrl+,"),
            new("documentation", "chrome.hub.tools.documentation", "F1"),
            MenuEntry.Sep,
            new("checkUpdates",  "chrome.hub.tools.checkUpdates"),
        }),
        new MenuSection("help", "chrome.menu.help", new MenuEntry[]
        {
            new("sampleGraphs", "chrome.hub.help.sampleGraphs"),
            // In-app HTML docs (DocViewerWindow). README + Changelog render the
            // bundled offline pages; dispatched by MainWindow.HandleHelpMenu.
            new("readme",       "chrome.hub.help.readme"),
            new("changelog",    "chrome.hub.help.changelog"),
            MenuEntry.Sep,
            new("about",        "chrome.hub.help.about"),
            new("openGithub",   "chrome.hub.help.openGithub"),
        }),
    };

    // Architect's menu set — surfaced when the Architect pillar is active.
    // MenuToken is shared with Hub for the section labels (file/edit/view/help)
    // so the localized title strings are reused; pillar prefix lives on the
    // ItemToken (architect.file.new, …) so MainWindow.OnMenuItemInvoked
    // dispatches to the active pillar's MainView. Mirrors the historical
    // ArchitectChrome menu layout.
    public static IReadOnlyList<MenuSection> Architect { get; } = new[]
    {
        new MenuSection("file", "chrome.menu.file", new MenuEntry[]
        {
            new("architect.file.new",        "chrome.architect.file.newGraph",  "Ctrl+N"),
            new("architect.file.open",       "chrome.architect.file.open",      "Ctrl+O"),
            new("architect.file.openRecent", "chrome.architect.file.openRecent"),
            // TODO §2 resolution — force-shows the always-available Welcome
            // card overlay (non-destructive: the open graph stays loaded
            // behind it). Dispatched to MainView.ShowWelcome.
            new("architect.file.welcome",    "chrome.architect.file.welcome"),
            new("architect.file.save",       "chrome.architect.file.save",      "Ctrl+S"),
            MenuEntry.Sep,
            // 0.10.0 (arch-ux-state #5) — surfaces the rolling .phxg.bak[1-3]
            // chain. Dispatched to MainView.RestoreFromBackupAsync.
            new("architect.file.restoreBackup", "chrome.architect.file.restoreBackup"),
            MenuEntry.Sep,
            new("architect.file.export",     "chrome.architect.file.exportPhx"),
        }),
        new MenuSection("edit", "chrome.menu.edit", new MenuEntry[]
        {
            new("architect.edit.undo",  "chrome.architect.edit.undo",     "Ctrl+Z"),
            new("architect.edit.redo",  "chrome.architect.edit.redo",     "Ctrl+Y"),
            MenuEntry.Sep,
            new("architect.edit.find",  "chrome.architect.edit.findNode", "Ctrl+F"),
            new("architect.edit.group", "chrome.architect.edit.group",    "Ctrl+G"),
        }),
        new MenuSection("view", "chrome.menu.view", new MenuEntry[]
        {
            //  — accelerators rebound to match the canvas chord
            // authority in LogicCanvasView.Keyboard.cs (the canvas owns the
            // live bindings). Pre-fix the chrome advertised Shift+F / Ctrl+G
            // for Frame / Show Grid, but the canvas actually fires F →
            // FrameSelection and Ctrl+G → CollapseSelectionToMacro (Group),
            // so the menu's hints lied. Frame Selection is now bare F
            // matching canvas authority; Show Grid moves to Ctrl+Shift+G so
            // Ctrl+G can be the new Edit → Group accelerator.
            new("architect.view.frameSelection", "chrome.architect.view.frameSelection", "F"),
            new("architect.view.showGrid",       "chrome.architect.view.showGrid",       "Ctrl+Shift+G"),
            new("architect.view.liveDebug",      "chrome.architect.view.liveDebug"),
        }),
        new MenuSection("help", "chrome.menu.help", new MenuEntry[]
        {
            new("architect.help.nodeReference", "chrome.architect.help.nodeReference", "F1"),
        }),
    };

    // Visualist's menu set — surfaced when the Visualist pillar is active.
    // Same MenuToken / pillar-prefixed ItemToken pattern as Architect above.
    public static IReadOnlyList<MenuSection> Visualist { get; } = new[]
    {
        new MenuSection("file", "chrome.menu.file", new MenuEntry[]
        {
            new("visualist.file.newLayer",   "chrome.visualist.file.newLayer"),
            new("visualist.file.openLayer",  "chrome.visualist.file.openLayer"),
            // Recent — routed to MainView.OpenRecentLayerAsync, which pops a
            // RecentFilesDialog rebuilt from RecentFiles.Load() every time it
            // opens (the always-current Recent surface). A true inline
            // on-open-rebuilt MenuFlyoutSubItem isn't reachable here: WinUI 3's
            // MenuFlyoutSubItem / MenuBarItem expose no Opening event (only
            // FlyoutBase does, and the MenuBarItemFlyout isn't public), and the
            // shared BuildMenuStripBundle pipeline that would have to grow that
            // support is outside this lane's edit scope (Visualist section
            // only). The dialog is the lane-compatible dynamic-Recent surface.
            new("visualist.file.openRecent", "chrome.visualist.file.openRecent"),
            MenuEntry.Sep,
            new("visualist.file.save",       "chrome.visualist.file.save",   "Ctrl+S"),
            new("visualist.file.saveAs",     "chrome.visualist.file.saveAs", "Ctrl+Shift+S"),
            MenuEntry.Sep,
            // Import Media (Ctrl+I) — restores the pre-T15 File ▸ Import Media
            // fast path (MainForm.cs:129). Routed via MainWindow.OnMenuItemInvoked
            // to MainView.ImportMediaAsync, which copies the picked file into
            // Hub's data/media/<kind>/ tree.
            new("visualist.file.importMedia", "chrome.visualist.file.importMedia", "Ctrl+I"),
        }),
        new MenuSection("edit", "chrome.menu.edit", new MenuEntry[]
        {
            new("visualist.edit.undo",      "chrome.visualist.edit.undo", "Ctrl+Z"),
            new("visualist.edit.redo",      "chrome.visualist.edit.redo", "Ctrl+Y"),
            MenuEntry.Sep,
            new("visualist.edit.addWidget", "chrome.visualist.edit.addWidget"),
        }),
        new MenuSection("view", "chrome.menu.view", new MenuEntry[]
        {
            new("visualist.view.layerCanvas",  "chrome.visualist.view.layerCanvas"),
            new("visualist.view.widgetEditor", "chrome.visualist.view.widgetEditor"),
        }),
        // B26 (audit/winui-regressions-2026-05-24) — Window → New Visualist
        // Window. Mirrors Architect's File → New behaviour (each invocation
        // spawns a sibling top-level window) but lives under its own
        // "window" section so the entry is discoverable as the multi-window
        // gesture rather than buried in File. Token + label localisation
        // falls through Localizer.T defaults until lang files refresh.
        new MenuSection("window", "chrome.menu.window", new MenuEntry[]
        {
            new("visualist.window.new",           "chrome.visualist.window.new", "Ctrl+Shift+N"),
            // Window switcher — restores the pre-T15 MainForm Window menu
            // (RebuildWindowsMenu over MdiChildren). Routed via
            // MainWindow.OnMenuItemInvoked to MainView.ShowWindowSwitcherAsync,
            // which lists VisualistWindowRegistry.Snapshot() in a dialog
            // rebuilt every open (always-current) and activates the pick.
            // An inline on-open-rebuilt list isn't reachable here for the same
            // MenuFlyoutSubItem/Opening limitation noted on Recent above; the
            // dialog is the lane-compatible dynamic switcher. The sibling
            // window itself carries an inline Window → Switch To submenu.
            new("visualist.window.switch",        "chrome.visualist.window.switch"),
            // C3 (audit/winui-regressions-2026-05-24) — Preset Gallery.
            // Surfaced under Window because the gallery is a standalone
            // top-level window (matches the "New Visualist Window" entry's
            // placement). The Hub menu dispatcher routes the token through
            // MainView.OpenPresetGallery.
            new("visualist.window.presetGallery", "chrome.visualist.window.presetGallery"),
        }),
        // Sprint C — Tools menu reintroduces the pre-0.9.0 media-library
        // delete affordance (CHANGELOG 0.6.4) via a single Visualist
        // dialog. Token + label localisation falls through Localizer.T
        // defaults so a fresh lang file doesn't need a regeneration.
        new MenuSection("tools", "chrome.menu.tools", new MenuEntry[]
        {
            new("visualist.tools.mediaLibrary", "chrome.visualist.tools.mediaLibrary"),
        }),
        new MenuSection("help", "chrome.menu.help", new MenuEntry[]
        {
            new("visualist.help.compositorReference", "chrome.visualist.help.compositorReference"),
        }),
    };
}
