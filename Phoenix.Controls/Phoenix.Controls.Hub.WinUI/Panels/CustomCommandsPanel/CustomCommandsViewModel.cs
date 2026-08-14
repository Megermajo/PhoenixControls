using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.UI;
using CustomCommandsSvc = Phoenix.Controls.Hub.Core.CustomCommandsService;

namespace Phoenix.Controls.Hub.WinUI.Panels.CustomCommandsPanel;

/// <summary>
/// ViewModel for the Hub Custom Commands page — the button-side front-end onto the same
/// command list the built-in chat provider and the Command.OnCustom event node drive.
/// Reaches <see cref="CustomCommandsSvc.Instance"/> DIRECTLY and subscribes to its
/// ConfigChanged event.
///
/// Config editing model: the whole <see cref="CustomCommandsConfig"/> is deep-cloned
/// into a private working copy. Every field edits that clone and schedules a debounced
/// <c>SaveConfigAsync</c>. Because SaveConfigAsync deep-clones the incoming object (so
/// the panel can't alias the hot-path config), a self-triggered ConfigChanged is
/// detected by comparing serialized content (not reference) and skipped; a foreign
/// change rebuilds the rows. Mirrors QuotesViewModel.
/// </summary>
public sealed class CustomCommandsViewModel : ObservableObject, IDisposable
{
    private readonly CustomCommandsSvc _svc = CustomCommandsSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;

    private CustomCommandsConfig _working;
    private bool _disposed;
    private bool _dirty;

