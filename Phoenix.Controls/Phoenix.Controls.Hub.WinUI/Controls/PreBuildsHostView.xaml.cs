using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;

namespace Phoenix.Controls.Hub.WinUI.Controls;

/// <summary>
/// Hosts every pre-built tool behind a Settings-shaped left rail, as the surface
/// the chrome's Pre-Builds tab foregrounds.
///
/// <para>Replaces ToolTabHost's dropdown-plus-tab-strip model (Majo, 1.1.7). The
/// swap-surface contract is unchanged — MainWindow shows this in
/// <c>MainPaneRegion</c> and hides it again when a pillar tab is clicked, so tool
/// state survives — but the tools are now all listed rather than opened one menu
/// pick at a time, and none of them can be "closed", so the empty-host state that
/// ToolTabHost had to raise <c>LastTabClosed</c> for no longer exists.</para>
///
/// <para>Pages are built lazily on first selection and then kept alive, matching
/// how the pillar MainViews are constructed: opening Loyalty once costs its VM
/// load, and every later visit is a ContentControl swap. Each page is
/// <see cref="IDisposable"/>; <see cref="DisposeAllTools"/> runs at shutdown so
/// the debounced config flushes land before <c>Environment.Exit</c>.</para>
///
/// <para>GIVEAWAY is the one tool with two homes. It renders inline here like the
/// others AND keeps its pop-out window, reached from the ⧉ on its rail row (Majo:
/// "inline + a pop-out button"). While the window is up, the inline column shows
/// the popped-out notice instead of a second live copy — the same rule
/// <c>HubWorkspaceView</c> applies to the four workspace panels.</para>
/// </summary>
public sealed partial class PreBuildsHostView : UserControl
{
    /// <summary>A rail row: the tool, its localized label, and whether it can pop out.</summary>
    private sealed record ToolRow(PreBuildKind Kind, string LabelKey, string LabelFallback, bool CanPopOut = false);

    /// <summary>A rail group: a caption plus the tools under it.</summary>
    private sealed record ToolGroup(string CaptionKey, string CaptionFallback, IReadOnlyList<ToolRow> Tools);

    /// <summary>
    /// The rail, grouped by what the streamer is trying to do rather than by the
    /// order the tools were built in (which is what the old dropdown showed).
    /// Fourteen flat rows do not scan; four captions do.
    ///
    /// <para>Giveaway sits under VIEWERS &amp; REWARDS rather than first, because
    /// its old position at the top of the dropdown was an artifact of it being the
    /// first pre-build to ship.</para>
    /// </summary>
    private static readonly IReadOnlyList<ToolGroup> Groups = new[]
    {
        new ToolGroup("prebuilds.group.chat_commands", "CHAT & COMMANDS", new[]
        {
            new ToolRow(PreBuildKind.CustomCommands, "pillartab.prebuilds.customcommands", "Custom Commands"),
            new ToolRow(PreBuildKind.Quotes,         "pillartab.prebuilds.quotes",         "Quotes"),
            new ToolRow(PreBuildKind.Polls,          "pillartab.prebuilds.polls",          "Polls"),
        }),
        new ToolGroup("prebuilds.group.viewers_rewards", "VIEWERS & REWARDS", new[]
        {
            new ToolRow(PreBuildKind.Loyalty,        "pillartab.prebuilds.loyalty",        "Loyalty"),
            new ToolRow(PreBuildKind.Ranks,          "pillartab.prebuilds.ranks",          "Ranks"),
            new ToolRow(PreBuildKind.Giveaway,       "pillartab.prebuilds.giveaway",       "Giveaway", CanPopOut: true),
            new ToolRow(PreBuildKind.UserManagement, "pillartab.prebuilds.usermanagement", "User Management"),
        }),
        new ToolGroup("prebuilds.group.stream_media", "STREAM & MEDIA", new[]
        {
            new ToolRow(PreBuildKind.Timer,          "pillartab.prebuilds.timer",          "Timer"),
            new ToolRow(PreBuildKind.Alerts,         "pillartab.prebuilds.alerts",         "Alerts"),
            new ToolRow(PreBuildKind.Soundboard,     "pillartab.prebuilds.soundboard",     "Soundboard"),
            new ToolRow(PreBuildKind.SongRequest,    "pillartab.prebuilds.songrequest",    "Song Requests"),
            new ToolRow(PreBuildKind.Counters,       "pillartab.prebuilds.counters",       "Counters"),
            new ToolRow(PreBuildKind.Scheduling,     "pillartab.prebuilds.scheduling",     "Scheduling"),
        }),
        new ToolGroup("prebuilds.group.moderation", "MODERATION", new[]
        {
            new ToolRow(PreBuildKind.Automod,        "pillartab.prebuilds.automod",        "Automod"),
        }),
    };

