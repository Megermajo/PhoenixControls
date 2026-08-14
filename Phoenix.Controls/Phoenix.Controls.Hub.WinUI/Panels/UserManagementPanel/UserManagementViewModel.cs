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
// Aliased rather than a namespace import: Phoenix.Controls.Shared.Core also holds
// Paths / CommandManifest / the engine, none of which this VM wants in scope.
using ChatVerb = Phoenix.Controls.Shared.Core.ChatVerb;
using Presence = Phoenix.Controls.Hub.Core.ViewerPresenceService;
using ToolCommandInfo = Phoenix.Controls.Hub.Core.ToolCommandInfo;
using UserMgmtSvc = Phoenix.Controls.Hub.Core.UserManagementService;

namespace Phoenix.Controls.Hub.WinUI.Panels.UserManagementPanel;

/// <summary>
/// ViewModel for the Hub User Management page — the button-side front-end onto the
/// same config blob the UserManagementService chat tap + group overlay consume. Like
/// the Scheduling / Timer VMs it reaches <see cref="UserMgmtSvc.Instance"/> DIRECTLY
/// for reads AND writes and subscribes to its ConfigChanged / RuntimeChanged events —
/// Hub.WinUI already references the Hub runtime and the service is always-on, so no
/// cross-lane seam is needed.
///
/// Config editing model: the whole <see cref="UserManagementConfig"/> is deep-cloned
/// into per-row VMs + the toggle/message fields on construction. ONE standard group
/// keeps a membership Phoenix owns — Regular — and it is a permanent
/// <see cref="GroupRowVm"/> instance (re-seeded in place on a foreign reload) mapping
/// to the config's Regulars list plus its RegularWatchHours rule; custom groups and
/// personalized welcomes are rebuilt wholesale. Every edit schedules a debounced save;
/// the save REBUILDS a fresh config from the current rows + fields and hands it to
/// <c>UpdateConfigAsync</c> (which deep-owns it, rebuilds the group index, persists,
/// and raises ConfigChanged). A self-triggered ConfigChanged is detected by
/// reference-equality against the last pushed instance and skipped; a foreign
/// ConfigChanged reloads everything. The welcomed/greeted counters are NEVER stored in
/// the config — they are ephemeral service runtime state pulled in on load /
/// RuntimeChanged.
///
/// ★ Moderator / VIP / Subscriber are no longer edited here and no longer exist in the
/// config. They stay pickable groups everywhere a group is picked, but their
/// membership is the platform's answer, so the page renders them as three read-only
/// <see cref="PlatformGroupRowVm"/> cards that say so. See that type's doc for why the
/// cards are still shown rather than removed.
///
/// ★ The WATCH TIME section is the panel's one HUB-WIDE surface. Its three sampler
/// settings live in <see cref="ConfigManager.Current"/> (AppConfig), NOT in this
/// tool's config blob, and are written with <c>ConfigManager.SaveDeferred</c> —
/// deliberately outside <see cref="ScheduleSave"/> / <see cref="BuildConfig"/>, which
/// own the User-Management blob and nothing else. Watch time records with every
/// pre-build tool switched off; routing its settings through this tool's save would
/// tie a background data source to a tool the streamer may never enable.
///
/// Since the master-detail rebuild the page is a 1.4* settings column behind a
/// segmented section selector (<see cref="SelectedSection"/> plus the seven
/// *SectionVisibility projections) with the live viewer queue in the 0.9* column.
/// <see cref="StatusPillText"/> / <see cref="StatusPulsing"/> project
/// UserManagementService.PillState into the header band.
/// </summary>
public sealed class UserManagementViewModel : ObservableObject, IDisposable
{
    private readonly UserMgmtSvc _svc = UserMgmtSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    // Coalesces the watch-time BROWSER re-projection while the streamer types in the
    // search box. The projection walks the whole mirror, which on a channel with a
    // long history is thousands of entries — cheap once, wasteful per keystroke.
    private readonly DispatcherQueueTimer? _watchFilterTimer;

    // The one permanent standard-group row (never rebuilt — see class doc).
    private readonly GroupRowVm _regRow;

    private bool _masterEnabled;
    private bool _welcomingEnabled;
    private bool _generalWelcomeEnabled;
    private string _generalWelcomeMessage = "";
    private bool _greetingEnabled;
    private string _greetingMessage = "";
    private UserManagementConfig? _lastPushed;   // reference-equality self-trigger guard
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;
    private int _selectedSection;

    // ── Viewer queue (the tool's fourth part) ────────────────────────────
    // Config-bearing fields only. The LINE itself is not config — it lives in the
    // databank's open "Queues" table — so it is pulled from the service on load and on
    // RuntimeChanged, exactly like the welcomed/greeted counters (see class doc).
    private bool _queueEnabled;
    private string _queueName = "";
    private string _queueJoinCommand = "";
    private string _queueLeaveCommand = "";
    private string _queueListCommand = "";
    private string _queuePositionCommand = "";
    // The four moderator SUB-verbs, typed after the list command. They were string
    // literals in the parser until this pass, which made them the only queue words a
    // streamer could neither see nor change — so the panel now owns them like any
    // other verb.
    private string _queueNextSubCommand = "";
    private string _queuePickSubCommand = "";
    private string _queueRemoveSubCommand = "";
    private string _queueClearSubCommand = "";
    private int _queueMaxSize;
    private int _queueSubPriority;
    private int _queueVipPriority;
    private int _queueCooldownSeconds;
    private bool _queueOverlayEnabled;
    private int _queueOverlaySize;
    private QueueRoles _queueJoinRoles = QueueRoles.All();
    private QueueRoles _queueModRoles = QueueRoles.Mods();
    private string _queueJoinedMessage = "";
    private string _queueAlreadyMessage = "";
    private string _queueFullMessage = "";
    private string _queueLeftMessage = "";
    private string _queueNotQueuedMessage = "";
    private string _queuePositionMessage = "";
    private string _queueListMessage = "";
    private string _queueEmptyMessage = "";
    private string _queueNextMessage = "";
    private string _queueRemovedMessage = "";
    private string _queueClearedMessage = "";

