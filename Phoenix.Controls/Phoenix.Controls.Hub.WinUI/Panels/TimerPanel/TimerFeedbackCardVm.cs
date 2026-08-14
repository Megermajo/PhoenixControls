using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// One "what happens when this fires" card on the Timer page — the editable face of a
/// <see cref="TimerFeedbackConfig"/> (an on/off gate, a templated chat line, and an
/// overlay layer + trigger).
///
/// <para>Three of these exist per timer, one per event that has an Architect event node:
/// ZERO, MILESTONE (whose fields are the DEFAULTS every goal inherits) and ADD. There is
/// deliberately no card for subtract / start / stop / pause / resume / reset / happy hour
/// / cap-reached: those raise no script event, so a tool response for them would be a
/// capability no graph could reach.</para>
///
/// <para>Drafts are held here and pushed by <see cref="CommitAsync"/> on Enter /
/// focus-loss, matching the settings card's draft-then-commit grammar rather than writing
/// on every keystroke. <see cref="IsEditing"/> is what keeps a mid-typing edit alive
/// across the TimersChanged refreshes the service fires every tick — the same problem the
/// milestone rows solve with their own editing flags.</para>
/// </summary>
public sealed class TimerFeedbackCardVm : ObservableObject
{
    private readonly Func<string, IReadOnlyList<string>> _triggersFor;
    private readonly Func<Task> _commit;

    private bool _enabled;
    private string _message = "";
    private string _layerId = "";
    private string _triggerName = "";

    public TimerFeedbackCardVm(
        string eyebrow,
        string tokenHint,
        string messagePlaceholder,
        ObservableCollection<string> layerOptions,
        Func<string, IReadOnlyList<string>> triggersFor,
        Func<Task> commit)
    {
        Eyebrow = eyebrow;
        TokenHint = tokenHint;
        MessagePlaceholder = messagePlaceholder;
        LayerOptions = layerOptions;
        _triggersFor = triggersFor;
        _commit = commit;
    }

    /// <summary>Section heading ("ON ZERO", …) — static per card.</summary>
    public string Eyebrow { get; }

    /// <summary>The tokens this event actually binds, verbatim for the streamer. Only
    /// tokens the matching raise site fills are listed; one that is never bound would
    /// render empty and would promise a value no graph can reach.</summary>
    public string TokenHint { get; }

    /// <summary>Example line shown in the empty message box.</summary>
    public string MessagePlaceholder { get; }

    /// <summary>Shared layer-id list, owned by <see cref="TimerViewModel"/> — one
    /// registry read serves all three cards.</summary>
    public ObservableCollection<string> LayerOptions { get; }

    /// <summary>Trigger names on the currently chosen layer. Per-card, because it
    /// depends on this card's own layer.</summary>
    public ObservableCollection<string> TriggerOptions { get; } = new();

    /// <summary>True while a box in this card holds focus; suppresses re-seeding.</summary>
    public bool IsEditing { get; set; }

    /// <summary>The card's gate. Read by the view when it paints the pill (the Timer
    /// page's toggles are hand-rolled Border+Ellipse pills painted from code-behind, not
    /// bound controls), so there is no setter for XAML to reach.</summary>
    public bool Enabled
    {
        get => _enabled;
        private set => Set(ref _enabled, value);
    }

    public string Message
    {
        get => _message;
        set => Set(ref _message, value ?? "");
    }

    public string LayerId
    {
        get => _layerId;
        set
        {
            if (Set(ref _layerId, value ?? "")) RefreshTriggerOptions();
        }
    }

    public string TriggerName
    {
        get => _triggerName;
        set => Set(ref _triggerName, value ?? "");
    }

    /// <summary>Loads the card from the stored config. Skipped while the streamer is
    /// mid-edit so a background refresh cannot overwrite the caret.</summary>
    public void Seed(TimerFeedbackConfig? cfg)
    {
        if (IsEditing) return;
        Enabled = cfg?.Enabled ?? false;
        Message = cfg?.Message ?? "";
        LayerId = cfg?.LayerId ?? "";
        TriggerName = cfg?.TriggerName ?? "";
    }

    /// <summary>A detached copy of the current drafts, for the whole-config write.</summary>
    public TimerFeedbackConfig ToConfig() => new()
    {
        Enabled = _enabled,
        Message = _message,
        LayerId = _layerId,
        TriggerName = _triggerName,
    };

    /// <summary>Flips the gate and persists immediately — a toggle has no "commit"
    /// affordance of its own, so waiting for focus-loss would leave it looking applied
    /// while nothing was saved.</summary>
    public Task ToggleEnabledAsync()
    {
        Enabled = !_enabled;
        return _commit();
    }

    /// <summary>Pushes the drafts through the service. The whole per-timer feedback
    /// config is written at once (the <c>SetActionConfigAsync</c> shape), so every card
    /// commits through the same single write path.</summary>
    public Task CommitAsync() => _commit();

    /// <summary>Re-derives <see cref="TriggerOptions"/> from the chosen layer. Called on
    /// every layer change and when the drop-down opens, so a layer edited in Visualist
    /// while the page is open still offers its new triggers.</summary>
    public void RefreshTriggerOptions()
    {
        IReadOnlyList<string> names = _triggersFor(_layerId);
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
