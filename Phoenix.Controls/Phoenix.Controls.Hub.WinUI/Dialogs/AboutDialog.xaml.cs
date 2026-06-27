using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

/// <summary>
/// C10 (audit/winui-regressions-2026-05-24) — Hub "About" dialog.
/// <para/>
/// Before this sprint <c>Help → About</c> only logged a single version
/// line to the System Log; the menu entry felt like a dead end. This
/// dialog is the proper landing surface for the version / git SHA /
/// credits / GitHub URL operators routinely want when filing an issue.
/// <para/>
/// Visual treatment matches the Architect <c>KeyboardShortcutsDialog</c>
/// (the other ContentDialog using the forged-charcoal Coal* / Ember* /
/// Brass* tokens from PhoenixDark.xaml) so the dialog reads as the same
/// shell-chrome family. Per the per-pillar paint-helper rule in
/// <c>feedback_visualist_architect_chrome_independence.md</c> this stays
/// in Hub.WinUI — no shared paint helper introduced for the dialog.
/// </summary>
public sealed partial class AboutDialog : ContentDialog
{
    private const string GitHubUrl = "https://github.com/" + UpdateChecker.DefaultGitHubRepo;

    public AboutDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try
            {
                ApplyLocalizedChrome();
                HydrateMetadata();
            }
            catch (Exception ex)
            {
                // The About dialog isn't load-bearing — failure to populate
                // a field is far better than blocking dialog open. Failures
                // route through System tier per
                // feedback_no_modal_dialogs_for_repeatable_rejections.md.
                GlobalLogger.Error("AboutDialog", "metadata hydration failed", ex);
            }
        };
    }

    /// <summary>
    /// Convenience open helper — mirrors KeyboardShortcutsDialog.ShowAsync.
    /// Routes failures through GlobalLogger consistently and short-circuits
    /// on null <paramref name="xamlRoot"/> so a mid-teardown caller can't
    /// crash the menu dispatch.
    /// </summary>
    public static async System.Threading.Tasks.Task ShowAsync(XamlRoot xamlRoot)
    {
        if (xamlRoot is null)
        {
            GlobalLogger.Log("AboutDialog: no XamlRoot available — skipping show.",
                "Hub.Dialogs", LogLevel.System);
            return;
        }
        try
        {
            var dlg = new AboutDialog { XamlRoot = xamlRoot };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Hub.Dialogs", "AboutDialog.ShowAsync", ex);
        }
    }

    private void ApplyLocalizedChrome()
    {
        Title           = Localizer.T("dialog.about.title", "About Phoenix Controls");
        CloseButtonText = Localizer.T("dialog.about.button.close", "Close");

        TaglineText.Text = Localizer.T(
            "dialog.about.tagline",
            "Multi-process Twitch streamer suite — Hub · Architect · Visualist.");

        CreditsText.Text = Localizer.T(
            "dialog.about.credits",
            "Built by Majo · with Claude Code");

        // Year stamp pulled from the running clock so the line stays current
        // without an annual edit. Phoenix Controls is Majo's project; the
        // copyright text is intentionally short — no "All rights reserved"
        // boilerplate.
        int year = DateTime.UtcNow.Year;
        CopyrightText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Localizer.T("dialog.about.copyright",
                "© {0} Majo. Released under the project's repository license."),
            year);

        GitHubLinkText.Text = GitHubUrl;
    }

    /// <summary>
    /// Reads version + branch + SHA + configuration from the running process.
    /// Each field falls back to a sensible placeholder when the metadata
    /// isn't resolvable (copy-only installs strip the .git folder; the
    /// AssemblyConfigurationAttribute is omitted on some custom configs).
    /// </summary>
    private void HydrateMetadata()
    {
        var asm = Assembly.GetExecutingAssembly();

        // VERSION — informational version first (preserves SemVer suffixes
        // from Directory.Build.props), falling back to AssemblyName.Version.
        // GitInfoService.GetAssemblyVersion already strips MSBuild's SourceLink
        // "+sha" suffix so the displayed value matches the version-stamp files.
        string versionLabel;
        try { versionLabel = "v" + GitInfoService.GetAssemblyVersion(asm); }
        catch
        {
            var v = asm.GetName().Version;
            versionLabel = v is null
                ? "v0.0.0"
                : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
        VersionValue.Text = versionLabel;

        // GIT SHA — null when running from a published bundle without the
        // .git folder. The displayed value uses the 7-char short form
        // matching `git log --abbrev-commit` defaults.
        string? sha = SafeCall(() => GitInfoService.GetLocalShaShort());
        GitShaValue.Text = string.IsNullOrEmpty(sha)
            ? Localizer.T("dialog.about.git_sha_unavailable", "(not in a git checkout)")
            : sha;

        // BRANCH — same caveat as SHA. Detached-HEAD states render the
        // unavailable string so the dialog never lies about being on a
        // branch.
        string? branch = SafeCall(() => GitInfoService.GetCurrentBranch());
        BranchValue.Text = string.IsNullOrEmpty(branch)
            ? Localizer.T("dialog.about.branch_unavailable", "(detached / unavailable)")
            : branch;

        // BUILD — AssemblyConfigurationAttribute carries "Debug" / "Release"
        // (MSBuild populates it from $(Configuration) by default). The DEV /
        // RELEASE labelling matches the HubChrome's VersionStamp formatting.
        string configuration = asm.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
                               ?? "Unknown";
        BuildConfigValue.Text = string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : configuration;
    }

    private void OnGitHubLinkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute=true so the user's default browser handles the
            // navigation — the same idiom as MainWindow.OpenUrl. The hyperlink
            // is the canonical entry point for the GitHub repo URL displayed
            // immediately above it.
            // Disambiguate against Phoenix.Controls.Hub.Core.Process — the
            // System.Diagnostics version is what we want for shell-execute.
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName        = GitHubUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("AboutDialog", $"OpenUrl failed for '{GitHubUrl}'", ex);
        }
    }

    private static T? SafeCall<T>(Func<T?> fn) where T : class
    {
        try { return fn(); }
        catch { return null; }
    }
}
