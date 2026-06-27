using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Localization
{
    /// <summary>
    /// LanguageBundle — an immutable in-memory snapshot of one language's UI strings.
    /// Loaded from <c>{exeBin}/lang/{language}.json</c> at startup by <see cref="Localizer"/>.
    /// The wrapping type exists so the lookup path has a stable contract independent of
    /// the underlying dictionary representation, and so future bundle features (plural forms,
    /// per-key metadata) can land without rewriting every callsite.
    /// </summary>
    internal sealed class LanguageBundle
    {
        /// <summary>Language code this bundle represents (filename without extension).</summary>
        public string Language { get; }

        private readonly IReadOnlyDictionary<string, string> _entries;

        private LanguageBundle(string language, IReadOnlyDictionary<string, string> entries)
        {
            Language = language;
            _entries = entries;
        }

        /// <summary>Number of localized keys in this bundle.</summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Looks up <paramref name="key"/> in this bundle. Returns <c>true</c> and the value
        /// when present; <c>false</c> and an empty string otherwise. Case-sensitive.
        /// </summary>
        public bool TryGet(string key, out string value)
        {
            if (_entries.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }
            value = string.Empty;
            return false;
        }

        /// <summary>
        /// Loads a bundle from disk. Returns <c>null</c> on any failure (missing file,
        /// malformed JSON, IO error) — failures are logged via <see cref="GlobalLogger"/>
        /// at <see cref="LogLevel.CriticalError"/>. Callers treat <c>null</c> as
        /// "no bundle available" and proceed through their fallback chain.
        /// </summary>
        public static LanguageBundle? LoadFromFile(string path, string language)
        {
            try
            {
                if (!File.Exists(path)) return null;
                // File.ReadAllText with no encoding defaults to the system ANSI
                // code page on Windows (1252 on en-US machines). The bundles are
                // UTF-8 — without this every non-ASCII char (·, …, é, etc.) was
                // being decoded as mojibake (`·` → `Â·`, `…` → `â€¦`).
                string json = File.ReadAllText(path, Encoding.UTF8);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict == null) return null;
                return new LanguageBundle(language, dict);
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"Localizer: failed to load bundle '{path}' ({language}): {ex.Message}",
                    "Localizer", LogLevel.CriticalError);
                return null;
            }
        }
    }
}
