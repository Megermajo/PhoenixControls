using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// Row-level VM for the milestones list. A milestone fires the first time the value
/// its timer DISPLAYS reaches the target — elapsed on a Stopwatch, remaining on a
/// Subathon/Countdown — and once reached it carries a lit ember badge. The remove
/// affordance carries the milestone <see cref="Id"/> so the view can route a delete
/// without a back-reference to the source list.
///
/// <para>Both the label and the target are EDITABLE. They used to be get-only
/// projections rendered as read-only TextBlocks, with no service call behind them, so
/// clicking the time did nothing at all and a mistyped goal could only be deleted and
/// re-added — the reported "the textpill can not be edited". Editing commits through
/// <see cref="Commit"/> on focus-loss / Enter.</para>
///
/// <para>The class is mutable and observable ON PURPOSE. The list is reconciled by Id
/// rather than rebuilt, so a row's values are refreshed in place via
/// <see cref="Update"/> while the streamer is typing in a sibling row. Replacing the
/// row object (the old Clear + re-Add) would tear the focused TextBox out from under
/// the caret on the very next 1 Hz refresh — and worse, on the refresh caused by the
/// row's own commit.</para>
/// </summary>
public sealed class TimerMilestoneRowVm : ObservableObject
{
    private readonly Func<TimerMilestoneRowVm, System.Threading.Tasks.Task> _commit;
    private readonly Func<TimerMilestoneRowVm, System.Threading.Tasks.Task> _commitFeedback;
    private readonly Func<string, IReadOnlyList<string>> _triggersFor;

    private string _labelDraft = "";
    private string _targetDraft = "";
    private bool _reached;
    private string _messageDraft = "";
    private string _layerIdDraft = "";
    private string _triggerNameDraft = "";
    private bool _feedbackExpanded;

    public TimerMilestoneRowVm(
        TimerMilestone m,
        string targetText,
        Func<TimerMilestoneRowVm, System.Threading.Tasks.Task> commit,
        ObservableCollection<string> layerOptions,
        Func<string, IReadOnlyList<string>> triggersFor,
        Func<TimerMilestoneRowVm, System.Threading.Tasks.Task> commitFeedback)
    {
        Id = m.Id;
        _commit = commit ?? (static _ => System.Threading.Tasks.Task.CompletedTask);
        _commitFeedback = commitFeedback ?? (static _ => System.Threading.Tasks.Task.CompletedTask);
        _triggersFor = triggersFor ?? (static _ => Array.Empty<string>());
        LayerOptions = layerOptions ?? new ObservableCollection<string>();
        Update(m, targetText);
    }

    public string Id { get; }

    /// <summary>
    /// Refreshes this row from the authoritative milestone WITHOUT replacing the
    /// object. Drafts the streamer is mid-edit are left alone: an in-flight edit must
    /// survive the refresh its own commit triggers (SetMilestoneAsync bumps
    /// UpdatedAtUnixMs, which is part of the selection stamp).
    /// </summary>
    public void Update(TimerMilestone m, string targetText)
    {
        if (!IsEditingLabel) SetLabelDraft(string.IsNullOrWhiteSpace(m.Label) ? "" : m.Label);
        if (!IsEditingTarget) SetTargetDraft(targetText);
        if (!IsEditingFeedback)
        {
            MessageDraft = m.Message ?? "";
            LayerIdDraft = m.LayerId ?? "";
            TriggerNameDraft = m.TriggerName ?? "";
        }
        HasFeedback = !string.IsNullOrWhiteSpace(m.Message)
                      || !string.IsNullOrWhiteSpace(m.LayerId)
                      || !string.IsNullOrWhiteSpace(m.TriggerName);

        Reached = m.Reached;
    }

    /// <summary>True while the streamer has focus in this row's label box.</summary>
    public bool IsEditingLabel { get; set; }

    /// <summary>True while the streamer has focus in this row's target box.</summary>
    public bool IsEditingTarget { get; set; }

    public string LabelDraft
    {
        get => _labelDraft;
        set => Set(ref _labelDraft, value ?? "");
    }

    /// <summary>The "time pill" — a duration string ("1h", "90s", "1h30m").</summary>
    public string TargetDraft
    {
        get => _targetDraft;
        set => Set(ref _targetDraft, value ?? "");
    }

