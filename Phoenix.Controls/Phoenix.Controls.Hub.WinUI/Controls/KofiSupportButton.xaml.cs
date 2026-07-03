using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Controls;

/// <summary>
/// Discrete "support development" button — a compact, monospace chrome control
/// that opens Majo's Ko-fi page in the default browser. Faithful to the
/// "Ko-Fi Chrome Compact" design (Ko-fi cup mark + "Support my work" + NE
/// arrow, coal gradient with an ember-glow hover). It is intentionally small
/// and quiet: Phoenix Controls is not a commercial tool, this is just a subtle
/// hint that the project can be supported.
/// <para/>
/// Used as a footer in <c>SettingsDialog</c> and <c>WelcomeDialog</c>. Per the
/// per-pillar paint rule it lives in Hub.WinUI and is not lifted into Shared.
/// </summary>
public sealed partial class KofiSupportButton : UserControl
{
    // Majo's Ko-fi page — baked into the source design ("Ko-Fi Chrome Compact").
    private const string KofiUrl = "https://ko-fi.com/megermajo";

    public KofiSupportButton()
    {
        InitializeComponent();

        // Localized label + tooltip. The XAML carries the canonical English as
        // a design-time / fallback value; Localizer.T overrides it at runtime
        // (returns the same English fallback if the active language has no key).
        // The label is the Button's Content, rendered by the template's
        // ContentPresenter — RootButton is the only element in the UserControl
        // namescope, so the inner template parts aren't reachable from here.
        RootButton.Content = Localizer.T("control.kofi.support_label", "Support my work");
        ToolTipService.SetToolTip(
            RootButton,
            Localizer.T("control.kofi.tooltip", "Support Phoenix Controls on Ko-fi"));
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute=true hands the URL to the default browser — the
            // same idiom as AboutDialog.OnGitHubLinkClick / MainWindow.OpenUrl.
            // Fully qualified to avoid the Phoenix.Controls.Hub.Core.Process clash.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = KofiUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("KofiSupportButton", $"OpenUrl failed for '{KofiUrl}'", ex);
        }
    }
}
