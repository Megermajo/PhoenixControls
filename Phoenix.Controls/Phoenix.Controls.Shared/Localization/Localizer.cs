using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Localization
{
    /// <summary>
    /// Localizer — UI string lookup service. Each exe (Hub, Architect, Visualist)
    /// calls <see cref="Init"/> once during startup before any form is constructed,
    /// then UI code reads <see cref="T"/> for every user-visible string.
    ///
    /// Naming note: distinct from <c>Phoenix.Controls.Hub.Core.Translation</c>,
    /// which translates user-generated *content* (live captions/chat) between
    /// broadcast languages via plug-in providers. Localizer translates the
    /// editor's own *UI surface* into the user's chosen language.
    ///
    /// Lookup chain for <see cref="T"/>:
    /// <list type="number">
    ///   <item>current bundle (e.g. <c>de.json</c>)</item>
    ///   <item>fallback bundle (<see cref="FallbackLanguage"/> = en)</item>
    ///   <item>caller-supplied <c>fallback</c> argument, or <c>[key]</c> as a last resort</item>
    /// </list>
    /// A miss in the current bundle but hit in the fallback is logged once per key
    /// at <see cref="LogLevel.Debug"/> so stale translations surface during
    /// development.
    ///
    /// Live language switching is intentionally not supported in v1: the user picks
    /// a language and restarts the suite. A <c>LanguageChanged</c> event will be added
    /// alongside the future live-switch milestone — declaring it speculatively now
    /// triggers an unused-event warning, so it lands when the first subscriber does.
    /// </summary>
    public static class Localizer
    {
        /// <summary>The canonical/source language. Always treated as the last bundle
        /// before falling through to <c>[key]</c>.</summary>
        public const string FallbackLanguage = "en";

        private static readonly object _lock = new();
        private static LanguageBundle? _current;
        private static LanguageBundle? _fallback;
        private static string _currentLanguage = FallbackLanguage;
        private static IReadOnlyList<string> _available = Array.Empty<string>();
        private static readonly HashSet<string> _missingKeysLogged = new();

        /// <summary>The language code currently in use (e.g. "en", "de").</summary>
        public static string Current
        {
            get { lock (_lock) return _currentLanguage; }
        }

        /// <summary>
        /// Language codes for which a bundle was found at startup, sorted. Includes
        /// <see cref="FallbackLanguage"/> when its bundle exists. Empty when the
        /// <c>lang/</c> folder is missing.
        /// </summary>
        public static IReadOnlyList<string> Available
        {
            get { lock (_lock) return _available; }
        }

        /// <summary>
        /// Initializes the service from <c>{baseDir}/lang/*.json</c>. Each consumer
        /// passes <see cref="AppDomain.CurrentDomain.BaseDirectory"/> from its
        /// <c>Program.cs</c> before constructing any form. Safe to call multiple
        /// times — later calls reload bundles from disk and clear the missing-key log.
        /// </summary>
        public static void Init(string baseDir)
        {
            try
            {
                string langDir = Path.Combine(baseDir, "lang");
                List<string> languages = new();
                if (Directory.Exists(langDir))
                {
                    foreach (var file in Directory.EnumerateFiles(langDir, "*.json"))
                    {
                        languages.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
                else
                {
                    GlobalLogger.Log(
                        $"Localizer: language folder missing at '{langDir}' — UI strings will render as [key] placeholders.",
                        "Localizer", LogLevel.CriticalError);
                }
                languages.Sort(StringComparer.OrdinalIgnoreCase);

                string desired = LanguageConfig.Load();
                bool desiredAvailable = languages.Contains(desired, StringComparer.OrdinalIgnoreCase);
                string resolvedLang = desiredAvailable ? desired : FallbackLanguage;

                if (!desiredAvailable && !string.Equals(desired, FallbackLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    GlobalLogger.Log(
                        $"Localizer: requested language '{desired}' has no bundle in '{langDir}'; falling back to '{FallbackLanguage}'. " +
                        "Preference left unchanged so a later release that ships the bundle picks it up.",
                        "Localizer", LogLevel.System);
                }

                LanguageBundle? fallback = LoadBundle(langDir, FallbackLanguage);
                LanguageBundle? current = string.Equals(resolvedLang, FallbackLanguage, StringComparison.OrdinalIgnoreCase)
                    ? fallback
                    : LoadBundle(langDir, resolvedLang);

                lock (_lock)
                {
                    _available = languages;
                    _currentLanguage = resolvedLang;
                    _fallback = fallback;
                    _current = current;
                    _missingKeysLogged.Clear();
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"Localizer.Init failed: {ex.Message} — UI strings will render as [key] placeholders.",
                    "Localizer", LogLevel.CriticalError);
            }
        }

        /// <summary>
        /// Looks up <paramref name="key"/> through the current → fallback → literal chain.
        /// Pass <paramref name="fallback"/> to override the final <c>[key]</c> placeholder
        /// — useful for runtime strings that should degrade to a sensible default
        /// rather than a developer-facing bracketed key.
        /// </summary>
        public static string T(string key, string? fallback = null)
        {
            if (string.IsNullOrEmpty(key)) return fallback ?? string.Empty;

            LanguageBundle? cur, fb;
            lock (_lock)
            {
                cur = _current;
                fb = _fallback;
            }

            if (cur != null && cur.TryGet(key, out var v)) return v;
            if (fb != null && fb.TryGet(key, out var fv))
            {
                ReportStaleTranslation(key);
                return fv;
            }
            return fallback ?? $"[{key}]";
        }

        /// <summary>
        /// Logs, once per key per session, that the active bundle had no entry
        /// and the English fallback was used.
        ///
        /// <para>The class summary has promised this since the bundles shipped
        /// and it never actually happened: <c>_missingKeysLogged</c> was
        /// declared and cleared on <see cref="Init"/> but nothing ever added to
        /// it, so a de/fr/es gap produced English text with no exception, no log
        /// line, and no failing test — invisible from every direction at once.
        /// The build-time guards (<c>LocalizationBundleParityTests</c>,
        /// <c>LocalizationKeyCoverageTests</c>) are still the real net; this is
        /// the runtime signal for the case they cannot see, a translator's
        /// hand-edited bundle on a user's disk.</para>
        ///
        /// <para>Deliberately outside <c>_lock</c> for the log call itself — a
        /// <see cref="GlobalLogger"/> handler is free to call back into
        /// <see cref="T"/> (chrome re-rendering a status line does exactly
        /// that), and holding the bundle lock across that re-entry would
        /// deadlock.</para>
        /// </summary>
        private static void ReportStaleTranslation(string key)
        {
            string language;
            lock (_lock)
            {
                if (string.Equals(_currentLanguage, FallbackLanguage, StringComparison.OrdinalIgnoreCase)) return;
                if (!_missingKeysLogged.Add(key)) return;
                language = _currentLanguage;
            }

            // Line shape is the one lang/README.md has documented since the
            // bundles shipped — it is what the README tells people to filter the
            // System Log for, so it is a contract, not a free-form message.
            GlobalLogger.Log(
                $"Localizer: missing key '{key}' in '{language}' — using '{FallbackLanguage}' fallback",
                "Localizer", LogLevel.Debug);
        }

        private static LanguageBundle? LoadBundle(string langDir, string language)
        {
            string path = Path.Combine(langDir, language + ".json");
            return LanguageBundle.LoadFromFile(path, language);
        }
    }
}
