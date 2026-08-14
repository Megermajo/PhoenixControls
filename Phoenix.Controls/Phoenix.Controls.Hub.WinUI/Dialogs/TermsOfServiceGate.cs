using System;
using System.IO;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

/// <summary>
/// The Terms-of-Service consent gate. Decides — before ANY Hub service boots —
/// whether the current terms have been accepted, and if not, blocks on the
/// <see cref="TermsOfServiceWindow"/> until the user accepts or declines.
///
/// <para><b>Where it runs.</b> <see cref="App.OnLaunchedCore"/> calls
/// <see cref="EnsureAcceptedAsync"/> after the pre-UI phase (DB / config ready)
/// but BEFORE <c>HubBootstrapper.BootAsync</c>. That ordering is what makes the
/// gate genuinely fail-closed: while the pop-up is open there is no MainWindow
/// and no running service — the Streamer.bot connection, the HUD server, the
/// script engine and every scheduler are all still un-started — so the app is
/// non-functional exactly as required. A decline exits the process before any of
/// them ever start.</para>
///
/// <para><b>Versioning ("after a change").</b> The user's accepted version is
/// persisted in <see cref="Phoenix.Controls.Shared.Models.AppConfig.AcceptedTosVersion"/>.
/// The gate re-appears whenever <see cref="CurrentVersion"/> exceeds the stored
/// value — i.e. on a fresh install (stored 0) OR whenever the terms are revised
/// and this constant is bumped. Bumping <see cref="CurrentVersion"/> is the one
/// switch that re-prompts every existing user for the new terms.</para>
/// </summary>
public static class TermsOfServiceGate
{
    /// <summary>
    /// The current Terms-of-Service revision. Bump this by 1 whenever the wording
    /// in <c>Docs/terms-of-service.html</c> materially changes — that forces the
    /// gate to re-prompt every existing user (whose stored
    /// <c>AcceptedTosVersion</c> is now lower) on their next launch. Start at 1
    /// so a clean install (stored default 0) always sees the terms once.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Ensure the current terms are accepted. Returns <c>true</c> when the app may
    /// proceed to boot (already accepted this version, or the user just accepted),
    /// <c>false</c> when the caller must exit the process (declined / closed, or —
    /// fail-closed — the gate could not be shown at all).
    /// </summary>
    /// <remarks>Must be called on the UI thread — it constructs a Window.</remarks>
    public static async Task<bool> EnsureAcceptedAsync()
    {
        try
        {
            // Load config now so AcceptedTosVersion reflects disk. Config is
            // otherwise first loaded inside BootAsync (WS.LoadSettings), which
            // runs AFTER this gate — so we load it explicitly here. BootAsync's
            // later reload re-reads the same file, which by then carries the
            // version we persist on accept below, so the two stay consistent.
            ConfigManager.Load(Paths.AppConfigJson);

            int accepted = ConfigManager.Current.AcceptedTosVersion;
            if (accepted >= CurrentVersion)
            {
                // Already agreed to these (or newer) terms — no gate.
                return true;
            }

            GlobalLogger.Log(
                $"Terms of Service gate: stored acceptance v{accepted} < current v{CurrentVersion} — showing consent pop-up before boot.",
                "TermsOfServiceGate", LogLevel.System);

            string docsDir = ResolveDocsDir();
            var window = new TermsOfServiceWindow(
                Localizer.T("dialog.tos.window.title", "Phoenix Controls — Terms of Service"),
                docsDir,
                EssentialsFallback);

            bool userAccepted = await window.Completion.ConfigureAwait(true);

            if (!userAccepted)
            {
                GlobalLogger.Log(
                    "Terms of Service declined — Hub will not start.",
                    "TermsOfServiceGate", LogLevel.System);
                return false;
            }

            // Persist the acceptance synchronously so it is durable BEFORE the
            // Hub boots (a crash mid-boot must not re-prompt) and before
            // BootAsync's config reload reads it back.
            ConfigManager.Current.AcceptedTosVersion = CurrentVersion;
            try
            {
                ConfigManager.Save(Paths.AppConfigJson);
            }
            catch (Exception ex)
            {
                // A persist failure is non-fatal to THIS session (the user did
                // accept) — they'll just be re-prompted next launch. Log loudly.
                GlobalLogger.Error("TermsOfServiceGate", "persist AcceptedTosVersion failed", ex);
            }

            GlobalLogger.Log(
                $"Terms of Service accepted (v{CurrentVersion}). Proceeding to boot.",
                "TermsOfServiceGate", LogLevel.System);
            return true;
        }
        catch (Exception ex)
        {
            // Fail-closed: if we cannot obtain a real acceptance we must NOT let
            // the app become functional. Exit rather than boot on an unconfirmed
            // consent.
            GlobalLogger.Error("TermsOfServiceGate",
                "EnsureAcceptedAsync failed — refusing to boot without confirmed consent", ex);
            return false;
        }
    }

    private static string ResolveDocsDir()
        => Path.Combine(AppContext.BaseDirectory, "Docs");

    // Last-ditch plain-text terms shown only if BOTH the HTML page AND the
    // bundled Docs/terms-of-service.txt can't be read. Deliberately terse — the
    // full, authoritative text lives in the bundled document; this only
    // guarantees the user is never asked to accept a literally-blank screen.
    //
    // One key per paragraph rather than one key for the whole block: the
    // \r\n structure stays in code, so a translator never has to reproduce
    // escape sequences inside a legal string, and each clause is translatable
    // on its own. A property (not a const) because Localizer.T is a call —
    // it resolves per read, which is what a language switch needs.
    private static string EssentialsFallback =>
        Localizer.T("dialog.tos.fallback.title",
            "PHOENIX CONTROLS — TERMS OF SERVICE (summary)") + "\r\n\r\n" +
        Localizer.T("dialog.tos.fallback.intro",
            "The full terms could not be displayed. In short:") + "\r\n\r\n" +
        Localizer.T("dialog.tos.fallback.bullet_free",
            "• Phoenix Controls is provided free of charge and always will be — no fees, subscriptions or hidden costs of any kind.") + "\r\n" +
        Localizer.T("dialog.tos.fallback.bullet_warranty",
            "• It is provided \"as is\", without warranty. To the fullest extent permitted by law, Megermajo Productions is not liable for any damage or loss arising from downloading, installing or using it. Nothing here excludes liability that cannot be excluded by law (intent, gross negligence, or injury to life, body or health).") + "\r\n" +
        Localizer.T("dialog.tos.fallback.bullet_privacy",
            "• No personal data is collected. The software sends no telemetry to Megermajo Productions; everything it creates stays on your own computer. Any third-party services you connect (Streamer.bot, Twitch/YouTube/Kick, AI providers, webhooks) are governed by those parties' own terms.") + "\r\n" +
        Localizer.T("dialog.tos.fallback.bullet_accept",
            "• Clicking \"I Accept\" forms a binding agreement to the full terms. If you do not agree, decline and the app will close.") + "\r\n\r\n" +
        Localizer.T("dialog.tos.fallback.outro",
            "Please relaunch to view the complete document.");
}
