using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

public sealed partial class VarChainTraceDialog : ContentDialog
{
    public sealed record Row(string NodeId, string Title, string Subtitle, string ShortNodeId);

    private Graph? _graph;

    /// <summary>The variable name the user typed/picked when they clicked "Pin to canvas".</summary>
    public string? PinnedVar { get; private set; }

    /// <summary>
    ///  Set by the launcher (LogicCanvasView.Menus.TraceVariable)
    /// to the canvas's reveal-node hook. Clicking a writer/reader row invokes it
    /// with the node id so the canvas selects + frames + flashes that node, and
    /// the dialog hides so the node is visible.
    /// </summary>
    public System.Action<string>? NavigateToNode { get; set; }

    public VarChainTraceDialog()
    {
        InitializeComponent();
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
        //  Editable ComboBox drops focus after Enter;
        // restore it so the user can immediately type another query.
        try { sender.Focus(FocusState.Programmatic); } catch { /* best-effort */ }
    }

    //  Click a writer/reader row → reveal the node on the
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

    //  Show a per-side "No writers/readers for {var}."
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
