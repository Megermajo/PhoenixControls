using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// AnimatedPinRegistry — pure helpers that derive "is this pin animated?"
    /// state from the <see cref="WidgetTimeline"/> keyframe set. The single
    /// source of truth is the timeline itself; a pin is animated iff at least
    /// one of its component <see cref="Keyframe.ParameterPath"/> values has a
    /// matching keyframe. No separate flag is persisted on the node.
    ///
    /// Keeps the right-click-pin-→-animate gesture decoupled from
    /// <see cref="Phoenix.Controls.Visualist.Controls.WidgetGraphCanvas"/>
    /// internals so headless tests can pin behaviour without driving paint.
    ///
    /// Vector pins (Vector2 / Vector3 / Vector4) animate per-component because
    /// the existing inline-pill scheme stores the components as separate
    /// scalar attributes (e.g. a Vector2 "Translate" socket has TranslateX /
    /// TranslateY attribute fallbacks). Animating a vector pin therefore
    /// inserts one keyframe pair per component and the timeline carries one
    /// track per component path — interchangeable with the per-pill gesture.
    /// </summary>
    public static class AnimatedPinRegistry
    {
        /// <summary>
        /// True for the data types eligible for pin-side animation: numeric
        /// scalars (Scalar / Float / Int) and vectors (Vector2 / Vector3 /
        /// Vector4). Other types either aren't tween-friendly today or have
        /// their own author surface.
        /// </summary>
        public static bool IsAnimatablePinType(SocketDataType dt) => dt switch
        {
            SocketDataType.Scalar or
            SocketDataType.Float  or
            SocketDataType.Int     => true,
            SocketDataType.Vector2 or
            SocketDataType.Vector3 or
            SocketDataType.Vector4 => true,
            // Color animates as four scalar channel tracks (R/G/B/A, 0–255).
            // The decomposition lives in GetPinComponents (".R"/".G"/".B"/".A"
            // suffixes) and ReadComponentLiteral parses the node's hex literal
            // into per-channel seed values; compositor.js recombines them via
            // attrAnimatedColor. Keeping colour on the same scalar machinery as
            // vectors means the timeline / sampler / interpolation are unchanged.
            SocketDataType.Color   => true,
            _                       => false,
        };

        /// <summary>
        /// Returns the per-component (componentName, attrKey) tuples for a pin.
        /// Both fields are equal today — the runtime contract uses
        /// <c>"&lt;nodeId&gt;.&lt;componentName&gt;"</c> as the parameter path
        /// and the inline-pill attribute key matches the component name. They
        /// stay separate so the contract can diverge later (e.g. a future
        /// aliasing scheme) without rewriting every caller.
        /// </summary>
        public static IReadOnlyList<(string ComponentName, string AttrKey)> GetPinComponents(Socket socket)
        {
            if (socket is null) return Array.Empty<(string, string)>();
            string baseName = socket.Name ?? string.Empty;
            return socket.DataType switch
            {
                SocketDataType.Vector2 => new[]
                {
                    (baseName + "X", baseName + "X"),
                    (baseName + "Y", baseName + "Y"),
                },
                SocketDataType.Vector3 => new[]
                {
                    (baseName + "X", baseName + "X"),
                    (baseName + "Y", baseName + "Y"),
                    (baseName + "Z", baseName + "Z"),
                },
                SocketDataType.Vector4 => new[]
                {
                    (baseName + "X", baseName + "X"),
                    (baseName + "Y", baseName + "Y"),
                    (baseName + "Z", baseName + "Z"),
                    (baseName + "W", baseName + "W"),
                },
                // Colour → four channel tracks. The ".R"/".G"/".B"/".A" suffix is
                // load-bearing: it (a) makes the parameter path `<id>.<key>.R` that
                // compositor.js's attrAnimatedColor samples, and (b) is the marker
                // ReadComponentLiteral keys off to parse the node's hex literal into
                // the channel's 0–255 seed value (the base attr stores ONE hex, not
                // four numeric attrs like a vector does).
                SocketDataType.Color => new[]
                {
                    (baseName + ".R", baseName + ".R"),
                    (baseName + ".G", baseName + ".G"),
                    (baseName + ".B", baseName + ".B"),
                    (baseName + ".A", baseName + ".A"),
                },
                _ => new[] { (baseName, baseName) },
            };
        }

        /// <summary>
        /// The four channel component keys for a colour parameter whose hex literal
        /// lives under <paramref name="colorKey"/> (e.g. "Value" / "Color" /
        /// "StrokeColor"). Used by the socket-LESS animate path (Colour constants
        /// have no input socket, so <see cref="GetPinComponents"/> is never reached)
        /// to stay byte-identical to the socket-backed decomposition above.
        /// </summary>
        public static string[] GetColorChannelKeys(string colorKey)
        {
            colorKey ??= string.Empty;
            return new[] { colorKey + ".R", colorKey + ".G", colorKey + ".B", colorKey + ".A" };
        }

        /// <summary>
        /// Build the canonical parameter path string used in
        /// <see cref="Keyframe.ParameterPath"/>. Centralised so the gesture and
        /// the runtime contract can never drift on format.
        /// </summary>
        public static string MakeParameterPath(Node node, string componentName)
        {
            if (node is null) return componentName ?? string.Empty;
            return node.Id + "." + (componentName ?? string.Empty);
        }

        /// <summary>
        /// Reads the literal scalar value of a component from
        /// <see cref="Node.Attributes"/>. Falls back to 0 when the
        /// attribute is missing or unparsable — matches the runtime's
        /// "missing → 0" expectation. Case-insensitive lookup so legacy graphs
        /// with mixed-case attribute keys still resolve.
        /// </summary>
        public static double ReadComponentLiteral(Node node, string attrKey)
        {
            if (node is null || node.Attributes is null || string.IsNullOrEmpty(attrKey)) return 0;

            // Colour channel ("<colorKey>.R"/".G"/".B"/".A"): the node stores ONE
            // hex literal under the base key, not four numeric attrs — parse the hex
            // and return the channel as 0–255 so the seeded keyframe starts at the
            // current colour. Only intercepts when the base attribute is genuinely a
            // hex colour; otherwise falls through to the plain numeric read below
            // (so a non-colour attr that happens to end in ".R" is unaffected).
            if (TrySplitColorChannel(attrKey, out string baseKey, out char channel)
                && TryLookupAttr(node, baseKey, out var baseRaw)
                && TryParseHexColorChannels(baseRaw, out double cr, out double cg, out double cb, out double ca))
            {
                return channel switch { 'R' => cr, 'G' => cg, 'B' => cb, 'A' => ca, _ => 0 };
            }

            if (!TryLookupAttr(node, attrKey, out var raw)) return 0;
            if (string.IsNullOrEmpty(raw)) return 0;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        // Case-insensitive attribute lookup (legacy graphs carry mixed-case keys).
        private static bool TryLookupAttr(Node node, string attrKey, out string? raw)
        {
            raw = null;
            if (node?.Attributes is null || string.IsNullOrEmpty(attrKey)) return false;
            if (node.Attributes.TryGetValue(attrKey, out raw)) return true;
            foreach (var kv in node.Attributes)
            {
                if (string.Equals(kv.Key, attrKey, StringComparison.OrdinalIgnoreCase))
                {
                    raw = kv.Value;
                    return true;
                }
            }
            return false;
        }

        // Split "<base>.R|G|B|A" → (base, channel). Requires the dot + a single
        // R/G/B/A so vector suffixes ("X"/"Y"/…, no dot) never collide.
        private static bool TrySplitColorChannel(string attrKey, out string baseKey, out char channel)
        {
            baseKey = attrKey;
            channel = '\0';
            if (string.IsNullOrEmpty(attrKey) || attrKey.Length < 3) return false;
            char last = attrKey[attrKey.Length - 1];
            if (last != 'R' && last != 'G' && last != 'B' && last != 'A') return false;
            if (attrKey[attrKey.Length - 2] != '.') return false;
            baseKey = attrKey.Substring(0, attrKey.Length - 2);
            channel = last;
            return true;
        }

        /// <summary>
        /// Parse a (optionally JSON-quoted) hex colour string into 0–255 channels.
        /// Accepts #RGB / #RRGGBB / #RRGGBBAA (alpha last, default 255). Matches the
        /// byte order compositor.js's parseHexColor and Convert.ColorFromRGBA emit so
        /// the seed values round-trip identically through the runtime sampler.
        /// </summary>
        public static bool TryParseHexColorChannels(string? raw, out double r, out double g, out double b, out double a)
        {
            r = g = b = 255; a = 255;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2).Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            if (s.Length != 6 && s.Length != 8) return false;
            foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
            r = Convert.ToInt32(s.Substring(0, 2), 16);
            g = Convert.ToInt32(s.Substring(2, 2), 16);
            b = Convert.ToInt32(s.Substring(4, 2), 16);
            a = s.Length == 8 ? Convert.ToInt32(s.Substring(6, 2), 16) : 255;
            return true;
        }

        /// <summary>
        /// True when at least one of the pin's component parameter paths has
        /// a keyframe on the timeline.
        /// </summary>
        public static bool IsPinAnimated(WidgetTimeline? timeline, Node node, Socket socket)
        {
            if (timeline is null || node is null || socket is null) return false;
            if (!IsAnimatablePinType(socket.DataType)) return false;
            var components = GetPinComponents(socket);
            if (components.Count == 0) return false;
            foreach (var c in components)
            {
                string path = MakeParameterPath(node, c.ComponentName);
                foreach (var kf in timeline.Keyframes)
                {
                    if (string.Equals(kf.ParameterPath, path, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Build a HashSet of (nodeId, socketId) for every animated pin in the
        /// graph. Paint code calls this once per repaint and does O(1) lookups
        /// per socket instead of scanning the keyframe list per-socket per-paint.
        /// </summary>
        public static HashSet<(string NodeId, string SocketId)> BuildAnimatedPinSet(
            WidgetTimeline? timeline, Graph? graph)
        {
            var result = new HashSet<(string, string)>();
            if (timeline is null || graph is null) return result;
            if (timeline.Keyframes is null || timeline.Keyframes.Count == 0) return result;

            // Index keyframe paths up-front so every (node, socket) probe is a
            // hash hit instead of a linear scan.
            var animatedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kf in timeline.Keyframes)
            {
                if (!string.IsNullOrEmpty(kf.ParameterPath))
                    animatedPaths.Add(kf.ParameterPath);
            }
            if (animatedPaths.Count == 0) return result;

            foreach (var node in graph.Nodes)
            {
                foreach (var socket in node.Sockets)
                {
                    if (socket.Type != SocketType.Input) continue;
                    if (!IsAnimatablePinType(socket.DataType)) continue;
                    foreach (var c in GetPinComponents(socket))
                    {
                        if (animatedPaths.Contains(MakeParameterPath(node, c.ComponentName)))
                        {
                            result.Add((node.Id, socket.Id));
                            break;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Seed two keyframes per component on the given pin: one at
        /// <paramref name="playheadMs"/> and one a second later. The seed
        /// values are read from the node's literal attributes — preserving
        /// the existing static value as the at-rest fallback. If the timeline
        /// has zero duration, bumps it to fit the second keyframe.
        ///
        /// Returns the number of keyframes appended (0 when the pin's data
        /// type isn't animatable). Caller pushes undo + marks dirty.
        /// </summary>
        public static int SeedAnimation(WidgetTimeline timeline, Node node,
            Socket socket, double playheadMs)
        {
            if (timeline is null || node is null || socket is null) return 0;
            if (!IsAnimatablePinType(socket.DataType)) return 0;
            if (timeline.Keyframes is null) timeline.Keyframes = new List<Keyframe>();

            double startMs = playheadMs < 0 ? 0 : playheadMs;
            double endMs   = startMs + 1000.0;
            if (timeline.DurationMs < endMs) timeline.DurationMs = endMs;

            int added = 0;
            foreach (var c in GetPinComponents(socket))
            {
                double literal = ReadComponentLiteral(node, c.AttrKey);
                string path    = MakeParameterPath(node, c.ComponentName);
                timeline.Keyframes.Add(new Keyframe
                {
                    ParameterPath = path,
                    TimeMs        = startMs,
                    Value         = JsonSerializer.SerializeToElement(literal),
                    Curve         = KeyframeCurve.Linear,
                });
                timeline.Keyframes.Add(new Keyframe
                {
                    ParameterPath = path,
                    TimeMs        = endMs,
                    Value         = JsonSerializer.SerializeToElement(literal),
                    Curve         = KeyframeCurve.Linear,
                });
                added += 2;

                // B42 (audit/winui-regressions-2026-05-24) — cross-pillar mirror.
                // Architect's NodeView.Pins.cs queries AnimatedVarTracker per
                // socket so a Var.Set / Var.Get whose VariableName matches an
                // animated Visualist pin lights up its animated-pin badge.
                // We mirror both the component-name only (so Var.Set("TranslateX")
                // on Architect side picks up the badge through naming convention)
                // and the full parameter path (so a tooling layer that uses
                // nodeId.component as a binding key gets the same coverage).
                MirrorMarkAnimated(c.ComponentName);
                MirrorMarkAnimated(path);
            }
            return added;
        }

        /// <summary>
        /// Remove every keyframe whose <see cref="Keyframe.ParameterPath"/>
        /// matches one of the pin's component paths. Returns the number of
        /// keyframes removed. Caller pushes undo + marks dirty.
        /// </summary>
        public static int RemoveAnimation(WidgetTimeline timeline, Node node, Socket socket)
        {
            if (timeline is null || node is null || socket is null) return 0;
            if (timeline.Keyframes is null || timeline.Keyframes.Count == 0) return 0;

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in GetPinComponents(socket))
                paths.Add(MakeParameterPath(node, c.ComponentName));

            int removed = timeline.Keyframes.RemoveAll(k =>
                k != null && !string.IsNullOrEmpty(k.ParameterPath) && paths.Contains(k.ParameterPath));

            // B42 — drop the cross-pillar mirror for every component we cleared.
            // The shared tracker is reference-counted, so a duplicate
            // un-mark (e.g. concurrent same-component remove on two pins) just
            // bumps the count down past zero and is idempotent at the
            // is-animated level. Same dual-key contract as SeedAnimation —
            // we drop both the component-name key and the full parameter path.
            if (removed > 0)
            {
                foreach (var c in GetPinComponents(socket))
                {
                    MirrorMarkNotAnimated(c.ComponentName);
                    MirrorMarkNotAnimated(MakeParameterPath(node, c.ComponentName));
                }
            }
            return removed;
        }

        /// <summary>
        /// B42 helper — wraps the cross-pillar mirror call so the call sites
        /// don't carry the namespace fanout. Centralised so a future change
        /// to the shared-tracker contract (e.g. a different naming convention
        /// or an additional channel) lives in one place.
        /// </summary>
        private static void MirrorMarkAnimated(string? varName)
        {
            if (string.IsNullOrEmpty(varName)) return;
            try { AnimatedVarTracker.MarkAnimated(varName); }
            catch { /* tracker is best-effort — see its own swallowing contract */ }
        }

        private static void MirrorMarkNotAnimated(string? varName)
        {
            if (string.IsNullOrEmpty(varName)) return;
            try { AnimatedVarTracker.MarkNotAnimated(varName); }
            catch { /* tracker is best-effort */ }
        }
    }
}
