using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

// [DIALOG-NO-XAML-FIX 2026-06-29] No .xaml / InitializeComponent — a
// code-constructed library ContentDialog (Architect.WinUI) throws
// XamlParseException at Application.LoadComponent when `new`'d detached (proven
// by the 1.0.6 runtime stack trace; resource stripping never helped because the
// throw is in the XAML parse itself). Content is built in code; the default
// template still resolves at ShowAsync against Hub's app scope. The two
// ListView ItemTemplates are still authored as DataTemplate markup and loaded
// via XamlReader.Load — template content is deferred and resolves at row
// realization, so the {Binding}/{ThemeResource} refs inside are safe there.
// See NameTypeDialog / ConfirmDialog / DialogTheme.cs for the full rationale.
public sealed class VarChainTraceDialog : ContentDialog
{
    public sealed record Row(string NodeId, string Title, string Subtitle, string ShortNodeId);

    private Graph? _graph;

    /// <summary>The variable name the user typed/picked when they clicked "Pin to canvas".</summary>
    public string? PinnedVar { get; private set; }

    /// <summary>
    /// Set by the launcher (LogicCanvasView.Menus.TraceVariable)
    /// to the canvas's reveal-node hook. Clicking a writer/reader row invokes it
    /// with the node id so the canvas selects + frames + flashes that node, and
    /// the dialog hides so the node is visible.
    /// </summary>
    public System.Action<string>? NavigateToNode { get; set; }

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly ComboBox VarCombo;
    private readonly TextBlock VariableLabel;
    private readonly Border WritersBorder;
    private readonly Border ReadersBorder;
    private readonly TextBlock WritersHeader;
    private readonly TextBlock ReadersHeader;
    private readonly ListView WritersList;
    private readonly ListView ReadersList;
    private readonly TextBlock WritersEmpty;
    private readonly TextBlock ReadersEmpty;
    private readonly TextBlock SummaryFooter;

    // Identical row template for both the WRITERS and READERS lists. Authored as
    // DataTemplate markup (preserved verbatim from the old XAML) and loaded via
    // XamlReader.Load — the only template form XamlReader supports is classic
    // {Binding}, which these rows already use. The {ThemeResource} refs are
    // deferred (template content), so they resolve at row realization, not load.
    private const string RowTemplateXaml =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "<StackPanel Spacing=\"1\" Margin=\"0,3,0,3\" ToolTipService.ToolTip=\"Click to jump to this node\">" +
                "<TextBlock Text=\"{Binding Title}\" FontSize=\"12\" Foreground=\"{ThemeResource TextStrongBrush}\" />" +
                "<TextBlock Text=\"{Binding Subtitle}\" FontSize=\"10\" Foreground=\"{ThemeResource TextLabelBrush}\" />" +
                "<TextBlock Text=\"{Binding ShortNodeId}\" FontSize=\"9\" Foreground=\"{ThemeResource TextLabelBrush}\" />" +
            "</StackPanel>" +
        "</DataTemplate>";

    public VarChainTraceDialog()
    {
        Title = Localizer.T("architect.dialog.var_chain.title", "Trace Variable");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = Localizer.T("architect.dialog.var_chain.pin_button",
            "Pin to canvas (dim others)");
        CloseButtonText = Localizer.T("common.button.close", "Close");
        DefaultButton = ContentDialogButton.Primary;

        // Root: Grid 640x500 with four rows (Auto / Auto / * / Auto).
        var root = new Grid { Width = 640, Height = 500 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0 — read-only reference InfoBar.
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = Localizer.T("architect.dialog.var_chain.infobar.title", "Read-only reference"),
            Message = Localizer.T("architect.dialog.var_chain.infobar.body",
                "Click a row to jump to that node on the canvas, or pin a variable to highlight its chain."),
            IsOpen = true,
            IsClosable = false,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(info, 0);
        root.Children.Add(info);

        // Row 1 — Variable label + editable ComboBox.
        var queryGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        queryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        queryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        VariableLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 11,
            Text = Localizer.T("architect.dialog.var_chain.variable_label", "Variable"),
        };
        Grid.SetColumn(VariableLabel, 0);

        VarCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13,
        };
        Grid.SetColumn(VarCombo, 1);
        VarCombo.TextSubmitted += OnVarSubmitted;
        VarCombo.SelectionChanged += OnVarSelectionChanged;

