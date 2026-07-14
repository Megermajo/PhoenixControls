using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.WinUI.Contracts;
using RuntimeGiveaway = Phoenix.Controls.Shared.Models.Giveaway;
using RuntimeEntrant = Phoenix.Controls.Shared.Models.GiveawayEntrant;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Adapts the Hub-runtime GiveawayService.Instance to the UI-facing
// IGiveawaySource contract: maps runtime models → DTO records, formats the
// derived "avg / last entry" display strings, and forwards the service's change
// events. The page binds to this; the giveaway.* script commands drive the same
// underlying service, so a chat-driven entry shows up live on the page.
//
// Events arrive on whatever thread the service raised them (a script thread or
// the UI thread); the page ViewModel marshals to the dispatcher before touching
// XAML — this bridge just forwards.
internal sealed class GiveawaySourceBridge : IGiveawaySource, IDisposable
{
    private readonly GiveawayService _svc = GiveawayService.Instance;
    private long? _defaultId;
    private bool _disposed;

    public GiveawaySourceBridge()
    {
        _svc.GiveawaysChanged += OnGiveawaysChanged;
        _svc.EntrantsChanged += OnEntrantsChanged;
        _ = RefreshDefaultAsync();
    }

    public long? DefaultGiveawayId => _defaultId;

    public event EventHandler? GiveawaysChanged;
    public event EventHandler<long>? EntrantsChanged;

    private async void OnGiveawaysChanged(object? sender, EventArgs e)
    {
        await RefreshDefaultAsync().ConfigureAwait(false);
        GiveawaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEntrantsChanged(object? sender, long id) => EntrantsChanged?.Invoke(this, id);

    private async Task RefreshDefaultAsync()
    {
        try { _defaultId = await _svc.GetDefaultIdAsync().ConfigureAwait(false); }
        catch { /* best-effort cache; the pin badge just won't show until next refresh */ }
    }

    public async Task<IReadOnlyList<GiveawayInfo>> ListAsync(CancellationToken ct = default)
        => (await _svc.ListAsync().ConfigureAwait(false)).Select(ToInfo).ToList();

    public async Task<GiveawayInfo?> GetAsync(long id, CancellationToken ct = default)
    {
        var g = await _svc.GetAsync(id).ConfigureAwait(false);
        return g is null ? null : ToInfo(g);
    }

    public async Task<IReadOnlyList<GiveawayEntrantInfo>> GetEntrantsAsync(long id, CancellationToken ct = default)
        => (await _svc.GetEntrantsAsync(id).ConfigureAwait(false)).Select(ToEntrant).ToList();

    public async Task<IReadOnlyList<GiveawayActivityEntry>> GetActivityAsync(long id, CancellationToken ct = default)
        => (await _svc.GetActivityAsync(id).ConfigureAwait(false))
            .Select(a => new GiveawayActivityEntry(FormatClock(a.Time), a.Kind, a.Message)).ToList();

    public async Task<GiveawayInfo> CreateAsync(string title, CancellationToken ct = default)
        => ToInfo(await _svc.CreateAsync(title, "broadcaster").ConfigureAwait(false));

    public Task CloseAsync(long id, CancellationToken ct = default) => _svc.CloseAsync(id);

    public async Task<(string WinnerName, int WinnerTickets, int OddsOneIn)?> DrawWinnerAsync(long id, CancellationToken ct = default)
    {
        var r = await _svc.DrawWinnerAsync(id).ConfigureAwait(false);
        return r is null ? null : (r.Value.Name, r.Value.Tickets, r.Value.OddsOneIn);
    }

    public Task SetDefaultAsync(long id, bool isDefault, CancellationToken ct = default)
        => _svc.SetDefaultAsync(id, isDefault);

    // ── runtime → DTO mapping ───────────────────────────────────────────────
    private static GiveawayInfo ToInfo(RuntimeGiveaway g) => new(
        g.Id, g.Key, g.Title, ParseStatus(g.Status), g.OpenedAt, g.ClosedAt, g.OpenedBy, g.IsDefault,
        g.Entrants, g.Tickets, FormatAvg(g.Tickets, g.Entrants), FormatLastEntry(g.LastEntry),
        g.SubscriberBonusFactor, g.ModBonusFactor, g.CapPerUser, g.TicketPrice, g.Winners);

    private static GiveawayEntrantInfo ToEntrant(RuntimeEntrant e)
        => new(e.Username, ParseRole(e.Role), e.Tickets, FormatLastEntry(e.LastEntry));

    private static GiveawayStatus ParseStatus(string? s) => (s ?? "open").ToLowerInvariant() switch
    {
        "closed" => GiveawayStatus.Closed,
        "drawn"  => GiveawayStatus.Drawn,
        _        => GiveawayStatus.Open,
    };

    private static ChatRole ParseRole(string? r) => (r ?? "viewer").ToLowerInvariant() switch
    {
        "broadcaster"            => ChatRole.Broadcaster,
        "mod"                    => ChatRole.Mod,
        "vip"                    => ChatRole.Vip,
        "sub" or "subscriber"    => ChatRole.Sub,
        _                        => ChatRole.Viewer,
    };

    private static string FormatAvg(int tickets, int entrants)
        => entrants <= 0 ? "—" : (tickets / (double)entrants).ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatClock(string? iso)
    {
        if (!string.IsNullOrWhiteSpace(iso) &&
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return iso ?? "";
    }

    private static string FormatLastEntry(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso) ||
            !DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return "—";
        var span = DateTime.UtcNow - dt.ToUniversalTime();
        if (span.TotalSeconds < 1)  return "now";
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h";
        return dt.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.GiveawaysChanged -= OnGiveawaysChanged;
        _svc.EntrantsChanged -= OnEntrantsChanged;
    }
}
