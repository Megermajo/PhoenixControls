using System.Linq;
using Microsoft.UI.Xaml.Controls.Primitives;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Foundation;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Spawn-palette partial — opens a SpawnPaletteFlyout at the right-clicked
// canvas point, listens for Selected, then NodeRegistry.CreateNode +
// LogicCanvasView.AddNode at the canvas-space cursor.
public sealed partial class LogicCanvasView
{
    private SpawnPaletteFlyout? _spawnFlyout;
    private Point _spawnCanvasPoint;

    // 200 ms open-path guard (arch-perf P1). The spawn-palette is reachable
    // from three triggers: RMB-context-menu "Spawn Node…", Ctrl+Space, and a
    // wire-drop onto empty canvas. Without a guard, rapid double-RMB / spam
    // Ctrl+Space could re-enter ShowAt mid-open and produce two flyouts
    // stacked at the same anchor (or worse, a flyout with stale context if
    // SetContext fires between two ShowAt calls). The debounce matches the
    // SearchBox typing-debounce inside the flyout itself for symmetry.
    private System.DateTime _lastSpawnOpenUtc = System.DateTime.MinValue;
    private static readonly System.TimeSpan SpawnOpenDebounce = System.TimeSpan.FromMilliseconds(200);

    // sourceDirection lets the COMPATIBLE filter inside the
    // SpawnPaletteFlyout walk the right pin pool (Inputs vs Outputs) when
    // the wire-drop originated from a non-Output socket. Default Output
    // preserves the pre-fix behaviour for callers that don't thread the
    // direction.
    private void ShowSpawnPalette(Point hostPoint,
                                  SocketDataType? compatibilityFilter = null,
                                  SocketType? sourceDirection = null)
    {
        var nowUtc = System.DateTime.UtcNow;
        if (nowUtc - _lastSpawnOpenUtc < SpawnOpenDebounce)
        {
            // Coalesce — refresh the would-be anchor so the next allowed open
            // lands at the latest cursor position, but skip the actual ShowAt.
            _spawnCanvasPoint = HostToCanvas(hostPoint);
            return;
        }
        _lastSpawnOpenUtc = nowUtc;

        _spawnCanvasPoint = HostToCanvas(hostPoint);

        if (_spawnFlyout is null)
        {
            _spawnFlyout = new SpawnPaletteFlyout();
            _spawnFlyout.Selected             += OnSpawnPaletteSelected;
            _spawnFlyout.FrameActionRequested += OnSpawnPaletteFrameAction;
            // Re-focus the canvas on every dismiss so F / Del / Home / arrow
            // keys keep working after the flyout closes.
            _spawnFlyout.Closed += (_, _) =>
            {
                try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
                catch { /* never break dismissal on a focus failure */ }
            };
        }

        // Snapshot the macro-graph context — when the canvas is editing a
        // sub-graph that already has Macro.Entry/Exit, the palette suppresses
        // the duplicate entry rather than letting the user spawn a second
        // (which would just route to the silent reject path).
        bool isMacroGraph  = false;
        bool hasMacroEntry = false;
        bool hasMacroExit  = false;
        if (_vm is not null)
        {
            foreach (var n in _vm.Graph.Nodes)
            {
                if (n.Title == "Macro.Entry") { isMacroGraph = true; hasMacroEntry = true; }
                else if (n.Title == "Macro.Exit") { isMacroGraph = true; hasMacroExit  = true; }
                if (hasMacroEntry && hasMacroExit) break;
            }
        }

        _spawnFlyout.SetContext(compatibilityFilter, isMacroGraph, hasMacroEntry, hasMacroExit, sourceDirection);

        // Closed-cleanup hook — drops the pending wire-drop source if the
        // user dismisses the palette without picking. Pre-fix the pending
        // source survived to the next palette open and tried to auto-wire
        // an unrelated spawn.
        _spawnFlyout.ClosedCleanup = () => _pendingWireDropSource = null;

        _spawnFlyout.ShowAt(HostRoot, new FlyoutShowOptions
        {
            Position           = hostPoint,
            ShowMode           = FlyoutShowMode.Standard,
            // Auto lets WinUI flip the placement
            // relative to the cursor anchor to keep the (tall) palette on-screen.
            // The prior hard-coded BottomEdgeAlignedRight clipped the results
            // list when the palette opened near the bottom / right screen edge.
            Placement          = FlyoutPlacementMode.Auto,
        });
    }

