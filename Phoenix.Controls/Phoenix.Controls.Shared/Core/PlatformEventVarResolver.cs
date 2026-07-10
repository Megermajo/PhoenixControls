using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// Evaluates a <see cref="PlatformEventDef"/>'s extraction rules against a
    /// Streamer.bot event's <c>data</c> JSON element. Lives in Shared so the
    /// Hub's dispatch and the test suite exercise the exact same code.
    ///
    /// Streamer.bot does not publish WS payload schemas for YouTube/Kick, so
    /// every probe is best-effort: a missing field simply leaves the variable
    /// unset (script land renders the token literally, same convention as the
    /// bespoke Twitch probes in ScriptManager.BuildGenericEventVars).
    /// </summary>
    public static class PlatformEventVarResolver
    {
        /// <summary>
        /// Resolves every declared var of <paramref name="def"/> into
        /// <paramref name="vars"/>, plus <c>event.payload</c> (raw JSON of the
        /// data element — the machine-readable fallback for thin nodes, the
        /// same convention on_webhook uses).
        /// </summary>
        public static void Apply(PlatformEventDef def, JsonElement data, IDictionary<string, string> vars)
        {
            try { vars["event.payload"] = data.GetRawText(); }
            catch { /* non-object payloads have no useful raw form */ }

            foreach (var v in def.Vars)
            {
                string? value = Resolve(data, v);
                if (value is not null) vars[v.VarName] = value;
            }

            // Kick.MassGiftSubscription: SB < 1.0.5 carries no count field —
            // fall back to the recipients array length.
            if (!vars.ContainsKey("user.count") &&
                def.Title.Equals("Kick.MassGiftSubscription", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var key in new[] { "recipients", "recipient" })
                {
                    if (data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty(key, out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        vars["user.count"] = arr.GetArrayLength().ToString();
                        break;
                    }
                }
            }
        }

        internal static string? Resolve(JsonElement data, PlatformEventVar def)
        {
            bool nestedUserFirst = def.Kind is PlatformProbeKind.NestedUserStr or PlatformProbeKind.NestedUserBool;
            foreach (var probe in def.Probes)
            {
                if (!TryResolveElement(data, probe, nestedUserFirst, out var el))
                    continue;

                switch (def.Kind)
                {
                    case PlatformProbeKind.Str:
                    case PlatformProbeKind.NestedUserStr:
                    {
                        string s = AsString(el);
                        if (s.Length > 0) return s;
                        break;
                    }
                    case PlatformProbeKind.Int:
                    {
                        // Normalize floats/exponents to an integer string — a
                        // payload sending viewerCount: 123.0 must not surface as
                        // "123.0" when the string branch would have yielded "123".
                        if (el.ValueKind == JsonValueKind.Number)
                        {
                            if (el.TryGetInt64(out var i)) return i.ToString();
                            if (el.TryGetDouble(out var d)) return ((long)d).ToString();
                            break;
                        }
                        if (el.ValueKind == JsonValueKind.String &&
                            long.TryParse(el.GetString(), out var n)) return n.ToString();
                        break;
                    }
                    case PlatformProbeKind.Bool:
                    case PlatformProbeKind.NestedUserBool:
                    {
                        if (el.ValueKind == JsonValueKind.True) return "true";
                        if (el.ValueKind == JsonValueKind.False) return "false";
                        if (el.ValueKind == JsonValueKind.String &&
                            bool.TryParse(el.GetString(), out var b)) return b ? "true" : "false";
                        break;
                    }
                    case PlatformProbeKind.JoinArray:
                    {
                        if (el.ValueKind != JsonValueKind.Array) break;
                        var sb = new StringBuilder();
                        int joined = 0;
                        foreach (var item in el.EnumerateArray())
                        {
                            // Bounded: PresentViewers-class heartbeats can carry
                            // thousands of entries; 500 matches the engine's
                            // MaxAggregateLoopIterations, so a for_each over the
                            // joined list can consume everything we emit anyway.
                            if (joined >= 500) break;
                            string entry = item.ValueKind switch
                            {
                                JsonValueKind.String => item.GetString() ?? string.Empty,
                                JsonValueKind.Object => FirstObjectName(item),
                                _ => string.Empty,
                            };
                            if (entry.Length == 0) continue;
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append(entry);
                            joined++;
                        }
                        if (sb.Length > 0) return sb.ToString();
                        break;
                    }
                }
            }
            return null;
        }

        // Probe resolution order: (1) data.user.<probe> when the kind asks for
        // the nested actor object, (2) dotted-path walk through nested objects,
        // (3) the full probe string as one literal flat key — Streamer.bot's
        // trigger-variable exports flatten nested groups into dotted key names,
        // so both shapes must hit.
        private static bool TryResolveElement(JsonElement data, string probe, bool nestedUserFirst, out JsonElement el)
        {
            el = default;
            if (data.ValueKind != JsonValueKind.Object) return false;

            if (nestedUserFirst &&
                data.TryGetProperty("user", out var user) &&
                user.ValueKind == JsonValueKind.Object &&
                user.TryGetProperty(probe, out el))
                return true;

            if (probe.IndexOf('.') >= 0 && TryWalkDotted(data, probe, out el))
                return true;

            return data.TryGetProperty(probe, out el);
        }

        private static bool TryWalkDotted(JsonElement data, string path, out JsonElement el)
        {
            el = data;
            foreach (var segment in path.Split('.'))
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(segment, out el))
                {
                    el = default;
                    return false;
                }
            }
            return true;
        }

        private static string AsString(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            _ => string.Empty,
        };

        private static string FirstObjectName(JsonElement obj)
        {
            foreach (var key in new[] { "name", "userName", "login", "user", "displayName" })
            {
                if (obj.TryGetProperty(key, out var el) &&
                    el.ValueKind == JsonValueKind.String &&
                    el.GetString() is { Length: > 0 } s)
                    return s;
            }
            return string.Empty;
        }
    }
}
