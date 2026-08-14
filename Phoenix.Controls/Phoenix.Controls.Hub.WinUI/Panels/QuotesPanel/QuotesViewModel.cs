using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Catalog = Phoenix.Controls.Hub.Core.BuiltInCommandCatalog;
using QuotesSvc = Phoenix.Controls.Hub.Core.QuotesService;
using ToolCommandInfo = Phoenix.Controls.Hub.Core.ToolCommandInfo;

namespace Phoenix.Controls.Hub.WinUI.Panels.QuotesPanel;

/// <summary>
/// ViewModel for the Hub Quotes page — the button-side front-end onto the same quote
/// store the quote.* script nodes and the !addquote / !quote / !delquote chat
/// commands drive. Reaches <see cref="QuotesSvc.Instance"/> DIRECTLY for reads AND
/// writes and subscribes to its ConfigChanged / QuotesChanged events.
///
/// Config editing model: the whole <see cref="QuotesConfig"/> is deep-cloned into a
/// private working copy. Every settings field edits that clone and schedules a
/// debounced <c>SaveConfigAsync</c>. Because SaveConfigAsync deep-clones the incoming
/// object (so the panel can't alias the hot-path config), a self-triggered
/// ConfigChanged is detected by comparing serialized content (not reference) and
/// skipped; a foreign change rebuilds the settings. Quotes are NEVER cached in the
/// config — they come from the OPEN "Quotes" table via <c>ListAsync</c> and refresh
/// on load / on QuotesChanged.
/// </summary>
public sealed class QuotesViewModel : ObservableObject, IDisposable
{
    private readonly QuotesSvc _svc = QuotesSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;

    // Coalesces a QuotesChanged burst into ONE reconcile. One event costs a DB read
    // plus the roster reconcile, and a !delquote sweep or a script adding several
    // quotes fires the event once per write, so the event is debounced rather than
    // serviced per write. Explicit refreshes (LoadAsync, the REFRESH button) still
    // go straight through, unthrottled.
    private readonly DispatcherQueueTimer? _refreshTimer;

    private QuotesConfig _working;
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;
    // True only while OUR SaveConfigAsync is in flight. QuotesService.SaveConfigAsync
    // deep-clones AND Normalize()s the incoming config, so the persisted _svc.Config is
    // a different reference AND can differ byte-for-byte from _working (trimmed command
    // words, defaulted-blank commands) — the old serialized-content compare therefore
    // mis-read our own save as a foreign change and rebuilt the settings mid-edit,
    // yanking whatever the streamer was typing. A one-shot suppression flag detects the
    // self-save deterministically instead.
    private bool _selfSaving;

