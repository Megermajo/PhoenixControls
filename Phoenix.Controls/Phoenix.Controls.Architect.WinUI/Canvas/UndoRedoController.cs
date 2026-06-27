using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Snapshot-based undo/redo. Each Push() captures the current graph as a
// JSON string; Undo() pops, swaps with the present, restores; Redo() inverts.
//
// Whole-graph snapshots, not GraphDelta — the WinForms canvas's GraphDelta
// hierarchy is more efficient but considerably more code. For Architect.WinUI
// at typical graph sizes (under ~200 nodes) the per-snapshot serialise cost
// is sub-millisecond and the memory cost is bounded by MaxHistoryDepth.
//
// CRITICAL: the controller's Push/Undo/Redo all *replace* the underlying
// Graph reference because the deserialised graph is a fresh object.
// Use the constructor's <paramref name="apply"/> callback to swap the graph
// into the host (LogicCanvasViewModel.LoadGraph) so node/link VM collections
// rebind in lockstep.
//
//  — Operation entries: in addition to whole-graph snapshot
// entries, callers may push callback-driven *operations* via
// <see cref="CreateOperation"/>. These carry their own Undo / Redo
// callbacks and live in the same undo stack as graph snapshots so Ctrl+Z
// walks both kinds of edits in interleaved chronological order. Used by
// the Databank browser to make cell edits reversible without round-
// tripping the (graph-only) JSON snapshot through the DB.
public sealed class UndoRedoController
{
    //  Restored to 1000 to match the WinForms baseline
    // (Canvas.UndoRedo.cs MaxUndoStackDepth). The WinUI port had silently cut
    // it to 100 — a 10x reduction with no justification — so users could undo
    // 90% fewer operations than the prior shell. The baseline's note holds:
    // 1000 frames is plenty for real-world authoring (typical sessions are
    // under 50), and the secondary MaxHistoryBytes budget below still bounds
    // worst-case memory for large graphs, so restoring the count cap costs
    // nothing in the common case while honouring the authoring guarantee.
    private const int MaxHistoryDepth = 1000;

    //  Secondary cap on the accumulated size of the
    // JSON graph snapshots on the undo stack. Each Push serializes the WHOLE
    // graph, so a deep history of a large (e.g. 1000-node) graph could pin
    // 100 x ~1 MB ≈ 100 MB. This budget trims the OLDEST entries early when the
    // accumulated snapshot text exceeds it — newest entries (the ones the user
    // is most likely to undo to) are always kept, and at least one entry
    // survives even if a single snapshot exceeds the budget. For typical
    // graphs (tens of nodes → tens of KB per snapshot) the count cap dominates
    // and this never triggers. Avoids the correctness risk of diff-based
    // snapshots while bounding worst-case memory.
    private const long MaxHistoryBytes = 64L * 1024 * 1024; // 64 MB

    private readonly Func<Graph> _capture;
    private readonly Action<Graph> _apply;
    // Mixed stack: each entry is either a JSON graph snapshot (string)
    // or an UndoOperation (callback pair). The discriminator is the
    // runtime type of the boxed object — kept simple to avoid a sealed
    // class hierarchy for a two-case union.
    private readonly Stack<object> _undo = new();
    private readonly Stack<object> _redo = new();
    private bool _suppressed;

    //  Running total of the UTF-16 byte size of every JSON
    // snapshot string currently on the _undo stack (UndoOperation entries count
    // as 0). Maintained incrementally by Push / CreateOperation / TrimUndo /
    // Reset / Undo / Redo so TrimUndo's MaxHistoryBytes fast-path is O(1)
    // instead of re-summing the whole stack on every push. Invariant:
    // _undoBytes == Σ (s.Length * 2) over every `string s` in _undo.
    private long _undoBytes;

    private static readonly JsonSerializerOptions s_opts = new()
    {
        Converters = { new ArgbColorConverter() },
        WriteIndented = false,
    };

    public UndoRedoController(Func<Graph> capture, Action<Graph> apply)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _apply   = apply   ?? throw new ArgumentNullException(nameof(apply));
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// True while <see cref="Undo"/> / <see cref="Redo"/> are in the middle of
    /// applying a snapshot. The shared-history rewire in 0.10.0 reuses the
    /// AVM-owned controller across the parent canvas and every open
    /// SubGraphWindow; the canvas's GraphLoaded handler used to call
    /// <see cref="Reset"/> unconditionally on every <c>_vm.LoadGraph(g)</c>,
    /// which would have wiped the redo stack the moment Undo replaced the
    /// graph. Callers (LogicCanvasView.OnVmGraphLoaded) check this flag and
    /// skip Reset when the load is the controller's own Apply.
    /// </summary>
    public bool IsApplying { get; private set; }

