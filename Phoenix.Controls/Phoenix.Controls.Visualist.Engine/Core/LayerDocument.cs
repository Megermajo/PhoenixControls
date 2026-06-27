using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// LayerDocument — in-memory mutable wrapper around <see cref="Layer"/>.
    /// Tracks dirty state and undo history for the Layer Designer (Phase 3).
    /// </summary>
    public sealed class LayerDocument : IDisposable
    {
        public Layer Layer { get; private set; }
        public string?        FilePath { get; private set; }
        public bool           IsDirty  { get; private set; }

        public event Action? OnChanged;

        private readonly Stack<string> _undo = new();
        private readonly Stack<string> _redo = new();
        private const int MaxUndoDepth = 50;

        // ── Auto-save (off by default) ───────────────────────────────────
        // When AutoSyncEnabled is true, every MarkDirty arms a debounced
        // timer; on fire the document writes itself to disk so Hub's
        // LayerWatcher broadcasts LAYER_RELOADED and OBS / preview surfaces
        // reflect the edit live. Off by default per Majo's requirement.
        // Decoupled from VisualistUserConfig so this class stays headless-
        // testable without touching the singleton.

        public bool AutoSyncEnabled { get; set; } = false;

        /// <summary>Debounce window in ms. Lower bound enforced internally so
        /// pathological zero / negative values can't spin the timer.</summary>
        public int AutoSaveDebounceMs { get; set; } = 300;

        private System.Threading.Timer? _autoSaveTimer;
        private readonly object _autoSaveGate = new();
        private bool          _disposed;

        // Captured on the first MarkDirty / SaveAs that wires up auto-save.
        // The timer thread Posts back through this so the actual serialization
        // runs on the same thread as graph/widget mutations — without it, a
        // 300ms-debounced Save() can race a user's "add node" / "drag rect"
        // action mid-mutation and silently truncate the in-memory list mid-
        // serialize. That's the "regularly forgets given nodes" symptom.
        private SynchronizationContext? _autoSaveSyncContext;

        /// <summary>Fired whenever an auto-save actually wrote to disk. Used
        /// by tests to await a debounced save without polling IsDirty.</summary>
        public event Action? OnAutoSaved;

        public LayerDocument(Layer? layer = null, string? filePath = null)
        {
            Layer    = layer ?? new Layer
            {
                Name       = "untitled",
                Resolution = new LayerResolution { Width = 1920, Height = 1080 },
                Preset     = LayerPreset.FullHD,
            };
            FilePath = filePath;
            IsDirty  = false;
        }

        public static LayerDocument Open(string path)
        {
            var layer = LayerSerializer.Read(path);
            // QC50-12 — one-shot upgrade for legacy graph attributes whose
            // serialised form has since changed (Particles.Emit Position /
            // Velocity moved from comma-CSV to per-component scalars). Runs
            // on load only; the canonical form is written back on the next
            // Save without an extra MarkDirty here — the migrator's job is
            // to keep stale .phxlayer files renderable, not force a rewrite.
            LayerGraphMigrator.Migrate(layer);
            return new LayerDocument(layer, path);
        }

        public void Save()
        {
            if (FilePath is null)
                throw new InvalidOperationException("LayerDocument has no FilePath; use SaveAs.");
            CancelAutoSave();
            LayerSerializer.Write(FilePath, Layer);
            IsDirty = false;
            OnChanged?.Invoke();
        }

        public void SaveAs(string path)
        {
            FilePath = path;
            Save();
        }

        public void MarkDirty()
        {
            IsDirty = true;
            OnChanged?.Invoke();
            ArmAutoSave();
        }

        // ── Auto-save plumbing ───────────────────────────────────────────

        private void ArmAutoSave()
        {
            if (_disposed) return;
            if (!AutoSyncEnabled) return;
            if (FilePath is null) return; // Save As required first; never silently overwrite a scratch file.
            // Capture the UI SynchronizationContext lazily so RunAutoSave can
            // marshal serialization back to the same thread that's mutating
            // the in-memory graph. Bare timer-thread serialize was racing the
            // UI's Add/Remove on Graph.Nodes, producing files missing the
            // tail of the most-recent mutation ("nodes get forgotten" symptom).
            _autoSaveSyncContext ??= SynchronizationContext.Current;
            int ms = Math.Max(50, AutoSaveDebounceMs);
            lock (_autoSaveGate)
            {
                if (_autoSaveTimer is null)
                    _autoSaveTimer = new System.Threading.Timer(_ => DispatchAutoSave(), null, ms, Timeout.Infinite);
                else
                    _autoSaveTimer.Change(ms, Timeout.Infinite);
            }
        }

        // Called on the timer thread. If we captured a UI SyncContext, Post
        // the real save onto it so serialization is single-threaded against
        // user mutations. If not (headless / test path), fall through to the
        // direct call so FlushAutoSaveForTests stays meaningful.
        private void DispatchAutoSave()
        {
            var ctx = _autoSaveSyncContext;
            if (ctx is null)
            {
                RunAutoSave();
                return;
            }
            ctx.Post(_ => RunAutoSave(), null);
        }

        private void CancelAutoSave()
        {
            lock (_autoSaveGate)
            {
                _autoSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void RunAutoSave()
        {
            if (_disposed) return;
            if (!AutoSyncEnabled || FilePath is null || !IsDirty) return;
            try
            {
                LayerSerializer.Write(FilePath, Layer);
                IsDirty = false;
                try { OnChanged?.Invoke(); } catch { }
                try { OnAutoSaved?.Invoke(); } catch { }
            }
            catch (Exception ex)
            {
                // Auto-save runs on a background thread — don't crash. Log so
                // the user can see why their drag wasn't reflected and either
                // resolve the lock or fall back to manual Ctrl+S.
                GlobalLogger.Error("LayerDocument", $"auto-save to '{FilePath}' failed", ex);
            }
        }

        /// <summary>Test hook — fires the debounced save synchronously instead
        /// of waiting for the real timer.</summary>
        internal void FlushAutoSaveForTests() => RunAutoSave();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_autoSaveGate)
            {
                try { _autoSaveTimer?.Dispose(); } catch { }
                _autoSaveTimer = null;
            }
        }

        /// <summary>True when at least one snapshot is on the undo stack.</summary>
        public bool CanUndo => _undo.Count > 0;

        /// <summary>True when at least one snapshot is on the redo stack.</summary>
        public bool CanRedo => _redo.Count > 0;

        /// <summary>
        /// Fires when <see cref="CanUndo"/> / <see cref="CanRedo"/> may have
        /// flipped — after every <see cref="PushUndo"/>, <see cref="Undo"/>,
        /// <see cref="Redo"/>. HubChrome's Edit-menu enable bridge subscribes
        /// here when the active pillar is Visualist so the menu items track
        /// the document's stacks without the chrome having to poll.
        /// </summary>
        public event EventHandler? CanExecuteChanged;

        public void PushUndo()
        {
            // Serialize can throw LayerNumericValidationException when the
            // layer holds a NaN/Infinity keyframe ( P0-7). Mirror the
            // protective pattern in Undo/Redo: catch the rejection, log it
            // non-modally, and skip the snapshot rather than letting an
            // unhandled exception abort the caller's mutation flow
            // (AddWidget / RemoveWidget call this before mutating). The undo
            // stack stays internally consistent — nothing was popped/pushed.
            string snapshot;
            try
            {
                snapshot = LayerSerializer.Serialize(Layer);
            }
            catch (Phoenix.Controls.Shared.Core.LayerNumericValidationException ex)
            {
                GlobalLogger.Log(
                    $"PushUndo skipped — layer holds {ex.Issues.Count} non-finite numeric value(s); undo snapshot not captured.",
                    "LayerDocument",
                    Phoenix.Controls.Shared.Models.LogLevel.Warning);
                return;
            }
            _undo.Push(snapshot);
            _redo.Clear();
            while (_undo.Count > MaxUndoDepth) _undo.TryPop(out _);
            RaiseCanExecuteChanged();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            // Capture the current state and pop the undo snapshot, but only
            // commit (push to redo + swap Layer) once Deserialize succeeds.
            // If it throws (corrupt snapshot / JsonException / InvalidData),
            // restore the popped snapshot so CanUndo and the undo history
            // survive — a discarded snapshot was a silent lost-edit hazard.
            string currentSnapshot = LayerSerializer.Serialize(Layer);
            string undoSnapshot = _undo.Pop();
            Layer newLayer;
            try
            {
                newLayer = LayerSerializer.Deserialize(undoSnapshot);
            }
            catch
            {
                _undo.Push(undoSnapshot);
                throw;
            }
            _redo.Push(currentSnapshot);
            Layer = newLayer;
            MarkDirty();
            RaiseCanExecuteChanged();
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            // Mirror of Undo: pop the redo snapshot first but only commit
            // once Deserialize succeeds; on failure restore the redo stack so
            // CanRedo and forward-navigation survive a corrupt snapshot.
            string currentSnapshot = LayerSerializer.Serialize(Layer);
            string redoSnapshot = _redo.Pop();
            Layer newLayer;
            try
            {
                newLayer = LayerSerializer.Deserialize(redoSnapshot);
            }
            catch
            {
                _redo.Push(redoSnapshot);
                throw;
            }
            _undo.Push(currentSnapshot);
            Layer = newLayer;
            MarkDirty();
            RaiseCanExecuteChanged();
            return true;
        }

        private void RaiseCanExecuteChanged()
        {
            // Per-handler try/catch so a faulting subscriber can't corrupt the
            // document's own undo bookkeeping. Mirrors the pattern used in
            // Architect's UndoRedoController.
            var handlers = CanExecuteChanged;
            if (handlers is null) return;
            foreach (var h in handlers.GetInvocationList())
            {
                try { ((EventHandler)h).Invoke(this, EventArgs.Empty); }
                catch { /* best-effort notification */ }
            }
        }

        public LayerWidget AddWidget(string name = "New Widget", WidgetPreset? preset = null)
        {
            PushUndo();
            var w = new LayerWidget
            {
                Name   = name,
                Rect   = new WidgetRect { X = 100, Y = 100, Width = 400, Height = 200 },
                ZIndex = Layer.Widgets.Count,
                Preset = preset,
                Triggers =
                {
                    new WidgetTrigger { Name = "onStartup", Graph = WidgetPresets.GetStartingGraph(preset), Timeline = new WidgetTimeline() },
                },
            };
            Layer.Widgets.Add(w);
            MarkDirty();
            return w;
        }

        public bool RemoveWidget(LayerWidget widget)
        {
            PushUndo();
            bool removed = Layer.Widgets.Remove(widget);
            if (removed) MarkDirty();
            return removed;
        }

        public string DisplayName => Path.GetFileNameWithoutExtension(FilePath ?? Layer.Name);
    }
}
