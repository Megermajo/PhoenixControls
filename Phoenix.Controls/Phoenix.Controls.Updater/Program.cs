using System;
using System.IO;
using System.Threading.Tasks;

namespace Phoenix.Controls.Updater;

/// <summary>
/// Entry point. Parses the CLI, hands off to <see cref="UpdateRunner"/>,
/// returns 0 on success, 1 on rollback, 2 on hard-failure (no rebuild).
/// All real work — including logging — lives in <see cref="UpdateRunner"/>.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!UpdaterArgs.TryParse(args, out UpdaterArgs parsed, out string error))
        {
            // BH — round-2 audit: parse-fail used to write only to stderr.
            // Hub had already exited by the time the Updater console flashed
            // and disappeared — user saw the suite vanish with nothing in
            // updater.log, nothing in last-update-result.json, no
            // System-log entry. Now: surface the parse error to disk so
            // Hub's next launch reads last-update-result.json and pops a
            // dialog via the existing "Failed" surface.
            Console.Error.WriteLine($"Phoenix.Controls.Updater: {error}");
            Console.Error.WriteLine(
                "Usage:\n" +
                "  Update mode (preferred):\n" +
                "    Phoenix.Controls.Updater --update <archivePath> [--target <suiteRoot>]\n" +
                "                              [--archive-sha256 <hex>] [--release-tag <tag>]\n" +
                "                              [--no-relaunch]\n" +
                "  Releases mode (legacy URL-based):\n" +
                "    Phoenix.Controls.Updater --install-root <path> --hub-pid <pid>\n" +
                "                              --asset-url <url> --asset-sha256 <hex>\n" +
                "                              [--release-tag <tag>] [--launch-script <path>]\n" +
                "                              [--no-relaunch]");
            EmergencyWriteParseFailure(error, string.Join(" ", args));
            return 2;
        }

        var runner = new UpdateRunner(parsed);
        UpdateOutcome outcome = await runner.RunAsync();
        return outcome switch
        {
            UpdateOutcome.Success    => 0,
            UpdateOutcome.RolledBack => 1,
            _                        => 2,
        };
    }

    /// <summary>
    /// Best-effort disk surface for a CLI parse failure. UpdateRunner hasn't
    /// been constructed yet (we don't have a valid args object), so this
    /// hand-writes a minimal updater.log line + last-update-result.json so
    /// the next Hub launch can surface the failure via the existing
    /// SurfacePreviousUpdateResult path. Schema must match what
    /// UpdateChecker.ReadAndClearLastUpdateResult expects.
    /// </summary>
    private static void EmergencyWriteParseFailure(string parseError, string rawArgs)
    {
        try
        {
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                UpdateRunner.BrandFolder, "Hub");
            try { Directory.CreateDirectory(stateDir); }
            catch { stateDir = Path.Combine(Path.GetTempPath(), UpdateRunner.BrandFolder + ".Updater"); Directory.CreateDirectory(stateDir); }

            string logPath    = Path.Combine(stateDir, "updater.log");
            string resultPath = Path.Combine(stateDir, "last-update-result.json");

            try
            {
                File.AppendAllText(logPath,
                    $"=== run @ {DateTime.UtcNow:O} ===\n" +
                    $"[CLI parse failed] {parseError}\n" +
                    $"[args] {rawArgs}\n\n");
            }
            catch { }

            try
            {
                string json =
                    "{\n" +
                    "  \"outcome\": \"Failed\",\n" +
                    "  \"oldSha\": null,\n" +
                    "  \"newSha\": null,\n" +
                    $"  \"buildLogPath\": null,\n" +
                    $"  \"errorMessage\": {System.Text.Json.JsonSerializer.Serialize($"Updater CLI parse failed: {parseError}")},\n" +
                    $"  \"timestamp\": \"{DateTime.UtcNow:O}\"\n" +
                    "}";
                string tmp = resultPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, resultPath, overwrite: true);
            }
            catch { }
        }
        catch { /* never throw out of emergency-handling path */ }
    }
}