    private readonly Dictionary<PreBuildKind, Button> _railButtons = new();
    private readonly Dictionary<PreBuildKind, UserControl> _pages = new();
    private readonly List<PreBuildKind> _order = new();
    private readonly HashSet<PreBuildKind> _poppedOut = new();

    private Func<PreBuildKind, UserControl?>? _viewFactory;
    private PreBuildKind? _selected;

    // Set by DisposeAllTools. Shutdown closes the Giveaway pop-out BEFORE it
    // disposes the rail, and that window's Closed handler calls SetPoppedOut(false)
    // — which would otherwise rebuild the very page teardown is tearing down, and
    // would leave a live VM behind entirely if Closed arrived after the sweep.
    private bool _disposed;

    /// <summary>
    /// Raised when a rail row's ⧉ is pressed. MainWindow owns the window (it holds
    /// the single-instance field and the Closed hook), so the host only asks.
    /// </summary>
    public event EventHandler<PreBuildKind>? PopOutRequested;

    public PreBuildsHostView()
    {
        InitializeComponent();

        BringToFrontButton.Content = Localizer.T("prebuilds.poppedout.bring_to_front", "Bring it to the front");

        BuildRail();
    }

    /// <summary>
    /// Hands the host its page factory. Called once by MainWindow; the factory is
    /// consulted the first time each tool is selected and never again.
    /// </summary>
    public void Configure(Func<PreBuildKind, UserControl?> viewFactory)
        => _viewFactory = viewFactory ?? throw new ArgumentNullException(nameof(viewFactory));

    /// <summary>The tool the rail is currently showing, if any.</summary>
    public PreBuildKind? SelectedTool => _selected;

    /// <summary>
    /// Shows <paramref name="kind"/>, building its page on first use. Called by
    /// MainWindow when the Pre-Builds tab is clicked (with the remembered tool) and
    /// by the rail rows themselves.
    /// </summary>
    public void Select(PreBuildKind kind)
    {
        if (_disposed) return;
        if (!_railButtons.ContainsKey(kind)) return;

        _selected = kind;
        RefreshRailVisuals();

        // A popped-out tool shows the notice instead of a live second copy.
        if (_poppedOut.Contains(kind))
        {
            ShowPoppedOutNotice(kind);
            return;
        }

        PoppedOutNotice.Visibility = Visibility.Collapsed;

        if (!_pages.TryGetValue(kind, out var page))
        {
            page = BuildPage(kind);
            if (page is null)
            {
                // No factory yet (a click before MainWindow wired HubServices) or
                // the factory threw. Say so rather than showing an empty column.
                ContentHost.Content = BuildUnavailableNotice(kind);
                return;
            }
            _pages[kind] = page;
        }

        ContentHost.Content = page;

        // Same entrance beat ToolTabHost played on tab activation: the page settles
        // in rather than snapping. Composited and self-clearing.
        Animation.AnimateExtensions.FadeSlideIn(page, slideY: 8, durationMs: 150);
    }

    /// <summary>
    /// Selects the first tool if nothing is selected yet — what MainWindow calls
    /// when the Pre-Builds tab is clicked for the first time in a session.
    /// </summary>
    public void SelectDefault()
    {
        if (_selected is { } current) { Select(current); return; }
        if (_order.Count > 0) Select(_order[0]);
    }

