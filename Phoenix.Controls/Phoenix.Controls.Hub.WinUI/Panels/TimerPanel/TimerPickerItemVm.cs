using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// Row-level VM for the timer picker dropdown (top action strip). Mirrors the
/// Giveaway picker row: name + slug + state + remaining, a DEFAULT pin badge,
/// and a state-colored status string (running → ok green, paused → ember,
/// ended → error, stopped → secondary).
/// </summary>
public sealed class TimerPickerItemVm
{
    public TimerPickerItemVm(StreamTimer t)
    {
        Slug = t.Slug;
        Name = t.Name;
        IsDefault = t.IsDefault;
        State = t.State;
        StateText = LocalizedStateText(t.State);
        RemainingText = Phoenix.Controls.Hub.Core.TimerService.FormatDuration(t.RemainingMs, "short");

        StateBrush = t.State switch
        {
            TimerRunState.Running => TimerBrushes.Lookup("OkBrush",                0x6F, 0xA4, 0x6B),
            TimerRunState.Paused  => TimerBrushes.Lookup("EmberPrimaryBrush",      0xE5, 0xA2, 0x4E),
            TimerRunState.Ended   => TimerBrushes.Lookup("ErrBrush",               0xC8, 0x5C, 0x54),
            _                     => TimerBrushes.Lookup("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72),
        };
    }

    // The row's state word. Looked up rather than ToString()-ed so it reads in the
    // streamer's language alongside the rest of the page; the default arm keeps the
    // old lowercased enum name, so a run state added later renders exactly as before.
    private static string LocalizedStateText(TimerRunState state) => state switch
    {
        TimerRunState.Running => Localizer.T("panel.timer.picker.state.running", "running"),
        TimerRunState.Paused  => Localizer.T("panel.timer.picker.state.paused", "paused"),
        TimerRunState.Ended   => Localizer.T("panel.timer.picker.state.ended", "ended"),
        TimerRunState.Stopped => Localizer.T("panel.timer.picker.state.stopped", "stopped"),
        _                     => state.ToString().ToLowerInvariant(),
    };

    public string Slug { get; }
    public string Name { get; }
    public bool IsDefault { get; }
    public TimerRunState State { get; }
    public string StateText { get; }
    public string RemainingText { get; }
    public Brush StateBrush { get; }

    public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;

    // Picker rows show the stable slug (e.g. "t-2026-07-16-a") as the visible
    // monospace id.
    public string DisplayId => Slug;
}
