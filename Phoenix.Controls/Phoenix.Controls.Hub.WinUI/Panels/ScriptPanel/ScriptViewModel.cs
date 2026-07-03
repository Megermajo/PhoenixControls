using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.ScriptPanel;

public sealed class ScriptViewModel : ObservableObject, IDisposable
{
    private readonly IScriptHostMonitor _monitor;
    // Per-VM dispatcher pump, ctor-injected by PanelFactory.
    private readonly UiDispatcherPump _ui;
    private readonly Dictionary<string, ScriptRowVm> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

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
        // Snapshot before iterating: OnStatusChanged.Apply() can synchronously
        // Rows.Add() on this same UI thread (UiDispatcherPump HasThreadAccess
        // fast-path), which would mutate Rows mid-enumeration and throw
        // InvalidOperationException.
        foreach (var row in Rows.ToList()) row.RefreshBrushes();
    }

    public string SummaryText
    {
        get
        {
            var loaded = Rows.Count;
            var errored = Rows.Count(r => r.HasError);
            return $"{loaded} loaded · {errored} errored";
        }
    }

    public Task ToggleEnabledAsync(ScriptRowVm row)
        => _monitor.SetEnabledAsync(row.Status.Name, !row.Status.Enabled);

    public Task ReloadAsync(ScriptRowVm row)
        => _monitor.ReloadAsync(row.Status.Name);

    public Task OpenInArchitectAsync(ScriptRowVm row)
        => _monitor.OpenInArchitectAsync(row.Status.Name);

    private void OnStatusChanged(object? sender, ScriptStatus updated)
    {
        // Fan-out: this VM is one of N subscribers.
        void Apply()
        {
            // Cache the SummaryText inputs (loaded count + errored count) before
            // mutating Rows so we only re-raise the O(n) getter when one of them
            // actually changed — an existing-row Status swap frequently leaves
            // both unchanged.
            var oldLoaded = Rows.Count;
            var oldErrored = Rows.Count(r => r.HasError);

            if (_byPath.TryGetValue(updated.Path, out var row))
            {
                row.Status = updated;
            }
            else
            {
                var row2 = new ScriptRowVm(updated);
                _byPath[updated.Path] = row2;
                Rows.Add(row2);
            }

            var newLoaded = Rows.Count;
            var newErrored = Rows.Count(r => r.HasError);
            if (newLoaded != oldLoaded || newErrored != oldErrored)
                Raise(nameof(SummaryText));
        }

        // HasThreadAccess fast-path baked into Post.
        _ui.Post(Apply);
    }
}