        queryGrid.Children.Add(VariableLabel);
        queryGrid.Children.Add(VarCombo);
        Grid.SetRow(queryGrid, 1);
        root.Children.Add(queryGrid);

        // Row 2 — two-column body (WRITERS / READERS), 10px column spacing.
        var body = new Grid { ColumnSpacing = 10 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // WRITERS panel — 3px left accent stripe (cyan via
        // VarChainWriterBrush, applied in code below), neutral on the other edges.
        WritersHeader = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = 11,
            CharacterSpacing = 80,
        };
        Grid.SetRow(WritersHeader, 0);

        // IsItemClickEnabled so a row click jumps to
        // (selects + frames + flashes) the node on canvas.
        WritersList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplateXaml),
        };
        WritersList.ItemClick += OnRowClick;
        WritersList.ContainerContentChanging += OnRowContainerChanging;
        Grid.SetRow(WritersList, 1);

        WritersEmpty = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(2, 6, 0, 0),
            FontSize = 11,
            Text = Localizer.T("architect.dialog.var_chain.writers_empty", "No writers."),
        };
        Grid.SetRow(WritersEmpty, 1);

        var writersInner = new Grid();
        writersInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        writersInner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        writersInner.Children.Add(WritersHeader);
        writersInner.Children.Add(WritersList);
        writersInner.Children.Add(WritersEmpty);

        WritersBorder = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            Padding = new Thickness(6),
            Child = writersInner,
        };
        Grid.SetColumn(WritersBorder, 0);
        body.Children.Add(WritersBorder);

        // READERS panel — 3px amber left accent (VarChainReaderBrush),
        // mirroring the writers stripe.
        ReadersHeader = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = 11,
            CharacterSpacing = 80,
        };
        Grid.SetRow(ReadersHeader, 0);

        ReadersList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplateXaml),
        };
        ReadersList.ItemClick += OnRowClick;
        ReadersList.ContainerContentChanging += OnRowContainerChanging;
        Grid.SetRow(ReadersList, 1);

        ReadersEmpty = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(2, 6, 0, 0),
            FontSize = 11,
            Text = Localizer.T("architect.dialog.var_chain.readers_empty", "No readers."),
        };
        Grid.SetRow(ReadersEmpty, 1);

        var readersInner = new Grid();
        readersInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        readersInner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        readersInner.Children.Add(ReadersHeader);
        readersInner.Children.Add(ReadersList);
        readersInner.Children.Add(ReadersEmpty);

        ReadersBorder = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            Padding = new Thickness(6),
            Child = readersInner,
        };
        Grid.SetColumn(ReadersBorder, 1);
        body.Children.Add(ReadersBorder);

        Grid.SetRow(body, 2);
        root.Children.Add(body);

        // Row 3 —  aggregate writer/reader count footer.
        SummaryFooter = new TextBlock
        {
            Margin = new Thickness(2, 8, 0, 0),
            FontSize = 11,
        };
        Grid.SetRow(SummaryFooter, 3);
        root.Children.Add(SummaryFooter);

        Content = root;

        // Theme applied in code — see DialogTheme. (DataTemplate row brushes stay
        // in the template markup; template content is deferred and resolves at realization.)
        if (DialogTheme.Brush("BgPanelBrush")        is { } bg) Background  = bg;
        if (DialogTheme.Brush("BorderSoftBrush")     is { } bd) BorderBrush = bd;
        if (DialogTheme.Brush("TextLabelBrush")      is { } tl)
        {
            VariableLabel.Foreground = tl;
            WritersEmpty.Foreground  = tl;
            ReadersEmpty.Foreground  = tl;
            SummaryFooter.Foreground = tl;
        }
        if (DialogTheme.Brush("SelectionDimBrush")   is { } sd) { WritersBorder.Background  = sd; ReadersBorder.Background  = sd; }
        if (DialogTheme.Brush("VarChainWriterBrush") is { } wb) { WritersBorder.BorderBrush = wb; WritersHeader.Foreground = wb; }
        if (DialogTheme.Brush("VarChainReaderBrush") is { } rb) { ReadersBorder.BorderBrush = rb; ReadersHeader.Foreground = rb; }
        PrimaryButtonClick += OnPin;
    }

    public static VarChainTraceDialog ForGraph(XamlRoot root, Graph graph, string? initialVar = null)
    {
        var d = new VarChainTraceDialog
        {
            XamlRoot = root,
            _graph = graph,
        };
        var allVars = VarChainAnalyzer.EnumerateAllVars(graph);
        d.VarCombo.ItemsSource = allVars;
        if (!string.IsNullOrEmpty(initialVar))
        {
            d.VarCombo.Text = initialVar;
            d.RefreshChain(initialVar!);
        }
        else if (allVars.Count > 0)
        {
            d.VarCombo.SelectedIndex = 0;
            d.RefreshChain(allVars[0]);
        }
        else
        {
            d.RefreshChain("");
        }
        return d;
    }

    private void OnVarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VarCombo.SelectedItem is string s)
            RefreshChain(s);
    }

    private void OnVarSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        RefreshChain(args.Text);
        // Editable ComboBox drops focus after Enter;
        // restore it so the user can immediately type another query.
        try { sender.Focus(FocusState.Programmatic); } catch { /* best-effort */ }
    }

    // The row template is authored as DataTemplate markup and loaded through
    // XamlReader.Load, which cannot carry the loc: attached properties, so the
    // row's one chrome string resolves here per realized container instead.
    // The template literal stays as the pre-realization fallback.
    private static void OnRowContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer?.ContentTemplateRoot is FrameworkElement rowRoot)
        {
            ToolTipService.SetToolTip(rowRoot,
                Localizer.T("architect.dialog.var_chain.row.tip", "Click to jump to this node"));
        }
    }

    // Click a writer/reader row → reveal the node on the
    // canvas and hide this (non-modal) dialog so the node is visible.
    private void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Row row && !string.IsNullOrEmpty(row.NodeId))
        {
            NavigateToNode?.Invoke(row.NodeId);
            try { Hide(); } catch { /* best-effort */ }
        }
    }

    private void RefreshChain(string varName)
    {
        if (_graph is null) return;
        if (string.IsNullOrWhiteSpace(varName))
        {
            WritersHeader.Text = Localizer.T("architect.dialog.var_chain.writers_header_empty", "WRITERS — 0");
            ReadersHeader.Text = Localizer.T("architect.dialog.var_chain.readers_header_empty", "READERS — 0");
            WritersList.ItemsSource = System.Array.Empty<Row>();
            ReadersList.ItemsSource = System.Array.Empty<Row>();
            UpdateEmptyHints("", 0, 0);
            UpdateSummary("", 0, 0);
            return;
        }
        var trace = VarChainAnalyzer.Analyze(_graph, varName);
        WritersHeader.Text = string.Format(
            Localizer.T("architect.dialog.var_chain.writers_header_format", "WRITERS (set {{{0}}}) — {1}"),
            trace.VarName, trace.Writers.Count);
        ReadersHeader.Text = string.Format(
            Localizer.T("architect.dialog.var_chain.readers_header_format", "READERS (read {{{0}}}) — {1}"),
            trace.VarName, trace.Readers.Count);

        //  Build one node-id → context map for the writers and
        // readers we are about to display, instead of re-scanning every macro /
        // process for every row (the prior O(n×m) ResolveContext-per-row call).
        // The map is built over the union of the two result lists, so the macro /
        // process membership scan runs at most once per result node.
        var contextMap = BuildContextMap(trace.Writers.Concat(trace.Readers));

        WritersList.ItemsSource = trace.Writers
            .OrderBy(n => n.Title, System.StringComparer.OrdinalIgnoreCase)
            .Select(n => new Row(n.Id, n.Title, ResolveContextFromMap(contextMap, n), ShortId(n.Id)))
            .ToList();
        ReadersList.ItemsSource = trace.Readers
            .OrderBy(n => n.Title, System.StringComparer.OrdinalIgnoreCase)
            .Select(n => new Row(n.Id, n.Title, ResolveContextFromMap(contextMap, n), ShortId(n.Id)))
            .ToList();
        UpdateEmptyHints(trace.VarName, trace.Writers.Count, trace.Readers.Count);
        UpdateSummary(trace.VarName, trace.Writers.Count, trace.Readers.Count);
    }

    // Show a per-side "No writers/readers for {var}."
    // message when a valid variable simply has none on that side (vs. a blank
    // panel). Carries the variable name so the streamer doesn't have to infer
    // which query produced the empty result.
    private void UpdateEmptyHints(string varName, int writerCount, int readerCount)
    {
        if (WritersEmpty is not null)
        {
            WritersEmpty.Visibility = writerCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            WritersEmpty.Text = string.IsNullOrEmpty(varName)
                ? Localizer.T("architect.dialog.var_chain.writers_empty", "No writers.")
                : string.Format(
                    Localizer.T("architect.dialog.var_chain.writers_empty_named", "No writers for {0}"),
                    varName);
        }
        if (ReadersEmpty is not null)
        {
            ReadersEmpty.Visibility = readerCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            ReadersEmpty.Text = string.IsNullOrEmpty(varName)
                ? Localizer.T("architect.dialog.var_chain.readers_empty", "No readers.")
                : string.Format(
                    Localizer.T("architect.dialog.var_chain.readers_empty_named", "No readers for {0}"),
                    varName);
        }
    }

    //  Aggregate writer/reader footer, restoring the WinForms
    // VarChainTraceForm summary line: "No nodes write or read {var}." when both
    // sides are empty, otherwise "{n} writer(s) / {m} reader(s).".
    private void UpdateSummary(string varName, int writerCount, int readerCount)
    {
        if (SummaryFooter is null) return;
        if (string.IsNullOrEmpty(varName))
        {
            SummaryFooter.Text = string.Empty;
            return;
        }
        SummaryFooter.Text = (writerCount == 0 && readerCount == 0)
            ? string.Format(
                Localizer.T("architect.dialog.var_chain.summary_none", "No nodes write or read {0}."),
                varName)
            : string.Format(
                Localizer.T("architect.dialog.var_chain.summary_counts", "{0} writer(s) / {1} reader(s)."),
                writerCount, readerCount);
    }

    /// <summary>
    ///  Build a node-id → containing-context-subtitle map for the
    /// supplied result nodes in a single pass over the graph's macros /
    /// processes, replacing the per-row <c>ResolveContext</c> rescan. Each node
    /// resolves to a macro/process container subtitle, or a top-level short-id
    /// fallback when it lives on the root graph.
    /// </summary>
    private System.Collections.Generic.Dictionary<string, string> BuildContextMap(
        System.Collections.Generic.IEnumerable<Node> nodes)
    {
        var map = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
        if (_graph is null) return map;

        // Seed every result node with its top-level fallback subtitle; macro /
        // process scans below overwrite the entry when a container owns the node.
        foreach (var n in nodes)
        {
            if (n is null || string.IsNullOrEmpty(n.Id)) continue;
            if (!map.ContainsKey(n.Id))
                map[n.Id] = $"top-level · {ShortId(n.Id)}";
        }
        if (map.Count == 0) return map;

        // Single sweep of macros: a contained node id present in the map is
        // promoted to its macro context.
        foreach (var m in _graph.Macros)
        {
            if (m?.Graph?.Nodes is null) continue;
            foreach (var inner in m.Graph.Nodes)
            {
                if (inner?.Id is null) continue;
                if (map.ContainsKey(inner.Id))
                    map[inner.Id] = $"in macro \"{m.Name}\"";
            }
        }

        // Single sweep of processes.
        foreach (var p in _graph.Processes)
        {
            if (p?.Graph?.Nodes is null) continue;
            foreach (var inner in p.Graph.Nodes)
            {
                if (inner?.Id is null) continue;
                if (map.ContainsKey(inner.Id))
                    map[inner.Id] = $"in process \"{p.Name}\"";
            }
        }

        return map;
    }

    private static string ResolveContextFromMap(
        System.Collections.Generic.Dictionary<string, string> map, Node n)
    {
        if (string.IsNullOrEmpty(n.Id)) return string.Empty;
        return map.TryGetValue(n.Id, out var ctx) ? ctx : $"top-level · {ShortId(n.Id)}";
    }

    private static string ShortId(string id) =>
        string.IsNullOrEmpty(id) ? string.Empty : id.Substring(0, System.Math.Min(8, id.Length));

    private void OnPin(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        PinnedVar = string.IsNullOrWhiteSpace(VarCombo.Text)
            ? (VarCombo.SelectedItem as string)
            : VarCombo.Text.Trim();
    }
}