    public CustomCommandsViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        _working = Clone(_svc.Config);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();
        }

        BuildRows();

        _svc.ConfigChanged += OnConfigChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        foreach (var row in Commands) row.PropertyChanged -= OnRowPropertyChanged;
        _saveTimer?.Stop();
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.SaveConfigAsync(_working), "CustomCommandsViewModel", "final config flush"));
        }
    }

    // ── Bound command list (rebuilt wholesale on external reload) ────────
    public ObservableCollection<CommandRowVm> Commands { get; } = new();
    // Roster empty state (shown when there are no commands at all) — a different
    // question from "nothing is selected" (EmptyVisibility, below). The two were one
    // property before the house-shell rebuild, when the page had no selection concept.
    public Visibility NoCommandsVisibility => Commands.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    public string CountText => string.Format(
        CultureInfo.CurrentCulture,
        Localizer.T("panel.customcommands.roster.count", "{0} defined"),
        Commands.Count);

    // ── Selection (NEW with the house shell) ────────────────────────────
    // The roster moved to the 0.9* column as summary rows, so the per-row editor moved
    // to the 1.4* detail column and needs to know which command it is editing.
    // Selection is set from the roster row's Click handler by DataContext
    // pattern-match; there is no ListView and no SelectedItem in this grammar.
    private CommandRowVm? _selectedCommand;
    public CommandRowVm? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            var previous = _selectedCommand;
            if (!Set(ref _selectedCommand, value)) return;
            previous?.SetSelected(false);
            _selectedCommand?.SetSelected(true);
            Raise(nameof(HasSelection));
            Raise(nameof(DetailVisibility));
            Raise(nameof(EmptyVisibility));
        }
    }

    public bool HasSelection => _selectedCommand is not null;

    /// <summary>The selected command's editor card.</summary>
    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "pick a command" placeholder that stands in for the editor card.
    /// It replaces ONLY the editor: the variable library stays on screen with nothing
    /// selected, because it documents the tokens for the whole tool.</summary>
    public Visibility EmptyVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    // ── Chat commands (read-only; the detail column's ToolCommandList) ──

    /// <summary>
    /// Every command word this tool answers to: each row's trigger AND each of its
    /// aliases, which run the same response and share its two cooldown buckets.
    /// </summary>
    /// <remarks>
    /// <para><b>Tool-wide, not scoped to the selection</b>, and the aliases are why.
    /// The roster in the right column shows one line per command and names only the
    /// trigger, so an alias has never been visible anywhere on this page except inside
    /// the comma-separated ALIASES field of the single command that owns it — you had
    /// to click each command in turn to learn what the channel can actually type. This
    /// block lists the lot.</para>
    ///
    /// <para>Derived from the WORKING config, not the service's, so a renamed trigger
    /// or a newly typed alias appears immediately instead of 400 ms later when the
    /// debounce lands. Rows carry the per-command Enabled flag, so a disabled command
    /// dims together with every alias of it — which is the real behaviour:
    /// <c>Find</c> matches a disabled row's trigger and aliases exactly as it matches
    /// a live one's and the caller drops it immediately afterwards, so the word
    /// answers nothing here and never reaches a second command that happens to share
    /// it.</para>
    ///
    /// <para>Re-read whole on every raise: the catalogue allocates a fresh list, and a
    /// fresh instance is what <c>ToolCommandList</c>'s dependency property needs to
    /// notice a change at all — it fires on reference inequality.</para>
    /// </remarks>
    public IReadOnlyList<ToolCommandInfo> ChatCommands => BuiltInCommandCatalog.ForCustomCommands(_working);

    // ── Master switch ───────────────────────────────────────────────────
    // Bound OneWay to the strip's TogglePill; the host chip Button calls ToggleMaster().
    // The setter is kept because it is the single place the flip is applied, and it
    // bypasses the 400 ms debounce (SaveNow) exactly as before.
    public bool MasterEnabled
    {
        get => _working.Enabled;
        set
        {
            if (_working.Enabled == value) return;
            _working.Enabled = value;
            Raise(nameof(MasterEnabled));
            SaveNow();
            // SaveConfigAsync assigns the service's live config before its first await,
            // so by the time SaveNow returns the pill's source is already current.
            RaiseStatusPill();
        }
    }

    public void ToggleMaster() => MasterEnabled = !MasterEnabled;

    // ── Status pill ─────────────────────────────────────────────────────
    // Read from the service, never recomputed here: CustomCommandsService.PillState is
    // the authority and is unit-tested. Three states, and only three.
    //
    // There is deliberately NO "degraded because Counters is off" state. The service
    // documents why: {count} / {count.next} are served by seams wired to the raw
    // counter read/write, and NEITHER is gated on the Counters master switch — a
    // response reading {count} posts the real number with Counters disabled, so a
    // degraded pill would be telling the streamer something untrue about their own
    // chat output.
    //
    // No pulse either: a pulsing dot claims a continuous beat, and this tool answers
    // in bursts when chat happens to run a command. The ACTIVITY feed is where a fire
    // shows up.
    public string StatusPillText => _svc.PillState switch
    {
        CustomCommandsSvc.CustomCommandsPillState.Dormant           => "dormant",
        CustomCommandsSvc.CustomCommandsPillState.NothingConfigured => "live · nothing configured",
        _                                                           => "live",
    };

    public Color StatusPillColor => _svc.PillState switch
    {
        CustomCommandsSvc.CustomCommandsPillState.Dormant           => ThemeColor("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72),
        CustomCommandsSvc.CustomCommandsPillState.NothingConfigured => ThemeColor("WarnBrush", 0xE0, 0xA2, 0x3A),
        _                                                           => ThemeColor("OkBrush", 0x6F, 0xA4, 0x6B),
    };

    private void RaiseStatusPill()
    {
        Raise(nameof(StatusPillText));
        Raise(nameof(StatusPillColor));
    }

    // Palette lookup through the shared resolver so a PhoenixDark edit reaches the
    // pill, with the token's own literal as the fallback for a host that has not
    // merged the theme.
    private static Color ThemeColor(string key, byte r, byte g, byte b)
        => ToolBrushes.Lookup(key, r, g, b) is SolidColorBrush s
            ? s.Color
            : Color.FromArgb(0xFF, r, g, b);

    // ── Load hook ───────────────────────────────────────────────────────
    // The command list is config-only and already built in the ctor; what LoadAsync
    // does is refresh the status-pill projection for the freshly opened tab.
    public Task LoadAsync()
    {
        RaiseStatusPill();
        return Task.CompletedTask;
    }

    private void BuildRows()
    {
        foreach (var row in Commands) row.PropertyChanged -= OnRowPropertyChanged;
        // A wholesale rebuild replaces every row instance, so the old selection points
        // at an object that is no longer in the list. Re-select by trigger below.
        string? previousTrigger = _selectedCommand?.Trigger;
        SelectedCommand = null;
        Commands.Clear();
        _working.Commands ??= new List<CustomCommand>();
        foreach (var c in _working.Commands)
            Commands.Add(HookRow(new CommandRowVm(c, ScheduleSave)));
        RaiseListCounters();

        if (!string.IsNullOrWhiteSpace(previousTrigger))
        {
            foreach (var row in Commands)
            {
                if (string.Equals(row.Trigger, previousTrigger, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedCommand = row;
                    break;
                }
            }
        }
    }

    // ── Add / remove commands ───────────────────────────────────────────
    public void AddCommand()
    {
        var cmd = new CustomCommand { Trigger = NextTrigger(), Response = "", Roles = CommandRoles.All(), Enabled = true };
        _working.Commands ??= new List<CustomCommand>();
        _working.Commands.Add(cmd);
        var row = HookRow(new CommandRowVm(cmd, ScheduleSave));
        Commands.Add(row);
        RaiseListCounters();
        // A fresh command opens in the editor — otherwise ADD appears to do nothing
        // but grow a list on the far side of the page.
        SelectedCommand = row;
        ScheduleSave();
    }

    public void RemoveCommand(CommandRowVm row)
    {
        if (row is null) return;
        row.PropertyChanged -= OnRowPropertyChanged;
        if (ReferenceEquals(row, _selectedCommand)) SelectedCommand = null;
        _working.Commands?.Remove(row.Command);
        Commands.Remove(row);
        RaiseListCounters();
        ScheduleSave();
    }

    /// <summary>Strip danger verb — deletes whatever the detail column is editing.</summary>
    public void RemoveSelectedCommand()
    {
        var row = _selectedCommand;
        if (row is null) return;
        RemoveCommand(row);
    }

    /// <summary>Roster enable pill — the pill is display-only, so the flip lives here.</summary>
    public void ToggleRowEnabled(CommandRowVm row)
    {
        if (row is null) return;
        row.Enabled = !row.Enabled;
    }

    private CommandRowVm HookRow(CommandRowVm row)
    {
        // The pill can move on a per-row Enabled / Trigger edit; raises live off the
        // row's own PropertyChanged (unhooked on remove / rebuild / dispose).
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Both feed the service's "could anything answer?" count, so both can move the
        // pill. The value it reads is the SAVED config, so this lands after the
        // debounce completes as well (see SaveWorkingAsync).
        if (e.PropertyName == nameof(CommandRowVm.Enabled) || e.PropertyName == nameof(CommandRowVm.Trigger))
            RaiseStatusPill();
    }

    private void RaiseListCounters()
    {
        Raise(nameof(NoCommandsVisibility));
        Raise(nameof(CountText));
        // Whenever the LENGTH of the list moves, so does the command block. The
        // add / remove paths also reach it through ScheduleSave; BuildRows — the
        // foreign-reload rebuild, which swaps _working for a fresh clone — does not,
        // and this is the call it shares with them.
        Raise(nameof(ChatCommands));
    }

    private string NextTrigger()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _working.Commands ?? new List<CustomCommand>())
            if (!string.IsNullOrWhiteSpace(c.Trigger)) taken.Add(c.Trigger.Trim());
        for (int i = 1; ; i++)
        {
            string candidate = "command" + i.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ── Persistence ─────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        // The single funnel for every edit the CHAT COMMANDS block renders: each row
        // setter calls it through _changed, add / remove call it directly, and it is
        // the ONLY hook that catches a role tick — CommandRolesVm raises on itself and
        // calls this action, so nothing on CommandRowVm moves when a checkmark
        // changes and the block's Roles column would otherwise go stale.
        Raise(nameof(ChatCommands));
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        try { await _svc.SaveConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsViewModel", "SaveConfigAsync failed", ex); }
        // The pill reads the SAVED config, so a debounced edit (a per-row enable flip,
        // a trigger rename) only moves it once the write lands.
        _ui.Post(RaiseStatusPill);
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Never rebuild while the user has unsaved local edits in flight: a self-save's
        // event can arrive AFTER the user typed more (so _working has advanced past the
        // saved snapshot), and rebuilding from _svc.Config would silently drop that edit.
        // The pending edit saves via its own debounce; a foreign change is picked up on
        // the next event once the local edits settle.
        if (_dirty) return;
        // SaveConfigAsync deep-clones our working copy, so reference-equality can't
        // detect a self-save (unlike Counters). Compare serialized content instead: a
        // self-save is content-identical → skip the rebuild; a foreign change differs.
        if (SameContent(_svc.Config, _working)) { _ui.Post(RaiseStatusPill); return; }
        _ui.Post(() =>
        {
            // Re-check on the UI thread (same thread as the edit handlers) — an edit may
            // have landed between the background-thread check above and this dispatch.
            if (_dirty) return;
            _working = Clone(_svc.Config);
            BuildRows();
            Raise(nameof(MasterEnabled));
            RaiseStatusPill();
        });
    }

    private static bool SameContent(CustomCommandsConfig a, CustomCommandsConfig b)
    {
        try { return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b); }
        catch { return false; }
    }

    private static CustomCommandsConfig Clone(CustomCommandsConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new CustomCommandsConfig());
            return JsonSerializer.Deserialize<CustomCommandsConfig>(json) ?? new CustomCommandsConfig();
        }
        catch { return new CustomCommandsConfig(); }
    }
}
