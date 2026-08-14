using System;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// One redeemable reward on the Rewards tab — every <see cref="LoyaltyReward"/>
/// field surfaced as an inline editor, plus a delete hook. Edits mutate the
/// reward on the working config and schedule a debounced save through
/// <c>changed</c>; <see cref="Delete"/> asks the owning VM to drop this row.
/// Carries the per-row expand state for the hand-rolled collapsible card (the
/// WinUI Expander was removed after the Minigames first-realization crash).
/// </summary>
public sealed class LoyaltyRewardRowVm : ObservableObject
{
    private readonly Action<LoyaltyRewardRowVm> _delete;

    public LoyaltyRewardRowVm(LoyaltyReward reward, Action changed, Action<LoyaltyRewardRowVm> delete)
    {
        Reward = reward;
        _delete = delete;

        EnabledField      = LoyaltyField.Bool(() => reward.Enabled, v => reward.Enabled = v, changed);
        NameField         = LoyaltyField.Text(() => reward.Name, s => reward.Name = s, changed);
        CostField         = LoyaltyField.Long(() => reward.Cost, v => reward.Cost = v, changed);
        QuantityField     = LoyaltyField.Int(() => reward.Quantity, v => reward.Quantity = v, changed);
        PerUserCdField    = LoyaltyField.Int(() => reward.PerUserCooldownSeconds, v => reward.PerUserCooldownSeconds = v, changed);
        GlobalCdField     = LoyaltyField.Int(() => reward.GlobalCooldownSeconds, v => reward.GlobalCooldownSeconds = v, changed);
        LayerIdField      = LoyaltyField.Text(() => reward.LayerId, s => reward.LayerId = s, changed);
        TriggerNameField  = LoyaltyField.Text(() => reward.TriggerName, s => reward.TriggerName = s, changed);
        AnnounceField     = LoyaltyField.Text(() => reward.AnnounceMessage, s => reward.AnnounceMessage = s, changed);

        // Keep the card-header eyebrow in sync with live name edits.
        NameField.PropertyChanged += (_, _) => Raise(nameof(NameEyebrow));
    }

    public LoyaltyReward Reward { get; }

    public LoyaltyBoolField EnabledField { get; }
    public LoyaltyDraftField NameField { get; }
    public LoyaltyDraftField CostField { get; }
    public LoyaltyDraftField QuantityField { get; }
    public LoyaltyDraftField PerUserCdField { get; }
    public LoyaltyDraftField GlobalCdField { get; }
    public LoyaltyDraftField LayerIdField { get; }
    public LoyaltyDraftField TriggerNameField { get; }
    public LoyaltyDraftField AnnounceField { get; }

    /// <summary>Uppercased reward name for the card-header eyebrow.</summary>
    public string NameEyebrow
    {
        get
        {
            string n = (NameField.Text ?? string.Empty).Trim();
            return n.Length == 0
                ? Localizer.T("panel.loyalty.reward.eyebrow_fallback", "REWARD")
                : n.ToUpperInvariant();
        }
    }

    // Collapsible-card state — collapsed by default (the old Expander default).
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
            {
                Raise(nameof(BodyVisibility));
                Raise(nameof(ChevronGlyph));
            }
        }
    }

    public Visibility BodyVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;
    // Segoe MDL2 ChevronUp (expanded) / ChevronDown (collapsed) — the exact
    // pair the Giveaway SETTINGS collapsible uses.
    public string ChevronGlyph => _isExpanded ? "" : "";

    public void Delete() => _delete(this);
}
