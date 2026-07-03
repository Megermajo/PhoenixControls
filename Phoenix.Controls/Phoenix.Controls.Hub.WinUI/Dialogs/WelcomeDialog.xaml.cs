using System;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

// First-run welcome dialog (TODO P0 #1) — surfaces the optional sample-
// graph picker on first launch. Scans data/logic/examples/ at open time
// and renders one row per .phxg / .phx file. "Use this" copies the file
// into Hub's active data/logic/ folder so Architect can pick it up via
// LogicWatcher; Skip dismisses without copying. Either path flips
// AppConfig.SeenWelcomeDialog so the dialog never re-opens unbidden.
//
// The examples/ folder ships with a curated set of numbered samples
// (01_starter_command, 02_raid_alert, …). If a future build empties
// the folder the dialog still renders a graceful empty state so the
// user can dismiss without a confusing "0 samples" gallery.
public sealed partial class WelcomeDialog : ContentDialog
{
    public ObservableCollection<SampleGraphRowVm> Samples { get; } = new();

    /// <summary>True after the user copied a sample into data/logic/.</summary>
    public bool SamplePicked { get; private set; }

    /// <summary>Absolute path of the copied sample inside data/logic/, or
    /// <c>null</c> if the user dismissed without picking. Carries the final
    /// destination (including any numeric suffix applied to avoid
    /// overwriting an existing file) so the caller can route the streamer
    /// straight into Architect with the file already loaded.</summary>
    public string? PickedSamplePath { get; private set; }

