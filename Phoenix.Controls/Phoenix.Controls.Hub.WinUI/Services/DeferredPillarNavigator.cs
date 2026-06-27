using System;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

// HubBootstrapper.BootAsync needs an IPillarNavigator at construction time
// (passed into ScriptHostMonitor) but Hub.MainWindow — the real navigator —
// can't exist until BootAsync completes (the splash window owns the
// dispatcher queue while bootstrapping). This deferred navigator lets the
// caller pass a placeholder during boot and attach the real implementation
// once the MainWindow is constructed. Calls before the real navigator is
// attached log + drop; the only caller is
// ScriptHostMonitor.OpenInArchitectAsync, which can't fire until the user
// clicks a panel row — which can't happen before MainWindow is up.
public sealed class DeferredPillarNavigator : IPillarNavigator
{
    private IPillarNavigator? _inner;

    public void Attach(IPillarNavigator navigator)
        => _inner = navigator ?? throw new ArgumentNullException(nameof(navigator));

    public void NavigateTo(PillarKind kind, string? openTarget = null)
    {
        if (_inner is null)
        {
            GlobalLogger.Log(
                $"PillarNavigator: NavigateTo({kind}) before MainWindow attached — dropped.",
                "DeferredPillarNavigator", Phoenix.Controls.Shared.Models.LogLevel.System);
            return;
        }
        _inner.NavigateTo(kind, openTarget);
    }
}
