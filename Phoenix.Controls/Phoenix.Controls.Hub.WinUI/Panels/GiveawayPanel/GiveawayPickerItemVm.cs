using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// Row-level VM for the giveaway picker dropdown (top action strip —
/// giveaway.jsx <c>GiveawayPicker</c> list item). Surfaces title + id +
/// status + entrant / ticket counts, the DEFAULT pin badge, and a
/// status-colored status string (open → ok green, drawn → ember, otherwise
/// secondary).
/// </summary>
public sealed class GiveawayPickerItemVm
{
    public GiveawayPickerItemVm(GiveawayInfo info)
    {
        Info = info;
        Id = info.Id;
        Key = info.Key;
        Title = info.Title;
        IsDefault = info.IsDefault;
        Status = info.Status;
        StatusText = info.Status.ToString().ToLowerInvariant();
        EntrantsText = info.Entrants.ToString();
        TicketsText = info.Tickets + " tickets";

        StatusBrush = info.Status switch
        {
            GiveawayStatus.Open  => GiveawayBrushes.Lookup("OkBrush",                  0x6F, 0xA4, 0x6B),
            GiveawayStatus.Drawn => GiveawayBrushes.Lookup("EmberPrimaryBrush",        0xE5, 0xA2, 0x4E),
            _                    => GiveawayBrushes.Lookup("CoalSecondaryTextBrush",   0x9C, 0x8A, 0x72),
        };
    }

    public GiveawayInfo Info { get; }
    public long Id { get; }
    public string Key { get; }
    public string Title { get; }
    public bool IsDefault { get; }
    public GiveawayStatus Status { get; }
    public string StatusText { get; }
    public string EntrantsText { get; }
    public string TicketsText { get; }
    public Brush StatusBrush { get; }

    public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;

    // Picker rows use the giveaway Key (e.g. "g-2024-11-22-a") as the visible
    // monospace id — falls back to the numeric Id when no key is set.
    public string DisplayId => string.IsNullOrEmpty(Key) ? Id.ToString() : Key;
}
