using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.ScriptPanel;

public sealed class ScriptViewModel : ObservableObject, IDisposable
{
    private readonly IScriptHostMonitor _monitor;
    // Per-VM dispatcher pump, ctor-injected by PanelFactory.
    private readonly UiDispatcherPump _ui;
    private readonly Dictionary<string, ScriptRowVm> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    // Incremental errored-row counter feeding SummaryText. StatusChanged
    // fires per run start/complete AND per RAM/CPU metric sample, so the
    // previous Rows.Count(r => r.HasError) LINQ scans (two per update) ran
    // O(n) continuously across all loaded scripts. HasError derives purely
    // from Status.State, so comparing the single mutated row's old vs new
    // value keeps the counter exact.
    private int _erroredCount;

    // Dispatcher-hop coalescing. StatusChanged fires
    // synchronously on the producer thread per status/metric sample across
    // all loaded scripts; the previous code did one _ui.Post per update.
    // Accumulate pending statuses on the producer side and enqueue a single
    // FlushPending per dispatcher tick — subsequent arrivals join the
    // in-flight batch instead of scheduling a new hop. Mirrors
    // SystemLogViewModel.FlushPending.
    private readonly object _pendingLock = new();
    private List<ScriptStatus> _pendingUpdates = new();
    private bool _flushScheduled;

    public ScriptViewModel(IScriptHostMonitor monitor, DispatcherQueue? dispatcher)
    {
        _monitor = monitor;
        _ui = new UiDispatcherPump(dispatcher);
        _monitor.StatusChanged += OnStatusChanged;
        // Fan-out: every subscriber to StatusChanged gets every update;
        // no primary-subscriber gate.
        foreach (var s in _monitor.Snapshot())
        {
            var row = new ScriptRowVm(s);
            _byPath[s.Path] = row;
            Rows.Add(row);
            if (row.HasError) _erroredCount++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.StatusChanged -= OnStatusChanged;
    }

    public ObservableCollection<ScriptRowVm> Rows { get; } = new();

    /// <summary>
    /// Runtime theme-swap hook. Drops the static row-tint brush
    /// cache and re-raises StateBrush / RowBackgroundBrush PropertyChanged
    /// on every row so x:Bind OneWay picks up the new theme without
    /// rebuilding the row VMs.
    /// </summary>
    public void RefreshBrushes()
    {
        ScriptRowVm.InvalidateBrushCache();
        // Snapshot before iterating: FlushPending() can synchronously
        // Rows.Add() on this same UI thread (UiDispatcherPump HasThreadAccess
        // fast-path), which would mutate Rows mid-enumeration and throw
        // InvalidOperationException.
        foreach (var row in Rows.ToList()) row.RefreshBrushes();
    }

    public string SummaryText => string.Format(
        Localizer.T("panel.scripts.summary_format", "{0} loaded · {1} errored"),
        Rows.Count, _erroredCount);

    public Task ToggleEnabledAsync(ScriptRowVm row)
        => _monitor.SetEnabledAsync(row.Status.Name, !row.Status.Enabled);

    public Task ReloadAsync(ScriptRowVm row)
        => _monitor.ReloadAsync(row.Status.Name);

    public Task OpenInArchitectAsync(ScriptRowVm row)
        => _monitor.OpenInArchitectAsync(row.Status.Name);

    private void OnStatusChanged(object? sender, ScriptStatus updated)
    {
        // Fan-out: this VM is one of N subscribers.
        bool needsSchedule;
        lock (_pendingLock)
        {
            _pendingUpdates.Add(updated);
            needsSchedule = !_flushScheduled;
            _flushScheduled = true;
        }
        if (!needsSchedule) return;

        // HasThreadAccess fast-path baked into Post.
        _ui.Post(FlushPending);
    }

    private void FlushPending()
    {
        List<ScriptStatus> batch;
        lock (_pendingLock)
        {
            batch = _pendingUpdates;
            _pendingUpdates = new List<ScriptStatus>();
            _flushScheduled = false;
        }
        // Cache the SummaryText inputs (loaded count + errored count) before
        // applying the batch so we only re-raise the getter when one of them
        // actually changed — an existing-row Status swap frequently leaves
        // both unchanged.
        int oldLoaded = Rows.Count;
        int oldErrored = _erroredCount;
        foreach (var updated in batch)
        {
            if (_byPath.TryGetValue(updated.Path, out var row))
            {
                bool hadError = row.HasError;
                row.Status = updated;
                if (row.HasError != hadError) _erroredCount += row.HasError ? 1 : -1;
            }
            else
            {
                var row2 = new ScriptRowVm(updated);
                _byPath[updated.Path] = row2;
                Rows.Add(row2);
                if (row2.HasError) _erroredCount++;
            }
        }
        if (Rows.Count != oldLoaded || _erroredCount != oldErrored)
            Raise(nameof(SummaryText));
    }
}
