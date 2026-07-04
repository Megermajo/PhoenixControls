using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

/// <summary>
/// Shared tail of every Hub-initiated update: spawn the Updater against the
/// suite root, surface the in-app progress dialog, then exit Hub so the file
/// swap can proceed. Both entry points — Settings → "Force download" and the
/// startup update prompt — must run through this single path so an apply-side
/// fix always lands in every flow at once (the T15 suite-root bugs had to be
/// patched per-call-site because this tail was duplicated).
/// </summary>
internal static class UpdateApplyFlow
{
    /// <summary>
    /// Returns false when the Updater could not be spawned (logged as
    /// CriticalError; Hub stays up). On success Hub is already exiting when
    /// this returns — callers must not touch UI afterwards.
    ///
    /// <paramref name="progressRoot"/> must not have another ContentDialog
    /// open on it — WinUI 3 allows one per XamlRoot, so callers dismiss
    /// their own dialog first.
    /// </summary>
    public static bool BeginApplyWithProgress(
        UpdateStatus.ReleaseAvailable release, XamlRoot? progressRoot, string logSource)
    {
        // The Updater needs the SUITE ROOT ({app}\, parent of Hub\ /
        // Updater\), not the Hub exe's own folder. AppContext.BaseDirectory
        // is {app}\Hub\ in the installer layout, so use the shared resolver
        // to climb to the suite root.
        string installRoot = UpdateChecker.ResolveSuiteRoot();
        bool spawned = UpdateChecker.BeginApply(release, installRoot);
        if (!spawned)
        {
            GlobalLogger.Log(
                "Could not spawn Phoenix.Controls.Updater.exe — see SystemLog for details.",
                logSource, LogLevel.CriticalError);
            return false;
        }

        // BeginApply staged the sentinel + cleared any leftover progress
        // file. Open the progress dialog AFTER spawning the Updater so
        // the dialog's first poll has a real progress entry to read.
        if (progressRoot is not null)
        {
            var progressDialog = new UpdaterProgressDialog
            {
                XamlRoot = progressRoot,
            };
            // Fire-and-forget ShowAsync — Hub is about to exit; we just
            // need the dialog visible while the Updater downloads.
            _ = progressDialog.ShowAsync();
        }

        // The Updater waits on the sentinel PID before mutating files, so
        // the dialog gets ~30s of polling visibility before Hub goes down.
        Application.Current.Exit();
        return true;
    }
}
