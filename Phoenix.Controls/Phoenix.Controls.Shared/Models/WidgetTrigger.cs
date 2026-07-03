using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Models
{
    /// <summary>
    /// `Name` is validated. A widget trigger name is
    /// either the literal "onStartup" or the form "onTrigger:&lt;identifier&gt;"
    /// where the identifier starts with a letter and is followed by letters,
    /// digits, or underscores. Anything else would round-trip through the
    /// plain-text clipboard channel emitted by
    /// VisualTriggerSnippetProducer.CopyToClipboard ("visual.trigger(...)")
    /// and could land on disk as part of a .phx export filename or a runtime
    /// dispatch key — so we treat the property as a constrained identifier
    /// rather than a free-form string.
    ///
    /// Setter discipline:
    ///   * The default <see cref="Name"/> setter performs validation and
    ///     silently no-ops + logs invalid input. The
    ///     existing literal sets in the codebase ("onStartup",
    ///     "onTrigger:greet", etc.) keep working unchanged.
    ///   * <see cref="SetForLoad(string)"/> bypasses validation. The JSON
    ///     deserializer routes through it via the private NameJson shadow
    ///     property below, so old .phxlayer files with technically-invalid
    ///     names still load — <see cref="LayerSerializer.Deserialize"/>
    ///     then sweeps the document and rewrites bad names to
    ///     <c>trigger_&lt;hash&gt;</c> with a Communication-tier log.
    /// </summary>
    public sealed class WidgetTrigger
    {
        // ^(onStartup|onTrigger:<identifier>)$ — matches the two shapes the
        // codebase already uses (see Phoenix.Controls.Hub/data/layers/*.phxlayer
        // and the project docs's "Layer / widget model" section).
        // The `trigger_<8hex>` alternative accepts
        // MakeFallbackName's deterministic SHA-fallback as a first-class valid
        // name. Before this, LayerSerializer's load-time migration (line ~155)
        // rewrote an unsalvageable name to MakeFallbackName(raw) — a bare
        // `trigger_<hash>` — and then stored it via SetForLoad, but that result
        // STILL failed IsValidName, so the "every surviving name satisfies the
        // validator" invariant the migration claims was violated. MakeFallbackName
        // stays the bare-identifier producer (Visualist callers prepend
        // `onTrigger:` themselves; the validator asserts the hex form), so the
        // reconciliation belongs here: bless the exact fallback shape.
        private static readonly Regex _validName = new(
            @"^(onStartup|onTrigger:[A-Za-z][A-Za-z0-9_]*|trigger_[0-9a-f]{8})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private string _name = "onStartup";

        /// <summary>
        /// Trigger name. Setting an invalid value is silently dropped and
        /// logged at System tier — the property keeps its previous value.
        /// Use <see cref="SetForLoad(string)"/> from deserialization /
        /// migration paths where the raw on-disk value must be preserved.
        /// </summary>
        [JsonIgnore]
        public string Name
        {
            get => _name;
            set
            {
                if (string.Equals(value, _name, StringComparison.Ordinal))
                {
                    return; // no-op on identity set; avoids spurious log churn
                }

                if (IsValidName(value))
                {
                    _name = value;
                    return;
                }

                // Invalid input is silently dropped — the property keeps its
                // previous value. We do NOT throw, because callers may be
                // data-bound setters (Visualist trigger-rename textbox) that
                // fire on every keystroke; throwing would crash the UI.
            }
        }

        /// <summary>
        /// JSON-bound shadow setter. <see cref="System.Text.Json"/> writes
        /// through this private accessor so deserialization preserves the
        /// raw on-disk name even when it's invalid by the new rules. The
        /// load-time migration in <see cref="LayerSerializer.Deserialize"/>
        /// then rewrites unsalvageable names to <c>trigger_&lt;hash&gt;</c>.
        /// </summary>
        [JsonInclude]
        [JsonPropertyName("name")]
        private string NameJson
        {
            get => _name;
            set => SetForLoad(value ?? "");
        }

        [JsonPropertyName("graph")]    public Graph          Graph    { get; set; } = new();
        [JsonPropertyName("timeline")] public WidgetTimeline Timeline { get; set; } = new();

        /// <summary>
        /// Per-trigger MASTER volume (0..1) that scales ALL audio in this
        /// trigger at runtime (compositor.js multiplies each Audio.Play node's
        /// own Volume attribute by this value). Kept a plain double — clamping
        /// is applied at the use-sites. Older .phxlayer files lacking the
        /// "volume" key default to 1.0 (no audio change).
        /// </summary>
        [JsonPropertyName("volume")]   public double         Volume   { get; set; } = 1.0;

        /// <summary>
        /// True iff <paramref name="candidate"/> matches the trigger-name shape.
        /// </summary>
        public static bool IsValidName(string? candidate)
        {
            return !string.IsNullOrEmpty(candidate) && _validName.IsMatch(candidate);
        }

        /// <summary>
        /// Bypass-validation setter for the load path. Call from JSON
        /// converters or migration code; do not call from UI handlers.
        /// </summary>
        public void SetForLoad(string raw)
        {
            _name = raw ?? "";
        }

        /// <summary>
        /// Compute a deterministic fallback name (<c>trigger_&lt;hash&gt;</c>)
        /// for a trigger whose persisted name failed validation. The hash is
        /// the first eight hex chars of SHA-256(raw) — stable across loads
        /// of the same file so a renamed trigger keeps its identity.
        /// </summary>
        public static string MakeFallbackName(string raw)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(raw ?? "");
            byte[] hash  = SHA256.HashData(bytes);
            var sb = new StringBuilder("trigger_", 16);
            for (int i = 0; i < 4; i++)
            {
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