    public QuotesViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        _working = Clone(_svc.Config);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            _refreshTimer = dispatcher.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(300);
            _refreshTimer.IsRepeating = false;
            _refreshTimer.Tick += (_, _) => { if (!_disposed) _ = RefreshQuotesAsync(); };
        }

        BuildSettings();

        _svc.ConfigChanged += OnConfigChanged;
        _svc.QuotesChanged += OnQuotesChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.QuotesChanged -= OnQuotesChanged;
        _saveTimer?.Stop();
        _refreshTimer?.Stop();
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.SaveConfigAsync(_working), "QuotesViewModel", "final config flush"));
        }
    }

    // ── Bound quote list ────────────────────────────────────────────────
    public ObservableCollection<QuoteRowVm> Quotes { get; } = new();
    public bool HasQuotes => Quotes.Count > 0;
    // Roster empty state ("No quotes yet") — the STORE being empty, which is a
    // different question from "nothing is selected" (EmptyVisibility, below). The two
    // were one property before the house-shell rebuild, when the page had no
    // selection concept at all.
    public Visibility NoQuotesVisibility => HasQuotes ? Visibility.Collapsed : Visibility.Visible;
    public string CountText =>
        string.Format(Localizer.T("panel.quotes.roster.count", "{0} stored"), Quotes.Count);

    // ── Selection (NEW with the house shell) ────────────────────────────
    // The roster moved to the 0.9* column, so the per-row inline editor moved to the
    // 1.4* detail column and needs to know WHICH row it is editing. Selection is set
    // from the roster row's Click handler (Tag/DataContext pattern-match); there is no
    // ListView and no SelectedItem in this grammar.
    //
    // Switching selection away from a half-typed row does NOT discard the draft: the
    // draft lives on the QuoteRowVm, the row stays in the roster, and it wears an
    // "unsaved" marker (QuoteRowVm.DirtyVisibility) until Save commits it.
    private QuoteRowVm? _selectedQuote;
    public QuoteRowVm? SelectedQuote
    {
        get => _selectedQuote;
        set
        {
            var previous = _selectedQuote;
            if (!Set(ref _selectedQuote, value)) return;
            previous?.SetSelected(false);
            _selectedQuote?.SetSelected(true);
            Raise(nameof(HasSelection));
            Raise(nameof(DetailVisibility));
            Raise(nameof(EmptyVisibility));
        }
    }

    public bool HasSelection => _selectedQuote is not null;

    /// <summary>The selected-quote editor card.</summary>
    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "pick a quote" placeholder that stands in for the editor card.
    /// It replaces ONLY the editor: the ADD card and the SETTINGS card stay
    /// on screen with nothing selected, because neither is scoped to one quote.</summary>
    public Visibility EmptyVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    // ── Manual-add inputs ───────────────────────────────────────────────
    private string _newName = "";
    public string NewName { get => _newName; set { _newName = value ?? ""; Raise(nameof(NewName)); } }

    private string _newText = "";
    public string NewText { get => _newText; set { _newText = value ?? ""; Raise(nameof(NewText)); } }

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
            RaiseStatusPill();
            SaveNow();
        }
    }

    public void ToggleMaster() => MasterEnabled = !MasterEnabled;

    // ── Status pill ─────────────────────────────────────────────────────
    // Quotes has NO liveness beat and therefore NO pulsing dot: QuotesService is
    // explicit that a quote store has no readout widget and no Streamer.bot
    // dependency, so there is no "degraded" state to distinguish and nothing that
    // ticks. Three states only.
    //
    // Computed here rather than read from a service property because QuotesService
    // exposes none — see the cross-lane note in the panel report. Both inputs are
    // panel-local anyway: the master flag on the working config, and the size of the
    // roster this page just read.
    public string StatusPillText
    {
        get
        {
            if (!_working.Enabled)
                return Localizer.T("panel.quotes.pill.dormant", "dormant · databank still writable");
            return Quotes.Count == 0
                ? Localizer.T("panel.quotes.pill.no_quotes", "live · no quotes yet")
                : Localizer.T("panel.quotes.pill.live", "live");
        }
    }

    private void RaiseStatusPill()
    {
        Raise(nameof(StatusPillText));
    }

    // ── Role checkmark sets (rebuilt on foreign reload) ─────────────────
    public QuoteRolesVm AddRoles { get; private set; } = null!;
    public QuoteRolesVm GetRoles { get; private set; } = null!;
    public QuoteRolesVm DeleteRoles { get; private set; } = null!;

    // ── Command words + reply template ──────────────────────────────────
    public string AddCommand
    {
        get => _working.AddCommand ?? "";
        set { var v = value ?? ""; if ((_working.AddCommand ?? "") == v) return; _working.AddCommand = v; Raise(nameof(AddCommand)); ScheduleSave(); }
    }

    public string GetCommand
    {
        get => _working.GetCommand ?? "";
        set { var v = value ?? ""; if ((_working.GetCommand ?? "") == v) return; _working.GetCommand = v; Raise(nameof(GetCommand)); ScheduleSave(); }
    }

    public string DeleteCommand
    {
        get => _working.DeleteCommand ?? "";
        set { var v = value ?? ""; if ((_working.DeleteCommand ?? "") == v) return; _working.DeleteCommand = v; Raise(nameof(DeleteCommand)); ScheduleSave(); }
    }

    public string ReplyTemplate
    {
        get => _working.ReplyTemplate ?? "";
        set { var v = value ?? ""; if ((_working.ReplyTemplate ?? "") == v) return; _working.ReplyTemplate = v; Raise(nameof(ReplyTemplate)); ScheduleSave(); }
    }

    // ── Chat commands (read-only; the detail column's ToolCommandList) ──

    /// <summary>
    /// The three verbs this tool answers to — read, add, delete — each with the
    /// argument shape it expects and the role set that gates it.
    /// </summary>
    /// <remarks>
    /// <para>Derived from the WORKING config, not the service's, so a renamed command
    /// word or a re-ticked permission row shows here immediately instead of 400 ms
    /// later when the debounce lands. That is the whole reason
    /// <c>BuiltInCommandCatalog</c> takes a config parameter rather than reaching for
    /// the live service itself.</para>
    ///
    /// <para>Re-read whole on every raise: the catalogue allocates a fresh list, and a
    /// fresh instance is exactly what <c>ToolCommandList</c>'s dependency property
    /// needs to notice a change at all — it fires on reference inequality, so mutating
    /// a cached list in place would update nothing. The read also deep-clones the
    /// config (see below), so it is a JSON round-trip of four strings and three
    /// checkmark sets plus three rows, on a raise that fires per settled edit and not
    /// per keystroke — the caching this invites would buy nothing measurable and would
    /// re-open the reference-equality trap above.</para>
    ///
    /// <para>The rows do NOT dim when the tool is switched off. None of the three has
    /// a per-command enable, and the master switch is the strip's own headline state
    /// (the pill and the pinned band both say it) rather than a per-row fact — see the
    /// Enabled rule in <c>BuiltInCommandCatalog</c>'s header.</para>
    ///
    /// <para><b>★ The blank field is normalized before the ask</b>, and it has to be.
    /// The catalogue drops any row whose verb canonicalizes to empty, because a blank
    /// verb provably matches nothing — right for Counters and Custom Commands, where a
    /// blank word really is a dead command. It is WRONG here: a cleared box in this
    /// tool does not mean "no command", it means "the default", because
    /// <c>QuotesService.Normalize</c> substitutes the tool's own default word every
    /// time the config is loaded or saved. Handing the raw working copy over would
    /// hide a command that answers chat the moment the debounce lands — the exact
    /// failure this whole block exists to prevent, arrived at from the other
    /// direction.</para>
    ///
    /// <para>Note that only a WHITESPACE-blank field is substituted, matching
    /// <c>Normalize</c> exactly. A field holding just "!" survives normalization and
    /// then canonicalizes to empty, so it is genuinely dead — and the catalogue
    /// dropping that row is the honest answer.</para>
    /// </remarks>
    public IReadOnlyList<ToolCommandInfo> ChatCommands
    {
        get
        {
            // A CLONE, never _working itself. Writing the defaults into the working
            // copy would persist them on the next save — turning "left blank, so use
            // the default" into "the panel typed the default into your config", which
            // is a silent edit the streamer never made and cannot undo by clearing the
            // box again.
            var ask = Clone(_working);
            // Defaults read off a fresh config rather than spelled as literals here:
            // the model's property initializers are the DECLARATION of what each
            // default word is, and QuotesService.Normalize mirrors them. A literal in
            // this file would be a third copy of the same three strings, and the one
            // most likely to be forgotten.
            var fallback = new QuotesConfig();
            if (string.IsNullOrWhiteSpace(ask.AddCommand))    ask.AddCommand    = fallback.AddCommand;
            if (string.IsNullOrWhiteSpace(ask.GetCommand))    ask.GetCommand    = fallback.GetCommand;
            if (string.IsNullOrWhiteSpace(ask.DeleteCommand)) ask.DeleteCommand = fallback.DeleteCommand;
            return Catalog.ForQuotes(ask);
        }
    }

    private void BuildSettings()
    {
        _working.AddRoles    ??= QuoteRoles.Mods();
        _working.GetRoles    ??= QuoteRoles.All();
        _working.DeleteRoles ??= QuoteRoles.Mods();
        AddRoles    = new QuoteRolesVm(_working.AddRoles, ScheduleSave);
        GetRoles    = new QuoteRolesVm(_working.GetRoles, ScheduleSave);
        DeleteRoles = new QuoteRolesVm(_working.DeleteRoles, ScheduleSave);
        Raise(nameof(AddRoles));
        Raise(nameof(GetRoles));
        Raise(nameof(DeleteRoles));
        Raise(nameof(AddCommand));
        Raise(nameof(GetCommand));
        Raise(nameof(DeleteCommand));
        Raise(nameof(ReplyTemplate));
        Raise(nameof(MasterEnabled));
        Raise(nameof(ChatCommands));
        RaiseStatusPill();
    }

    // ── Load / refresh quotes ───────────────────────────────────────────
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshQuotesAsync(force: true).ConfigureAwait(false);
    }

    // force: explicit refreshes (initial load, manual button) reconcile immediately;
    // a live event-driven refresh (force == false) defers while any row carries an
    // uncommitted Text/Name edit, so a background QuotesChanged burst doesn't shuffle
    // rows under the streamer's cursor. Either way the reconcile below preserves a
    // dirty row's in-progress edit (SyncFromEntry no-ops on a dirty row), so no
    // half-typed edit is ever discarded — the old Clear()+re-add silently was.
    private async Task RefreshQuotesAsync(bool force = false)
    {
        List<QuoteEntry> list;
        try { list = await _svc.ListAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("QuotesViewModel", "ListAsync failed", ex); return; }

        _ui.Post(() =>
        {
            if (!force && AnyQuoteDirty()) return;
            ReconcileQuotes(list);
            Raise(nameof(HasQuotes));
            Raise(nameof(NoQuotesVisibility));
            Raise(nameof(CountText));
            RaiseStatusPill();
        });
    }

    // Reconcile the bound row list against a freshly-read DB snapshot IN PLACE, keyed
    // by each quote's stable Number (numbers are never reused): clean rows update from
    // the DB, dirty rows keep their in-progress edit, new quotes insert at their ordered
    // slot, and deleted quotes are removed. Replaces the old Clear()+re-add, which
    // discarded a half-typed edit and reset the scroll position on every refresh.
    private void ReconcileQuotes(List<QuoteEntry> list)
    {
        var desired = new HashSet<int>();
        foreach (var q in list) desired.Add(q.Number);

        // Drop rows whose quote no longer exists (iterate backwards for safe removal).
        for (int i = Quotes.Count - 1; i >= 0; i--)
            if (!desired.Contains(Quotes[i].Number))
            {
                // A selected row that leaves the store takes the selection with it,
                // or the detail column keeps editing a quote that is gone.
                if (ReferenceEquals(Quotes[i], _selectedQuote)) SelectedQuote = null;
                Quotes.RemoveAt(i);
            }

        var byNumber = new Dictionary<int, QuoteRowVm>();
        foreach (var r in Quotes) byNumber[r.Number] = r;

        // ListAsync is Number-ascending; ensure each quote sits at its ordered slot.
        for (int i = 0; i < list.Count; i++)
        {
            var q = list[i];
            if (byNumber.TryGetValue(q.Number, out var existing))
            {
                existing.SyncFromEntry(q);            // no-op while that row is mid-edit
                int cur = Quotes.IndexOf(existing);
                if (cur != i) Quotes.Move(cur, i);
            }
            else
            {
                Quotes.Insert(i, new QuoteRowVm(q));
            }
        }
    }

    private bool AnyQuoteDirty()
    {
        foreach (var r in Quotes)
            if (r.IsDirty) return true;
        return false;
    }

    // ── Quote operations ────────────────────────────────────────────────
    public async Task AddQuoteAsync()
    {
        string name = (_newName ?? "").Trim();
        string text = (_newText ?? "").Trim();
        if (name.Length == 0 || text.Length == 0) return;
        try
        {
            // AddAsync raises QuotesChanged → OnQuotesChanged → RefreshQuotesAsync,
            // which reconciles the new row in. No explicit refresh here — the old
            // second RefreshQuotesAsync(force:true) was a redundant DB read + reconcile.
            await _svc.AddAsync(text, name, "panel").ConfigureAwait(false);
            _ui.Post(() => { NewName = ""; NewText = ""; });
        }
        catch (Exception ex) { GlobalLogger.Error("QuotesViewModel", "AddQuoteAsync failed", ex); }
    }

    public async Task UpdateQuoteAsync(QuoteRowVm row)
    {
        if (row is null) return;
        try
        {
            await _svc.UpdateAsync(row.Number, row.Text, row.Name).ConfigureAwait(false);
            row.MarkClean();   // committed → releases the refresh guard for this row
        }
        catch (Exception ex) { GlobalLogger.Error("QuotesViewModel", "UpdateQuoteAsync failed", ex); }
    }

    public async Task DeleteQuoteAsync(QuoteRowVm row)
    {
        if (row is null) return;
        try
        {
            // The row is going away — drop any in-progress edit so it can't keep the
            // dirty guard armed and leave the deleted row lingering on screen.
            row.MarkClean();
            // Clear the selection BEFORE the write: the reconcile that the resulting
            // QuotesChanged drives would otherwise be editing a row that no longer
            // exists for however long the round-trip takes.
            if (ReferenceEquals(row, _selectedQuote)) SelectedQuote = null;
            // DeleteAsync raises QuotesChanged → OnQuotesChanged → RefreshQuotesAsync,
            // which reconciles the row out. No explicit refresh here (was a redundant
            // second DB read + reconcile).
            await _svc.DeleteAsync(row.Number).ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("QuotesViewModel", "DeleteQuoteAsync failed", ex); }
    }

    public Task RefreshAsync() => RefreshQuotesAsync(force: true);

    // ── Persistence ─────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        // The single funnel for every edit the CHAT COMMANDS block renders — the
        // three command-word setters above and all three QuoteRolesVm gates, which
        // raise on themselves and call this action, so nothing else on this VM moves
        // when a permission checkmark changes.
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
        // SaveConfigAsync raises ConfigChanged synchronously (before this await
        // returns), so hold the self-save flag across the whole call — OnConfigChanged
        // fires while it is set and bails out.
        _selfSaving = true;
        try { await _svc.SaveConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("QuotesViewModel", "SaveConfigAsync failed", ex); }
        finally { _selfSaving = false; }
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Ignore the ConfigChanged our own SaveConfigAsync just raised — otherwise the
        // Normalize()/deep-clone the service applies makes it look foreign and we'd
        // rebuild the settings out from under the streamer's cursor. A foreign change
        // (another surface editing the same config) arrives with the flag clear.
        if (_selfSaving) return;
        _ui.Post(() =>
        {
            _working = Clone(_svc.Config);
            BuildSettings();
        });
    }

    // Raised on the WRITING thread (a chat handler, a script node, this panel's own
    // add / update / delete), so the timer is touched only after the marshal.
    private void OnQuotesChanged(object? sender, EventArgs e)
    {
        _ui.Post(() =>
        {
            if (_disposed) return;
            // No dispatcher (design-time / test host) means no timer, so refresh
            // straight through — the debounce is an optimisation, not a correctness
            // requirement.
            if (_refreshTimer is null) { _ = RefreshQuotesAsync(); return; }
            // Stop-then-Start on a non-repeating timer restarts the 300 ms window, so a
            // burst of writes costs one DB read and one feed rebuild, 300 ms after the
            // LAST write.
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private static QuotesConfig Clone(QuotesConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new QuotesConfig());
            return JsonSerializer.Deserialize<QuotesConfig>(json) ?? new QuotesConfig();
        }
        catch { return new QuotesConfig(); }
    }
}
