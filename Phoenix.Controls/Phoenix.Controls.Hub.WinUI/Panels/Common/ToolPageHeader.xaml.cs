using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// The 48 px Pre-Build page header band: tool name · master switch · one state
/// phrase · verbs.
///
/// <para>It replaces three stacked rows — the action strip, the state-sentence
/// banner and the status pill — which between them rendered the same boolean
/// three times in three vocabularies (DISABLED / OFF / dormant) and cost
/// ~120 px before a single setting was visible.</para>
///
/// <para><b>The pill is not deleted, it is re-scoped.</b>
/// <c>ToolStatusPill</c> and the service-side state machines behind it stay —
/// they carry the 1.1.7 honesty work, where two pills that showed a green light
/// over a dead tool were fixed and three requested states were refused as
/// dishonest. That audit lives in the PREDICATES, not the markup, and
/// <c>PreBuildStatusPillTests</c> still guards them. What changes is only where
/// the result is rendered: config pages get <see cref="StateText"/>; watch
/// surfaces keep the pill.</para>
///
/// <para>Deliberately plain properties, not DependencyProperties: every
/// consumer sets these once from code-behind at construction. Adding DP
/// machinery for a write-once label would be ceremony.</para>
/// </summary>
public sealed partial class ToolPageHeader : UserControl
{
    public ToolPageHeader()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the master switch is toggled by the user.</summary>
    public event EventHandler<bool>? MasterToggled;

    /// <summary>Raised by the optional refresh verb.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised by the optional single brass CTA.</summary>
    public event EventHandler? PrimaryCtaInvoked;

    /// <summary>Suppresses <see cref="MasterToggled"/> while the host seeds the
    /// switch from config, so restoring state cannot look like a user gesture
    /// and write back a change the streamer never made.</summary>
    private bool _seeding;

    /// <summary>Tool name, DisplayFont 19.</summary>
    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value ?? string.Empty;
    }

    /// <summary>Master enable. Setting this does NOT raise
    /// <see cref="MasterToggled"/>.</summary>
    public bool IsOn
    {
        get => MasterSwitch.IsOn;
        set
        {
            if (MasterSwitch.IsOn == value) return;
            _seeding = true;
            try { MasterSwitch.IsOn = value; }
            finally { _seeding = false; }
        }
    }

    /// <summary>
    /// The one state phrase, lowercase, state AND consequence in three words or
    /// fewer — "off", "live · earning", "armed · queue open", "error · no bot
    /// link". <paramref name="kind"/> picks the foreground.
    /// </summary>
    public void SetState(string text, ToolStateKind kind = ToolStateKind.Dormant)
    {
        StateText.Text = text ?? string.Empty;
        string key = kind switch
        {
            ToolStateKind.Live  => "OkBrush",
            ToolStateKind.Error => "ErrBrush",
            _                   => "CoalSecondaryTextBrush",
        };
        if (Application.Current.Resources.TryGetValue(key, out object? brush)
            && brush is Microsoft.UI.Xaml.Media.Brush b)
        {
            StateText.Foreground = b;
        }
    }

    /// <summary>The English wording a caller gets when it omits the tooltip. A
    /// parameter default must be a compile-time constant, so the localization of
    /// this string happens in the method body instead.</summary>
    private const string DefaultRefreshTooltip = "Refresh";

    /// <summary>Show the refresh verb (28 px icon button). A caller that passes
    /// its own wording is expected to have localized it already; the default is
    /// localized here, and matched by value so an explicit "Refresh" translates
    /// too.</summary>
    public void EnableRefresh(string tooltip = DefaultRefreshTooltip)
    {
        string label = string.Equals(tooltip, DefaultRefreshTooltip, StringComparison.Ordinal)
            ? Localizer.T("panel.common.header.refresh.tip", DefaultRefreshTooltip)
            : tooltip;

        RefreshButton.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(RefreshButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RefreshButton, label);
    }

    /// <summary>
    /// Show the single brass CTA. Only call this where the page has a genuine
    /// add-verb; ToolShellStyles allows exactly one brass CTA per visible
    /// surface, and the live column is part of that surface.
    /// </summary>
    public void EnablePrimaryCta(string label)
    {
        PrimaryCtaLabel.Text = label ?? string.Empty;
        PrimaryCta.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrimaryCta, label ?? string.Empty);
    }

    private void OnMasterToggled(object sender, RoutedEventArgs e)
    {
        if (_seeding) return;
        MasterToggled?.Invoke(this, MasterSwitch.IsOn);
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
        => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnPrimaryCtaClicked(object sender, RoutedEventArgs e)
        => PrimaryCtaInvoked?.Invoke(this, EventArgs.Empty);
}

/// <summary>Foreground tier for the header's state phrase.</summary>
public enum ToolStateKind
{
    /// <summary>Off / idle — CoalSecondaryText.</summary>
    Dormant,
    /// <summary>Running and doing its job — OkBrush.</summary>
    Live,
    /// <summary>Enabled but unable to work — ErrBrush.</summary>
    Error,
}
