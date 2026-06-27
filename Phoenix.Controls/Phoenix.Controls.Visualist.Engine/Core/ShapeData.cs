using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// ShapeData — sweep 23. Vertex list model + JSON parse/serialize helpers
    /// for the <c>Mask.Polygon</c> / <c>Mask.Bezier</c> nodes' <c>Vertices</c>
    /// attribute. The compositor.js side parses the same JSON inline; this C#
    /// model exists for the ShapeEditor + tests + the WidgetEditorForm vertex-
    /// keyframe seeding path.
    ///
    /// Vertex shape: <c>{ x, y, cp1x?, cp1y?, cp2x?, cp2y? }</c>.
    /// - Polygon vertices use only x/y; cp* are ignored if present.
    /// - Bezier vertices may carry cp1 (incoming handle, the curve segment
    ///   reaching this vertex from the previous one bends through it) and
    ///   cp2 (outgoing handle, the curve to the next vertex starts through it).
    /// - Missing cp* fields fall back to the anchor coord (straight line).
    /// </summary>
    public static class ShapeData
    {
        /// <summary>
        /// Hard cap on vertices per shape. The kernel pays a per-vertex cost in
        /// canvas path operations; 256 is plenty for any reasonable mask and
        /// keeps a malformed JSON from blowing up render time.
        /// </summary>
        public const int MaxVertices = 256;

        public sealed class Vertex
        {
            [JsonPropertyName("x")]    public double  X    { get; set; }
            [JsonPropertyName("y")]    public double  Y    { get; set; }
            [JsonPropertyName("cp1x")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Cp1X { get; set; }
            [JsonPropertyName("cp1y")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Cp1Y { get; set; }
            [JsonPropertyName("cp2x")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Cp2X { get; set; }
            [JsonPropertyName("cp2y")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Cp2Y { get; set; }

            public bool HasBezierHandles =>
                Cp1X.HasValue && Cp1Y.HasValue && Cp2X.HasValue && Cp2Y.HasValue;
        }

        /// <summary>
        /// Parse a JSON vertex array from a node attribute string. Tolerates
        /// malformed input (returns empty list) so a hand-edited or stale graph
        /// never crashes the editor / kernel — the rendered shape just
        /// degenerates to nothing, which the author can fix in the editor.
        /// Caps at <see cref="MaxVertices"/>.
        /// </summary>
        public static List<Vertex> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<Vertex>();
            try
            {
                var parsed = JsonSerializer.Deserialize<List<Vertex>>(json);
                if (parsed is null) return new List<Vertex>();
                if (parsed.Count > MaxVertices)
                    parsed = parsed.GetRange(0, MaxVertices);
                return parsed;
            }
            catch (JsonException)
            {
                return new List<Vertex>();
            }
        }

        /// <summary>
        /// Serialize a vertex list back to JSON. Cp* fields are omitted when
        /// null so a polygon vertex doesn't carry dead bezier metadata.
        /// </summary>
        public static string Serialize(List<Vertex> vertices)
        {
            return JsonSerializer.Serialize(vertices ?? new List<Vertex>());
        }

        /// <summary>
        /// Default polygon vertices — a centred unit square inside the layer
        /// (0.3..0.7 on both axes). Matches the <c>Mask.Polygon</c> template
        /// default.
        /// </summary>
        public static List<Vertex> DefaultPolygon() => new()
        {
            new Vertex { X = 0.3, Y = 0.3 },
            new Vertex { X = 0.7, Y = 0.3 },
            new Vertex { X = 0.7, Y = 0.7 },
            new Vertex { X = 0.3, Y = 0.7 },
        };

        /// <summary>
        /// Default bezier vertices — a soft two-vertex curve. Matches the
        /// <c>Mask.Bezier</c> template default.
        /// </summary>
        public static List<Vertex> DefaultBezier() => new()
        {
            new Vertex { X = 0.5, Y = 0.2, Cp1X = 0.7, Cp1Y = 0.2, Cp2X = 0.8, Cp2Y = 0.5 },
            new Vertex { X = 0.8, Y = 0.8, Cp1X = 0.7, Cp1Y = 0.8, Cp2X = 0.5, Cp2Y = 0.8 },
        };

        // ── ParameterPath grammar (sweep 23) ──────────────────────────────────
        // Animation paths for vertex coords use the form
        //   vertex[<index>].<axis>
        // where axis ∈ { x, y, cp1x, cp1y, cp2x, cp2y }.
        // The keyframe sampler is path-string-opaque, so the contract is purely
        // a producer/consumer convention. ShapeEditor produces these strings;
        // the JS sampler reads them via vertexAnimated() in compositor.js;
        // FormatVertexPath here is the single source of truth that both sides
        // and the test suite agree on.

        public static string FormatVertexPath(int index, string axis)
        {
            if (index < 0) index = 0;
            return $"vertex[{index}].{axis}";
        }

        /// <summary>
        /// Pretty-print a vertex parameterPath for the TimelinePanel track
        /// label. <c>vertex[3].x</c> → <c>"Vertex 3 X"</c>; non-vertex paths
        /// pass through unchanged.
        /// </summary>
        public static string FormatVertexPathLabel(string parameterPath)
        {
            if (string.IsNullOrEmpty(parameterPath)) return parameterPath ?? "";
            const string prefix = "vertex[";
            int p = parameterPath.IndexOf(prefix, StringComparison.Ordinal);
            if (p < 0) return parameterPath;
            int closeBracket = parameterPath.IndexOf(']', p + prefix.Length);
            if (closeBracket < 0) return parameterPath;
            string indexStr = parameterPath.Substring(p + prefix.Length, closeBracket - (p + prefix.Length));
            int dot = parameterPath.IndexOf('.', closeBracket);
            if (dot < 0) return parameterPath;
            string axis = parameterPath.Substring(dot + 1);
            // Capitalise axis for display: x → X, cp1x → Cp1X.
            string prettyAxis = axis switch
            {
                "x"    => "X",
                "y"    => "Y",
                "cp1x" => "Cp1X",
                "cp1y" => "Cp1Y",
                "cp2x" => "Cp2X",
                "cp2y" => "Cp2Y",
                _      => axis,
            };
            return $"Vertex {indexStr} {prettyAxis}";
        }
    }
}
