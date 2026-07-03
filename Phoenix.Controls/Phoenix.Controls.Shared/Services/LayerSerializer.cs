using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    public static class LayerSerializer
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            WriteIndented = true,
            // AllowNamedFloatingPointLiterals lets us
            // READ back any "NaN"/"Infinity" string already persisted in
            // an older .phxlayer without throwing; but on the WRITE path
            // Serialize() refuses to emit a non-finite double regardless
            // of this flag for the JsonElement Value column, so we still
            // pre-validate via ValidateForSave below. The flag exists
            // mainly to make recovery loads (open a corrupted file, fix
            // the keyframe in Visualist, save clean) possible instead of
            // dead-on-arrival.
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters =
            {
                new LayerColorJsonConverter(),
                new JsonStringEnumConverter<LayerPreset>(new LowerCaseNamingPolicy()),
                new JsonStringEnumConverter<WidgetPreset>(new LowerCaseNamingPolicy()),
                new JsonStringEnumConverter<KeyframeCurve>(JsonNamingPolicy.CamelCase),
            }
        };

        public static Layer Read(string path)
        {
            string json = File.ReadAllText(path);
            return Deserialize(json);
        }

        public static void Write(string path, Layer layer)
        {
            // Crash-safe save: write the bytes to a sibling .tmp file, fsync
            // them to physical storage, then swap atomically into place. The
            // earlier File.Move(overwrite:true) path was delete-then-rename
            // internally — a power loss between the delete and the rename
            // could leave the destination missing entirely. ReplaceFile (via
            // File.Replace) uses NTFS' atomic-swap semantics when the target
            // already exists; first-time writes have no existing file to
            // protect and can fall through to a plain Move.
            string json = Serialize(layer);
            string tempPath = path + ".tmp";
            bool moved = false;
            try
            {
                // Explicit fsync — File.WriteAllText flushes the .NET stream
                // but does NOT push the OS page cache to disk. Without
                // FlushToDisk a crash mid-replace can swap a still-cached
                // (and possibly zero-length) temp file into place.
                using (var fs = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    // NTFS atomic replace — never leaves the destination
                    // missing. ignoreMetadataErrors:true keeps the swap
                    // working when ACLs / timestamps can't be copied
                    // (e.g. cross-share moves), which is the documented
                    // Win32 ReplaceFile fast-path.
                    File.Replace(
                        tempPath, path,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
                moved = true;
            }
            finally
            {
                // If the write OR the swap failed, the .tmp file
                // is now an orphan sitting next to the live .phxlayer with
                // partial / stale content. Leaving it on disk piles up junk
                // every failed save AND confuses any tooling that globs
                // `*.phxlayer*`. Best-effort delete; if the delete itself
                // fails we swallow because we're already in the exceptional
                // path and the caller's original exception is what they
                // need to see.
                if (!moved && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch { /* best effort cleanup */ }
                }
            }
        }

        public static string Serialize(Layer layer)
        {
            // Pre-flight numeric validation. A keyframe
            // with a NaN TimeMs (or non-finite bezier handle, or numeric
            // Value) makes System.Text.Json throw ArgumentException mid-
            // write, which previously surfaced to the user as a hard save
            // failure with no breadcrumb back to the offending widget. Now
            // we walk the layer first, log each issue to GlobalLogger
            // (no modal for repeatable rejections),
            // and throw a specific exception the caller can catch to
            // surface a non-modal warning chip / log entry instead of a
            // ContentDialog.
            var issues = NumericGuards.ValidateLayerForSave(layer);
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    var kfTag = issue.KeyframeIndex >= 0
                        ? $"keyframe #{issue.KeyframeIndex}"
                        : "timeline";
                    GlobalLogger.Log(
                        $"layer save refused: widget '{issue.WidgetName}' ({issue.WidgetId}) trigger '{issue.TriggerName}' {kfTag} field '{issue.Field}' parameter '{issue.ParameterPath}' is NaN/Infinity",
                        "LayerSerializer",
                        Models.LogLevel.Warning);
                }
                throw new LayerNumericValidationException(issues);
            }
            return JsonSerializer.Serialize(layer, _options);
        }

        public static Layer Deserialize(string json)
        {
            var layer = JsonSerializer.Deserialize<Layer>(json, _options)
                ?? throw new InvalidDataException("Layer JSON deserialized to null.");

            // Load-time migration for trigger names that
            // pre-date WidgetTrigger's identifier validation. The JSON
            // converter uses WidgetTrigger.SetForLoad so the raw on-disk
            // name survives deserialization; here we rewrite anything that
            // wouldn't pass IsValidName to a deterministic trigger_<hash>
            // fallback so the runtime / exporter / clipboard producer all
            // see a well-formed identifier. Logged once per rewrite at
            // Communication tier (no modal for repeatable rejections).
            foreach (var widget in layer.Widgets)
            {
                if (widget.Triggers is null) continue;
                foreach (var trigger in widget.Triggers)
                {
                    string raw = trigger.Name ?? "";
                    if (!WidgetTrigger.IsValidName(raw))
                    {
                        string fallback = WidgetTrigger.MakeFallbackName(raw);
                        GlobalLogger.Log(
                            $"Layer load — trigger name '{raw}' on widget '{widget.Id}' is invalid; rewriting to '{fallback}'.",
                            source: "LayerSerializer",
                            level: Models.LogLevel.Communication);
                        trigger.SetForLoad(fallback);
                    }

                    // Soft cap on per-track keyframe count.
                    // Unbounded timelines turn into perceptible sample-time
                    // lag once the cached sort has to rebuild. 256 is the
                    // soft ceiling: we warn, we don't truncate. The freeze
                    // posture says don't break existing user layers, so the
                    // overage is reported via GlobalLogger and the layer
                    // loads as authored.
                    var kfs = trigger.Timeline?.Keyframes;
                    if (kfs is { Count: > KeyframeSoftCapPerTrack })
                    {
                        GlobalLogger.Log(
                            $"Layer load — widget '{widget.Id}' trigger '{trigger.Name}' timeline has {kfs.Count} keyframes (soft cap {KeyframeSoftCapPerTrack}); consider splitting the track.",
                            source: "LayerSerializer",
                            level: Models.LogLevel.Communication);
                    }
                }
            }

            return layer;
        }

        /// <summary>
        /// Soft ceiling on <c>WidgetTimeline.Keyframes</c>
        /// per track. Crossed only by warning, never by rejection (existing
        /// user layers must keep loading per the freeze posture). The cache
        /// in <c>WidgetTimeline.SortedKeyframes</c> still works above the
        /// cap; the warning exists so Majo learns about runaway tracks
        /// before they become a perf complaint.
        /// </summary>
        public const int KeyframeSoftCapPerTrack = 256;
    }

    file sealed class LowerCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }

    file sealed class LayerColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return Color.FromArgb(reader.GetInt32());

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                byte r = 0, g = 0, b = 0, a = 255;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string name = reader.GetString() ?? "";
                    reader.Read();
                    switch (name)
                    {
                        case "R": r = reader.GetByte(); break;
                        case "G": g = reader.GetByte(); break;
                        case "B": b = reader.GetByte(); break;
                        case "A": a = reader.GetByte(); break;
                        default:  reader.Skip();        break;
                    }
                }
                return Color.FromArgb(a, r, g, b);
            }

            reader.Skip();
            return Color.Empty;
        }

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.ToArgb());
    }
}