    private void SetLabelDraft(string v) => LabelDraft = v;
    private void SetTargetDraft(string v) => TargetDraft = v;

    public bool Reached
    {
        get => _reached;
        private set
        {
            if (!Set(ref _reached, value)) return;
            Raise(nameof(ReachedBadgeVisibility));
            Raise(nameof(LabelBrush));
        }
    }

    public Brush LabelBrush => Reached
        ? TimerBrushes.Lookup("Ember200Brush", 0xF2, 0xC7, 0x7F)
        : TimerBrushes.Lookup("CoalPrimaryTextBrush", 0xE6, 0xDD, 0xD0);

    public Visibility ReachedBadgeVisibility => Reached ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Pushes the current drafts to the service. No-ops when nothing changed
    /// (the caller cannot know that; the VM compares before dispatching).</summary>
    public System.Threading.Tasks.Task CommitAsync() => _commit(this);

    // ── Per-goal feedback (chat line + overlay hookup) ───────────────────
    // Folded away behind a chevron by default: the row's job is the goal itself, and a
    // permanently-open three-field block would push the list down to two visible rows.
    // Empty fields inherit the timer-wide MILESTONE defaults, which is why a goal can be
    // left untouched and still announce.

    /// <summary>Shared layer-id list owned by the page (one registry read for all rows).</summary>
    public ObservableCollection<string> LayerOptions { get; }

    /// <summary>Trigger names on this row's chosen layer.</summary>
    public ObservableCollection<string> TriggerOptions { get; } = new();

    /// <summary>True while a feedback box in this row holds focus — the reconciler
    /// leaves the drafts alone, exactly as it does for the label and target boxes.</summary>
    public bool IsEditingFeedback { get; set; }

    public bool FeedbackExpanded
    {
        get => _feedbackExpanded;
        set
        {
            if (!Set(ref _feedbackExpanded, value)) return;
            Raise(nameof(FeedbackBodyVisibility));
            Raise(nameof(FeedbackChevronGlyph));
            if (value) RefreshTriggerOptions();
        }
    }

    public Visibility FeedbackBodyVisibility => _feedbackExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string FeedbackChevronGlyph => _feedbackExpanded ? "" : "";

    private bool _hasFeedback;
    /// <summary>True when this goal overrides any of the timer-wide defaults — lights a
    /// dot on the collapsed row so an override is visible without opening it.</summary>
    public bool HasFeedback
    {
        get => _hasFeedback;
        private set { if (Set(ref _hasFeedback, value)) Raise(nameof(FeedbackDotVisibility)); }
    }
    public Visibility FeedbackDotVisibility => _hasFeedback ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>This goal's own chat line. Empty = use the timer-wide default.</summary>
    public string MessageDraft
    {
        get => _messageDraft;
        set => Set(ref _messageDraft, value ?? "");
    }

    /// <summary>This goal's own overlay layer. Empty = use the timer-wide default.</summary>
    public string LayerIdDraft
    {
        get => _layerIdDraft;
        set { if (Set(ref _layerIdDraft, value ?? "")) RefreshTriggerOptions(); }
    }

    /// <summary>This goal's own overlay trigger. Empty = use the timer-wide default.</summary>
    public string TriggerNameDraft
    {
        get => _triggerNameDraft;
        set => Set(ref _triggerNameDraft, value ?? "");
    }

    public void ToggleFeedbackExpanded() => FeedbackExpanded = !FeedbackExpanded;

    /// <summary>Persists this row's feedback overrides through the service.</summary>
    public System.Threading.Tasks.Task CommitFeedbackAsync() => _commitFeedback(this);

    /// <summary>Re-derives <see cref="TriggerOptions"/> from the chosen layer.</summary>
    public void RefreshTriggerOptions()
    {
        IReadOnlyList<string> names = _triggersFor(_layerIdDraft);
        bool same = names.Count == TriggerOptions.Count;
        if (same)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (!string.Equals(names[i], TriggerOptions[i], StringComparison.Ordinal)) { same = false; break; }
            }
        }
        if (same) return;
        TriggerOptions.Clear();
        foreach (var n in names) TriggerOptions.Add(n);
    }
}
