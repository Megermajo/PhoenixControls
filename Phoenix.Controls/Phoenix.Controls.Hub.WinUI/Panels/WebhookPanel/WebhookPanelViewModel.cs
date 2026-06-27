using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.WebhookPanel;

/// <summary>
/// C16 (audit/winui-regressions-2026-05-24) — VM for the Hub
/// "Recent Webhooks" tail panel.
///
/// Source of events: <see cref="HUDServer.OnWebhookFired"/>. The audit
/// brief proposed two paths — tail GlobalLogger filtered to the
/// "HUDServer" / Communication tier, or add a dedicated event. We
/// took the event path (cleaner: avoids parsing log strings, no
/// timestamp-rounding mismatch between the log's "yyyy-MM-dd HH:mm:ss"
/// stamp and the wall-clock at which the fire was accepted).
///
/// Lifetime: subscribes to <c>HubHost.HUD.OnWebhookFired</c> on
/// construction and unsubscribes on <see cref="Dispose"/>. Each
/// pop-out gets its own VM so multiple instances run independent
/// buffers (no shared mutable state across pop-outs).
///
/// Buffer: a rolling MaxRows-deep newest-first list. Per the audit
/// brief — last 100 webhook fires. Older entries fall off when the
/// 101st arrives.
/// </summary>
public sealed class WebhookPanelViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 100;

    private readonly UiDispatcherPump _ui;
    private readonly Action<WebhookActivity> _onWebhookFiredHandler;
    private bool _disposed;
    private string _searchText = string.Empty;

    /// <summary>
    /// Newest-first buffer of every fire we've observed since
    /// construction. <see cref="VisibleRows"/> is the search-filtered
    /// projection of this; the two stay in sync via
    /// <see cref="RebuildVisibleRowsFromBuffer"/>.
    /// </summary>
    private readonly LinkedList<WebhookRowVm> _buffer = new();

    public WebhookPanelViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);

        // Capture as a strongly-typed delegate so the matching `-=` in
        // Dispose actually removes the subscription (closure-over-method-
        // group += / -= dance silently produces non-equal delegates that
        // never unsubscribe). The handler marshals back to the UI thread
        // via the per-VM pump before touching VisibleRows.
        _onWebhookFiredHandler = OnWebhookFired;

        // Subscribe through HubHost so the VM works regardless of which
        // HUDServer instance is current (test rigs can rebind). When the
        // server isn't constructed yet (extremely early boot) the
        // subscription no-ops; the next fire after HUDServer.Construct()
        // would not feed this VM, but in practice HubBootstrapper builds
        // HUDServer before any panel can be displayed.
        var hud = HubHost.HUD;
        if (hud is not null)
        {
            hud.OnWebhookFired += _onWebhookFiredHandler;
        }
        else
        {
            // Log + carry on. The panel still renders an empty list and
            // the operator can use the System Log instead — better than
            // an inert dialog. Surfacing the absence at System tier
            // avoids the recurring-rejection modal anti-pattern.
            GlobalLogger.Log(
                "WebhookPanel: HubHost.HUD is null at VM construction — webhook tail will stay empty until Hub finishes booting.",
                "WebhookPanel", Shared.Models.LogLevel.System);
        }
    }

    public ObservableCollection<WebhookRowVm> VisibleRows { get; } = new();

    /// <summary>
    /// Substring filter over <see cref="WebhookRowVm.Endpoint"/> and
    /// <see cref="WebhookRowVm.RemoteAddress"/>. Empty disables the
    /// filter. Rebuilds <see cref="VisibleRows"/> from the in-memory
    /// buffer on every change.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_searchText, next, StringComparison.Ordinal)) return;
            _searchText = next;
            RebuildVisibleRowsFromBuffer();
            Raise(nameof(SearchText));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            var hud = HubHost.HUD;
            if (hud is not null) hud.OnWebhookFired -= _onWebhookFiredHandler;
        }
        catch { /* shutdown best-effort */ }
    }

    private void OnWebhookFired(WebhookActivity activity)
    {
        if (_disposed) return;
        // Marshal to the UI thread before touching the bound collection.
        // Webhook fires can land on any thread (HttpListener accept-loop
        // worker pool); WinUI raises COMException if a bound
        // DependencyProperty mutates off the dispatcher.
        _ui.Post(() =>
        {
            if (_disposed) return;
            _buffer.AddFirst(new WebhookRowVm(
                activity.FiredAtUtc,
                activity.Endpoint,
                activity.RemoteAddress,
                activity.PayloadBytes));
            while (_buffer.Count > MaxRows)
            {
                _buffer.RemoveLast();
            }
            RebuildVisibleRowsFromBuffer();
        });
    }

    private void RebuildVisibleRowsFromBuffer()
    {
        VisibleRows.Clear();
        if (_searchText.Length == 0)
        {
            foreach (var row in _buffer) VisibleRows.Add(row);
            return;
        }
        foreach (var row in _buffer)
        {
            if (RowMatchesSearch(row)) VisibleRows.Add(row);
        }
    }

    private bool RowMatchesSearch(WebhookRowVm row)
    {
        return Contains(row.Endpoint) || Contains(row.RemoteAddress);

        bool Contains(string s) =>
            !string.IsNullOrEmpty(s) && s.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
