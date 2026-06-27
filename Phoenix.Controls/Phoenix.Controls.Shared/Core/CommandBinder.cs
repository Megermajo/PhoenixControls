using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// R19 — typed argument schema for Script command registrations.
    ///
    /// Background: every <c>RegisterCommand</c> handler in <c>ScriptManager.cs</c> historically
    /// took a raw <c>string[] args</c> and parsed each entry by hand (<c>int.TryParse</c>,
    /// <c>bool.TryParse</c>, JSON deserialize, etc.). That meant:
    ///   * No central record of which command takes what types — schema lived inline in handlers.
    ///   * Same coercion code copy-pasted ~80 times with subtle drift (some used InvariantCulture,
    ///     some Ordinal, some allowed empty-string-as-zero, some didn't).
    ///   * No way for the manifest test (<c>CommandManifestTests</c>) to detect type drift between
    ///     the manifest declaration and the handler's actual expectations.
    ///
    /// The binder lives next to <see cref="CommandManifest"/> and provides:
    ///   * <see cref="ArgType"/> — the type taxonomy of script-side argument shapes.
    ///   * <see cref="ArgSpec"/> — name + type + optionality + default, replacing the
    ///     previous bare <c>string</c> arg-names list.
    ///   * <see cref="CommandBinder"/> — pure static binder that turns <c>(spec, raw string[])</c>
    ///     into a typed <c>BoundArgs</c> dict.
    ///
    /// Sweep 13 ships the binder + the schema types alongside the existing string-only registration
    /// path; the engine dispatch site is unchanged and handlers continue to receive
    /// <c>string[]</c>. Migration of individual handlers to opt into the typed dict is incremental
    /// (sweep 14+) — handlers can call <c>CommandBinder.BindArgs(spec, rawArgs)</c> themselves to
    /// pull the typed values, with <c>string[]</c> still available as the fallback.
    /// </summary>
    public enum ArgType
    {
        /// <summary>Raw <see cref="string"/> — the legacy default. No coercion, never null.</summary>
        String,

        /// <summary>32-bit signed integer parsed via <see cref="int.TryParse(string?, NumberStyles, IFormatProvider?, out int)"/> with <see cref="CultureInfo.InvariantCulture"/>.</summary>
        Int,

        /// <summary>Single-precision float, InvariantCulture, allows decimal point only (no comma).</summary>
        Float,

        /// <summary>
        /// Double-precision float (sweep 16). InvariantCulture; same parse style as <see cref="Float"/>
        /// but stores 64-bit precision. Use for math.* handlers where the legacy code path uses
        /// <c>double.TryParse</c> and arithmetic stability matters; <see cref="Float"/> is for
        /// convenience args (percentages, speeds) where 32-bit suffices.
        /// </summary>
        Double,

        /// <summary>Boolean — accepts <c>true</c>/<c>false</c>/<c>1</c>/<c>0</c>/<c>yes</c>/<c>no</c> (case-insensitive).</summary>
        Bool,

        /// <summary>JSON value parsed into a <see cref="JsonElement"/> (object/array/scalar). Empty input yields an undefined element.</summary>
        Json,

        /// <summary>Pipe-string list (`a|b|c`) parsed into <see cref="IReadOnlyList{T}"/> of trimmed strings; empty input yields an empty list.</summary>
        List,

        /// <summary>
        /// Key-value pair dictionary parsed into <see cref="IReadOnlyDictionary{TKey,TValue}"/> of <c>string→string</c>.
        /// Two ingest shapes:
        ///   * Single arg of pipe-separated pairs: <c>"k1=v1|k2=v2"</c>
        ///   * Variadic: each remaining raw arg is one <c>"k=v"</c> entry (sweep 16, paired with <see cref="ArgSpec.Variadic"/>).
        /// Both produce the same dict shape. Keys are case-insensitive (mirrors the legacy
        /// hand-rolled <c>Dictionary&lt;string,string&gt;(StringComparer.OrdinalIgnoreCase)</c> in
        /// <c>visual.trigger_queued</c> / <c>wait_for_visual</c> / <c>event.trigger</c>). On a
        /// duplicate key, last-wins. Entries without an <c>=</c> are silently skipped — same
        /// tolerance as the prior in-handler loops.
        /// </summary>
        KvPairs,
    }

    /// <summary>
    /// One positional argument's contract. <see cref="Optional"/> arguments can be omitted by the
    /// script — when missing, <see cref="Default"/> (or the type's natural default) is bound.
    ///
    /// <see cref="Variadic"/> is a sweep-14 addition: when set on the LAST <see cref="ArgSpec"/>
    /// of a command, the binder collects every remaining raw arg into an
    /// <see cref="IReadOnlyList{T}"/> of strings under that spec's name. Supports the
    /// <c>text.format(template, A, B, …, Z)</c> / <c>visual.trigger_queued(layer, widget,
    /// trigger, …key=val)</c> shape without forcing the manifest to enumerate every slot.
    /// Variadic specs are mutually exclusive with <see cref="Type"/> = anything other than
    /// <see cref="ArgType.String"/> or <see cref="ArgType.List"/>; the binder ignores
    /// <see cref="Type"/> when <see cref="Variadic"/> is true and always produces an
    /// <see cref="IReadOnlyList{T}"/> of trimmed strings.
    /// </summary>
    public sealed record ArgSpec(
        string Name,
        ArgType Type,
        bool Optional = false,
        string? Default = null,
        bool Variadic = false);

    /// <summary>Result of a successful bind. Read values via <see cref="Get{T}"/>; raw string still available via <see cref="Raw"/>.</summary>
    public sealed class BoundArgs
    {
        private readonly Dictionary<string, object?> _byName;

        /// <summary>Original positional string array passed by the engine. Preserved for handlers that haven't migrated yet.</summary>
        public IReadOnlyList<string> Raw { get; }

        public BoundArgs(IReadOnlyList<string> raw, Dictionary<string, object?> byName)
        {
            Raw     = raw;
            _byName = byName;
        }

        /// <summary>Strongly-typed accessor. Throws <see cref="KeyNotFoundException"/> if the argument isn't in the spec.</summary>
        public T Get<T>(string name)
        {
            if (!_byName.TryGetValue(name, out var v))
                throw new KeyNotFoundException($"BoundArgs: no argument named '{name}'.");
            return v is T t ? t : (T)Convert.ChangeType(v!, typeof(T), CultureInfo.InvariantCulture);
        }

        /// <summary>Non-throwing accessor. Returns <paramref name="fallback"/> when missing or type-mismatched.</summary>
        public T GetOrDefault<T>(string name, T fallback)
        {
            if (!_byName.TryGetValue(name, out var v) || v is null) return fallback;
            try { return v is T t ? t : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public bool ContainsKey(string name) => _byName.ContainsKey(name);
    }

    /// <summary>
    /// Pure static binder. Given a <see cref="CommandSpec"/> with typed <c>ArgSpecs</c> and a raw
    /// positional <c>string[]</c>, produces a <see cref="BoundArgs"/> dict. On any coercion
    /// failure the binder substitutes the type's natural default and records the failure in the
    /// optional <paramref name="errors"/> list — handlers stay reachable; the engine logs the
    /// failure but doesn't tear down the script.
    /// </summary>
    public static class CommandBinder
    {
        /// <summary>
        /// Binds <paramref name="rawArgs"/> against <paramref name="argSpecs"/>. Returns a
        /// <see cref="BoundArgs"/> populated with one entry per spec. <paramref name="errors"/>
        /// (when supplied) receives a human-readable line per coercion failure.
        /// </summary>
        public static BoundArgs BindArgs(
            IReadOnlyList<ArgSpec> argSpecs,
            IReadOnlyList<string> rawArgs,
            List<string>? errors = null)
        {
            if (argSpecs is null) throw new ArgumentNullException(nameof(argSpecs));
            if (rawArgs  is null) throw new ArgumentNullException(nameof(rawArgs));

            var byName = new Dictionary<string, object?>(argSpecs.Count, StringComparer.Ordinal);

            for (int i = 0; i < argSpecs.Count; i++)
            {
                var spec = argSpecs[i];

                // Sweep 14a — variadic LAST spec collects every remaining raw arg.
                // Verified at registration time that this is the last entry; the binder
                // tolerates it appearing mid-list by simply consuming the rest from
                // here on out (callers who declare it mid-list deserve the surprise).
                //
                // Sweep 16 — Variadic + KvPairs collects each remaining raw arg as a
                // "k=v" entry into IReadOnlyDictionary<string,string>. Mirrors the
                // hand-rolled loop the migrated handlers (event.trigger /
                // visual.trigger_queued / wait_for_visual / visual.trigger /
                // db.insert_row) used to do inline. Entries without an `=` are silently
                // skipped (same tolerance as the legacy in-handler split).
                if (spec.Variadic)
                {
                    if (spec.Type == ArgType.KvPairs)
                    {
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int j = i; j < rawArgs.Count; j++)
                        {
                            var entry = rawArgs[j] ?? string.Empty;
                            var kv = entry.Split('=', 2);
                            if (kv.Length == 2)
                                dict[kv[0].Trim()] = kv[1].Trim();
                        }
                        byName[spec.Name] = (IReadOnlyDictionary<string, string>)dict;
                        break;
                    }

                    var rest = new List<string>();
                    for (int j = i; j < rawArgs.Count; j++)
                        rest.Add((rawArgs[j] ?? string.Empty).Trim());
                    byName[spec.Name] = (IReadOnlyList<string>)rest;
                    break;  // variadic eats everything; no specs after it.
                }

                bool present = i < rawArgs.Count;
                string raw   = present ? (rawArgs[i] ?? string.Empty) : (spec.Default ?? string.Empty);

                if (!present && !spec.Optional && spec.Default is null)
                {
                    errors?.Add($"missing required arg #{i} '{spec.Name}'");
                }

                object? coerced = TryCoerce(spec, raw, errors);
                byName[spec.Name] = coerced;
            }

            return new BoundArgs(rawArgs, byName);
        }

        private static object? TryCoerce(ArgSpec spec, string raw, List<string>? errors)
        {
            switch (spec.Type)
            {
                case ArgType.String:
                    return raw;

                case ArgType.Int:
                {
                    if (string.IsNullOrEmpty(raw)) return 0;
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
                    errors?.Add($"arg '{spec.Name}': expected Int, got '{raw}'");
                    return 0;
                }

                case ArgType.Float:
                {
                    if (string.IsNullOrEmpty(raw)) return 0f;
                    if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                    errors?.Add($"arg '{spec.Name}': expected Float, got '{raw}'");
                    return 0f;
                }

                case ArgType.Double:
                {
                    if (string.IsNullOrEmpty(raw)) return 0d;
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                    errors?.Add($"arg '{spec.Name}': expected Double, got '{raw}'");
                    return 0d;
                }

                case ArgType.Bool:
                {
                    if (string.IsNullOrEmpty(raw)) return false;
                    string lower = raw.Trim().ToLowerInvariant();
                    switch (lower)
                    {
                        case "true": case "1": case "yes": case "y": case "on":  return true;
                        case "false": case "0": case "no":  case "n": case "off": return false;
                        default:
                            errors?.Add($"arg '{spec.Name}': expected Bool, got '{raw}'");
                            return false;
                    }
                }

                case ArgType.Json:
                {
                    if (string.IsNullOrEmpty(raw)) return default(JsonElement);
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        // Clone so the JsonElement outlives the disposed doc.
                        return doc.RootElement.Clone();
                    }
                    catch (Exception ex)
                    {
                        errors?.Add($"arg '{spec.Name}': expected Json, got '{raw}' ({ex.GetType().Name})");
                        return default(JsonElement);
                    }
                }

                case ArgType.List:
                {
                    if (string.IsNullOrEmpty(raw)) return Array.Empty<string>() as IReadOnlyList<string>;
                    var parts = raw.Split('|');
                    var trimmed = new string[parts.Length];
                    for (int i = 0; i < parts.Length; i++) trimmed[i] = parts[i].Trim();
                    return (IReadOnlyList<string>)trimmed;
                }

                case ArgType.KvPairs:
                {
                    // Single-arg shape: "k1=v1|k2=v2". The Variadic shape is handled
                    // earlier in BindArgs (rest-collect each raw entry as one k=v).
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (string.IsNullOrEmpty(raw)) return (IReadOnlyDictionary<string, string>)dict;
                    foreach (var part in raw.Split('|'))
                    {
                        var kv = part.Split('=', 2);
                        if (kv.Length == 2)
                            dict[kv[0].Trim()] = kv[1].Trim();
                    }
                    return (IReadOnlyDictionary<string, string>)dict;
                }

                default:
                    return raw;
            }
        }
    }
}