    private void OnSpawnPaletteSelected(object? sender, string templateTitle)
    {
        //  Runtime singleton gate. The palette UI suppresses the
        // duplicate Macro.Entry/Exit entry via SetContext, but a stale snapshot
        // (rapid spawn before the context refreshes) or a future non-suppressing
        // caller could still reach here, so reject at the spawn gate too. Logs
        // via GlobalLogger (System) — never a modal — per the standing rule.
        if (WouldDuplicateSubGraphSingleton(templateTitle))
            return;

        var node = NodeRegistry.CreateNode(
            templateTitle,
            new System.Drawing.Point((int)_spawnCanvasPoint.X, (int)_spawnCanvasPoint.Y));
        if (node is null) return;

        //  Macro.Call / Process.Spawn carry the referenced sub-graph's
        // actual Entry/Exit socket signature, not just the template default,
        // so a macro/process with custom inputs/outputs is wireable the moment
        // it lands. Resolve the referenced macro/process from the spawned
        // node's attributes (the palette only sets MacroId/ProcessId for nodes
        // dragged from the rail; a bare palette spawn has no id yet, in which
        // case the lookup misses and the template default stands until the
        // sub-graph is opened). Sync BEFORE AddNode so the NodeView is built
        // from the final socket list.
        if (_vm is not null)
        {
            if (templateTitle == "Macro.Call"
                && node.Attributes.TryGetValue("MacroId", out var mid)
                && !string.IsNullOrEmpty(mid))
            {
                var macro = _vm.Graph.Macros.FirstOrDefault(m => m.MacroId == mid);
                // Reuse the ViewModel's canonical macro→call socket-sync.
                if (macro is not null) _vm.RefreshMacroCallSockets(node, macro);
            }
            else if (templateTitle == "Process.Start"
                && node.Attributes.TryGetValue("ProcessId", out var pid)
                && !string.IsNullOrEmpty(pid))
            {
                var proc = _vm.Graph.Processes.FirstOrDefault(p => p.ProcessId == pid);
                if (proc is not null) SyncProcessSpawnNodeSockets(node, proc);
            }
        }

        AddNode(node, _spawnCanvasPoint.X, _spawnCanvasPoint.Y);

        // Pre-T15 ShowFilteredSpawnMenu auto-wire — when the palette was
        // opened by a wire-drop on empty canvas, find the spawned node's
        // VM and connect the source socket to its first compatible input.
        if (_pendingWireDropSource is not null && _vm is not null)
        {
            NodeViewModel? newVm = null;
            foreach (var n in _vm.Nodes) if (n.Model.Id == node.Id) { newVm = n; break; }
            if (newVm is not null)
            {
                var compat = FindFirstCompatibleInput(_pendingWireDropSource, newVm);
                if (compat is not null) TryCreateLink(_pendingWireDropSource, compat);
                else
                    // Spawned node had no
                    // compatible socket for the dropped wire — log instead of
                    // silently leaving the wire unconnected.
                    GlobalLogger.Log(
                        $"Wire not connected: spawned \"{newVm.Title}\" has no socket compatible with {_pendingWireDropSource.DataType}",
                        "Architect.Canvas", LogLevel.System);
            }
            _pendingWireDropSource = null;
        }
    }

    /// <summary>
    /// Spawn palette routed a frame action (Add Comment Frame / Add
    /// Placeholder Frame).
    /// <para>
    ///  Frame footprint restored to the pre-T15 320×220 default with a
    /// (-10, -30) position offset (both audit runs flagged the WinUI 240×160 /
    /// no-offset regression). The offset lands the frame's header strip just
    /// above-left of the cursor so the user's intended anchor sits inside the
    /// frame body rather than clipped at the top-left corner. The pre-T15
    /// AutoSizeFrameToOverlap overlap-avoidance grow pass is followed up by
    /// <see cref="GrowFrameToCoverOverlaps"/> so the new frame doesn't bisect
    /// a node it visually overlaps.
    /// </para>
    /// </summary>
    private void OnSpawnPaletteFrameAction(object? sender, string frameKind)
    {
        if (_vm is null) return;
        double fx = _spawnCanvasPoint.X - 10;
        double fy = _spawnCanvasPoint.Y - 30;
        if (string.Equals(frameKind, "placeholder", System.StringComparison.OrdinalIgnoreCase))
        {
            var fvm = AddFrame(fx, fy, 320, 220,
                "Placeholder", ArchitectCanvasPalette.PlaceholderFrameDefault);
            if (fvm is not null)
            {
                fvm.Model.IsPlaceholder = true;
                GrowFrameToCoverOverlaps(fvm);
            }
        }
        else
        {
            var fvm = AddFrame(fx, fy, 320, 220,
                "Comment", ArchitectCanvasPalette.CommentFrameDefault);
            if (fvm is not null) GrowFrameToCoverOverlaps(fvm);
        }
    }