    /// <summary>
    /// Tells the host whether <paramref name="kind"/> is currently living in its
    /// own window. MainWindow calls this on open and on close, so the inline
    /// column and the notice stay in step with the window's real lifetime.
    /// </summary>
    public void SetPoppedOut(PreBuildKind kind, bool poppedOut)
    {
        if (_disposed) return;
        bool changed = poppedOut ? _poppedOut.Add(kind) : _poppedOut.Remove(kind);
        if (!changed) return;

        // Dispose the inline copy while the window owns the tool: two live VMs on
        // one service is legal (the workspace pop-outs do it) but pointless here,
        // and the rebuild on return is a single page construction.
        if (poppedOut && _pages.Remove(kind, out var page))
        {
            try { if (page is IDisposable d) d.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("PreBuildsHostView", $"dispose inline '{kind}'", ex); }
            if (ReferenceEquals(ContentHost.Content, page)) ContentHost.Content = null;
        }

        if (_selected == kind) Select(kind);
    }

    /// <summary>
    /// Disposes every built tool page (running each VM's unsubscribe + final config
    /// flush) and returns a Task that completes when those flushes have landed.
    /// MainWindow's shutdown coordinator awaits it as one more tracked step — the
    /// structural fix for tool edits dying with the process.
    /// </summary>
    public Task DisposeAllTools()
    {
        // Latched FIRST: MainWindow closes the Giveaway pop-out just above this
        // call, and that window's Closed handler re-enters through SetPoppedOut.
        _disposed = true;

        foreach (var kv in _pages)
        {
            try { if (kv.Value is IDisposable d) d.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("PreBuildsHostView", $"DisposeAllTools '{kv.Key}'", ex); }
        }
        _pages.Clear();
        _selected = null;
        try { ContentHost.Content = null; }
        catch { /* tearing down — best-effort */ }

        return ToolConfigFlushTracker.DrainAsync();
    }

    // ── Page construction ────────────────────────────────────────────────

    private UserControl? BuildPage(PreBuildKind kind)
    {
        if (_viewFactory is null)
        {
            GlobalLogger.Log($"Pre-build '{kind}' selected before the page factory was wired — ignoring.",
                "PreBuildsHostView", LogLevel.Debug);
            return null;
        }
        try
        {
            return _viewFactory(kind);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PreBuildsHostView", $"page factory for '{kind}' threw", ex);
            return null;
        }
    }

    private TextBlock BuildUnavailableNotice(PreBuildKind kind) => new()
    {
        Text = string.Format(
            Localizer.T("prebuilds.unavailable_format", "{0} could not be opened — see the system log."),
            LabelFor(kind)),
        FontFamily = ResFont("MonoFont"),
        FontSize = 12,
        Foreground = ResBrush("CoalSecondaryTextBrush"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 360,
        TextAlignment = TextAlignment.Center,
    };

    private void ShowPoppedOutNotice(PreBuildKind kind)
    {
        ContentHost.Content = null;
        PoppedOutText.Text = string.Format(
            Localizer.T("prebuilds.poppedout.format", "{0} is open in its own window."),
            LabelFor(kind));
        PoppedOutNotice.Visibility = Visibility.Visible;
    }

    private void OnBringToFrontClick(object sender, RoutedEventArgs e)
    {
        // Re-raising PopOutRequested is the "focus the existing window" path —
        // MainWindow's opener is single-instance and activates what is already up.
        if (_selected is { } kind && _poppedOut.Contains(kind))
            PopOutRequested?.Invoke(this, kind);
    }

    // ── Rail ─────────────────────────────────────────────────────────────

    private void BuildRail()
    {
        RailPanel.Children.Clear();
        _railButtons.Clear();
        _order.Clear();

        var railFont = ResFont("SansFont");
        bool first = true;

        foreach (var group in Groups)
        {
            RailPanel.Children.Add(BuildGroupCaption(group, first));
            first = false;

            foreach (var tool in group.Tools)
            {
                var button = BuildRailRow(tool, railFont);
                _railButtons[tool.Kind] = button;
                _order.Add(tool.Kind);
                RailPanel.Children.Add(button);
            }
        }

        RefreshRailVisuals();
    }

    private TextBlock BuildGroupCaption(ToolGroup group, bool isFirst) => new()
    {
        Text = Localizer.T(group.CaptionKey, group.CaptionFallback),
        FontFamily = ResFont("MonoFont"),
        FontSize = 9,
        CharacterSpacing = 140,
        Foreground = ResBrush("CoalSecondaryTextBrush"),
        Margin = new Thickness(12, isFirst ? 0 : 14, 12, 4),
        // The caption labels the rows under it; the rows carry their own names, so
        // narrating it twice would only pad the list.
        TextWrapping = TextWrapping.Wrap,
    };

    private Button BuildRailRow(ToolRow tool, FontFamily railFont)
    {
        string label = Localizer.T(tool.LabelKey, tool.LabelFallback);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(text, 0);
        content.Children.Add(text);

        if (tool.CanPopOut)
        {
            // ⧉ — the same glyph and 16×16 chrome PanelHeader's pop-out uses, so
            // the affordance reads the same everywhere in Hub. Its own Click opens
            // the window; PointerPressed is marked handled so the press does not
            // also select the row underneath it.
            var popOut = new Button
            {
                Width = 20,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Foreground = ResBrush("CoalSecondaryTextBrush"),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Content = "", // OpenInNewWindow
                VerticalAlignment = VerticalAlignment.Center,
            };
            string popOutTip = Localizer.T("prebuilds.popout.tooltip", "Open in a separate window");
            ToolTipService.SetToolTip(popOut, popOutTip);
            AutomationProperties.SetName(popOut, string.Format(
                Localizer.T("prebuilds.popout.aria_format", "Open {0} in a separate window"), label));
            popOut.Click += (_, _) => PopOutRequested?.Invoke(this, tool.Kind);
            popOut.PointerPressed += (_, e) => e.Handled = true;
            Grid.SetColumn(popOut, 1);
            content.Children.Add(popOut);
        }

        var button = new Button
        {
            Content = content,
            Tag = tool.Kind,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = 34,
            Padding = new Thickness(12, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            FontFamily = railFont,
            FontSize = 13,
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += OnRailRowClick;
        // Up / Down / Home / End walk the rail. The old tab strip had the same
        // keys on its horizontal axis; without them a 14-row rail is Tab-only.
        button.KeyDown += (_, e) => OnRailRowKeyDown(tool.Kind, e);
        return button;
    }

    private void OnRailRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PreBuildKind kind }) Select(kind);
    }

