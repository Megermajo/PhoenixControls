using System;
using System.Threading.Tasks;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// One editable Settings-card field with the Giveaway VM's draft / seed /
/// commit / revert seam, generalised so the Timer settings card can carry a
/// dozen numeric inputs without a dozen hand-rolled property triples.
///
/// The field owns only the plumbing (dirtiness tracking + pristine re-seed);
/// the two delegates supplied at construction own the type-specific bits:
///   • <c>format</c> renders the stored value → seed text.
///   • <c>plan</c> parses the draft against the current timer and returns
///     (Valid, Changed, Save) — Valid=false reverts, Changed=false normalises
///     cosmetic variants without a write, otherwise Save persists it.
///
/// Validation posture per the standing rule: numeric range clamp only, no
/// grammar control. The commit path never throws at the caller — a save fault
/// is logged, and an invalid draft silently reverts.
/// </summary>
public sealed class TimerDraftField : ObservableObject
{
    public delegate (bool Valid, bool Changed, Func<Task>? Save) Planner(string draft, StreamTimer timer);

    private readonly Func<StreamTimer, string> _format;
    private readonly Planner _plan;

    private string _draft = string.Empty;
    private string _seeded = string.Empty;
    private string? _seededSlug;
    private bool _hasSelection;

    public TimerDraftField(Func<StreamTimer, string> format, Planner plan)
    {
        _format = format;
        _plan = plan;
    }

    /// <summary>Raw text bound two-way to the input box.</summary>
    public string Draft
    {
        get => _draft;
        set => Set(ref _draft, value ?? string.Empty);
    }

    /// <summary>Disabled (greyed) when no timer is selected.</summary>
    public bool IsEnabled
    {
        get => _hasSelection;
        private set => Set(ref _hasSelection, value);
    }

    /// <summary>Force-seed from the stored value and mark pristine.</summary>
    public void Seed(StreamTimer? t)
    {
        _seededSlug = t?.Slug;
        _seeded = t is null ? string.Empty : _format(t);
        Draft = _seeded;
        IsEnabled = t is not null;
    }

    /// <summary>
    /// Re-seed only when the selection changed OR the draft is still pristine —
    /// an uncommitted mid-typing edit for the SAME timer survives the
    /// TimersChanged refreshes the service fires on every tick / state change.
    /// </summary>
    public void SeedIfPristine(StreamTimer? t)
    {
        if (_seededSlug != t?.Slug || _draft == _seeded) Seed(t);
        else IsEnabled = t is not null;
    }

    /// <summary>Drop an in-progress edit back to the stored value.</summary>
    public void Revert(StreamTimer? t) => Seed(t);

    /// <summary>Parse + persist the draft; revert on invalid, no-op on unchanged.</summary>
    public async Task CommitAsync(StreamTimer? t)
    {
        if (t is null) return;
        (bool valid, bool changed, Func<Task>? save) = _plan(_draft, t);
        if (!valid || !changed || save is null)
        {
            // invalid → revert to stored; unchanged → normalise cosmetic
            // variants ("007", "4 h", padded input) back to the stored form.
            Seed(t);
            return;
        }
        // Mark pristine BEFORE awaiting so the TimersChanged refresh the save
        // triggers keeps the just-committed text instead of clobbering it.
        _seededSlug = t.Slug;
        _seeded = _draft;
        try { await save().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "commit setting failed", ex); }
    }
}
