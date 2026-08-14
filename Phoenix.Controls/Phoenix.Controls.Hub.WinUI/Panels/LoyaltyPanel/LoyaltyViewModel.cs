using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Catalog = Phoenix.Controls.Hub.Core.BuiltInCommandCatalog;
using CommandInfo = Phoenix.Controls.Hub.Core.ToolCommandInfo;
using LoyaltySvc = Phoenix.Controls.Hub.Core.LoyaltyService;
using SbLink = Phoenix.Controls.Hub.Core.WS;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// ViewModel for the Hub Loyalty page — the button-side front-end onto the same
/// viewer-points economy the loyalty.* script nodes drive. Like the Timer VM
/// (and unlike Giveaway) it reaches <see cref="LoyaltySvc.Instance"/> DIRECTLY
/// for reads AND writes and subscribes to its ConfigChanged / BalancesChanged /
/// Activity events — Hub.WinUI already references the Hub runtime and the
/// service is always-on, so no cross-lane seam is needed.
///
/// Config editing model: the whole <see cref="LoyaltyConfig"/> is deep-cloned
/// into a private working copy on construction. Every field edits that clone in
/// place and schedules a debounced <c>UpdateConfigAsync</c> (which replaces +
/// persists + re-ensures the wallet tables). Because UpdateConfigAsync assigns
/// our clone as the live config, a self-triggered ConfigChanged is detected by
/// reference-equality and skipped; a foreign ConfigChanged rebuilds the fields.
/// Loyalty is the one tool whose service does NOT clone on save, which is why
/// <c>ReferenceEquals</c> is a valid self-save test here and nowhere else.
///
/// Since the house-shell rebuild this VM additionally owns:
///
///   * <see cref="SelectedSection"/> — the six retired Pivot tabs, now the
///     detail column's segmented sections.
///   * <see cref="SelectedViewerList"/> — which of BALANCES / LEDGER /
///     LEADERBOARD the 0.9* live column shows.
///   * <see cref="StatusPulsing"/> — the predicate behind the header band's
///     state phrase (Enabled &amp;&amp; Streamer.bot connected), consumed by the
///     view's <c>SyncHeader</c>.
/// </summary>
public sealed class LoyaltyViewModel : ObservableObject, IDisposable
{
    private readonly LoyaltySvc _svc = LoyaltySvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    private readonly DispatcherQueueTimer? _viewerTimer;

    private LoyaltyConfig _working;
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;

