using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Phoenix.Controls.Shared.Models
{
    public sealed class WidgetRect
    {
        [JsonPropertyName("x")]      public int X      { get; set; }
        [JsonPropertyName("y")]      public int Y      { get; set; }
        [JsonPropertyName("width")]  public int Width  { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
    }

    public enum WidgetPreset { Image, Video, Text, Audio, WebSource, Particles, Chat, CC }

    public sealed class LayerWidget
    {
        [JsonPropertyName("id")]        public string                Id        { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("name")]      public string                Name      { get; set; } = "";
        [JsonPropertyName("rect")]      public WidgetRect            Rect      { get; set; } = new();
        [JsonPropertyName("zIndex")]    public int                   ZIndex    { get; set; }
        [JsonPropertyName("preset")]    public WidgetPreset?         Preset    { get; set; }
        // B37 (audit/winui-regressions-2026-05-24) — base64 PNG payload
        // (typically "data:image/png;base64,…"); populated on save via
        // Visualist's WidgetThumbnailCapture.CaptureBase64Async. Round-trips
        // unchanged through LayerSerializer; rendered back as the per-widget
        // thumb on the layer canvas (WidgetView.ThumbnailB64).
        [JsonPropertyName("thumbnail")] public string?               Thumbnail { get; set; }

        // Per-widget event-transition duration in MILLISECONDS (0–1000). When this
        // widget's active trigger changes at runtime, compositor.js dips the old
        // content out to blank then fades the new content in over this duration
        // (a "dip-to-blank" cross-state transition) instead of cutting instantly.
        // 0 = instant cut (legacy behaviour — byte-identical to pre-transition).
        // Clamped on assignment so a malformed .phxlayer can't inject NaN/Infinity
        // or a runaway value that would lock the widget render queue.
        private double _transitionMs;
        [JsonPropertyName("transitionMs")]
        public double TransitionMs
        {
            get => _transitionMs;
            set => _transitionMs = double.IsNaN(value) || value <= 0
                ? 0
                : (value > 1000 ? 1000 : value);
        }

        [JsonPropertyName("triggers")]  public List<WidgetTrigger>   Triggers  { get; set; } = new();
    }
}
