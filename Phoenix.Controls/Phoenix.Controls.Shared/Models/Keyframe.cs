using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phoenix.Controls.Shared.Models
{
    public enum KeyframeCurve { Linear, EaseIn, EaseOut, EaseInOut, Bezier, Step }

    public sealed class Keyframe
    {
        [JsonPropertyName("time")]          public double        TimeMs        { get; set; }
        [JsonPropertyName("parameterPath")] public string        ParameterPath { get; set; } = "";
        [JsonPropertyName("value")]         public JsonElement   Value         { get; set; }
        [JsonPropertyName("curve")]         public KeyframeCurve Curve         { get; set; } = KeyframeCurve.Linear;

        // Sweep 21 — optional custom bezier control points for the Bezier curve.
        // When all four are non-null AND Curve == Bezier, KeyframeInterpolation
        // uses these handles. Otherwise it falls back to the (0.25, 0.1, 0.25, 1.0)
        // ease-like default. JsonIgnoreCondition.WhenWritingNull keeps the JSON
        // small for Linear/Step keyframes — they don't carry handle fields.
        [JsonPropertyName("p1x")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? BezierP1X { get; set; }

        [JsonPropertyName("p1y")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? BezierP1Y { get; set; }

        [JsonPropertyName("p2x")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? BezierP2X { get; set; }

        [JsonPropertyName("p2y")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? BezierP2Y { get; set; }

        public bool HasCustomBezierHandles =>
            BezierP1X.HasValue && BezierP1Y.HasValue && BezierP2X.HasValue && BezierP2Y.HasValue;
    }
}
