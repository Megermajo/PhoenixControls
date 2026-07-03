using System.Collections.ObjectModel;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.ViewModels;

// Slim view-model for the right-hand inspector. 0.10.0 collapsed the
// inspector to a single Description string — the node body owns every
// editable affordance now. Selection
// is set externally; rebuild Description off the new model.
//
// Fields is retained as a typed (empty) ObservableCollection because
// DatabankInspector still drives one through a shared InspectorField
// template; LogicInspector.xaml no longer binds it.
public sealed class LogicInspectorViewModel : ObservableObject
{
    private string _displayTitle = string.Empty;
    private string _eyebrow      = "Inspector";
    private string _description  = string.Empty;
    private bool   _hasSelection;

    // Reusable scratch buffers for SetMultiNodes — the multi-node
    // summary fires on every multi-selection change, and allocating a fresh
    // Dictionary + List on each call pressures the GC. Cleared (not
    // re-allocated) per call; the VM is single-threaded (UI-thread only) so
    // the shared buffers are safe.
    private readonly System.Collections.Generic.Dictionary<string, int> _multiCounts =
        new(System.StringComparer.Ordinal);
    private readonly System.Collections.Generic.List<Node> _multiNodesScratch = new();

    public LogicInspectorViewModel()
    {
        Fields = new ObservableCollection<InspectorFieldViewModel>();
    }

    public string DisplayTitle
    {
        get => _displayTitle;
        private set
        {
            if (SetField(ref _displayTitle, value))
                HasSelection = !string.IsNullOrEmpty(value);
        }
    }

    public string Eyebrow
    {
        get => _eyebrow;
        private set => SetField(ref _eyebrow, value);
    }

    /// <summary>
    /// Resolved description text — Node.Description override takes
    /// precedence over the registered NodeTemplate.Description. Bound by
    /// LogicInspector.xaml's body TextBlock. Multi-selection / link paths
    /// repurpose this slot for their summary text.
    /// </summary>
    public string Description
    {
        get => _description;
        private set => SetField(ref _description, value);
    }

    /// <summary>
    /// True when the inspector has something to show. Bound by
    /// <c>LogicInspector.xaml</c> to swap between body + empty-state hint.
    /// </summary>
    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetField(ref _hasSelection, value);
    }

    /// <summary>
    /// Retained typed collection so the shared <c>InspectorField</c>
    /// templated row still has a binding source on DatabankInspector.
    /// LogicInspector.xaml does not bind Fields anymore.
    /// </summary>
    public ObservableCollection<InspectorFieldViewModel> Fields { get; }

    /// <summary>Bind to a single Node — populates Description from the override or template.</summary>
    public void SetNode(Node? node)
    {
        Fields.Clear();
        if (node is null)
        {
            DisplayTitle = string.Empty;
            Eyebrow      = "Inspector";
            Description  = string.Empty;
            return;
        }

        DisplayTitle = node.Title ?? string.Empty;
        Eyebrow      = string.IsNullOrEmpty(node.Category) ? "Inspector" : node.Category!;

        // Resolved description — instance override wins, fall back to the
        // NodeRegistry template description, fall back to a polite placeholder
        // so the body never looks empty for the user.
        var override_ = (node.Description ?? string.Empty).Trim();
        if (override_.Length > 0)
        {
            Description = override_;
        }
        else
        {
            var template = Phoenix.Controls.Architect.Core.NodeRegistry.GetTemplate(node.Title ?? string.Empty);
            var fromTemplate = (template?.Description ?? string.Empty).Trim();
            Description = fromTemplate.Length > 0
                ? fromTemplate
                : "No description provided for this node.";
        }
    }

    /// <summary>
    /// Bind to a multi-node selection. Reuses Description as the summary
    /// line so the slim panel still tells the user what's selected.
    /// </summary>
    public void SetMultiNodes(System.Collections.Generic.IReadOnlyList<Node> nodes)
    {
        Fields.Clear();
        if (nodes is null || nodes.Count == 0)
        {
            DisplayTitle = string.Empty;
            Eyebrow      = "Inspector";
            Description  = string.Empty;
            return;
        }

        DisplayTitle = $"{nodes.Count} nodes";
        Eyebrow      = "Multi-selection";

        // Reuse the cached counts dictionary instead of allocating a
        // new one per multi-selection event.
        _multiCounts.Clear();
        foreach (var n in nodes)
        {
            string title = n.Title ?? "(untitled)";
            _multiCounts[title] = _multiCounts.TryGetValue(title, out var c) ? c + 1 : 1;
        }
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var kv in _multiCounts)
        {
            if (!first) sb.Append('\n');
            first = false;
            if (kv.Value > 1) sb.Append($"{kv.Key}  ×{kv.Value}");
            else              sb.Append(kv.Key);
        }
        Description = sb.ToString();
    }

    /// <summary>Convenience overload for non-collection callers.</summary>
    public void SetMultiNodes(System.Collections.Generic.IEnumerable<NodeViewModel> nodeVms)
    {
        // Reuse the cached scratch list rather than allocating a new
        // List<Node> per call. SetMultiNodes(IReadOnlyList<Node>) only reads
        // the list before returning, so reusing the buffer is safe.
        _multiNodesScratch.Clear();
        foreach (var v in nodeVms) if (v is not null) _multiNodesScratch.Add(v.Model);
        SetMultiNodes(_multiNodesScratch);
    }

    /// <summary>Bind to a selected Link — wire-level inspector view.</summary>
    public void SetLink(Link? link, LogicCanvasViewModel canvasContext)
    {
        Fields.Clear();
        if (link is null)
        {
            DisplayTitle = string.Empty;
            Eyebrow      = "Inspector";
            Description  = string.Empty;
            return;
        }

        var graph = canvasContext.Graph;
        var fromNode   = graph.FindNodeById(link.FromNodeId);
        var toNode     = graph.FindNodeById(link.ToNodeId);
        var fromSocket = graph.FindSocketById(link.FromSocketId);
        var toSocket   = graph.FindSocketById(link.ToSocketId);

        DisplayTitle = $"{fromNode?.Title ?? "?"} → {toNode?.Title ?? "?"}";
        Eyebrow      = "Wire";

        var kind = (fromSocket?.DataType ?? SocketDataType.Any).ToString();
        // Null-coalesce every endpoint so a wire whose node / socket
        // was deleted mid-inspection renders "?.?" instead of a bare "."
        // (string interpolation of two nulls). Mirrors DisplayTitle's "? → ?"
        // fallback on the line above.
        Description =
            $"From  {fromNode?.Title ?? "?"}.{fromSocket?.Name ?? "?"}\n" +
            $"To    {toNode?.Title ?? "?"}.{toSocket?.Name ?? "?"}\n" +
            $"Kind  {kind}";
    }
}
