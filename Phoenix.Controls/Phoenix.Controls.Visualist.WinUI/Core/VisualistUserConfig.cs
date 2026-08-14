using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Core
{
    /// <summary>
    /// VisualistUserConfig — global, persisted, per-user Visualist editor
    /// preferences that don't belong inside any specific .phxlayer file.
    ///
    /// Lives at <c>%LOCALAPPDATA%/PhoenixControls/Visualist/visualist.user.json</c>
    /// (same root that <see cref="UrlImageCache"/> already uses, so all
    /// design-time-only state for Visualist is co-located outside the repo).
    ///
    /// Surface is small on purpose — anything that needs to live ON the layer
    /// (resolution, widgets, triggers) stays in the .phxlayer file; this file
    /// captures only Visualist-side authoring conveniences (preview backdrop
    /// colour, auto-save toggle, per-surface preview enables) so the same Hub
    /// runtime would never read it.
    ///
    /// (Ported verbatim from the pre-T15 WinForms baseline
    /// <c>Phoenix.Controls.Visualist/Core/VisualistUserConfig.cs</c> during the
    /// Visualist WinUI parity restoration — Visualist regression audit
    /// 2026-05-31, Lane A. Schema + persistence path are byte-identical to the
    /// baseline so an in-place install's existing visualist.user.json keeps
    /// loading unchanged.)
    /// </summary>
    public sealed class VisualistUserConfig
    {
        // ── Schema ────────────────────────────────────────────────────────

        public PreviewBgColor PreviewBackground { get; set; } = PreviewBgColor.Black;

        /// <summary>
        /// Off by default. When ON, edits to a saved <c>LayerDocument</c> are
        /// auto-saved after a short debounce so Hub's LayerWatcher broadcasts
        /// LAYER_RELOADED and OBS / preview surfaces refresh live. Auto-save
        /// only kicks in once the user has chosen a path explicitly via
        /// Save As — an unsaved scratch document is never written silently.
        /// </summary>
        public bool AutoSyncOnEdit { get; set; } = false;

        /// <summary>
        /// Master switch for the layer-canvas widget preview (single hidden
        /// WebView2 + per-widget rect blit). When OFF, <c>LayerCanvas</c> falls
        /// back to its labeled-rectangle placeholder rendering. The Preview
        /// ToggleButton that flipped this was removed from the canvas command
        /// bar, so <see cref="LoadOrDefault"/> coerces the value to
        /// <c>true</c> on load; the property survives only so persisted JSON
        /// that still carries <c>false</c> keeps deserializing.
        /// </summary>
        public bool CanvasWidgetPreviewEnabled { get; set; } = true;

        /// <summary>Embedded preview pane inside <c>WidgetEditorView</c>.</summary>
        public bool EditorEmbeddedPreviewEnabled { get; set; } = true;

        /// <summary>Popout <c>LayerPreviewWindow</c> — keep available even
        /// when the embedded pane is on so authors can pop a big view.</summary>
        public bool EditorPopoutPreviewEnabled { get; set; } = true;

        /// <summary>Width (in CSS pixels) of the floating embedded preview
        /// overlay inside the widget editor. The pane height auto-derives
        /// from the widget's aspect ratio + the resize-grip band height.
        /// Persisted so the user's chosen size carries between sessions.</summary>
        public int EditorPreviewWidth { get; set; } = 480;

        /// <summary>
        /// Absolute path of the <c>.phxlayer</c> that was the selected/open
        /// document when Visualist last had a real (saved) layer loaded.
        /// Restored by the LayerRail on startup so the rail reopens where the
        /// user left off instead of always defaulting to the alphabetically
        /// first file. Null until a saved layer has been opened; a stored path
        /// that no longer exists on disk falls back to the first enumerated
        /// layer. Written verbatim (no validation) — mirrors the rest of this
        /// config's save-as-is contract.
        /// </summary>
        public string? LastLayerPath { get; set; }

        /// <summary>
        /// Persisted width (CSS px) of the layer-file rail column in the shell.
        /// Clamped to the shell's Rail Min/Max on restore, so an out-of-range
        /// stored value can't wedge the column. Written on splitter release.
        /// </summary>
        public double RailWidth { get; set; } = 200;

        /// <summary>
        /// Persisted width (CSS px) of the inspector column in the shell.
        /// Clamped to the shell's Inspector Min/Max on restore. Written on
        /// splitter release.
        /// </summary>
        public double InspectorWidth { get; set; } = 280;

        /// <summary>
        /// Persisted manual height (CSS px) override for the widget-editor
        /// timeline surface. <c>null</c> = auto (height follows the track count).
        /// Set by the timeline's resize handle; clamped to the editor's Min/Resize-Max
        /// on use so it stays sane across widgets with differing track counts.
        /// </summary>
        public double? TimelineHeight { get; set; }

        // ── Singleton + change notification ──────────────────────────────

        // Events aren't serialized by System.Text.Json (they aren't properties),
        // so no annotation is needed — but keep the public surface so subscribers
        // can react to in-process flips of any setting.
        public event Action? OnChanged;

        private static VisualistUserConfig? _instance;
        private static readonly object _gate = new();

        public static VisualistUserConfig Instance
        {
            get
            {
                if (_instance is not null) return _instance;
                lock (_gate)
                {
                    if (_instance is not null) return _instance;
                    _instance = LoadOrDefault();
                }
                return _instance!;
            }
        }

        // Public for unit tests; production code goes through Instance.
        public VisualistUserConfig() { }

        // ── Persistence ──────────────────────────────────────────────────

        public static string DefaultPath() =>
            Path.Combine(Paths.LocalAppData("Visualist"), "visualist.user.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented           = true,
            PropertyNamingPolicy    = null,
            DefaultIgnoreCondition  = JsonIgnoreCondition.Never,
        };

        public static VisualistUserConfig LoadOrDefault(string? path = null)
        {
            string p = path ?? DefaultPath();
            if (!File.Exists(p)) return new VisualistUserConfig();
            try
            {
                string json = File.ReadAllText(p);
                if (string.IsNullOrWhiteSpace(json)) return new VisualistUserConfig();
                var cfg = JsonSerializer.Deserialize<VisualistUserConfig>(json, JsonOpts);
                if (cfg is null) return new VisualistUserConfig();

                // One-shot restore: the layer-canvas "Preview" thumbnail
                // ToggleButton was removed by the fix sweep, but this flag still
                // gates thumbnail rendering — a user who had unticked it before
                // the sweep would be stranded with thumbnails off and no UI to
                // re-enable them. Coerce to ON here, at load, before the value
                // reaches any consumer (Instance only ever hands out configs
                // that passed through this method). The coercion runs on every
                // load and is idempotent — and since no UI can write `false`
                // anymore, it is effectively one-shot and needs no migration
                // flag. The property itself is kept so old JSON carrying
                // `false` still deserializes cleanly before being coerced.
                cfg.CanvasWidgetPreviewEnabled = true;

                return cfg;
            }
            catch (Exception ex)
            {
                // Corrupt or partial file — surface in the log and start fresh.
                // Don't crash the editor over user-config drift.
                GlobalLogger.Error("VisualistUserConfig", $"failed to load '{p}', using defaults", ex);
                return new VisualistUserConfig();
            }
        }

        public void Save(string? path = null)
        {
            string p = path ?? DefaultPath();
            try
            {
                string? dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(this, JsonOpts);
                File.WriteAllText(p, json);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("VisualistUserConfig", $"failed to save '{p}'", ex);
            }
        }

        /// <summary>
        /// Mutate one or more properties through the action and persist + fan
        /// out to subscribers. Keeps every call site terse (<c>Cfg.Update(c =&gt;
        /// c.AutoSyncOnEdit = true)</c>) and ensures we never forget the Save()
        /// or the OnChanged broadcast.
        /// </summary>
        public void Update(Action<VisualistUserConfig> mutate)
        {
            if (mutate is null) return;
            mutate(this);
            Save();
            try { OnChanged?.Invoke(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("VisualistUserConfig", "OnChanged subscriber threw", ex);
            }
        }

        // V14 removed ResetInstanceForTests() — no test ever called it, and it was
        // not a safe hook to leave lying around: nulling _instance abandons every
        // live OnChanged subscriber (VisualistViewModel subscribes in its ctor and
        // unsubscribes in Dispose), so after a reset a still-alive VM would hold a
        // dead config while the next Instance access minted a fresh one whose
        // broadcasts reach nobody. Isolate through Update(...) + restore instead.
    }

    /// <summary>
    /// Preview backdrops. <c>Transparent</c> is the default — the URL carries
    /// no <c>?bg=</c> param, compositor.js paints no backdrop, and the
    /// WebView2's <c>DefaultBackgroundColor</c> is set to transparent so the
    /// hosting panel's BackColor (or whatever sits behind the WebView2)
    /// shows through. The three explicit colours stay available for users
    /// who want a fixed backdrop while authoring semi-transparent widgets.
    /// </summary>
    public enum PreviewBgColor
    {
        // Transparent (0) is retained ONLY so already-persisted configs deserialize;
        // it was removed from the canvas backdrop swatch row (it never rendered a
        // usable design-time backdrop) and the canvas now maps it to Black. Hex (4)
        // replaces it: a hex pasteboard pattern around the layer that highlights the
        // layer bounds ("hex outside, flat inside").
        Transparent = 0,
        Black       = 1,
        Gray        = 2,
        White       = 3,
        Hex         = 4,
    }

    public static class PreviewBgColorExtensions
    {
        /// <summary>The query-string token compositor.js expects, or empty
        /// for <see cref="PreviewBgColor.Transparent"/> (in which case the
        /// caller must skip the <c>&amp;bg=</c> param entirely).</summary>
        public static string ToQueryToken(this PreviewBgColor color) => color switch
        {
            PreviewBgColor.White => "white",
            PreviewBgColor.Gray  => "gray",
            PreviewBgColor.Black => "black",
            _                    => "",
        };

        /// <summary>The CSS / canvas fillStyle compositor.js paints with.</summary>
        public static string ToCssColor(this PreviewBgColor color) => color switch
        {
            PreviewBgColor.White => "#ffffff",
            PreviewBgColor.Gray  => "#808080",
            _                    => "#000000",
        };
    }
}