    public LoyaltyViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        _working = Clone(_svc.Config);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            _viewerTimer = dispatcher.CreateTimer();
            _viewerTimer.Interval = TimeSpan.FromMilliseconds(500);
            _viewerTimer.IsRepeating = false;
            _viewerTimer.Tick += (_, _) => _ = RefreshViewersAsync();
        }

        BuildAll();

        _svc.ConfigChanged += OnConfigChanged;
        _svc.BalancesChanged += OnBalancesChanged;
        _svc.Activity += OnActivity;

        // The pill's "degraded · Streamer.bot down" state is the only place the
        // page says the watch-time earner pays nobody while the socket is down
        // (ScriptManager.Loyalty returns an empty active-viewer list outright).
        // Reading IsConnected once at construction would freeze that state, so
        // the pill follows the socket. Fires on the socket's thread — marshal.
        SbLink.Instance.OnConnectionStatusChanged += OnSbConnectionChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.BalancesChanged -= OnBalancesChanged;
        _svc.Activity -= OnActivity;
        SbLink.Instance.OnConnectionStatusChanged -= OnSbConnectionChanged;
        _saveTimer?.Stop();
        _viewerTimer?.Stop();
        // Flush any pending edit so the last keystroke isn't lost on close.
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(() => _svc.UpdateConfigAsync(_working),
                    "LoyaltyViewModel", "final config flush"));
        }
    }

    // ── Bound collections (rebuilt wholesale on external reload) ─────────
    public ObservableCollection<LoyaltyLabeledField> CurrencyFields { get; } = new();
    public ObservableCollection<LoyaltyFieldGroup> EarnGroups { get; } = new();
    public ObservableCollection<LoyaltyCommandRowVm> Commands { get; } = new();
    public ObservableCollection<LoyaltyLabeledField> CommandOptions { get; } = new();
    public ObservableCollection<LoyaltyGameVm> Games { get; } = new();
    public ObservableCollection<LoyaltyRewardRowVm> Rewards { get; } = new();
    public ObservableCollection<LoyaltyLabeledField> OverlayFields { get; } = new();

    public ObservableCollection<LoyaltyBalanceRowVm> Balances { get; } = new();
    public ObservableCollection<LoyaltyLedgerRowVm> Ledger { get; } = new();
    public ObservableCollection<LoyaltyStandingRowVm> Leaderboard { get; } = new();

    // Empty-state visibilities for the three Viewers lists — computed from the
    // collection counts, re-raised on the UI thread after each RefreshViewersAsync
    // repopulation so the muted empty block shows only when a list is empty.
    public Visibility BalancesEmptyVisibility    => Balances.Count    == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LedgerEmptyVisibility      => Ledger.Count      == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LeaderboardEmptyVisibility => Leaderboard.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RewardsEmptyVisibility     => Rewards.Count     == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── Chat-command reference block ────────────────────────────────────
    //
    // Loyalty has by far the widest verb surface in the suite — nine economy commands,
    // five games, the raffle's join word, and four verbs that no field on this page ever
    // showed: the raffle's draw / cancel sub-verbs and the duel's accept / decline.
    // Those four are now editable further down; this block is where the streamer SEES
    // the whole set at once, with the role gate and usage shape each one really has.
    //
    // Built from the WORKING config so a renamed verb appears as soon as its box commits
    // instead of after the 400 ms save debounce and the service round-trip behind it —
    // the one surface whose whole job is "what does this tool answer to" must not be the
    // surface that lags.
    //
    // ★ A FRESH LIST EVERY TIME OR NOTHING UPDATES. ToolCommandList.Commands is a
    // dependency property and raises its change callback on reference inequality, so a
    // list mutated in place and re-assigned would render nothing. Catalog.For* allocates.
    private IReadOnlyList<CommandInfo> _chatCommands = Array.Empty<CommandInfo>();

    /// <summary>
    /// Every chat verb Loyalty answers to, as <c>BuiltInCommandCatalog</c> derives them
    /// from the working config. Re-raised whenever an edit is scheduled.
    /// </summary>
    public IReadOnlyList<CommandInfo> ChatCommands => _chatCommands;

    // Rebuilds the block and raises ONLY when the rendered rows actually changed.
    //
    // The gate earns its place here more than anywhere: ScheduleSave is the funnel for
    // every committed edit across ~150 inputs on this page — win messages, slot
    // multipliers, reward names — and a Loyalty rebuild is the full verb list plus one
    // row VM per row every time. Almost none of those edits touch a verb, a role tick,
    // an enable, or one of the two Gamble stake switches the usage shape reads.
    // ToolCommandInfo is a record struct, so Equals compares exactly the five columns
    // the streamer can see.
    private void RefreshChatCommands()
    {
        var next = Catalog.ForLoyalty(_working);
        if (SameRows(_chatCommands, next)) return;
        _chatCommands = next;
        Raise(nameof(ChatCommands));
    }

    private static bool SameRows(IReadOnlyList<CommandInfo> a, IReadOnlyList<CommandInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    // ── Detail sections (the six retired Pivot tabs) ────────────────────
    /// <summary>
    /// Section labels for the strip-side <c>ToolSectionSelector</c>, in the exact
    /// order the retired Pivot declared them. The detail column shows ONE at a
    /// time: ~150 discrete inputs stacked would bury Minigames and Rewards.
    /// </summary>
    public IList<string> SectionNames { get; } =
        new List<string>
        {
            Localizer.T("panel.loyalty.section.currency",  "Currency"),
            Localizer.T("panel.loyalty.section.earn",      "Earn"),
            Localizer.T("panel.loyalty.section.commands",  "Commands"),
            Localizer.T("panel.loyalty.section.minigames", "Minigames"),
            Localizer.T("panel.loyalty.section.rewards",   "Rewards"),
            Localizer.T("panel.loyalty.section.viewers",   "Viewers"),
        };

    /// <summary>Index of the Rewards section — the strip's ADD REWARD jumps here
    /// so the row it just created is on screen.</summary>
    public const int RewardsSectionIndex = 4;

    private int _selectedSection;

    /// <summary>Selected detail section, 0-5, indexing <see cref="SectionNames"/>.</summary>
    public int SelectedSection
    {
        get => _selectedSection;
        set
        {
            // Out-of-range is ignored rather than coerced: the selector binds
            // TwoWay and coercing would fight a source mid-initialization.
            if (value < 0 || value >= SectionNames.Count) return;
            if (!Set(ref _selectedSection, value)) return;
            Raise(nameof(CurrencyVisibility));
            Raise(nameof(EarnVisibility));
            Raise(nameof(CommandsVisibility));
            Raise(nameof(MinigamesVisibility));
            Raise(nameof(RewardsVisibility));
            Raise(nameof(ViewersVisibility));
        }
    }

    public Visibility CurrencyVisibility  => _selectedSection == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EarnVisibility      => _selectedSection == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CommandsVisibility  => _selectedSection == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MinigamesVisibility => _selectedSection == 3 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RewardsVisibility   => _selectedSection == 4 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ViewersVisibility   => _selectedSection == 5 ? Visibility.Visible : Visibility.Collapsed;

    // ── Live column: which viewer list is shown ─────────────────────────
    // The three lists used to be stacked down the Viewers tab. At 0.9* they
    // cannot be stacked and stay readable, so they share the column behind a
    // second selector. Every one of the three keeps its own column band, its own
    // empty state and its own wording.
    public IList<string> ViewerListNames { get; } =
        new List<string>
        {
            Localizer.T("panel.loyalty.viewers_list.balances",    "Balances"),
            Localizer.T("panel.loyalty.viewers_list.ledger",      "Ledger"),
            Localizer.T("panel.loyalty.viewers_list.leaderboard", "Leaderboard"),
        };

    private int _selectedViewerList;

    /// <summary>Selected live list, 0-2, indexing <see cref="ViewerListNames"/>.</summary>
    public int SelectedViewerList
    {
        get => _selectedViewerList;
        set
        {
            if (value < 0 || value >= ViewerListNames.Count) return;
            if (!Set(ref _selectedViewerList, value)) return;
            Raise(nameof(BalancesListVisibility));
            Raise(nameof(LedgerListVisibility));
            Raise(nameof(LeaderboardListVisibility));
            Raise(nameof(ViewerListCountText));
        }
    }

    public Visibility BalancesListVisibility    => _selectedViewerList == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LedgerListVisibility      => _selectedViewerList == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LeaderboardListVisibility => _selectedViewerList == 2 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Right-header count for whichever list is showing. The unit word
    /// changes with the list because the three count different things — wallets,
    /// ledger entries, and the overlay's own preview depth.</summary>
    public string ViewerListCountText => _selectedViewerList switch
    {
        0 => Balances.Count    == 1
                ? Localizer.T("panel.loyalty.viewers_list.count.wallet_one", "1 wallet")
                : string.Format(Localizer.T("panel.loyalty.viewers_list.count.wallet_many", "{0} wallets"),
                                Balances.Count.ToString(CultureInfo.InvariantCulture)),
        1 => Ledger.Count      == 1
                ? Localizer.T("panel.loyalty.viewers_list.count.entry_one", "1 entry")
                : string.Format(Localizer.T("panel.loyalty.viewers_list.count.entry_many", "{0} entries"),
                                Ledger.Count.ToString(CultureInfo.InvariantCulture)),
        _ => Leaderboard.Count == 1
                ? Localizer.T("panel.loyalty.viewers_list.count.standing_one", "1 standing")
                : string.Format(Localizer.T("panel.loyalty.viewers_list.count.standing_many", "{0} standings"),
                                Leaderboard.Count.ToString(CultureInfo.InvariantCulture)),
    };

    // ── Status pill predicate (consumed by SyncHeader in the code-behind) ─
    // Read live rather than latched: the socket can drop between two reads and
    // the pill must not claim the tool is earning through an outage. The catch
    // covers a host that never initialised the Streamer.bot link at all, where
    // "not connected" is the right answer.
    private static bool SbConnected
    {
        get { try { return SbLink.Instance.IsConnected; } catch { return false; } }
    }

    /// <summary>Only the earning state carries a liveness beat.</summary>
    public bool StatusPulsing => _working.Enabled && SbConnected;

    private void RaiseStatusPill()
    {
        Raise(nameof(StatusPulsing));
    }

    // ── Merge duplicate wallets (opt-in repair) ─────────────────────────
    // The wallet key is fixed to the login now, but rows already written under a
    // lowercased DISPLAY name cannot be paired automatically — nothing in the
    // suite maps a display name back to a login, and guessing would move money to
    // the wrong row. So the streamer nominates the pair and presses the button;
    // nothing here runs at boot. See LoyaltyService.MergeWalletsAsync.
    private string _mergeFromName    = "";
    private string _mergeIntoName    = "";
    private string _mergePreviewText = "";

    public string MergeFromName
    {
        get => _mergeFromName;
        set { if (_mergeFromName != value) { _mergeFromName = value ?? ""; Raise(); } }
    }

    public string MergeIntoName
    {
        get => _mergeIntoName;
        set { if (_mergeIntoName != value) { _mergeIntoName = value ?? ""; Raise(); } }
    }

    /// <summary>Result of the last Preview / Merge press. This is the repair's only
    /// feedback surface, so refusals are reported here verbatim rather than being a
    /// silent no-op.</summary>
    public string MergePreviewText
    {
        get => _mergePreviewText;
        private set { if (_mergePreviewText != value) { _mergePreviewText = value ?? ""; Raise(); } }
    }

    /// <summary>Reads both balances and states what a merge WOULD do. Pure read.</summary>
    public async Task PreviewWalletMergeAsync()
    {
        string from = (MergeFromName ?? "").Trim();
        string into = (MergeIntoName ?? "").Trim();
        if (from.Length == 0 || into.Length == 0)
        {
            MergePreviewText = Localizer.T("panel.loyalty.merge.result.preview_blank_pair",
                "Enter both a source and a destination row.");
            return;
        }
        // Wallet lookups collate NOCASE, so these name the SAME row — the DB layer
        // skips such a pair, and saying so here stops it reading as a silent no-op.
        if (string.Equals(from, into, StringComparison.OrdinalIgnoreCase))
        {
            MergePreviewText = Localizer.T("panel.loyalty.merge.result.same_row",
                "Source and destination are the same row — nothing to merge.");
            return;
        }
        try
        {
            long a = await _svc.GetBalanceAsync(from).ConfigureAwait(true);
            long b = await _svc.GetBalanceAsync(into).ConfigureAwait(true);
            MergePreviewText = string.Format(
                Localizer.T("panel.loyalty.merge.result.preview",
                    "'{0}' ({1:N0}) would be REMOVED and folded into '{2}' ({3:N0}), leaving '{2}' with {4:N0}."),
                from, a, into, b, a + b);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LoyaltyPanel", "Wallet merge preview", ex);
            MergePreviewText = Localizer.T("panel.loyalty.merge.result.preview_failed",
                "Preview failed — see the System Log.");
        }
    }

    /// <summary>Applies the nominated fold. Destructive on the source row, by explicit press.</summary>
    public async Task ApplyWalletMergeAsync()
    {
        string from = (MergeFromName ?? "").Trim();
        string into = (MergeIntoName ?? "").Trim();
        if (from.Length == 0 || into.Length == 0)
        {
            MergePreviewText = Localizer.T("panel.loyalty.merge.result.apply_blank_pair",
                "Enter both a source and a destination row.");
            return;
        }
        try
        {
            var applied = await _svc.MergeWalletsAsync(from, into).ConfigureAwait(true);
            if (applied.Count == 0)
            {
                MergePreviewText = string.Format(
                    Localizer.T("panel.loyalty.merge.result.nothing_merged",
                        "Nothing merged — '{0}' has no wallet row, or it names the same row as '{1}'."),
                    from, into);
            }
            else
            {
                var m = applied[0];
                MergePreviewText = string.Format(
                    Localizer.T("panel.loyalty.merge.result.merged",
                        "Merged '{0}' ({1:N0}) into '{2}' — new balance {3:N0}."),
                    m.FromName, m.FromBalance, m.ToName, m.ToBalance);
                MergeFromName = "";
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LoyaltyPanel", "Wallet merge", ex);
            MergePreviewText = Localizer.T("panel.loyalty.merge.result.failed",
                "Merge failed — see the System Log.");
        }
    }

    // ── Master switch ───────────────────────────────────────────────────
    public bool MasterEnabled
    {
        get => _working.Enabled;
        set
        {
            if (_working.Enabled == value) return;
            _working.Enabled = value;
            Raise(nameof(MasterEnabled));
            RaiseStatusPill();
            SaveNow();
        }
    }

    public string CurrencyLabel => _working.Currency.NamePlural;

    // ── Load / build ────────────────────────────────────────────────────
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshViewersAsync(force: true).ConfigureAwait(false);
    }

    private void BuildAll()
    {
        BuildCurrency();
        BuildEarn();
        BuildCommands();
        BuildGames();
        BuildRewards();
        BuildOverlay();
        // Covers the ctor and the foreign-reload path (ReloadFromConfig replaces
        // _working wholesale, so every verb, role tick and enable in the block may have
        // moved under us).
        RefreshChatCommands();
        Raise(nameof(MasterEnabled));
        Raise(nameof(CurrencyLabel));
        RaiseStatusPill();
    }

    // Rebuild fields from a fresh clone after a foreign config change.
    private void ReloadFromConfig()
    {
        _working = Clone(_svc.Config);
        BuildAll();
    }

    private void BuildCurrency()
    {
        var c = _working.Currency;
        CurrencyFields.Clear();
        CurrencyFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.currency.name.label", "Currency name"),
            Text(() => c.Name, s => c.Name = s)));
        CurrencyFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.currency.plural.label", "Plural name"),
            Text(() => c.NamePlural, s => c.NamePlural = s)));
        CurrencyFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.currency.abbrev.label", "Abbreviation"),
            Text(() => c.Abbreviation, s => c.Abbreviation = s)));
        CurrencyFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.currency.balance_table.label", "Balance table"),
            Text(() => c.BalanceTable, s => c.BalanceTable = s),
            Localizer.T("panel.loyalty.currency.balance_table.hint", "Open table — db.* nodes can read and write it.")));
        CurrencyFields.Add(LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.currency.ledger_enabled.label", "Write a ledger"),
            Bool(() => c.LedgerEnabled, v => c.LedgerEnabled = v)));
        // The gate named here is the row directly above: LedgerTableIfEnabled()
        // hands the DB layer a null ledger table whenever LedgerEnabled is false,
        // so no money call writes a row (LoyaltyService.cs:190-191).
        CurrencyFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.currency.ledger_table.label", "Ledger table"),
            Text(() => c.LedgerTable, s => c.LedgerTable = s),
            Localizer.T("panel.loyalty.currency.ledger_table.hint", "Append-only · written when Write a ledger is on.")));
        CurrencyFields.Add(LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.currency.log_watchtime.label", "Log watch-time payouts"),
            Bool(() => c.LogWatchTimePayouts, v => c.LogWatchTimePayouts = v),
            Localizer.T("panel.loyalty.currency.log_watchtime.hint", "High volume — off keeps the ledger readable.")));
    }

    private void BuildEarn()
    {
        var e = _working.Earn;
        EarnGroups.Clear();

        EarnGroups.Add(new LoyaltyFieldGroup(Localizer.T("panel.loyalty.earn.watchtime.title", "Watch-time"), new List<LoyaltyLabeledField>
        {
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.watchtime.enabled.label", "Enabled"),
                Bool(() => e.WatchTimeEnabled, v => e.WatchTimeEnabled = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.watchtime.interval.label", "Interval (minutes)"),
                Int(() => e.WatchTimeIntervalMinutes, v => e.WatchTimeIntervalMinutes = v, 1, 60)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.watchtime.amount.label", "Amount per interval"),
                Int(() => e.WatchTimeAmount, v => e.WatchTimeAmount = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.watchtime.active_only.label", "Active viewers only"),
                Bool(() => e.ActiveViewersOnly, v => e.ActiveViewersOnly = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.watchtime.online_only.label", "Online only"),
                Bool(() => e.OnlineOnly, v => e.OnlineOnly = v)),
        }));

        EarnGroups.Add(new LoyaltyFieldGroup(Localizer.T("panel.loyalty.earn.events.title", "Events"), new List<LoyaltyLabeledField>
        {
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.events.follow.label", "Follow"),
                Bool(() => e.FollowEnabled, v => e.FollowEnabled = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.follow_amount.label", "Follow amount"),
                Int(() => e.FollowAmount, v => e.FollowAmount = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.events.sub.label", "Subs / resubs"),
                Bool(() => e.SubEnabled, v => e.SubEnabled = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.sub_tier1.label", "Sub Tier 1"),
                Int(() => e.SubTier1, v => e.SubTier1 = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.sub_tier2.label", "Sub Tier 2"),
                Int(() => e.SubTier2, v => e.SubTier2 = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.sub_tier3.label", "Sub Tier 3"),
                Int(() => e.SubTier3, v => e.SubTier3 = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.sub_prime.label", "Sub Prime"),
                Int(() => e.SubPrime, v => e.SubPrime = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.events.resub_same.label", "Resub same as tier"),
                Bool(() => e.ResubSameAsTier, v => e.ResubSameAsTier = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.gift_sub.label", "Gift sub (each)"),
                Int(() => e.GiftSubAmount, v => e.GiftSubAmount = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.events.cheer.label", "Cheer"),
                Bool(() => e.CheerEnabled, v => e.CheerEnabled = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.cheer_per_100.label", "Per 100 bits"),
                Int(() => e.CheerPer100Bits, v => e.CheerPer100Bits = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.events.raid.label", "Raid"),
                Bool(() => e.RaidEnabled, v => e.RaidEnabled = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.raid_flat.label", "Raid flat"),
                Int(() => e.RaidFlat, v => e.RaidFlat = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.raid_per_viewer.label", "Raid per viewer"),
                Int(() => e.RaidPerViewer, v => e.RaidPerViewer = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.events.tip.label", "Tip"),
                Bool(() => e.TipEnabled, v => e.TipEnabled = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.tip_per_unit.label", "Tip per unit"),
                Int(() => e.TipPerUnit, v => e.TipPerUnit = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.events.first_activity.label", "First activity"),
                Bool(() => e.FirstActivityEnabled, v => e.FirstActivityEnabled = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.events.first_activity_amount.label", "First activity amount"),
                Int(() => e.FirstActivityAmount, v => e.FirstActivityAmount = v)),
        }));

        EarnGroups.Add(new LoyaltyFieldGroup(Localizer.T("panel.loyalty.earn.multipliers.title", "Multipliers"), new List<LoyaltyLabeledField>
        {
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.multipliers.subscriber.label", "Subscriber ×"),
                Dbl(() => e.SubMultiplier, v => e.SubMultiplier = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.multipliers.moderator.label", "Moderator ×"),
                Dbl(() => e.ModMultiplier, v => e.ModMultiplier = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.multipliers.vip.label", "VIP ×"),
                Dbl(() => e.VipMultiplier, v => e.VipMultiplier = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.multipliers.regular.label", "Regular ×"),
                Dbl(() => e.RegularMultiplier, v => e.RegularMultiplier = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.multipliers.regular_threshold.label", "Regular threshold (hrs)"),
                Int(() => e.RegularThresholdHours, v => e.RegularThresholdHours = v)),
            // The hint is the two ACCEPTED VALUES, not prose — translating them
            // would make the box reject what its own hint tells you to type.
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.multipliers.stacking.label", "Stacking"),
                Text(() => e.MultiplierStacking, s => e.MultiplierStacking = s), "highest | multiply"),
        }));

        var a = _working.AntiAbuse;
        EarnGroups.Add(new LoyaltyFieldGroup(Localizer.T("panel.loyalty.earn.antiabuse.title", "Anti-abuse"), new List<LoyaltyLabeledField>
        {
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.antiabuse.exclusions.label", "Extra excluded accounts"),
                Csv(() => a.ExtraExclusions, v => a.ExtraExclusions = v),
                Localizer.T("panel.loyalty.earn.antiabuse.exclusions.hint", "Comma-separated; added to the live Bot Accounts list.")),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.antiabuse.per_event_max.label", "Per-event max (0 = none)"),
                Int(() => a.PerEventMax, v => a.PerEventMax = v)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.earn.antiabuse.daily_cap.label", "Daily cap per user (0 = none)"),
                Int(() => a.DailyCapPerUser, v => a.DailyCapPerUser = v)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.earn.antiabuse.dedupe_follows.label", "De-dupe follows"),
                Bool(() => a.DedupeFollows, v => a.DedupeFollows = v)),
        }));
    }

    private void BuildCommands()
    {
        var cc = _working.Commands;
        CommandOptions.Clear();
        // The hint names COMMANDS and nothing else on purpose: AutoHandle sits below
        // the observe-only chat tap, so identity learning, the chat-activity note and
        // the first-activity award all keep running with it off
        // (ScriptManager.Loyalty.cs:349-357). A broader wording would be wrong.
        CommandOptions.Add(LoyaltyLabeledField.Check(
            Localizer.T("panel.loyalty.commands.auto_handle.label", "Auto-handle commands"),
            Bool(() => cc.AutoHandle, v => cc.AutoHandle = v),
            Localizer.T("panel.loyalty.commands.auto_handle.hint", "Off ignores every loyalty command in chat.")));
        // Label wording mirrors the Automod panel's twin of this setting
        // (AutomodView.xaml:333) — same mechanism, so same words.
        CommandOptions.Add(LoyaltyLabeledField.Check(
            Localizer.T("panel.loyalty.commands.suppress_author.label",
                "Suppress the author's chat scripts when a command is handled"),
            Bool(() => cc.SuppressAuthorDispatchWhenHandled, v => cc.SuppressAuthorDispatchWhenHandled = v)));

        Commands.Clear();
        // isModerator is the marker the view's fold reads — see
        // LoyaltyCommandRowVm.IsModerator for why the blurb can no longer be it.
        void Add(string label, string blurb, LoyaltyCommand cmd, bool isModerator = false)
            => Commands.Add(new LoyaltyCommandRowVm(label, blurb, cmd, ScheduleSave, isModerator));
        Add(Localizer.T("panel.loyalty.command.balance.label", "Balance"),
            Localizer.T("panel.loyalty.command.balance.blurb", "Show a viewer's balance"), cc.Balance);
        Add(Localizer.T("panel.loyalty.command.give.label", "Give"),
            Localizer.T("panel.loyalty.command.give.blurb", "Transfer points to another viewer"), cc.Give);
        Add(Localizer.T("panel.loyalty.command.top.label", "Top"),
            Localizer.T("panel.loyalty.command.top.blurb", "Announce the leaderboard"), cc.Top);
        Add(Localizer.T("panel.loyalty.command.watchtime.label", "Watch-time"),
            Localizer.T("panel.loyalty.command.watchtime.blurb", "Show accrued watch-time"), cc.Watchtime);
        Add(Localizer.T("panel.loyalty.command.add_points.label", "Add points"),
            Localizer.T("panel.loyalty.command.add_points.blurb", "Admin: credit a viewer (or 'all')"), cc.AddPoints, isModerator: true);
        Add(Localizer.T("panel.loyalty.command.set_points.label", "Set points"),
            Localizer.T("panel.loyalty.command.set_points.blurb", "Admin: set a viewer's balance"), cc.SetPoints, isModerator: true);
        Add(Localizer.T("panel.loyalty.command.remove_points.label", "Remove points"),
            Localizer.T("panel.loyalty.command.remove_points.blurb", "Admin: debit a viewer"), cc.RemovePoints, isModerator: true);
        Add(Localizer.T("panel.loyalty.command.wipe.label", "Wipe"),
            Localizer.T("panel.loyalty.command.wipe.blurb", "Admin: reset every balance to zero"), cc.Wipe, isModerator: true);
        Add(Localizer.T("panel.loyalty.command.redeem.label", "Redeem"),
            Localizer.T("panel.loyalty.command.redeem.blurb", "Spend points on a reward"), cc.Redeem);
    }

    private void BuildGames()
    {
        var g = _working.Games;
        Games.Clear();

        // Gamble
        var gamble = g.Gamble;
        var gambleFields = LoyaltyGameVm.BaseFields(gamble, ScheduleSave);
        gambleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.gamble.win_chance.label", "Win chance (0–1)"),
            Dbl(() => gamble.WinChance, v => gamble.WinChance = v, 1.0)));
        gambleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.gamble.payout.label", "Payout ×"),
            Dbl(() => gamble.PayoutMultiplier, v => gamble.PayoutMultiplier = v)));
        gambleFields.Add(LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.game.gamble.allow_all.label", "Allow all-in"),
            Bool(() => gamble.AllowAll, v => gamble.AllowAll = v)));
        gambleFields.Add(LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.game.gamble.allow_percent.label", "Allow percent bets"),
            Bool(() => gamble.AllowPercent, v => gamble.AllowPercent = v)));
        gambleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.gamble.win_message.label", "Win message"),
            Text(() => gamble.WinMessage, s => gamble.WinMessage = s)));
        gambleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.gamble.lose_message.label", "Lose message"),
            Text(() => gamble.LoseMessage, s => gamble.LoseMessage = s)));
        Games.Add(new LoyaltyGameVm(Localizer.T("panel.loyalty.game.gamble.title", "Gamble"),
            Bool(() => gamble.Enabled, v => gamble.Enabled = v), Roles(gamble.WhoCanPlay), gambleFields));

        // Slots
        var slots = g.Slots;
        var slotFields = LoyaltyGameVm.BaseFields(slots, ScheduleSave);
        slotFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.slots.symbols.label", "Symbols"),
            Csv(() => slots.Symbols, v => slots.Symbols = v)));
        slotFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.slots.triple_multipliers.label", "Triple multipliers"),
            CsvDbl(() => slots.TripleMultipliers, v => slots.TripleMultipliers = v)));
        slotFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.slots.any_two.label", "Any-two ×"),
            Dbl(() => slots.AnyTwoMultiplier, v => slots.AnyTwoMultiplier = v)));
        slotFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.slots.win_message.label", "Win message"),
            Text(() => slots.WinMessage, s => slots.WinMessage = s)));
        slotFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.slots.lose_message.label", "Lose message"),
            Text(() => slots.LoseMessage, s => slots.LoseMessage = s)));
        Games.Add(new LoyaltyGameVm(Localizer.T("panel.loyalty.game.slots.title", "Slots"),
            Bool(() => slots.Enabled, v => slots.Enabled = v), Roles(slots.WhoCanPlay), slotFields));

        // Duel
        var duel = g.Duel;
        var duelFields = LoyaltyGameVm.BaseFields(duel, ScheduleSave);
        duelFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.duel.win_chance.label", "Win chance (0–1)"),
            Dbl(() => duel.WinChance, v => duel.WinChance = v, 1.0)));
        duelFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.duel.accept_timeout.label", "Accept timeout (s)"),
            Int(() => duel.AcceptTimeoutSeconds, v => duel.AcceptTimeoutSeconds = v)));
        // The challenged viewer's two replies. Both were literals in the dispatcher until
        // the config gained the fields, which made them the only duel words a streamer
        // could neither see nor move — and "accept" / "decline" are common enough that a
        // channel's other bot may already own them. They sit next to the timeout because
        // that is the window they answer within; the hint names the token rather than the
        // word, since the challenge message renders the CONFIGURED verb through {accept}.
        duelFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.duel.accept_command.label", "Accept command"),
            Text(() => duel.AcceptCommand, s => duel.AcceptCommand = s),
            Localizer.T("panel.loyalty.game.duel.accept_command.hint",
                "Typed by the challenged viewer; {accept} in the challenge message renders it. Blank leaves a duel unanswerable."),
            isVerb: true));
        duelFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.duel.decline_command.label", "Decline command"),
            Text(() => duel.DeclineCommand, s => duel.DeclineCommand = s),
            Localizer.T("panel.loyalty.game.duel.decline_command.hint",
                "Blank switches the word off — an unanswered duel just expires on the timeout."),
            isVerb: true));
        duelFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.duel.challenge_message.label", "Challenge message"),
            Text(() => duel.ChallengeMessage, s => duel.ChallengeMessage = s)));
        duelFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.duel.win_message.label", "Win message"),
            Text(() => duel.WinMessage, s => duel.WinMessage = s)));
        Games.Add(new LoyaltyGameVm(Localizer.T("panel.loyalty.game.duel.title", "Duel"),
            Bool(() => duel.Enabled, v => duel.Enabled = v), Roles(duel.WhoCanPlay), duelFields));

        // Raffle (also carries a WhoCanStart role set)
        var raffle = g.Raffle;
        var raffleFields = LoyaltyGameVm.BaseFields(raffle, ScheduleSave);
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.default_winners.label", "Default winners"),
            Int(() => raffle.DefaultWinners, v => raffle.DefaultWinners = v)));
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.default_duration.label", "Default duration (s)"),
            Int(() => raffle.DefaultDurationSeconds, v => raffle.DefaultDurationSeconds = v)));
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.entry_fee.label", "Entry fee (0 = free)"),
            Int(() => raffle.EntryFee, v => raffle.EntryFee = v)));
        // The hint is the three ACCEPTED VALUES, not prose — see the Stacking note.
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.prize_mode.label", "Prize mode"),
            Text(() => raffle.PrizeMode, s => raffle.PrizeMode = s), "SplitPot | FixedEach | PotToOne"));
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.fixed_prize.label", "Fixed prize"),
            Int(() => raffle.FixedPrize, v => raffle.FixedPrize = v)));
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.join_command.label", "Join command"),
            Text(() => raffle.JoinCommand, s => raffle.JoinCommand = s), null, isVerb: true));
        // The two management SUB-verbs — a second word typed after the raffle command,
        // never a command of their own. They were literals in the dispatcher, so until
        // now the panel showed the raffle's start word and its join word while the two
        // words that actually END a raffle appeared nowhere on the page. Both are gated
        // by "Who can start", not by "Who can play" — whoever may open a raffle may close
        // it — and the CHAT COMMANDS block at the top of the page shows the full phrase.
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.draw_sub.label", "Draw sub-command"),
            Text(() => raffle.DrawSubCommand, s => raffle.DrawSubCommand = s),
            Localizer.T("panel.loyalty.game.raffle.draw_sub.hint",
                "Second word after the raffle command: closes entry and picks the winners."),
            isVerb: true, isSubVerb: true));
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.cancel_sub.label", "Cancel sub-command"),
            Text(() => raffle.CancelSubCommand, s => raffle.CancelSubCommand = s),
            Localizer.T("panel.loyalty.game.raffle.cancel_sub.hint",
                "Second word after the raffle command: calls the raffle off and refunds every entry fee."),
            isVerb: true, isSubVerb: true));
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.open_message.label", "Open message"),
            Text(() => raffle.OpenMessage, s => raffle.OpenMessage = s)));
        raffleFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.raffle.win_message.label", "Win message"),
            Text(() => raffle.WinMessage, s => raffle.WinMessage = s)));
        Games.Add(new LoyaltyGameVm(Localizer.T("panel.loyalty.game.raffle.title", "Raffle"),
            Bool(() => raffle.Enabled, v => raffle.Enabled = v),
            Roles(raffle.WhoCanPlay), raffleFields, Roles(raffle.WhoCanStart)));

        // Roulette
        var roulette = g.Roulette;
        var rouletteFields = LoyaltyGameVm.BaseFields(roulette, ScheduleSave);
        rouletteFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.roulette.straight_payout.label", "Straight payout ×"),
            Dbl(() => roulette.StraightPayout, v => roulette.StraightPayout = v)));
        rouletteFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.roulette.color_payout.label", "Color payout ×"),
            Dbl(() => roulette.ColorPayout, v => roulette.ColorPayout = v)));
        rouletteFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.roulette.win_message.label", "Win message"),
            Text(() => roulette.WinMessage, s => roulette.WinMessage = s)));
        rouletteFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.roulette.lose_message.label", "Lose message"),
            Text(() => roulette.LoseMessage, s => roulette.LoseMessage = s)));
        Games.Add(new LoyaltyGameVm(Localizer.T("panel.loyalty.game.roulette.title", "Roulette"),
            Bool(() => roulette.Enabled, v => roulette.Enabled = v), Roles(roulette.WhoCanPlay), rouletteFields));
    }

    private void BuildRewards()
    {
        Rewards.Clear();
        foreach (var r in _working.Rewards)
            Rewards.Add(new LoyaltyRewardRowVm(r, ScheduleSave, DeleteReward));
        Raise(nameof(RewardsEmptyVisibility));
    }

    private void BuildOverlay()
    {
        var o = _working.Overlay;
        OverlayFields.Clear();
        OverlayFields.Add(LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.overlay.enabled.label", "Overlay enabled"),
            Bool(() => o.Enabled, v => o.Enabled = v)));
        OverlayFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.overlay.size.label", "Leaderboard size"),
            Int(() => o.LeaderboardSize, v => o.LeaderboardSize = v, 1, 100)));
        // The hint is the three runtime TOKENS the format accepts — no prose to translate.
        OverlayFields.Add(LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.overlay.row_format.label", "Row format"),
            Text(() => o.RowFormat, s => o.RowFormat = s), "{rank} {name} {balance}"));
    }

    // ── Rewards add / delete ────────────────────────────────────────────
    public void AddReward()
    {
        var r = new LoyaltyReward
        {
            Id = Guid.NewGuid().ToString("N"),
            // Seed text for a row the streamer is about to rename, so it follows
            // the UI language. Once edited it is their data and never re-seeded.
            Name = Localizer.T("panel.loyalty.rewards.new_default_name", "New reward"),
        };
        _working.Rewards.Add(r);
        Rewards.Add(new LoyaltyRewardRowVm(r, ScheduleSave, DeleteReward));
        ScheduleSave();
        Raise(nameof(RewardsEmptyVisibility));
    }

    private void DeleteReward(LoyaltyRewardRowVm row)
    {
        _working.Rewards.Remove(row.Reward);
        Rewards.Remove(row);
        ScheduleSave();
        Raise(nameof(RewardsEmptyVisibility));
    }

    // ── Viewers: balances / ledger / leaderboard / lookup ───────────────
    private string _lookupUser = string.Empty;
    private string _lookupAmount = "100";
    private string _lookupResult = string.Empty;

    public string LookupUser { get => _lookupUser; set => Set(ref _lookupUser, value ?? string.Empty); }
    public string LookupAmount { get => _lookupAmount; set => Set(ref _lookupAmount, value ?? string.Empty); }
    public string LookupResult { get => _lookupResult; private set => Set(ref _lookupResult, value ?? string.Empty); }

    public async Task LookupBalanceAsync()
    {
        string u = (_lookupUser ?? string.Empty).Trim();
        if (u.Length == 0) { LookupResult = string.Empty; return; }
        try
        {
            long bal = await _svc.GetBalanceAsync(u).ConfigureAwait(false);
            _ui.Post(() => { if (!_disposed) LookupResult = $"{u}: {bal.ToString("N0", CultureInfo.InvariantCulture)} {_working.Currency.NamePlural}"; });
        }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyViewModel", "LookupBalanceAsync failed", ex); }
    }

    public Task LookupAddAsync() => AdjustLookupAsync("add");
    public Task LookupSetAsync() => AdjustLookupAsync("set");
    public Task LookupRemoveAsync() => AdjustLookupAsync("remove");

    private async Task AdjustLookupAsync(string op)
    {
        string u = (_lookupUser ?? string.Empty).Trim();
        if (u.Length == 0) return;
        if (!long.TryParse((_lookupAmount ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long amt))
            return;
        amt = Math.Max(0, amt);
        try
        {
            LoyaltyResult res = op switch
            {
                "add" => await _svc.AddAsync(u, amt, "hub-loyalty").ConfigureAwait(false),
                "set" => await _svc.SetAsync(u, amt, "hub-loyalty").ConfigureAwait(false),
                _     => await _svc.RemoveAsync(u, amt, "hub-loyalty").ConfigureAwait(false),
            };
            string msg = res.Ok
                ? $"{u}: {res.NewBalance.ToString("N0", CultureInfo.InvariantCulture)} {_working.Currency.NamePlural}"
                // No "(is the tool enabled?)" prompt here — the card's own hint
                // already states that adjustments need the tool on, and Outcome
                // names the actual refusal. {0} is the opcode and {1} the
                // service's own outcome word; both stay as the service spells
                // them, only the sentence around them is translated.
                : string.Format(Localizer.T("panel.loyalty.viewers.lookup.rejected", "{0} rejected — {1}"),
                                op, res.Outcome);
            _ui.Post(() => { if (!_disposed) LookupResult = msg; });
            await RefreshViewersAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyViewModel", $"AdjustLookupAsync({op}) failed", ex); }
    }

    // force: explicit refreshes (initial load, manual button, admin lookup) always rebuild;
    // a live event-driven refresh (force == false) skips ONLY the editable Balances list
    // while a "set to" draft is uncommitted, so its wholesale Clear()+re-add can't destroy
    // the focused TextBox / reset scroll mid-edit. Ledger + Leaderboard are display-only and
    // always refresh. The guarded list catches up on the next refresh once the draft clears.
    public async Task RefreshViewersAsync(bool force = false)
    {
        List<LoyaltyStanding> top;
        List<LoyaltyLedgerEntry> ledger;
        List<LoyaltyStanding> board;
        try
        {
            top = await _svc.TopAsync(200).ConfigureAwait(false);
            ledger = await _svc.LedgerAsync(200).ConfigureAwait(false);
            int size = _working.Overlay.LeaderboardSize > 0 ? _working.Overlay.LeaderboardSize : 10;
            board = await _svc.TopAsync(size).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LoyaltyViewModel", "RefreshViewersAsync failed", ex);
            return;
        }

        _ui.Post(() =>
        {
            if (_disposed) return;
            long maxTop = MaxBalance(top);
            if (force || !AnyBalanceDraftDirty())
            {
                Balances.Clear();
                foreach (var s in top) Balances.Add(new LoyaltyBalanceRowVm(s, ApplyBalanceSetAsync, maxTop));
            }
            Ledger.Clear();
            foreach (var l in ledger) Ledger.Add(new LoyaltyLedgerRowVm(l));
            Leaderboard.Clear();
            long maxBoard = MaxBalance(board);
            foreach (var s in board) Leaderboard.Add(new LoyaltyStandingRowVm(s, maxBoard));
            Raise(nameof(BalancesEmptyVisibility));
            Raise(nameof(LedgerEmptyVisibility));
            Raise(nameof(LeaderboardEmptyVisibility));
            Raise(nameof(ViewerListCountText));
        });
    }

    // True while any Balances row carries a non-empty uncommitted "set to" draft.
    private bool AnyBalanceDraftDirty()
    {
        foreach (var r in Balances)
            if (!string.IsNullOrWhiteSpace(r.SetDraft)) return true;
        return false;
    }

    // Highest balance in a standings list — the denominator for the per-row ember
    // weight bar (0 when the list is empty / all-zero, which floors every bar).
    private static long MaxBalance(List<LoyaltyStanding> rows)
    {
        long max = 0;
        foreach (var r in rows)
            if (r.Balance > max) max = r.Balance;
        return max;
    }

    private async Task ApplyBalanceSetAsync(string name, long amount)
    {
        try
        {
            var res = await _svc.SetAsync(name, amount, "hub-loyalty").ConfigureAwait(false);
            if (!res.Ok)
                GlobalLogger.Log($"Loyalty set {name}={amount} rejected: {res.Outcome} (enable the tool first).",
                    "LoyaltyViewModel", LogLevel.System);
            // BalancesChanged fires from the service on success and refreshes the list.
        }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyViewModel", "ApplyBalanceSetAsync failed", ex); }
    }

    // ── Persistence ─────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        // Every edit on the page funnels through here — the labelled fields' apply
        // delegates, the command rows, the game role sets — so this is the one place
        // that catches every change the command block renders (a verb, a role tick, a
        // per-command enable, AutoHandle, the two Gamble stake switches). The rebuild is
        // gated on the rows actually differing; see RefreshChatCommands.
        RefreshChatCommands();
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        // The master switch is the only writer that reaches SaveNow, and the catalogue
        // never folds a master toggle into a per-command Enabled — so this is normally a
        // no-op the equality gate swallows. It is here so the two save entry points stay
        // interchangeable rather than one of them silently leaving the block stale.
        RefreshChatCommands();
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (_disposed) return;
        _dirty = false;
        try { await _svc.UpdateConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyViewModel", "UpdateConfigAsync failed", ex); }
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _ui.Post(() =>
        {
            if (_disposed) return;
            // Our own UpdateConfigAsync assigns _working as the live config, so a
            // self-triggered ConfigChanged is a no-op; only a foreign change
            // (different reference) forces a field rebuild.
            if (ReferenceEquals(_svc.Config, _working)) return;
            ReloadFromConfig();
        });
    }

    private void OnBalancesChanged(object? sender, EventArgs e) => ScheduleViewerRefresh();

    /// <summary>
    /// ★ The activity payload's SIDE EFFECT, not its recording. The ring write
    /// moved down into <c>LoyaltyService.RaiseActivity</c> — this VM is built
    /// lazily when the tab is first opened, so recording here left every earn,
    /// spend and redemption before that moment out of a feed captioned "this
    /// session". Recording at the source fills the ring from Hub start.
    ///
    /// ⚠ Do NOT re-add a <c>ToolActivityRing.Record</c> here. The service already records
    /// every one of these lines; a second write would put each of them in the
    /// feed twice.
    ///
    /// What stays is the viewer-list refresh this handler has always done: a
    /// balance-changing line means the Balances / Ledger / Leaderboard lists are
    /// stale, and it is debounced by the 500 ms viewer timer.
    /// </summary>
    private void OnActivity(object? sender, string e) => ScheduleViewerRefresh();

    private void OnSbConnectionChanged(bool connected)
        => _ui.Post(() => { if (!_disposed) RaiseStatusPill(); });

    private void ScheduleViewerRefresh()
    {
        if (_viewerTimer is not null)
            _ui.Post(() => { if (!_disposed) { _viewerTimer.Stop(); _viewerTimer.Start(); } });
        else
            _ = RefreshViewersAsync();
    }

    // ── Field factory shims (fold ScheduleSave into every write) ────────
    private LoyaltyDraftField Text(Func<string> get, Action<string> set) => LoyaltyField.Text(get, set, ScheduleSave);
    private LoyaltyBoolField Bool(Func<bool> get, Action<bool> set) => LoyaltyField.Bool(get, set, ScheduleSave);
    private LoyaltyDraftField Int(Func<int> get, Action<int> set, int min = 0, int max = int.MaxValue)
        => LoyaltyField.Int(get, set, ScheduleSave, min, max);
    private LoyaltyDraftField Dbl(Func<double> get, Action<double> set, double max = double.MaxValue)
        => LoyaltyField.Double(get, set, ScheduleSave, 0.0, max);
    private LoyaltyDraftField Csv(Func<List<string>> get, Action<List<string>> set) => LoyaltyField.CsvStrings(get, set, ScheduleSave);
    private LoyaltyDraftField CsvDbl(Func<List<double>> get, Action<List<double>> set) => LoyaltyField.CsvDoubles(get, set, ScheduleSave);
    private LoyaltyRolesVm Roles(LoyaltyRoles roles) => new(roles ?? LoyaltyRoles.All(), ScheduleSave);

    private static readonly JsonSerializerOptions CloneOpts = new() { PropertyNameCaseInsensitive = true };
    private static LoyaltyConfig Clone(LoyaltyConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src, CloneOpts);
            return JsonSerializer.Deserialize<LoyaltyConfig>(json, CloneOpts) ?? new LoyaltyConfig();
        }
        catch
        {
            return new LoyaltyConfig();
        }
    }
}