    private void OnRailRowKeyDown(PreBuildKind from, KeyRoutedEventArgs e)
    {
        int index = _order.IndexOf(from);
        if (index < 0) return;

        int target;
        switch (e.Key)
        {
            case VirtualKey.Up:   target = index - 1; break;
            case VirtualKey.Down: target = index + 1; break;
            case VirtualKey.Home: target = 0; break;
            case VirtualKey.End:  target = _order.Count - 1; break;
            default: return;
        }
        e.Handled = true;

        // Clamped, not wrapping: arrowing off the end of a list should stop there.
        if (target < 0) target = 0;
        if (target > _order.Count - 1) target = _order.Count - 1;
        if (target == index) return;

        // Move focus only — Enter / Space commits, so arrowing past an expensive
        // page does not build every VM on the way through.
        if (_railButtons.TryGetValue(_order[target], out var button))
        {
            try { button.Focus(FocusState.Keyboard); }
            catch { /* detached mid-teardown */ }
        }
    }

    private void RefreshRailVisuals()
    {
        foreach (var kv in _railButtons)
        {
            bool active = _selected == kv.Key;
            var button = kv.Value;
            if (active)
            {
                button.Background  = ResBrush("EmberShadowBrush");
                button.BorderBrush = ResBrush("EmberPrimaryBrush");
                button.Foreground  = ResBrush("CoalPaperBrush");
            }
            else
            {
                button.Background  = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.Foreground  = ResBrush("CoalBodyTextBrush");
            }
        }
    }

    private static string LabelFor(PreBuildKind kind)
    {
        foreach (var group in Groups)
            foreach (var tool in group.Tools)
                if (tool.Kind == kind)
                    return Localizer.T(tool.LabelKey, tool.LabelFallback);
        return kind.ToString();
    }

    // ── Token resolution (no new hardcoded hex — pull from PhoenixDark) ──

    private static Brush ResBrush(string key)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var v)
            && v is Brush b)
            return b;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // The font tokens ship as x:String URIs (not FontFamily), so build the
    // FontFamily the way XAML's implicit converter would.
    private static FontFamily ResFont(string key)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var v)
            && v is string s && !string.IsNullOrWhiteSpace(s))
            return new FontFamily(s);
        return new FontFamily("Segoe UI");
    }
}