    /// <summary>
    ///  Self-contained replacement for the pre-T15 AutoSizeFrameToOverlap
    /// pass (absent from the WinUI port). After a frame spawns at its default
    /// footprint, grow it so any node whose bounds it already partially overlaps
    /// is fully enclosed (with a small pad), instead of bisecting that node.
    /// Only grows toward nodes the frame ALREADY touches — it never sweeps the
    /// whole graph into one frame. No-op when the frame overlaps nothing.
    /// </summary>
    private void GrowFrameToCoverOverlaps(FrameViewModel frame)
    {
        if (_vm is null || frame is null) return;
        const int pad = 16;
        var b = frame.Model.Bounds;
        int minX = b.Left, minY = b.Top, maxX = b.Right, maxY = b.Bottom;
        bool grew = false;
        foreach (var n in _vm.Nodes)
        {
            double nx = n.X, ny = n.Y, nw = n.Width, nh = n.Height;
            // Intersection test against the ORIGINAL footprint — only nodes the
            // spawned frame already overlaps pull the bounds outward.
            bool overlaps = nx < b.Right && nx + nw > b.Left
                         && ny < b.Bottom && ny + nh > b.Top;
            if (!overlaps) continue;
            grew = true;
            if (nx - pad < minX) minX = (int)System.Math.Floor(nx - pad);
            if (ny - pad < minY) minY = (int)System.Math.Floor(ny - pad);
            if (nx + nw + pad > maxX) maxX = (int)System.Math.Ceiling(nx + nw + pad);
            if (ny + nh + pad > maxY) maxY = (int)System.Math.Ceiling(ny + nh + pad);
        }
        if (!grew) return;
        // SetBounds raises X/Y/Width/Height change-notification so the
        // FrameView re-renders to the grown rectangle.
        frame.SetBounds(minX, minY, maxX - minX, maxY - minY);
        _vm.OnGraphMutated();
    }

    /// <summary>
    ///  Owned-file singleton gate shared by the palette spawn path and
    /// <c>CollapseSelectionToMacro</c>. Macro.Entry / Macro.Exit /
    /// Process.Entry / Process.Exit are singletons within their owning
    /// sub-graph (the exporter binds via FirstOrDefault, so a duplicate is
    /// silently dropped, breaking socket binding). Reject a second one here and
    /// log via GlobalLogger (System) — never a modal — so the slip is
    /// discoverable without interrupting authoring. Returns true when the spawn
    /// should be refused.
    /// <para>
    /// Self-contained equivalent of the pre-T15 Canvas.Helpers
    /// <c>TryRejectMacroSingleton</c>, which was never ported to WinUI. The
    /// AddNode (LogicCanvasView.xaml.cs) and ApplyPaste
    /// (LogicCanvasView.Clipboard.cs) paths are owned by other sprints' files;
    /// this guard covers the spawn-palette and collapse-to-macro paths this
    /// sprint owns.
    /// </para>
    /// </summary>
    private bool WouldDuplicateSubGraphSingleton(string? templateTitle)
        => WouldDuplicateSubGraphSingleton(_vm?.Graph, templateTitle);

    /// <summary>
    /// Graph-explicit overload so callers operating on a sub-graph other than
    /// the live canvas graph (e.g. CollapseSelectionToMacro building a fresh
    /// macro.Graph) can validate against that graph.
    /// </summary>
    private static bool WouldDuplicateSubGraphSingleton(Graph? graph, string? templateTitle)
    {
        if (graph is null || string.IsNullOrEmpty(templateTitle)) return false;
        bool isSingleton = templateTitle is "Macro.Entry" or "Macro.Exit"
                                          or "Process.Entry" or "Process.Exit";
        if (!isSingleton) return false;
        if (!graph.Nodes.Any(n => n.Title == templateTitle)) return false;

        string container = templateTitle.StartsWith("Process.", System.StringComparison.Ordinal)
            ? "process" : "macro";
        GlobalLogger.Log(
            $"Spawn rejected: '{templateTitle}' is a singleton inside its owning {container} " +
            "(the graph already has one). Duplicate would be dropped at export.",
            "Architect.Canvas",
            LogLevel.System);
        return true;
    }
}
