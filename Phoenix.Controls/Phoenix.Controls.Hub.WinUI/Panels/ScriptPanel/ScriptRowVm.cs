using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.ScriptPanel;

public sealed class ScriptRowVm : ObservableObject
{
    // Hub UI sweep P1 — exclude Twitch.ChatMessage event-trigger nodes
    // upstream of ScriptStatusDot. The actual filter is enforced one layer
    // up in Hub.Core's ScriptManager.IsEventTriggerNode, which drops
    // "Twitch.ChatMessage" titles from the Trigger-phase lifecycle stream
    // (project_script_window_redesign.md — chat would flood the view).
    // ScriptHostMonitor.Live + the early-return on Phase==Trigger in this
    // file's ScriptViewModel.OnStatusChanged keep the dot inert for
    // decorative pings. This constant documents the contract at the
    // consumer end so a future change at any layer has a grep hit.
    public const string ExcludedTriggerNodeType = "Twitch.ChatMessage";

    private ScriptStatus _status;

    public ScriptRowVm(ScriptStatus status)
    {
        _status = status;
    }

    public ScriptStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(ScriptName));
            Raise(nameof(IsEnabled));
            Raise(nameof(State));
            Raise(nameof(StateText));
            Raise(nameof(StateBrush));
            Raise(nameof(CpuText));
            Raise(nameof(RamText));
            Raise(nameof(RunsText));
            Raise(nameof(RowOpacity));
            Raise(nameof(RowBackgroundBrush));
            Raise(nameof(HasError));
            Raise(nameof(EnableActionLabel));
            // B5 (audit 2026-05-24) — re-raise QueueDepthText on every
            // Status change. StatusChanged fires on slot acquire/release
            // (Running / Finished / Queued / Error transitions), and the
            // depth is exactly the value that drifts across those —
            // re-raising here is the minimum-cost way to keep the "N / M"
            // text live without hooking the semaphore directly.
            Raise(nameof(QueueDepthText));
        }
    }

    // Context-menu label flips with current Enabled state.
    public string EnableActionLabel => _status.Enabled
        ? Localizer.T("panel.scripts.menu.disable", "Disable")
        : Localizer.T("panel.scripts.menu.enable",  "Enable");

    // .phx file name (mono column)
    public string ScriptName => $"{_status.Name}.phx";
    public bool IsEnabled => _status.Enabled;
    public ScriptState State => _status.State;

    public string StateText => _status.State switch
    {
        ScriptState.Idle    => Localizer.T("panel.scripts.state.idle",    "idle"),
        ScriptState.Running => Localizer.T("panel.scripts.state.running", "running"),
        ScriptState.Errored => Localizer.T("panel.scripts.state.errored", "errored"),
        ScriptState.Queued  => Localizer.T("panel.scripts.state.queued",  "queued"),
        _                   => "—",
    };

    // Perf-review H9 (2026-05-14): static brush singletons. Previous code
    // allocated a fresh SolidColorBrush on every getter call — and the Status
    // setter raises 11 PropertyChanged events, so a busy script panel was
    // churning the brush count every metric tick. RowBackgroundBrush was the
    // worst offender: a new transparent SolidColorBrush per row per Raise.
    // QC12-03: errored-row tint uses StatusRed (#E66464) family at 6% alpha.
    //
    //  Errored-row tint resolves through the StatusRedTintBrush
    // theme token (PhoenixDark.xaml). The hardcoded ARGB below is a defensive
    // fallback that matches the token literal — it only fires if the merged
    // dictionary fails to resolve the key (e.g. a test host without the
    // Shared.WinUI theme loaded). ResolveErrorRowTintBrush() caches the
    // resolved brush and InvalidateBrushCache() drops it on theme swap.
    private static readonly Brush s_transparentBrush = new SolidColorBrush(Colors.Transparent);
    private static Brush? s_errorRowTintBrush;

    private static Brush ResolveErrorRowTintBrush()
    {
        if (s_errorRowTintBrush is not null) return s_errorRowTintBrush;
        if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
            && res.TryGetValue("StatusRedTintBrush", out var resource)
            && resource is Brush b)
        {
            s_errorRowTintBrush = b;
            return b;
        }
        s_errorRowTintBrush = new SolidColorBrush(Color.FromArgb(0x0F, 0xE6, 0x64, 0x64));
        return s_errorRowTintBrush;
    }

    /// <summary>
    ///  Runtime theme-swap hook. Drops the cached errored-row tint
    /// brush and re-raises StateBrush / RowBackgroundBrush PropertyChanged
    /// so x:Bind OneWay re-resolves both against the freshly-merged theme.
    /// StateBrush goes through <see cref="ScriptStatusDot.ResolveStateBrush"/>
    /// which has no static cache — its lookup re-walks Application.Resources
    /// every call, so a re-raise alone is enough. Called by the panel VM
    /// when ActualThemeChanged on the host View fires.
    /// </summary>
    public void RefreshBrushes()
    {
        InvalidateBrushCache();
        Raise(nameof(StateBrush));
        Raise(nameof(RowBackgroundBrush));
    }

    /// <summary>
    ///  Drop the cached errored-row tint brush so the next
    /// <see cref="ResolveErrorRowTintBrush"/> call re-walks Application.Resources
    /// against the freshly-merged theme dictionary.
    /// </summary>
    public static void InvalidateBrushCache()
    {
        s_errorRowTintBrush = null;
    }

    // QC12-02 — route through the shared resolver on ScriptStatusDot so the
    // row text and the dot fill cannot drift apart. Previously the dot used
    // OkBrush (muted green) for Running while this property also used
    // OkBrush — but the design mandate is StatusGreenBrush (§2 Accents),
    // and the dot was being migrated separately. Funnel both through one
    // function and they stay aligned by construction.
    public Brush StateBrush => ScriptStatusDot.ResolveStateBrush(_status.State);

    // Hub UI sweep 2026-05-22 — format technical readouts with
    // InvariantCulture so the decimal separator is always "." regardless of
    // the user's OS locale. Pre-fix on a German install the CpuText rendered
    // as "40,9ms" (comma decimal) while RamText kept its no-decimal
    // formatting, producing the locale-mixed look Majo's 0.10.6 report
    // called out. The runtime number model is technical; the user is
    // reading milliseconds and bytes, not currency.
    public string CpuText => _status.State == ScriptState.Errored
        ? "—"
        : string.Format(CultureInfo.InvariantCulture, "{0:0.#}ms", _status.LastCpu.TotalMilliseconds);

    public string RamText => _status.LastRamBytes <= 0
        ? "—"
        : FormatBytes(_status.LastRamBytes);

    public string RunsText => _status.RunCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// B5 (audit 2026-05-24) — concurrent-execution slot indicator
    /// formatted as "N / M" where N is the in-flight count and M the
    /// configured concurrency cap (or "∞" when unlimited). Reads
    /// ScriptManager.GetQueueDepth on every PropertyChanged tick — that
    /// method is a one-line CurrentCount read so the cost is negligible.
    /// Falls back to "—" if the ScriptManager singleton isn't reachable
    /// (test rig / shutdown).
    /// </summary>
    public string QueueDepthText
    {
        get
        {
            try
            {
                var (used, total) = ScriptManager.Instance.GetQueueDepth(_status.Name);
                string totalText = total == int.MaxValue
                    ? "∞" // ∞
                    : total.ToString(CultureInfo.InvariantCulture);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} / {1}",
                    used.ToString(CultureInfo.InvariantCulture),
                    totalText);
            }
            catch (ObjectDisposedException)
            {
                // Expected during shutdown — the ScriptManager singleton's
                // semaphores are disposed before the panel tears down.
                return "—"; // em dash
            }
            catch (Exception ex)
            {
                // Unexpected — surface for diagnostics rather than swallowing
                // logic errors / null refs silently behind the "—" fallback.
                GlobalLogger.Error("ScriptRowVm", "QueueDepthText", ex);
                return "—"; // em dash
            }
        }
    }

    public double RowOpacity => _status.Enabled ? 1.0 : 0.45;

    public bool HasError => _status.State == ScriptState.Errored;

    // QC12-03 — errored-row highlight uses the StatusRed traffic-light color
    // (Design_Orders §2 Accents = 230,100,100 = #E66464) at ~6% alpha so the
    // tint matches the dot family rather than the muted ErrBrush. The static
    // singleton (s_errorRowTintBrush) preserves the perf-review H9 fix —
    // avoiding fresh brush allocation per PropertyChanged tick.
    public Brush RowBackgroundBrush =>
        _status.State == ScriptState.Errored ? ResolveErrorRowTintBrush() : s_transparentBrush;


    private static string FormatBytes(long bytes)
    {
        // Hub UI sweep 2026-05-22 — InvariantCulture so the decimal separator
        // is always "." regardless of the user's OS locale (see CpuText above).
        var inv = CultureInfo.InvariantCulture;
        if (bytes < 1024) return string.Format(inv, "{0}B", bytes);
        var kb = bytes / 1024.0;
        if (kb < 1024) return string.Format(inv, "{0:0.#}KB", kb);
        var mb = kb / 1024.0;
        return string.Format(inv, "{0:0.#}MB", mb);
    }
}