    public WelcomeDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyLocalizedChrome();
            HydrateSamples();
        };
        // ESC / system-close dismiss the dialog without firing
        // OnSkipClick, which left SeenWelcomeDialog=false and re-opened
        // the dialog on the next launch. Hook Closing so every
        // dismissal path marks the dialog as seen, not just the Skip
        // button. Save is idempotent so the duplicate call from
        // MarkSeenAndClose() → Hide() → Closing is harmless.
        Closing += OnDialogClosing;
    }

    private async void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Async save so a slow OneDrive-backed AppData write
        // doesn't peg the UI thread while the dialog dismiss animation
        // runs. GetDeferral keeps the dialog open until the write
        // completes — without the deferral the closing animation could
        // finish before the save lands and the user sees a flash of
        // the underlying window before the next state.
        var deferral = args.GetDeferral();
        try
        {
            ConfigManager.Current.SeenWelcomeDialog = true;
            await ConfigManager.SaveAsync(Paths.AppConfigJson).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WelcomeDialog",
                "save SeenWelcomeDialog on Closing failed", ex);
        }
        finally
        {
            try { deferral.Complete(); } catch { /* dialog tear-down race */ }
        }
    }

    /// <summary>
    /// Sets every user-visible string on the dialog from <see cref="Localizer"/>.
    /// Called once on Loaded — restart-required language switching means we
    /// don't need to re-apply mid-session. The English fallback passed to
    /// <see cref="Localizer.T"/> is the canonical EN string and matches the
    /// value in <c>lang/en.json</c> verbatim.
    /// </summary>
    private void ApplyLocalizedChrome()
    {
        Title             = Localizer.T("dialog.welcome.title", "Welcome to Phoenix Controls");
        PrimaryButtonText = Localizer.T("dialog.welcome.button.skip", "Skip — start blank");

        EyebrowText.Text   = Localizer.T("dialog.welcome.eyebrow.first_run", "00 · FIRST RUN");
        HeadingText.Text   = Localizer.T("dialog.welcome.heading", "Pick a sample graph to get started — or skip and start blank.");
        IntroBodyText.Text = Localizer.T("dialog.welcome.body.intro",
                                         "Sample graphs are copied into your data/logic folder. Open them in Architect to see how triggers, scripts, and the Hub bus work together. You can rename, delete, or extend them at any time.");

        EmptyStateTitle.Text = Localizer.T("dialog.welcome.empty.title", "NO SAMPLES YET");
        EmptyStateBody.Text  = Localizer.T("dialog.welcome.empty.body",
                                           "Drop .phxg / .phxlayer / .phx files into data/logic/examples/ to populate this picker. Use Skip to dismiss for now — the dialog won't open again.");

        // Discrete Ko-fi support hint (footer).
        SupportCaptionText.Text = Localizer.T("dialog.welcome.support.caption",
                                              "Free to use — supporting development is optional and always appreciated.");
    }

    private void HydrateSamples()
    {
        Samples.Clear();
        try
        {
            string useThisLabel = Localizer.T("dialog.welcome.button.use_this", "Use this");

            string examplesDir = Path.Combine(Paths.AppLogic, "examples");
            if (!Directory.Exists(examplesDir))
            {
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            // Localized empty-state text lists .phxg / .phxlayer / .phx as
            // acceptable drops — enumerate the same set or the picker
            // silently ignores files the dialog just told the user to drop.
            string[] patterns = { "*.phxg", "*.phx", "*.phxlayer" };
            foreach (var pattern in patterns)
            {
                foreach (var path in Directory.EnumerateFiles(examplesDir, pattern, SearchOption.TopDirectoryOnly))
                {
                    Samples.Add(new SampleGraphRowVm(
                        fileName: Path.GetFileName(path),
                        relativePath: $"data/logic/examples/{Path.GetFileName(path)}",
                        fullPath: path,
                        useThisLabel: useThisLabel));
                }
            }

            EmptyState.Visibility = Samples.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WelcomeDialog", "HydrateSamples failed", ex);
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private void OnUseSampleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string srcPath) return;
        try
        {
            string destDir = Paths.AppLogic;
            Directory.CreateDirectory(destDir);
            string destPath = Path.Combine(destDir, Path.GetFileName(srcPath));

            // Don't overwrite an existing file with the same name — the
            // user's customization wins. Append a numeric suffix instead.
            int suffix = 1;
            while (File.Exists(destPath))
            {
                string baseName = Path.GetFileNameWithoutExtension(srcPath);
                string ext = Path.GetExtension(srcPath);
                destPath = Path.Combine(destDir, $"{baseName} ({suffix}){ext}");
                suffix++;
            }

            File.Copy(srcPath, destPath, overwrite: false);
            SamplePicked = true;
            // Capture the FINAL destination (post-suffix) so the caller can
            // route into Architect with the freshly-copied file as the open
            // target — see App.ShowWelcomeDialogAsync.
            PickedSamplePath = destPath;
            GlobalLogger.Log($"Welcome: copied sample '{Path.GetFileName(srcPath)}' to {destPath}",
                "WelcomeDialog", LogLevel.System);
            MarkSeenAndClose();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WelcomeDialog", $"copy sample '{srcPath}' failed", ex);
            // Per feedback_no_modal_dialogs_for_repeatable_rejections.md
            // the failure surfaces in System Log — don't pop another dialog.
        }
    }

    private void OnSkipClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        MarkSeenAndClose();
    }

    private void MarkSeenAndClose()
    {
        // Hide() triggers Closing → OnDialogClosing which does
        // the async save under a deferral. Avoid double-writing here by
        // delegating to that path; setting the flag up front keeps the
        // semantics if Closing was already pending (e.g. ESC followed
        // by clicking "Use this" before the closing animation completes).
        ConfigManager.Current.SeenWelcomeDialog = true;
        // The PrimaryButton path closes the dialog itself; OnUseSampleClick
        // runs from a non-dialog button so we Hide() it explicitly. Hide()
        // raises Closing, which fires OnDialogClosing's async save.
        Hide();
    }
}

// Plain class (not a record) — WinUI 3's compiled-XAML data templates
// can't bind through positional record properties (init-only setters
// trip the XamlTypeInfo generator with CS8852).
public sealed class SampleGraphRowVm
{
    public SampleGraphRowVm(string fileName, string relativePath, string fullPath, string useThisLabel)
    {
        FileName = fileName;
        RelativePath = relativePath;
        FullPath = fullPath;
        UseThisLabel = useThisLabel;
    }

    public string FileName { get; }
    public string RelativePath { get; }
    public string FullPath { get; }

    /// <summary>Localized "Use this" caption for the row's primary button.
    /// Resolved by the dialog at hydrate time so each row carries the same
    /// translated label without binding to <see cref="Localizer"/> from XAML.</summary>
    public string UseThisLabel { get; }
}