    public UserManagementViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);

        // Display only — the Regular row's members save into the config's flat
        // Regulars list, never under this name, so translating the caption cannot
        // move a membership. Localized for the same reason its three platform
        // siblings below are.
        _regRow = GroupRowVm.Regular(Localizer.T("panel.usermgmt.groups.regular", "Regular"), ScheduleSave);
        StandardGroups = new ObservableCollection<GroupRowVm> { _regRow };

        // The three platform-owned groups, in the order every picker in the suite
        // lists them. Built once and never touched again — there is nothing here a
        // config change or a sample can move.
        PlatformGroups = new ObservableCollection<PlatformGroupRowVm>
        {
            new PlatformGroupRowVm(
                Localizer.T("panel.usermgmt.groups.moderator", "MODERATOR"),
                PlatformBadge,
                Localizer.T("panel.usermgmt.groups.moderator_source",
                    "Everyone your platform lists as a moderator of this channel.")),
            new PlatformGroupRowVm(
                Localizer.T("panel.usermgmt.groups.vip", "VIP"),
                PlatformBadge,
                Localizer.T("panel.usermgmt.groups.vip_source",
                    "Everyone your platform has given the VIP badge.")),
            new PlatformGroupRowVm(
                Localizer.T("panel.usermgmt.groups.subscriber", "SUBSCRIBER"),
                PlatformBadge,
                Localizer.T("panel.usermgmt.groups.subscriber_source",
                    "Everyone with an active subscription, as the platform reports it.")),
        };

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            _watchFilterTimer = dispatcher.CreateTimer();
            _watchFilterTimer.Interval = TimeSpan.FromMilliseconds(200);
            _watchFilterTimer.IsRepeating = false;
            _watchFilterTimer.Tick += (_, _) => ProjectWatchLists();
        }

        var cfg = Clone(_svc.Config);
        ApplyScalars(cfg);
        LoadFromConfig(cfg);

        _svc.ConfigChanged += OnConfigChanged;
        _svc.RuntimeChanged += OnRuntimeChanged;
        // The watch-time lists follow the background sampler, which raises this AFTER
        // it has credited minutes and refreshed the mirror — so one hook covers both
        // "who is here" and "how long they have watched". Raised on the sampler's own
        // thread; the handler marshals. Detached in Dispose.
        Presence.Instance.PresenceSampled += OnPresenceSampled;
    }

    // The non-collection fields, in ONE place. The ctor and the foreign-reload handler
    // both need the identical assignment list, and a field that only one of them sets
    // is a silent desync — the queue's dozen-plus settings made that a when, not an if.
    private void ApplyScalars(UserManagementConfig cfg)
    {
        _masterEnabled = cfg.Enabled;
        _welcomingEnabled = cfg.WelcomingEnabled;
        _generalWelcomeEnabled = cfg.GeneralWelcomeEnabled;
        _generalWelcomeMessage = cfg.GeneralWelcomeMessage ?? "";
        _greetingEnabled = cfg.GreetingEnabled;
        _greetingMessage = cfg.GreetingMessage ?? "";

        _queueEnabled = cfg.QueueEnabled;
        _queueName = cfg.QueueName ?? "";
        _queueJoinCommand = cfg.QueueJoinCommand ?? "";
        _queueLeaveCommand = cfg.QueueLeaveCommand ?? "";
        _queueListCommand = cfg.QueueListCommand ?? "";
        _queuePositionCommand = cfg.QueuePositionCommand ?? "";
        _queueNextSubCommand = cfg.QueueNextSubCommand ?? "";
        _queuePickSubCommand = cfg.QueuePickSubCommand ?? "";
        _queueRemoveSubCommand = cfg.QueueRemoveSubCommand ?? "";
        _queueClearSubCommand = cfg.QueueClearSubCommand ?? "";
        _queueMaxSize = cfg.QueueMaxSize;
        _queueSubPriority = cfg.QueueSubPriority;
        _queueVipPriority = cfg.QueueVipPriority;
        _queueCooldownSeconds = cfg.QueueCooldownSeconds;
        _queueOverlayEnabled = cfg.QueueOverlayEnabled;
        _queueOverlaySize = cfg.QueueOverlaySize;
        _queueJoinRoles = cfg.QueueJoinRoles ?? QueueRoles.All();
        _queueModRoles = cfg.QueueModRoles ?? QueueRoles.Mods();
        _queueJoinedMessage = cfg.QueueJoinedMessage ?? "";
        _queueAlreadyMessage = cfg.QueueAlreadyMessage ?? "";
        _queueFullMessage = cfg.QueueFullMessage ?? "";
        _queueLeftMessage = cfg.QueueLeftMessage ?? "";
        _queueNotQueuedMessage = cfg.QueueNotQueuedMessage ?? "";
        _queuePositionMessage = cfg.QueuePositionMessage ?? "";
        _queueListMessage = cfg.QueueListMessage ?? "";
        _queueEmptyMessage = cfg.QueueEmptyMessage ?? "";
        _queueNextMessage = cfg.QueueNextMessage ?? "";
        _queueRemovedMessage = cfg.QueueRemovedMessage ?? "";
        _queueClearedMessage = cfg.QueueClearedMessage ?? "";

        // The role VMs wrap the CLONED roles objects, so a checkbox edit mutates the
        // working config (which BuildConfig then snapshots) and never the live one.
        QueueJoinRoles = new QueueRolesVm(_queueJoinRoles, ScheduleSave);
        QueueModRoles = new QueueRolesVm(_queueModRoles, ScheduleSave);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.RuntimeChanged -= OnRuntimeChanged;
        // ★ ViewerPresenceService is an always-on process-lifetime singleton: a
        // subscriber that never detaches roots this VM and the whole page it closes
        // over for the life of Hub.
        Presence.Instance.PresenceSampled -= OnPresenceSampled;
        _saveTimer?.Stop();
        _watchFilterTimer?.Stop();
        // Flush any pending edit so the last keystroke isn't lost on close.
        if (_dirty)
        {
            _dirty = false;
            var cfg = BuildConfig();
            _lastPushed = cfg;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.UpdateConfigAsync(cfg), "UserManagementViewModel", "final config flush"));
        }
    }

    // ── Bound collections ────────────────────────────────────────────────
    /// <summary>
    /// The standard groups Phoenix still owns a membership for — today exactly one,
    /// Regular. A one-item collection rather than a bare property because the card is
    /// rendered by the same DataTemplate as a custom group, and that template hosts
    /// the GroupCard / GroupBody visual-tree walk which only an ItemsControl's
    /// unconditional container DataContext makes resolvable.
    /// </summary>
    public ObservableCollection<GroupRowVm> StandardGroups { get; }

    /// <summary>The three platform-owned groups — read-only cards, built once in the
    /// constructor.</summary>
    public ObservableCollection<PlatformGroupRowVm> PlatformGroups { get; }

    public ObservableCollection<GroupRowVm> CustomGroups { get; } = new();
    public ObservableCollection<WelcomeRowVm> Welcomes { get; } = new();

    /// <summary>The badge every platform card carries where the editable cards carry a
    /// member count. One constant so the three cards can never drift apart.</summary>
    private static string PlatformBadge
        => Localizer.T("panel.usermgmt.groups.platform_badge", "PLATFORM");

    /// <summary>The two TogglePill captions, resolved in one place. Seven pills on
    /// this page carry the same word, and WelcomeRowVm's two carry it inside a
    /// template — one key pair so a translation can never render six of the nine.
    /// Not cached: Localizer resolves against the language current at read time.</summary>
    internal static string PillOn => Localizer.T("panel.usermgmt.toggle.on", "ON");
    internal static string PillOff => Localizer.T("panel.usermgmt.toggle.off", "OFF");

    // ── Section selector (the 1.4* column shows one section at a time) ───
    // ~60 controls do not fit one scroller in a 1.4* column, and "Replies" and
    // "Groups" were unreachable-by-scroll in practice. Every section body stays
    // in the visual tree and only its Visibility flips, so a focused TextBox
    // still commits its LostFocus edit when the section changes.
    //
    // The chip LABELS are supplied by the View at Loaded, not here, so four of
    // the six can reuse lang keys the page already ships.
    public const int SectionWelcoming = 0;
    public const int SectionGreeting  = 1;
    public const int SectionQueue     = 2;
    public const int SectionOverlay   = 3;
    public const int SectionReplies   = 4;
    public const int SectionGroups    = 5;
    public const int SectionWatchTime = 6;

    public int SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_selectedSection == value) return;
            _selectedSection = value;
            Raise(nameof(SelectedSection));
            Raise(nameof(WelcomingSectionVisibility));
            Raise(nameof(GreetingSectionVisibility));
            Raise(nameof(QueueSectionVisibility));
            Raise(nameof(OverlaySectionVisibility));
            Raise(nameof(RepliesSectionVisibility));
            Raise(nameof(GroupsSectionVisibility));
            Raise(nameof(WatchTimeSectionVisibility));
        }
    }

    public Visibility WelcomingSectionVisibility => Section(SectionWelcoming);
    public Visibility GreetingSectionVisibility  => Section(SectionGreeting);
    public Visibility QueueSectionVisibility     => Section(SectionQueue);
    public Visibility OverlaySectionVisibility   => Section(SectionOverlay);
    public Visibility RepliesSectionVisibility   => Section(SectionReplies);
    public Visibility GroupsSectionVisibility    => Section(SectionGroups);
    public Visibility WatchTimeSectionVisibility => Section(SectionWatchTime);

    private Visibility Section(int index)
        => _selectedSection == index ? Visibility.Visible : Visibility.Collapsed;

    // ── Master switch (immediate save, like SchedulingViewModel.MasterEnabled) ─
    public bool MasterEnabled
    {
        get => _masterEnabled;
        set
        {
            if (_masterEnabled == value) return;
            _masterEnabled = value;
            Raise(nameof(MasterEnabled));
            // SaveNow, never ScheduleSave: the master flip bypasses the 400 ms
            // debounce on every one of the twelve tools.
            SaveNow();
        }
    }

    // ── Section gates (immediate save) ───────────────────────────────────
    // ★ All five stayed with their sections when the master moved to the strip:
    // welcoming, general-welcome, greeting, queue and overlay each still sit
    // inside the card they govern. The *Label pairs are the captions their
    // ToggleSwitches used to get free from OnContent / OffContent.
    public bool WelcomingEnabled
    {
        get => _welcomingEnabled;
        set
        {
            if (_welcomingEnabled == value) return;
            _welcomingEnabled = value;
            Raise(nameof(WelcomingEnabled));
            Raise(nameof(WelcomingEnabledLabel));
            SaveNow();
        }
    }

    public string WelcomingEnabledLabel => _welcomingEnabled ? PillOn : PillOff;

    public bool GeneralWelcomeEnabled
    {
        get => _generalWelcomeEnabled;
        set
        {
            if (_generalWelcomeEnabled == value) return;
            _generalWelcomeEnabled = value;
            Raise(nameof(GeneralWelcomeEnabled));
            Raise(nameof(GeneralWelcomeEnabledLabel));
            SaveNow();
        }
    }

    public string GeneralWelcomeEnabledLabel => _generalWelcomeEnabled ? PillOn : PillOff;

    // ── General welcome message (debounced text edit) ────────────────────
    public string GeneralWelcomeMessage
    {
        get => _generalWelcomeMessage;
        set
        {
            var v = value ?? "";
            if (_generalWelcomeMessage == v) return;
            _generalWelcomeMessage = v;
            Raise(nameof(GeneralWelcomeMessage));
            ScheduleSave();
        }
    }

    // ── First-time greeting (once ever) ──────────────────────────────────
    public bool GreetingEnabled
    {
        get => _greetingEnabled;
        set
        {
            if (_greetingEnabled == value) return;
            _greetingEnabled = value;
            Raise(nameof(GreetingEnabled));
            Raise(nameof(GreetingEnabledLabel));
            SaveNow();
        }
    }

    public string GreetingEnabledLabel => _greetingEnabled ? PillOn : PillOff;

    public string GreetingMessage
    {
        get => _greetingMessage;
        set
        {
            var v = value ?? "";
            if (_greetingMessage == v) return;
            _greetingMessage = v;
            Raise(nameof(GreetingMessage));
            ScheduleSave();
        }
    }

    // ── Viewer queue — settings ──────────────────────────────────────────
    // Section gate saves immediately (SaveNow, like the other gates); every text /
    // number field is a debounced ScheduleSave so typing doesn't hammer the databank.

    public bool QueueEnabled
    {
        get => _queueEnabled;
        set
        {
            if (_queueEnabled == value) return;
            _queueEnabled = value;
            Raise(nameof(QueueEnabled));
            Raise(nameof(QueueEnabledLabel));
            SaveNow();
        }
    }

    public string QueueEnabledLabel => _queueEnabled ? PillOn : PillOff;

    public string QueueName
    {
        get => _queueName;
        set
        {
            var v = value ?? "";
            if (_queueName == v) return;
            _queueName = v;
            Raise(nameof(QueueName));
            Raise(nameof(QueueNodeHintText));
            Raise(nameof(QueueOverlayKeyText));
            ScheduleSave();
        }
    }

    public string QueueJoinCommand
    {
        get => _queueJoinCommand;
        set
        {
            var v = value ?? "";
            if (_queueJoinCommand == v) return;
            _queueJoinCommand = v;
            Raise(nameof(QueueJoinCommand));
            Raise(nameof(QueueJoinClashText));
            Raise(nameof(QueueJoinClashVisibility));
            ScheduleSave();
        }
    }

    public string QueueLeaveCommand
    {
        get => _queueLeaveCommand;
        set
        {
            var v = value ?? "";
            if (_queueLeaveCommand == v) return;
            _queueLeaveCommand = v;
            Raise(nameof(QueueLeaveCommand));
            ScheduleSave();
        }
    }

    public string QueueListCommand
    {
        get => _queueListCommand;
        set
        {
            var v = value ?? "";
            if (_queueListCommand == v) return;
            _queueListCommand = v;
            Raise(nameof(QueueListCommand));
            Raise(nameof(QueueModVerbsHintText));
            ScheduleSave();
        }
    }

    public string QueuePositionCommand
    {
        get => _queuePositionCommand;
        set
        {
            var v = value ?? "";
            if (_queuePositionCommand == v) return;
            _queuePositionCommand = v;
            Raise(nameof(QueuePositionCommand));
            ScheduleSave();
        }
    }

    // The four moderator sub-verbs. Each one moves the hint sentence under the fields
    // as well as its own box, because that sentence is where a streamer reads the
    // shape they actually type ("!queue next"), and a sentence still naming the
    // default after a rename would be worse than no sentence at all.
    public string QueueNextSubCommand
    {
        get => _queueNextSubCommand;
        set { if (SetQueueSubVerb(ref _queueNextSubCommand, value)) Raise(nameof(QueueNextSubCommand)); }
    }

    public string QueuePickSubCommand
    {
        get => _queuePickSubCommand;
        set { if (SetQueueSubVerb(ref _queuePickSubCommand, value)) Raise(nameof(QueuePickSubCommand)); }
    }

    public string QueueRemoveSubCommand
    {
        get => _queueRemoveSubCommand;
        set { if (SetQueueSubVerb(ref _queueRemoveSubCommand, value)) Raise(nameof(QueueRemoveSubCommand)); }
    }

    public string QueueClearSubCommand
    {
        get => _queueClearSubCommand;
        set { if (SetQueueSubVerb(ref _queueClearSubCommand, value)) Raise(nameof(QueueClearSubCommand)); }
    }

    private bool SetQueueSubVerb(ref string field, string? value)
    {
        var v = value ?? "";
        if (field == v) return false;
        field = v;
        Raise(nameof(QueueModVerbsHintText));
        ScheduleSave();
        return true;
    }

    // Numeric settings ride string properties with a clamping parse, the CounterRowVm
    // idiom: WinUI's NumberBox is not used anywhere in this tool family, and a plain
    // TextBox plus a parse that REJECTS garbage (rather than zeroing) is what every
    // other numeric tool field does.
    public string QueueMaxSizeText
    {
        get => _queueMaxSize.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _queueMaxSize));
            if (_queueMaxSize == v) { Raise(nameof(QueueMaxSizeText)); return; }
            _queueMaxSize = v;
            Raise(nameof(QueueMaxSizeText));
            ScheduleSave();
        }
    }

    public string QueueSubPriorityText
    {
        get => _queueSubPriority.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _queueSubPriority));
            if (_queueSubPriority == v) { Raise(nameof(QueueSubPriorityText)); return; }
            _queueSubPriority = v;
            Raise(nameof(QueueSubPriorityText));
            ScheduleSave();
        }
    }

    public string QueueVipPriorityText
    {
        get => _queueVipPriority.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _queueVipPriority));
            if (_queueVipPriority == v) { Raise(nameof(QueueVipPriorityText)); return; }
            _queueVipPriority = v;
            Raise(nameof(QueueVipPriorityText));
            ScheduleSave();
        }
    }

    public string QueueCooldownText
    {
        get => _queueCooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _queueCooldownSeconds));
            if (_queueCooldownSeconds == v) { Raise(nameof(QueueCooldownText)); return; }
            _queueCooldownSeconds = v;
            Raise(nameof(QueueCooldownText));
            ScheduleSave();
        }
    }

    public bool QueueOverlayEnabled
    {
        get => _queueOverlayEnabled;
        set
        {
            if (_queueOverlayEnabled == value) return;
            _queueOverlayEnabled = value;
            Raise(nameof(QueueOverlayEnabled));
            Raise(nameof(QueueOverlayEnabledLabel));
            SaveNow();
        }
    }

    public string QueueOverlayEnabledLabel => _queueOverlayEnabled ? PillOn : PillOff;

    public string QueueOverlaySizeText
    {
        get => _queueOverlaySize.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Clamp(ParseInt(value, _queueOverlaySize), 1, 100);
            if (_queueOverlaySize == v) { Raise(nameof(QueueOverlaySizeText)); return; }
            _queueOverlaySize = v;
            Raise(nameof(QueueOverlaySizeText));
            ScheduleSave();
        }
    }

    public QueueRolesVm QueueJoinRoles
    {
        get => _queueJoinRolesVm;
        private set { _queueJoinRolesVm = value; Raise(nameof(QueueJoinRoles)); }
    }
    private QueueRolesVm _queueJoinRolesVm = null!;

    public QueueRolesVm QueueModRoles
    {
        get => _queueModRolesVm;
        private set { _queueModRolesVm = value; Raise(nameof(QueueModRoles)); }
    }
    private QueueRolesVm _queueModRolesVm = null!;

    // ── Viewer queue — reply templates ───────────────────────────────────
    public string QueueJoinedMessage
    {
        get => _queueJoinedMessage;
        set { if (SetQueueText(ref _queueJoinedMessage, value)) Raise(nameof(QueueJoinedMessage)); }
    }
    public string QueueAlreadyMessage
    {
        get => _queueAlreadyMessage;
        set { if (SetQueueText(ref _queueAlreadyMessage, value)) Raise(nameof(QueueAlreadyMessage)); }
    }
    public string QueueFullMessage
    {
        get => _queueFullMessage;
        set { if (SetQueueText(ref _queueFullMessage, value)) Raise(nameof(QueueFullMessage)); }
    }
    public string QueueLeftMessage
    {
        get => _queueLeftMessage;
        set { if (SetQueueText(ref _queueLeftMessage, value)) Raise(nameof(QueueLeftMessage)); }
    }
    public string QueueNotQueuedMessage
    {
        get => _queueNotQueuedMessage;
        set { if (SetQueueText(ref _queueNotQueuedMessage, value)) Raise(nameof(QueueNotQueuedMessage)); }
    }
    public string QueuePositionMessage
    {
        get => _queuePositionMessage;
        set { if (SetQueueText(ref _queuePositionMessage, value)) Raise(nameof(QueuePositionMessage)); }
    }
    public string QueueListMessage
    {
        get => _queueListMessage;
        set { if (SetQueueText(ref _queueListMessage, value)) Raise(nameof(QueueListMessage)); }
    }
    public string QueueEmptyMessage
    {
        get => _queueEmptyMessage;
        set { if (SetQueueText(ref _queueEmptyMessage, value)) Raise(nameof(QueueEmptyMessage)); }
    }
    public string QueueNextMessage
    {
        get => _queueNextMessage;
        set { if (SetQueueText(ref _queueNextMessage, value)) Raise(nameof(QueueNextMessage)); }
    }
    public string QueueRemovedMessage
    {
        get => _queueRemovedMessage;
        set { if (SetQueueText(ref _queueRemovedMessage, value)) Raise(nameof(QueueRemovedMessage)); }
    }
    public string QueueClearedMessage
    {
        get => _queueClearedMessage;
        set { if (SetQueueText(ref _queueClearedMessage, value)) Raise(nameof(QueueClearedMessage)); }
    }

    // Eleven identical debounced-text setters would be eleven chances to forget the
    // ScheduleSave. One helper; the caller only owns its own Raise.
    private bool SetQueueText(ref string field, string? value)
    {
        var v = value ?? "";
        if (field == v) return false;
        field = v;
        ScheduleSave();
        return true;
    }

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;

    // ── Viewer queue — derived text ──────────────────────────────────────

    /// <summary>The name a Queue.* node needs on its Name pin to address this exact
    /// line. Shown because the parity between the tool and the node band is only useful
    /// if the streamer can see what to type.</summary>
    public string QueueEffectiveName
    {
        get
        {
            string n = (_queueName ?? "").Trim();
            return n.Length == 0 ? "viewers" : n;
        }
    }

    public string QueueNodeHintText => string.Format(CultureInfo.InvariantCulture,
        Localizer.T("panel.usermgmt.queue.node_hint",
            "Architect: put \"{0}\" on the Name pin of any Queue.* node that has one to work this same line · Queue.OnChanged fires for chat joins"),
        QueueEffectiveName);

    public string QueueOverlayKeyText => string.Format(CultureInfo.InvariantCulture,
        Localizer.T("panel.usermgmt.queue.overlay_key",
            "Live channel keys:  queue.{0}.list  (a List.Live widget)  ·  queue.{0}.count"),
        QueueEffectiveName.ToLowerInvariant());

    /// <summary>
    /// The shape a moderator actually types, built from the CONFIGURED words rather
    /// than from the defaults they started as. Every word goes through
    /// <see cref="ChatVerb.Canonical"/>, which is what the parser compares with, so a
    /// streamer who typed "!next" into the field reads back the "next" chat will
    /// answer to instead of a doubled bang.
    ///
    /// <para>Two honest silences. A blank sub-verb is dropped from the sentence, and a
    /// blank LIST command drops the sentence entirely — an empty verb never matches
    /// (ChatVerb's empty-never-matches rule), and the management verbs ride the list
    /// command, so with it blank there is nothing to type at all. Printing a usage
    /// line for a command that cannot fire is the exact failure this whole pass
    /// exists to end.</para>
    /// </summary>
    public string QueueModVerbsHintText
    {
        get
        {
            string list = ChatVerb.Canonical(_queueListCommand);
            if (list.Length == 0)
                return Localizer.T("panel.usermgmt.queue.mod_verbs_no_list",
                    "The line command is empty, so the management verbs cannot be typed at all — they ride it.");

            var parts = new List<string>(4);
            AddModVerb(parts, list, _queueNextSubCommand, "");
            AddModVerb(parts, list, _queuePickSubCommand, " <user>");
            AddModVerb(parts, list, _queueRemoveSubCommand, " <user>");
            AddModVerb(parts, list, _queueClearSubCommand, "");
            if (parts.Count == 0)
                return Localizer.T("panel.usermgmt.queue.mod_verbs_none",
                    "No management sub-verbs are set — moderators cannot call or drop anyone from chat.");

            return string.Format(CultureInfo.InvariantCulture,
                Localizer.T("panel.usermgmt.queue.mod_verbs_list",
                    "Moderators manage the line with {0}."),
                string.Join("  ·  ", parts));
        }
    }

    private static void AddModVerb(List<string> parts, string list, string? configured, string argHint)
    {
        string verb = ChatVerb.Canonical(configured);
        if (verb.Length == 0) return;
        parts.Add("!" + list + " " + verb + argHint);
    }

    /// <summary>
    /// The tool's chat verbs for the read-only CHAT COMMANDS block — the eight queue
    /// commands with their canonical word, their role gate and their usage shape.
    ///
    /// <para>Built from <see cref="BuildConfig"/>, i.e. from the page's WORKING state,
    /// so the block reflects a rename the streamer has typed but the 400 ms debounce
    /// has not yet saved. Every read allocates a fresh list, which is what the
    /// ToolCommandList dependency property requires: it fires on reference
    /// inequality, so handing it the same instance twice would update nothing.</para>
    ///
    /// <para>It goes through the FULL BuildConfig even though the catalogue only reads
    /// the queue fields. Hand-rolling a slimmer config here would be a second place
    /// that has to learn about every new verb, and the first time the two drifted the
    /// panel would render a command list that disagreed with the config it just
    /// saved. The cost is one config object per raise, which is the same object the
    /// debounced save was going to build anyway.</para>
    /// </summary>
    public IReadOnlyList<ToolCommandInfo> ChatCommands => Catalog.ForUserManagement(BuildConfig());

    /// <summary>
    /// The one collision worth surfacing: the queue's join verb and the Loyalty raffle's
    /// join command. Both are "join" out of the box, and the built-in chat dispatch
    /// resolves it silently — Loyalty runs first and answers while a raffle is live, the
    /// queue answers otherwise. That IS the intended behaviour, but a streamer who has
    /// not read the dispatch order would experience it as "my queue randomly stops
    /// working", so the panel says it out loud instead of leaving it to be discovered.
    /// </summary>
    public string QueueJoinClashText => string.Format(CultureInfo.InvariantCulture,
        Localizer.T("panel.usermgmt.queue.join_clash",
            "Loyalty's raffle also uses !{0} and wins while a raffle runs — rename one to separate them."),
        QueueJoinCommandTrimmed);

    public Visibility QueueJoinClashVisibility
    {
        get
        {
            string mine = QueueJoinCommandTrimmed;
            if (mine.Length == 0) return Visibility.Collapsed;
            string raffle = (Phoenix.Controls.Hub.Core.LoyaltyService.Instance.Config?.Games?.Raffle?.JoinCommand ?? "").Trim();
            return string.Equals(mine, raffle, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string QueueJoinCommandTrimmed => (_queueJoinCommand ?? "").Trim();

    // ── Viewer queue — the live line ─────────────────────────────────────
    /// <summary>The waiting viewers, front first. Rebuilt wholesale on load and on every
    /// RuntimeChanged, because the store is the databank and a chat command or a script's
    /// Queue.Push can change it with the panel open.</summary>
    public ObservableCollection<QueueRowVm> Queue { get; } = new();

    public bool HasQueue => Queue.Count > 0;
    public Visibility QueueEmptyVisibility => HasQueue ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The line's scroller — the inverse of <see cref="QueueEmptyVisibility"/>.
    /// The empty state is a SIBLING of the scroller in the same cell, never inside
    /// it, so an empty list cannot leave a scrollbar behind.</summary>
    public Visibility QueueListVisibility => HasQueue ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Panel action: drop one viewer. Goes through the service, so it writes the
    /// same store, raises the same Queue.OnChanged and refreshes the same overlay as the
    /// chat verb.</summary>
    public async Task RemoveFromQueueAsync(QueueRowVm row)
    {
        if (row is null) return;
        await _svc.RemoveFromQueueAsync(row.Login).ConfigureAwait(true);
        await RefreshQueueAsync().ConfigureAwait(true);
    }

    /// <summary>Panel action: empty the line (confirmed in the UI).</summary>
    public async Task ClearQueueAsync()
    {
        await _svc.ClearQueueAsync().ConfigureAwait(true);
        await RefreshQueueAsync().ConfigureAwait(true);
    }

    // UI thread only — every caller is a click handler or the marshalled RuntimeChanged
    // continuation.
    private async Task RefreshQueueAsync()
    {
        List<QueueEntry> rows;
        try { rows = await _svc.ListQueueAsync().ConfigureAwait(true); }
        catch (Exception ex)
        {
            GlobalLogger.Error("UserManagementViewModel", "queue list load failed", ex);
            return;
        }
        Queue.Clear();
        foreach (var e in rows) Queue.Add(new QueueRowVm(e));
        Raise(nameof(HasQueue));
        Raise(nameof(QueueEmptyVisibility));
        Raise(nameof(QueueListVisibility));
        RaiseStats();
        // The pill's live label carries the queue length, so it moves with the line.
        RaisePillState();
    }

    /// <summary>State line for the greeting card — how many chatters the tool has
    /// ever seen (the set the once-ever greeting checks against).</summary>
    public string KnownChattersText
    {
        get
        {
            int n = _svc.KnownChatterCount;
            return n == 1
                ? Localizer.T("panel.usermgmt.greeting.known_one", "1 chatter remembered")
                : string.Format(CultureInfo.InvariantCulture,
                    Localizer.T("panel.usermgmt.greeting.known_many", "{0} chatters remembered"), n);
        }
    }

    /// <summary>The panel's confirmed destructive reset — forget every known
    /// chatter.</summary>
    public async Task ResetKnownChattersAsync()
    {
        await _svc.ResetKnownChattersAsync().ConfigureAwait(true);
        RaiseStats();
    }

    public bool HasWelcomes => Welcomes.Count > 0;
    public Visibility WelcomeEmptyVisibility => HasWelcomes ? Visibility.Collapsed : Visibility.Visible;

    public bool HasCustomGroups => CustomGroups.Count > 0;
    public Visibility CustomGroupsEmptyVisibility => HasCustomGroups ? Visibility.Collapsed : Visibility.Visible;

    // ── Live count projections ───────────────────────────────────────────
    /// <summary>Waiting viewers. Read from the SERVICE's cached count, not from the
    /// Queue collection: the service is refreshed by every mutation including a script's
    /// Queue.Push, whereas the collection is only as fresh as the last RefreshQueueAsync.</summary>
    public string StatQueueValue
        => _svc.QueueCount.ToString(CultureInfo.InvariantCulture);

    // UI-thread only — callers are row edits (ScheduleSave), add/remove, and the
    // _ui.Post continuation of the runtime/config event handlers.
    private void RaiseStats()
    {
        Raise(nameof(StatQueueValue));
        Raise(nameof(KnownChattersText));
    }

    // ── Header-band state (the pill's predicate, rendered in the band) ──
    // The state machine itself lives in UserManagementService.PillState so it can
    // be unit-tested without a ViewModel; this VM only maps it to text. Three of
    // the five states are ones the master switch cannot say, because the master
    // says nothing about the two gates under it: the queue has its own section
    // gate, and the greeting halves have theirs. A phrase that reported the master
    // alone would misstate the tool on its own page.
    // The count moved into the format string rather than staying a concatenation:
    // a translation has to be free to put the number somewhere else in the phrase.
    // The "no welcomes" variant still WRAPS this one, so the two never disagree
    // about how a line length is spelled.
    private string QueueLengthLabel
        => string.Format(CultureInfo.InvariantCulture,
            Localizer.T("panel.usermgmt.state.queue_length", "live · {0} in line"),
            _svc.QueueCount);

    public string StatusPillText => _svc.PillState switch
    {
        UserMgmtSvc.UserManagementPillState.DormantQueueOffToo =>
            Localizer.T("panel.usermgmt.state.dormant", "dormant · queue off too"),
        UserMgmtSvc.UserManagementPillState.IdleGroupsOnly =>
            Localizer.T("panel.usermgmt.state.idle_groups", "idle · groups only"),
        UserMgmtSvc.UserManagementPillState.WelcomingQueueOff =>
            Localizer.T("panel.usermgmt.state.welcoming_queue_off", "armed · welcoming, queue off"),
        UserMgmtSvc.UserManagementPillState.LiveQueueNoWelcome =>
            string.Format(CultureInfo.InvariantCulture,
                Localizer.T("panel.usermgmt.state.queue_no_welcomes", "{0}, no welcomes"),
                QueueLengthLabel),
        _ => QueueLengthLabel,
    };

    /// <summary>Only the fully live state pulses — it is the only one where both
    /// halves have a beat (arrivals are being greeted and the line is open).
    /// LiveQueueNoWelcome deliberately does not: the line answers its chat verbs,
    /// but a pulse there would read as the whole tool being live.</summary>
    public bool StatusPulsing
        => _svc.PillState == UserMgmtSvc.UserManagementPillState.LiveWithQueue;

    private void RaisePillState()
    {
        Raise(nameof(StatusPillText));
        Raise(nameof(StatusPulsing));
    }

    // ══════════════════ WATCH TIME (hub-wide, not tool config) ══════════════════
    //
    // ★ THE THREE SETTINGS BELOW ARE APPCONFIG, NOT USER-MANAGEMENT CONFIG. They are
    // written through ConfigManager.Current + ConfigManager.SaveDeferred — the idiom
    // LiveFeedViewModel.PersistChipState and SystemLogViewModel.PersistFilterState
    // already use — and they deliberately do NOT go through this VM's ScheduleSave /
    // BuildConfig path, which owns the tool blob and nothing else.
    //
    // The reason is a product decision, not tidiness: watch time is a passive
    // background data source that records with every pre-build tool switched off. A
    // streamer who never enables User Management still accrues minutes, and a Ranks
    // ladder, a group's watch-hour rule and a db.top("WatchTime", …) in somebody's
    // graph all read the same table. Parking the sampler's settings inside one tool's
    // blob would make a suite-wide fact look like that tool's property.
    //
    // The panel simply happens to be the best place to SHOW them: this is the page
    // where the hours are spent (the group rules) and browsed.

    public bool WatchTimeTrackingEnabled
    {
        get => ConfigManager.Current?.WatchTimeTrackingEnabled ?? true;
        set
        {
            var cfg = ConfigManager.Current;
            if (cfg is null || cfg.WatchTimeTrackingEnabled == value) return;
            cfg.WatchTimeTrackingEnabled = value;
            PersistAppConfig();
            Raise(nameof(WatchTimeTrackingEnabled));
            Raise(nameof(WatchTimeTrackingLabel));
            Raise(nameof(WatchTimeStateText));
        }
    }

    public string WatchTimeTrackingLabel => WatchTimeTrackingEnabled ? PillOn : PillOff;

    public bool WatchTimeOnlyWhenLive
    {
        get => ConfigManager.Current?.WatchTimeOnlyWhenLive ?? true;
        set
        {
            var cfg = ConfigManager.Current;
            if (cfg is null || cfg.WatchTimeOnlyWhenLive == value) return;
            cfg.WatchTimeOnlyWhenLive = value;
            PersistAppConfig();
            Raise(nameof(WatchTimeOnlyWhenLive));
            Raise(nameof(WatchTimeOnlyWhenLiveLabel));
            Raise(nameof(WatchTimeStateText));
        }
    }

    public string WatchTimeOnlyWhenLiveLabel => WatchTimeOnlyWhenLive ? PillOn : PillOff;

    /// <summary>
    /// The sampling cadence in seconds. Clamped to the SAME [15, 300] band
    /// ViewerPresenceService clamps to at read time — a field that let a streamer
    /// store 5 would show 5 while the sampler quietly ran at 15, which is a panel
    /// lying about the running system. Garbage parses back to the last good value,
    /// this page's numeric idiom.
    /// </summary>
    public string PresencePollSecondsText
    {
        get => PresencePollSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            var cfg = ConfigManager.Current;
            if (cfg is null) { Raise(nameof(PresencePollSecondsText)); return; }
            int current = PresencePollSeconds;
            int v = Math.Clamp(ParseInt(value, current), MinPollSeconds, MaxPollSeconds);
            if (v == current) { Raise(nameof(PresencePollSecondsText)); return; }
            cfg.ViewerPresencePollSeconds = v;
            PersistAppConfig();
            Raise(nameof(PresencePollSecondsText));
            Raise(nameof(WatchTimeStateText));
        }
    }

    // Mirrors ViewerPresenceService.MinPollSeconds / MaxPollSeconds, which are
    // internal to the Hub runtime assembly. Duplicated deliberately and named here so
    // the drift is one grep away rather than hidden inside a magic number.
    private const int MinPollSeconds = 15;
    private const int MaxPollSeconds = 300;

    private static int PresencePollSeconds
        => Math.Clamp(ConfigManager.Current?.ViewerPresencePollSeconds ?? 60, MinPollSeconds, MaxPollSeconds);

    private static void PersistAppConfig()
    {
        try
        {
            // Deferred, never synchronous: these setters run on the UI thread and a
            // config write is a DPAPI wrap plus a File.Replace, which can stall for
            // hundreds of ms on AV- or OneDrive-backed %AppData%.
            ConfigManager.SaveDeferred(Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("UserManagementViewModel", "watch-time AppConfig save failed", ex);
        }
    }

    /// <summary>One sentence saying what the sampler is actually doing right now —
    /// the three settings above interact (tracking off beats everything; live-only
    /// means nothing accrues off-stream), and three separate toggles cannot say
    /// that.</summary>
    public string WatchTimeStateText
    {
        get
        {
            // Deliberately NOT "the rules stop working": the totals already in the
            // WatchTime table stay exactly where they are and every watch-hour rule
            // keeps reading them. What stops is anyone GAINING hours — which is the
            // difference between a frozen leaderboard and a wiped one.
            if (!WatchTimeTrackingEnabled)
                return Localizer.T("panel.usermgmt.watchtime.state_off",
                    "OFF — nobody gains hours. The totals already recorded stay, and watch-hour group rules keep using them.");
            string cadence = string.Format(CultureInfo.InvariantCulture,
                Localizer.T("panel.usermgmt.watchtime.state_cadence", "sampled every {0}s"),
                PresencePollSeconds);
            return WatchTimeOnlyWhenLive
                ? string.Format(CultureInfo.InvariantCulture,
                    Localizer.T("panel.usermgmt.watchtime.state_live",
                        "ON while live — {0}. Nothing accrues between streams."), cadence)
                : string.Format(CultureInfo.InvariantCulture,
                    Localizer.T("panel.usermgmt.watchtime.state_always",
                        "ON always — {0}. A Hub left running overnight keeps crediting whoever sits in chat."), cadence);
        }
    }

    // ── The lists ────────────────────────────────────────────────────────
    // ★ ONE row VM per login, kept for the panel's life in _watchRows and only ever
    // UPDATED. The two collections below are projections that reference those same
    // instances, reconciled in place rather than cleared and refilled. Both halves of
    // that matter: PresenceSampled fires every poll (15-300s), so a wholesale rebuild
    // would tear down the container a streamer is mid-edit in — silently, on a clock
    // they cannot see — and a Clear() on an ObservableCollection re-realizes every
    // row even when nothing about the list changed.

    private readonly Dictionary<string, WatchTimeRowVm> _watchRows =
        new(StringComparer.OrdinalIgnoreCase);
    private string _watchSearch = "";

    /// <summary>The heaviest watchers, longest first.</summary>
    public ObservableCollection<WatchTimeRowVm> TopWatchers { get; } = new();

    /// <summary>The searchable browser — every tracked viewer, filtered by
    /// <see cref="WatchSearch"/> and capped at <see cref="BrowserCap"/> rendered
    /// rows.</summary>
    public ObservableCollection<WatchTimeRowVm> Watchers { get; } = new();

    /// <summary>How many of the top watchers the leaderboard card shows.</summary>
    private const int TopCount = 10;

    /// <summary>
    /// The browser's rendered-row cap. The list sits inside the page's own detail
    /// scroller — a nested ScrollViewer there is the classic trapped-wheel bug — so it
    /// is NOT virtualized, and a channel with years of history can hold thousands of
    /// rows. The cap keeps the page's layout pass bounded; the search box is how you
    /// reach past it, and the count line below says so out loud rather than letting a
    /// streamer conclude their viewer is untracked.
    /// </summary>
    private const int BrowserCap = 100;

    public string WatchSearch
    {
        get => _watchSearch;
        set
        {
            var v = value ?? "";
            if (_watchSearch == v) return;
            _watchSearch = v;
            Raise(nameof(WatchSearch));
            // Coalesced: one keystroke should not walk the whole mirror.
            if (_watchFilterTimer is null) ProjectWatchLists();
            else { _watchFilterTimer.Stop(); _watchFilterTimer.Start(); }
        }
    }

    public string WatchBrowserCountText
    {
        get
        {
            int total = _watchRows.Count;
            int shown = Watchers.Count;
            if (total == 0)
                return Localizer.T("panel.usermgmt.watchtime.browser_empty",
                    "No viewer has accrued watch time yet.");
            if (shown < total)
                return string.Format(CultureInfo.InvariantCulture,
                    Localizer.T("panel.usermgmt.watchtime.browser_partial",
                        "{0} viewers tracked · showing {1} — search to narrow it down."),
                    total, shown);
            return string.Format(CultureInfo.InvariantCulture,
                Localizer.T("panel.usermgmt.watchtime.browser_all", "{0} viewers tracked."), total);
        }
    }

    public Visibility TopWatchersEmptyVisibility
        => TopWatchers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Re-reads the presence service's watch-minute mirror into the row set, then
    /// re-projects both lists. UI thread only — every caller is LoadAsync, the
    /// marshalled PresenceSampled handler, the filter timer, or a completed manual
    /// write.
    /// </summary>
    private void RefreshWatchTime()
    {
        IReadOnlyDictionary<string, long> snapshot;
        try { snapshot = Presence.Instance.WatchMinutesSnapshot; }
        catch (Exception ex)
        {
            GlobalLogger.Error("UserManagementViewModel", "watch-time snapshot read failed", ex);
            return;
        }

        foreach (var (login, minutes) in snapshot)
        {
            if (string.IsNullOrWhiteSpace(login)) continue;
            string display = Presence.Instance.DisplayFor(login);
            if (_watchRows.TryGetValue(login, out var row))
            {
                row.Minutes = minutes;
                // A row created before this viewer ever spoke carries the login as its
                // display name (that is what DisplayFor answers when it has nothing
                // better). Once the real name is learned the row should show it, so the
                // name is re-read on every refresh rather than frozen at construction.
                row.UpdateDisplay(display);
            }
            else _watchRows[login] = new WatchTimeRowVm(login, display, minutes);
        }

        // A login that has left the mirror entirely — a hand-edited table, a dropped
        // row — must not linger in the browser claiming hours nobody stores.
        //
        // The count test is exact, not a heuristic: the loop above has just added
        // every snapshot key, so _watchRows is a superset of the snapshot's keys.
        // Equal counts therefore mean equal sets, and a bigger count means strays.
        if (_watchRows.Count > snapshot.Count)
        {
            var gone = new List<string>();
            foreach (var key in _watchRows.Keys)
                if (!snapshot.ContainsKey(key)) gone.Add(key);
            foreach (var key in gone) _watchRows.Remove(key);
        }

        ProjectWatchLists();
    }

    // UI thread only.
    private void ProjectWatchLists()
    {
        if (_disposed) return;

        var all = new List<WatchTimeRowVm>(_watchRows.Values);
        all.Sort(static (a, b) =>
        {
            int byMinutes = b.Minutes.CompareTo(a.Minutes);
            return byMinutes != 0
                ? byMinutes
                : string.Compare(a.Login, b.Login, StringComparison.OrdinalIgnoreCase);
        });

        var top = new List<WatchTimeRowVm>(TopCount);
        foreach (var row in all)
        {
            if (row.Minutes <= 0) break;   // sorted desc — the rest are all zero
            if (top.Count == TopCount) break;
            top.Add(row);
        }
        Reconcile(TopWatchers, top);

        string needle = _watchSearch.Trim();
        var filtered = new List<WatchTimeRowVm>(Math.Min(all.Count, BrowserCap));
        foreach (var row in all)
        {
            if (filtered.Count == BrowserCap) break;
            if (needle.Length > 0 &&
                row.Login.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                row.Display.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            filtered.Add(row);
        }
        Reconcile(Watchers, filtered);

        Raise(nameof(WatchBrowserCountText));
        Raise(nameof(TopWatchersEmptyVisibility));
    }

    /// <summary>
    /// Brings <paramref name="target"/> to exactly <paramref name="desired"/> — same
    /// instances, same order — with the fewest collection changes. An unchanged list
    /// raises NO change at all, which is the whole point: the refresh runs on the
    /// sampler's clock, and a row the streamer is typing in must survive it.
    /// Reference identity is the comparison, because the row instances are the stable
    /// per-login objects <see cref="_watchRows"/> owns.
    /// </summary>
    private static void Reconcile(ObservableCollection<WatchTimeRowVm> target, List<WatchTimeRowVm> desired)
    {
        var wanted = new HashSet<WatchTimeRowVm>(desired);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            int at = target.IndexOf(row);
            if (at < 0) target.Insert(i, row);
            else if (at != i) target.Move(at, i);
        }
    }

    // ── Manual corrections ───────────────────────────────────────────────

    /// <summary>
    /// Applies a row's hours/minutes boxes. An unparseable box is REJECTED — the
    /// stored value is left alone and the boxes are re-seeded from it — because the
    /// alternative reading of "nonsense in the field" is zero, and zero is the one
    /// value that quietly destroys a viewer's history.
    /// </summary>
    public async Task ApplyWatchTimeAsync(WatchTimeRowVm row)
    {
        if (row is null) return;
        long? minutes = row.EditedMinutes();
        if (minutes is null)
        {
            // A rejection, not a failure: logged rather than dialogged, per the house
            // rule on repeatable rejections.
            GlobalLogger.Log(
                $"Watch time: \"{row.HoursEdit}h {row.MinutesEdit}m\" for {row.Login} is not a number — left unchanged.",
                "UserManagementViewModel", LogLevel.System);
            row.SeedEdits();
            return;
        }
        await WriteWatchMinutesAsync(row, minutes.Value).ConfigureAwait(true);
    }

    /// <summary>Zeroes a viewer's accrued time. Confirmed in the View — see
    /// OnResetWatchTimeClick.</summary>
    public async Task ResetWatchTimeAsync(WatchTimeRowVm row)
    {
        if (row is null) return;
        await WriteWatchMinutesAsync(row, 0).ConfigureAwait(true);
    }

    private async Task WriteWatchMinutesAsync(WatchTimeRowVm row, long minutes)
    {
        bool ok;
        try { ok = await Presence.Instance.SetWatchMinutesAsync(row.Login, minutes).ConfigureAwait(true); }
        catch (Exception ex)
        {
            GlobalLogger.Error("UserManagementViewModel", "watch-time write failed", ex);
            ok = false;
        }
        if (_disposed) return;

        // Either way the row re-reads from the service: on success to pick up the
        // written value, on failure so the boxes stop showing a number that was never
        // stored. SetWatchMinutesAsync already logged the reason for a failure.
        RefreshWatchTime();
        row.SeedEdits();
        if (!ok)
            GlobalLogger.Log($"Watch time: could not update {row.Login}.",
                "UserManagementViewModel", LogLevel.System);
    }

    // Raised on the sampler's thread — marshal before touching a collection.
    private void OnPresenceSampled(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            RefreshWatchTime();
        });

    // ── Load / refresh runtime ───────────────────────────────────────────
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        RefreshRuntime();   // OnLoaded runs on the UI thread — safe to touch rows/stats directly
        RaisePillState();
        // The mirror is loaded at Hub start and refreshed on the sampler's cadence, so
        // there are hours to show the moment the page opens — waiting for the next
        // sample would leave the section blank for up to five minutes.
        RefreshWatchTime();
        await RefreshQueueAsync().ConfigureAwait(true);
    }

    /// <summary>Seeds the fixed rows in place and rebuilds the welcome / custom-group
    /// collections from a (deep-cloned, VM-owned) config. UI thread only.</summary>
    private void LoadFromConfig(UserManagementConfig cfg)
    {
        _regRow.LoadMembers(cfg.Regulars);
        _regRow.LoadWatchHours(cfg.RegularWatchHours);

        Welcomes.Clear();
        cfg.PersonalWelcomes ??= new List<WelcomeEntry>();
        foreach (var entry in cfg.PersonalWelcomes)
            Welcomes.Add(new WelcomeRowVm(entry, ScheduleSave));

        CustomGroups.Clear();
        cfg.CustomGroups ??= new List<UserGroup>();
        foreach (var group in cfg.CustomGroups)
            CustomGroups.Add(GroupRowVm.Custom(group, ScheduleSave));

        Raise(nameof(HasWelcomes));
        Raise(nameof(WelcomeEmptyVisibility));
        Raise(nameof(HasCustomGroups));
        Raise(nameof(CustomGroupsEmptyVisibility));
        RaiseStats();
    }

    // The runtime counters live in the service, not the config — re-read them into
    // the live projections. UI-thread only (called from LoadAsync / the marshalled
    // RuntimeChanged handler).
    private void RefreshRuntime() => RaiseStats();

    // ── Add / remove personalized welcomes ───────────────────────────────
    public void AddWelcome()
    {
        var entry = new WelcomeEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Enabled = true,
            Username = "",
            Message = "",
            AutoShoutout = false,
        };
        Welcomes.Add(new WelcomeRowVm(entry, ScheduleSave));
        Raise(nameof(HasWelcomes));
        Raise(nameof(WelcomeEmptyVisibility));
        ScheduleSave();
    }

    public void RemoveWelcome(WelcomeRowVm row)
    {
        if (row is null) return;
        // The config is rebuilt from the live rows on every save, so dropping the row
        // from the collection is enough.
        Welcomes.Remove(row);
        Raise(nameof(HasWelcomes));
        Raise(nameof(WelcomeEmptyVisibility));
        ScheduleSave();
    }

    // ── Add / remove custom groups ───────────────────────────────────────
    public void AddCustomGroup()
    {
        var row = GroupRowVm.Custom(new UserGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "",
        }, ScheduleSave);
        // A fresh group opens expanded so the name field is immediately editable.
        row.IsExpanded = true;
        CustomGroups.Add(row);
        Raise(nameof(HasCustomGroups));
        Raise(nameof(CustomGroupsEmptyVisibility));
        ScheduleSave();
    }

    public void RemoveCustomGroup(GroupRowVm row)
    {
        if (row is null || !row.IsCustom) return;
        CustomGroups.Remove(row);
        Raise(nameof(HasCustomGroups));
        Raise(nameof(CustomGroupsEmptyVisibility));
        ScheduleSave();
    }

    // ── Persistence ──────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        // Every row edit funnels through here (row _changed) on the UI thread —
        // cheapest single hook to keep the live count projections and the CHAT
        // COMMANDS block honest (any verb, role or enable edit reaches it).
        RaiseStats();
        Raise(nameof(ChatCommands));
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
        // The queue's section gate is the tool's per-command enable as far as the
        // catalogue is concerned, so the block dims and undims with it.
        Raise(nameof(ChatCommands));
        // UpdateConfigAsync opens with `await _configWriteGate.WaitAsync()`, which
        // completes synchronously while uncontended — so in the ordinary case the
        // service's PillState already reflects the flip by the time we get here and
        // the pill moves on the same frame as the chip. SaveWorkingAsync raises it
        // again once the write really lands, which covers the contended case.
        RaisePillState();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        // Build the fresh config on the current (UI) thread BEFORE awaiting so the
        // snapshot is taken from a stable, non-mutating view of the rows.
        var cfg = BuildConfig();
        _lastPushed = cfg;
        try { await _svc.UpdateConfigAsync(cfg).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("UserManagementViewModel", "UpdateConfigAsync failed", ex); }
        // Back off the UI thread here (ConfigureAwait(false) above) — marshal.
        _ui.Post(() => { if (!_disposed) RaisePillState(); });
    }

    // The single place the UI's editable state is projected back into a config blob.
    // Every list/entry is freshly built (ToSnapshot / ToMembers) — never shared with
    // the row-held working objects, since UpdateConfigAsync deep-owns the result.
    private UserManagementConfig BuildConfig()
    {
        var cfg = new UserManagementConfig
        {
            Enabled = _masterEnabled,
            WelcomingEnabled = _welcomingEnabled,
            GeneralWelcomeEnabled = _generalWelcomeEnabled,
            GeneralWelcomeMessage = _generalWelcomeMessage ?? "",
            GreetingEnabled = _greetingEnabled,
            GreetingMessage = _greetingMessage ?? "",
            PersonalWelcomes = new List<WelcomeEntry>(Welcomes.Count),
            // Regular is the only standard group with a membership to project — the
            // other three are the platform's and have no list here to write back.
            Regulars = _regRow.ToMembers(),
            RegularWatchHours = _regRow.WatchHours,
            CustomGroups = new List<UserGroup>(CustomGroups.Count),

            // Queue settings only. The LINE is not projected here and must not be — it
            // lives in the databank's open "Queues" table, and mirroring it into the blob
            // would let a stale panel snapshot overwrite a chat join.
            QueueEnabled = _queueEnabled,
            QueueName = _queueName ?? "",
            QueueJoinCommand = _queueJoinCommand ?? "",
            QueueLeaveCommand = _queueLeaveCommand ?? "",
            QueueListCommand = _queueListCommand ?? "",
            QueuePositionCommand = _queuePositionCommand ?? "",
            QueueNextSubCommand = _queueNextSubCommand ?? "",
            QueuePickSubCommand = _queuePickSubCommand ?? "",
            QueueRemoveSubCommand = _queueRemoveSubCommand ?? "",
            QueueClearSubCommand = _queueClearSubCommand ?? "",
            QueueMaxSize = _queueMaxSize,
            QueueSubPriority = _queueSubPriority,
            QueueVipPriority = _queueVipPriority,
            QueueCooldownSeconds = _queueCooldownSeconds,
            QueueOverlayEnabled = _queueOverlayEnabled,
            QueueOverlaySize = _queueOverlaySize,
            QueueJoinRoles = QueueJoinRoles.ToSnapshot(),
            QueueModRoles = QueueModRoles.ToSnapshot(),
            QueueJoinedMessage = _queueJoinedMessage ?? "",
            QueueAlreadyMessage = _queueAlreadyMessage ?? "",
            QueueFullMessage = _queueFullMessage ?? "",
            QueueLeftMessage = _queueLeftMessage ?? "",
            QueueNotQueuedMessage = _queueNotQueuedMessage ?? "",
            QueuePositionMessage = _queuePositionMessage ?? "",
            QueueListMessage = _queueListMessage ?? "",
            QueueEmptyMessage = _queueEmptyMessage ?? "",
            QueueNextMessage = _queueNextMessage ?? "",
            QueueRemovedMessage = _queueRemovedMessage ?? "",
            QueueClearedMessage = _queueClearedMessage ?? "",
        };
        foreach (var row in Welcomes)
            cfg.PersonalWelcomes.Add(row.ToSnapshot());
        foreach (var row in CustomGroups)
            cfg.CustomGroups.Add(row.ToSnapshot());
        return cfg;
    }

    // ── Service events (SafeEvent — raised on a background thread; MUST marshal) ─
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Self-triggered save assigns our pushed instance as the live config → skip.
        if (ReferenceEquals(_svc.Config, _lastPushed)) return;
        _ui.Post(() =>
        {
            if (_disposed) return;
            var cfg = Clone(_svc.Config);
            ApplyScalars(cfg);
            LoadFromConfig(cfg);
            Raise(nameof(MasterEnabled));
            Raise(nameof(WelcomingEnabled));
            Raise(nameof(WelcomingEnabledLabel));
            Raise(nameof(GeneralWelcomeEnabled));
            Raise(nameof(GeneralWelcomeEnabledLabel));
            Raise(nameof(GeneralWelcomeMessage));
            Raise(nameof(GreetingEnabled));
            Raise(nameof(GreetingEnabledLabel));
            Raise(nameof(GreetingMessage));
            RaisePillState();
            // Every queue field too — a foreign save that raised only the older half
            // would leave the queue card showing pre-save text with no way to tell.
            Raise(nameof(QueueEnabled));
            Raise(nameof(QueueEnabledLabel));
            Raise(nameof(QueueName));
            Raise(nameof(QueueJoinCommand));
            Raise(nameof(QueueLeaveCommand));
            Raise(nameof(QueueListCommand));
            Raise(nameof(QueuePositionCommand));
            Raise(nameof(QueueNextSubCommand));
            Raise(nameof(QueuePickSubCommand));
            Raise(nameof(QueueRemoveSubCommand));
            Raise(nameof(QueueClearSubCommand));
            Raise(nameof(QueueMaxSizeText));
            Raise(nameof(QueueSubPriorityText));
            Raise(nameof(QueueVipPriorityText));
            Raise(nameof(QueueCooldownText));
            Raise(nameof(QueueOverlayEnabled));
            Raise(nameof(QueueOverlayEnabledLabel));
            Raise(nameof(QueueOverlaySizeText));
            Raise(nameof(QueueJoinedMessage));
            Raise(nameof(QueueAlreadyMessage));
            Raise(nameof(QueueFullMessage));
            Raise(nameof(QueueLeftMessage));
            Raise(nameof(QueueNotQueuedMessage));
            Raise(nameof(QueuePositionMessage));
            Raise(nameof(QueueListMessage));
            Raise(nameof(QueueEmptyMessage));
            Raise(nameof(QueueNextMessage));
            Raise(nameof(QueueRemovedMessage));
            Raise(nameof(QueueClearedMessage));
            Raise(nameof(QueueNodeHintText));
            Raise(nameof(QueueOverlayKeyText));
            Raise(nameof(QueueModVerbsHintText));
            Raise(nameof(QueueJoinClashText));
            Raise(nameof(QueueJoinClashVisibility));
            // The command block is a projection of everything above it.
            Raise(nameof(ChatCommands));
        });
    }

    // RuntimeChanged carries the queue too: it is raised by every mutation of the line,
    // including a script's Queue.Push, so the panel's list follows a graph-driven change
    // without the user reopening the tab.
    private void OnRuntimeChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            RefreshRuntime();
            RaisePillState();
            _ = Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                RefreshQueueAsync, "UserManagementViewModel", "queue refresh");
        });

    // Deep clone through JSON round-trip (mirrors the Scheduling / Counters VMs) so
    // the working copy is fully detached from the live service config.
    private static UserManagementConfig Clone(UserManagementConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new UserManagementConfig());
            return JsonSerializer.Deserialize<UserManagementConfig>(json) ?? new UserManagementConfig();
        }
        catch { return new UserManagementConfig(); }
    }
}
