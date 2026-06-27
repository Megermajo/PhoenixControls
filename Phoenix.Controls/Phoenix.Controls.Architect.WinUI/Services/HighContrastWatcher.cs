using System;
using Phoenix.Controls.Shared.Services;
using Windows.UI.ViewManagement;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Architect-side query/subscription for the Windows high-contrast
/// accessibility setting. <see cref="Controls.ArchitectWindowChrome"/>
/// consults this on bind and on
/// <see cref="AccessibilitySettings.HighContrastChanged"/> so the coal/ember
/// chrome can collapse and the host window can fall back to the system title
/// bar — Design_Orders §4.7 mandates the fallback under HC themes.
///
/// <para>
/// Detection only — no paint code. Per the per-pillar chrome rule, each
/// pillar owns its own copy of this helper (Architect has this file; Hub
/// has the mirror copy in <c>Phoenix.Controls.Hub.WinUI/Services/</c>). It
/// lives at the pillar level rather than in <c>Shared.WinUI</c> because
/// <c>Shared.WinUI</c> targets plain <c>net8.0-windows</c> (no WinAppSDK /
/// no Windows10 SDK projection) by csproj contract — <c>AccessibilitySettings</c>
/// would not resolve there.
/// </para>
///
/// <para>
/// <see cref="AccessibilitySettings.HighContrastChanged"/> can fire on a
/// non-UI thread. The <paramref name="onChanged"/> callback is forwarded as
/// raised — callers are responsible for marshaling onto the dispatcher that
/// owns any UI elements they mutate.
/// </para>
/// </summary>
public sealed class HighContrastWatcher : IDisposable
{
    private readonly AccessibilitySettings _settings = new();
    private readonly Action _onChanged;
    private bool _subscribed;
    private bool _disposed;

    public HighContrastWatcher(Action onChanged)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        //  Subscribing to AccessibilitySettings.HighContrastChanged
        // can throw a COMException on unpackaged WinUI 3 hosts where the
        // platform event source can't be wired from the MainWindow ctor
        // thread (observed 2026-05-19 — boot crash with empty Exception.Message
        // surfaced as a blank BOOT FAILED splash). Treat the event hookup as
        // best-effort: on failure we degrade to poll-only via IsHighContrast,
        // which has its own try/catch and stays usable. _subscribed gates the
        // Dispose detach so we don't try to remove a handler that was never
        // added (which would throw the same COM HR a second time).
        try
        {
            _settings.HighContrastChanged += OnHighContrastChanged;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            // Process-wide dedupe: the same COMException fires for every
            // pillar that hosts a chrome (Hub MainWindow + Architect chrome
            // when its pillar tab binds), producing identical back-to-back
            // log lines at the same millisecond. Architect & Hub each own
            // their own watcher class (per-pillar chrome rule), so the gate
            // can't be a static on either type — use an AppDomain slot
            // keyed by a shared string so the first failure logs and the
            // rest stay quiet for the process lifetime.
            if (AppDomain.CurrentDomain.GetData("PhoenixControls.HCWatcher.FallbackLogged") is null)
            {
                AppDomain.CurrentDomain.SetData("PhoenixControls.HCWatcher.FallbackLogged", true);
                GlobalLogger.Log(
                    "HC live-watch unavailable (poll fallback active): " + ex.Message,
                    "HighContrastWatcher", Phoenix.Controls.Shared.Models.LogLevel.System);
            }
        }
    }

    /// <summary>True if the user has a Windows high-contrast theme active.</summary>
    public bool IsHighContrast
    {
        get
        {
            try { return _settings.HighContrast; }
            catch { return false; }
        }
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object e)
    {
        if (_disposed) return;
        try { _onChanged(); }
        catch { /* callback's job to handle its own failures */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_subscribed) return;
        try { _settings.HighContrastChanged -= OnHighContrastChanged; }
        catch { /* best-effort detach */ }
    }
}