    /// <summary>
    /// Fires whenever <see cref="CanUndo"/> / <see cref="CanRedo"/> may have
    /// flipped — i.e. after every <see cref="Push"/>, <see cref="Undo"/>,
    /// <see cref="Redo"/>, and <see cref="Reset"/>. HubChrome's Edit-menu
    /// Opening hook polls CanUndo/CanRedo each time the menu pops, but
    /// pillar-side observers (status strip badges, toolbars) can subscribe
    /// here for push-style updates without polling.
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Capture the current graph onto the undo stack. Clears the redo stack —
    /// any prior redo'able states are discarded once a new branch begins.
    /// Skipped during Undo/Redo to avoid recursive snapshotting.
    /// </summary>
    public void Push()
    {
        if (_suppressed) return;
        var json = JsonSerializer.Serialize(_capture(), s_opts);
        _undo.Push(json);
        _undoBytes += (long)json.Length * 2;
        TrimUndo();
        _redo.Clear();
        RaiseCanExecuteChanged();
    }

    /// <summary>
    ///  (P1-A7) — push a callback-driven operation onto the same
    /// undo stack the graph snapshots use. <paramref name="undo"/> reverses
    /// the side effect the caller has already applied; <paramref name="redo"/>
    /// reapplies it (so the redo stack can replay the change after Undo).
    ///
    /// Callers are expected to perform the original Apply themselves
    /// (e.g. the DB write happens before this method is invoked); the
    /// controller does NOT call <paramref name="redo"/> at registration
    /// time. Both callbacks run with <see cref="IsApplying"/> set and the
    /// internal _suppressed latch on so any nested Push from within Apply
    /// is a no-op.
    /// </summary>
    public void CreateOperation(Action undo, Action redo)
    {
        if (undo is null) throw new ArgumentNullException(nameof(undo));
        if (redo is null) throw new ArgumentNullException(nameof(redo));
        if (_suppressed) return;
        _undo.Push(new UndoOperation(undo, redo));
        TrimUndo();
        _redo.Clear();
        RaiseCanExecuteChanged();
    }

    // Trim the oldest entry by rebuilding the stack from the top when the
    // undo depth exceeds the cap. Object-typed because the stack now
    // carries both JSON snapshot strings and UndoOperation entries.
    private void TrimUndo()
    {
        //  Enforce BOTH the depth cap and the byte budget,
        // using the cached _undoBytes accumulator instead of re-summing the
        // whole stack. Pre-fix this iterated every entry on EVERY Push /
        // CreateOperation (O(n) per push → quadratic over a session); the cache
        // makes the common fast-path O(1). _undoBytes is kept in lockstep with
        // _undo by every mutator, so the fast-path test below is exact.
        if (_undo.Count <= MaxHistoryDepth && _undoBytes <= MaxHistoryBytes) return;

        // Stack.ToArray() enumerates top→bottom = newest→oldest. Keep newest
        // entries until either cap would be exceeded, then drop the rest
        // (oldest). Always keep at least one entry so a single oversized
        // snapshot doesn't wipe the whole history.
        var ordered = _undo.ToArray();
        _undo.Clear();
        int kept = 0;
        long keptBytes = 0;
        var keepList = new System.Collections.Generic.List<object>(Math.Min(ordered.Length, MaxHistoryDepth));
        foreach (var o in ordered)
        {
            long sz = o is string s ? (long)s.Length * 2 : 0;
            bool overCount = kept >= MaxHistoryDepth;
            bool overBytes = kept >= 1 && keptBytes + sz > MaxHistoryBytes;
            if (overCount || overBytes) break;
            keepList.Add(o);
            kept++;
            keptBytes += sz;
        }
        // Re-push oldest-kept first so the newest entry ends up on top.
        for (int i = keepList.Count - 1; i >= 0; i--) _undo.Push(keepList[i]);
        // Re-establish the invariant: _undoBytes == Σ bytes of the kept set.
        _undoBytes = keptBytes;
    }

