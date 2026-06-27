using System;
using System.IO;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Localization
{
    /// <summary>
    /// LanguageConfig — read/write the user's selected UI language at
    /// <c>%AppData%/PhoenixControls/language.json</c>. Lives outside the
    /// per-app <c>AppConfig</c> on purpose: Hub, Architect, and Visualist
    /// run as separate processes and all need to read the same preference at
    /// startup. <see cref="DB"/> uses the same <c>%AppData%/PhoenixControls/</c>
    /// folder for the SQLite databank, so the convention is established.
    ///
    /// Schema is intentionally trivial — a single string field — so corruption
    /// recovery is simpler than <see cref="ConfigManager"/> (no rotation): we
    /// log, default to <see cref="Localizer.FallbackLanguage"/>, and let the
    /// next <see cref="Save"/> overwrite the bad file.
    /// </summary>
    public static class LanguageConfig
    {
        private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

        /// <summary>
        /// Absolute path to <c>%AppData%/PhoenixControls/language.json</c>.
        /// Resolved once at type-init; safe to read from any process.
        /// </summary>
        public static string ConfigPath { get; } = Paths.RoamingAppData("language.json");

        /// <summary>
        /// Returns the persisted language code, or <see cref="Localizer.FallbackLanguage"/>
        /// when the file is missing, malformed, or empty. When the file is missing on first
        /// run, writes the default so subsequent reads have a stable file to update.
        /// </summary>
        public static string Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Save(Localizer.FallbackLanguage);
                    return Localizer.FallbackLanguage;
                }

                string json = File.ReadAllText(ConfigPath);
                var doc = JsonSerializer.Deserialize<LanguageRecord>(json);
                return string.IsNullOrWhiteSpace(doc?.Language)
                    ? Localizer.FallbackLanguage
                    : doc!.Language!;
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"LanguageConfig: failed to load '{ConfigPath}': {ex.Message} — defaulting to '{Localizer.FallbackLanguage}'",
                    "LanguageConfig", LogLevel.CriticalError);
                return Localizer.FallbackLanguage;
            }
        }

        /// <summary>
        /// Persists the chosen language code. Creates the parent folder when needed.
        /// Failures are logged but never thrown — a write error must not crash the
        /// language picker dialog.
        /// </summary>
        public static void Save(string language)
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(
                    ConfigPath,
                    JsonSerializer.Serialize(new LanguageRecord { Language = language }, _writeOpts));
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"LanguageConfig: failed to save '{ConfigPath}': {ex.Message}",
                    "LanguageConfig", LogLevel.CriticalError);
            }
        }

        private sealed class LanguageRecord
        {
            public string? Language { get; set; }
        }
    }
}
