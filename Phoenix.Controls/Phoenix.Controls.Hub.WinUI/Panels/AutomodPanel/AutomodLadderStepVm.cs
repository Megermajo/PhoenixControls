using System;
using System.Globalization;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.AutomodPanel;

/// <summary>
/// One rung of the escalation ladder in the Ladder section — an action (Warn / Delete /
/// Timeout / Ban) and, for Timeout, a duration. Strike N resolves to the rung at
/// index min(N-1, last). Each edit mutates the underlying step on the working
/// config and schedules a debounced save.
/// </summary>
public sealed class AutomodLadderStepVm : ObservableObject
{
    private readonly AutomodLadderStep _step;
    private readonly Action _changed;

    public AutomodLadderStepVm(int stepNumber, AutomodLadderStep step, Action changed)
    {
        StepNumber = stepNumber;
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _changed = changed ?? (static () => { });
    }

    internal AutomodLadderStep Step => _step;

    public int StepNumber { get; }
    public string StepLabel => string.Format(Localizer.T("panel.automod.ladder.rung.label", "Strike {0}"), StepNumber);

    /// <summary>ComboBox index over {Warn, Delete, Timeout, Ban} ↔ AutomodAction.</summary>
    public int ActionIndex
    {
        get => Math.Max(0, (int)_step.Action - 1);
        set
        {
            var action = (AutomodAction)(Math.Clamp(value, 0, 3) + 1);
            if (_step.Action == action) return;
            _step.Action = action;
            Raise(nameof(ActionIndex));
            Raise(nameof(DurationEnabled));
            _changed();
        }
    }

    /// <summary>Duration only matters for a Timeout rung.</summary>
    public bool DurationEnabled => _step.Action == AutomodAction.Timeout;

    public string DurationText
    {
        get => _step.DurationSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _step.DurationSeconds));
            if (_step.DurationSeconds == v) { Raise(nameof(DurationText)); return; }
            _step.DurationSeconds = v;
            Raise(nameof(DurationText));
            _changed();
        }
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