    /// <summary>Reset both stacks — call on graph load so a fresh file starts at undo depth 0.</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _undoBytes = 0; //  keep the accumulator in step.
        RaiseCanExecuteChanged();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var entry = _undo.Pop();
        //  Keep the byte accumulator in step with
        // the pop — TrimUndo's O(1) fast path depends on it (see _undoBytes).
        if (entry is string poppedSnap) _undoBytes -= (long)poppedSnap.Length * 2;
        //  Track whether the redo mirror was pushed so a
        // failure can roll it back symmetrically with the popped undo entry.
        bool redoPushed = false;
        _suppressed = true;
        IsApplying = true;
        try
        {
            switch (entry)
            {
                case UndoOperation op:
                    // Operation entries: capture the symmetric redo callback
                    // onto the redo stack BEFORE invoking undo, so a
                    // faulting undo can't strand the redo entry.
                    _redo.Push(op);
                    redoPushed = true;
                    op.Undo();
                    break;
                case string json:
                {
                    // Snapshot entries: serialise the present graph onto
                    // the redo stack, then deserialise + apply the popped
                    // historical state.
                    var current = JsonSerializer.Serialize(_capture(), s_opts);
                    _redo.Push(current);
                    redoPushed = true;
                    //  Pre-fix this used
                    // `?? new Graph()`, so a deserialisation that threw OR
                    // returned null silently replaced the live graph with an
                    // EMPTY one — total, irreversible data loss disguised as a
                    // successful undo. The baseline WinForms ApplyDeltaInverse
                    // guarded with `if (restored != null)` and skipped apply on
                    // null. Restore that: only apply a non-null graph; on null,
                    // log and leave the present state untouched.
                    var graph = JsonSerializer.Deserialize<Graph>(json, s_opts);
                    if (graph is null)
                    {
                        GlobalLogger.Log(
                            $"undo.snapshot-null: deserialised undo snapshot was null; skipping restore to avoid clobbering the live graph (json len={json.Length}).",
                            source: "Architect.UndoRedoController",
                            level: LogLevel.System);
                    }
                    else
                    {
                        _apply(graph);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            //  The entry was popped BEFORE the try; an
            // uncaught throw here would permanently lose that undo state AND
            // propagate to the dispatcher (the keyboard handlers have no
            // try/catch). Roll back to a consistent state: pop the redo mirror
            // we just pushed (if any) and restore the popped entry to the undo
            // stack so the user can retry, then report failure instead of
            // crashing.
            if (redoPushed && _redo.Count > 0) _redo.Pop();
            _undo.Push(entry);
            if (entry is string restoredSnap) _undoBytes += (long)restoredSnap.Length * 2;
            GlobalLogger.Error("Architect.UndoRedoController", "Undo failed; undo entry restored", ex);
            return false;
        }
        finally { _suppressed = false; IsApplying = false; }
        RaiseCanExecuteChanged();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var entry = _redo.Pop();
        //  Track the undo mirror push for symmetric rollback.
        bool undoPushed = false;
        // Snapshot the byte delta this re-push would add so a failure can undo
        // the accumulator change too.
        long undoBytesAdded = 0;
        _suppressed = true;
        IsApplying = true;
        try
        {
            switch (entry)
            {
                case UndoOperation op:
                    // Symmetric mirror of Undo: push the operation back
                    // onto the undo stack before replaying the forward
                    // side effect.
                    _undo.Push(op);
                    undoPushed = true;
                    op.Redo();
                    break;
                case string json:
                {
                    var current = JsonSerializer.Serialize(_capture(), s_opts);
                    _undo.Push(current);
                    undoPushed = true;
                    undoBytesAdded = (long)current.Length * 2;
                    _undoBytes += undoBytesAdded;
                    //  Same null-guard as Undo: never
                    // replace the live graph with an empty one on a failed
                    // deserialise.
                    var graph = JsonSerializer.Deserialize<Graph>(json, s_opts);
                    if (graph is null)
                    {
                        GlobalLogger.Log(
                            $"redo.snapshot-null: deserialised redo snapshot was null; skipping restore to avoid clobbering the live graph (json len={json.Length}).",
                            source: "Architect.UndoRedoController",
                            level: LogLevel.System);
                    }
                    else
                    {
                        _apply(graph);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            //  Roll back the undo mirror and restore the
            // popped redo entry so Redo can be retried, then report failure.
            if (undoPushed && _undo.Count > 0)
            {
                _undo.Pop();
                _undoBytes -= undoBytesAdded;
            }
            _redo.Push(entry);
            GlobalLogger.Error("Architect.UndoRedoController", "Redo failed; redo entry restored", ex);
            return false;
        }
        finally { _suppressed = false; IsApplying = false; }
        RaiseCanExecuteChanged();
        return true;
    }

    private void RaiseCanExecuteChanged()
    {
        // Per-handler try/catch so a faulting subscriber (e.g. a chrome menu
        // hook that disposed mid-flight) can't corrupt the controller's own
        // undo bookkeeping.
        var handlers = CanExecuteChanged;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((EventHandler)h).Invoke(this, EventArgs.Empty); }
            catch { /* best-effort notification — never let a subscriber kill the controller */ }
        }
    }

    /// <summary>
    ///  — callback-driven entry on the undo stack. Carries its
    /// own Undo / Redo so the controller can reverse side effects that
    /// don't live in the captured Graph (e.g. SQLite cell writes that
    /// JSON snapshots would miss).
    /// </summary>
    private sealed class UndoOperation
    {
        public UndoOperation(Action undo, Action redo)
        {
            Undo = undo;
            Redo = redo;
        }

        public Action Undo { get; }
        public Action Redo { get; }
    }

    /// <summary>
    /// Round-trip System.Drawing.Color via ARGB int — same wire format
    /// GraphSerializer uses for .phxg files. Keeps the colour fields on
    /// Node.HeaderColor / Socket.Color / Frame.FrameColor
    /// from collapsing to Color.Empty across deserialisation.
    /// </summary>
    private sealed class ArgbColorConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            if (reader.TokenType == JsonTokenType.Number) return Color.FromArgb(reader.GetInt32());
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                int a = 255, r = 0, g = 0, b = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var p = reader.GetString();
                    reader.Read();
                    int v = reader.GetInt32();
                    switch (p) { case "A": case "a": a = v; break; case "R": case "r": r = v; break; case "G": case "g": g = v; break; case "B": case "b": b = v; break; }
                }
                return Color.FromArgb(a, r, g, b);
            }
            return Color.White;
        }
        public override void Write(Utf8JsonWriter w, Color v, JsonSerializerOptions o) => w.WriteNumberValue(v.ToArgb());
    }
}
