using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Phoenix.Controls.Shared.Models
{
    public sealed class LayerResolution
    {
        [JsonPropertyName("width")]  public int Width  { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
    }

    public enum LayerPreset { Custom, FullHD, QHD, UHD, Vertical, Square }

    /// <summary>
    /// Maps the named <see cref="LayerPreset"/> values to canonical (width, height)
    /// pairs. <see cref="LayerPreset.Custom"/> returns the (0, 0) sentinel — callers
    /// are expected to keep their existing W/H when the preset is Custom rather
    /// than zeroing the resolution. Previously the
    /// InspectorPanel preset ComboBox only wrote <see cref="Layer.Preset"/>, never
    /// the resolution, so picking "QHD" was cosmetic.
    /// </summary>
    public static class LayerPresetExtensions
    {
        public static (int width, int height) ToResolution(this LayerPreset preset) => preset switch
        {
            LayerPreset.FullHD   => (1920, 1080),
            LayerPreset.QHD      => (2560, 1440),
            LayerPreset.UHD      => (3840, 2160),
            LayerPreset.Vertical => (1080, 1920),
            LayerPreset.Square   => (1080, 1080),
            LayerPreset.Custom   => (0, 0),
            _                    => (0, 0),
        };

        /// <summary>
        /// Reverse lookup — returns the named preset whose canonical resolution
        /// matches (<paramref name="width"/>, <paramref name="height"/>), or
        /// <see cref="LayerPreset.Custom"/> when no preset matches. Used by the
        /// InspectorPanel to auto-flip the preset combo to Custom when the user
        /// manually edits W/H to a non-preset value.
        /// </summary>
        public static LayerPreset MatchPreset(int width, int height)
        {
            foreach (LayerPreset p in new[]
                     {
                         LayerPreset.FullHD,
                         LayerPreset.QHD,
                         LayerPreset.UHD,
                         LayerPreset.Vertical,
                         LayerPreset.Square,
                     })
            {
                var (w, h) = p.ToResolution();
                if (w == width && h == height) return p;
            }
            return LayerPreset.Custom;
        }
    }

    public sealed class Layer
    {
        [JsonPropertyName("name")]       public string          Name       { get; set; } = "";
        [JsonPropertyName("resolution")] public LayerResolution Resolution { get; set; } = new();
        [JsonPropertyName("preset")]     public LayerPreset     Preset     { get; set; } = LayerPreset.Custom;
        [JsonPropertyName("widgets")]    public List<LayerWidget> Widgets  { get; set; } = new();

        /// <summary>
        /// Optional default target language (BCP-47, e.g. "de", "ja") for any Caption.LiveCaption
        /// node on this layer that doesn't override it. Empty disables translation; the
        /// Caption.LiveCaption.Translated output then echoes the original text.
        /// </summary>
        [JsonPropertyName("targetLanguage")] public string? TargetLanguage { get; set; }
    }
}
