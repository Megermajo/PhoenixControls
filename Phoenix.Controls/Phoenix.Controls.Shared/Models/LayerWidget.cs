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

    // Persisted as a LOWER-CASED SINGLE TOKEN via LayerSerializer's
    // JsonStringEnumConverter<WidgetPreset>(LowerCaseNamingPolicy) — "websource",
    // "cc", "alertbox". The NAME is the wire format, not the ordinal, so adding a
    // member anywhere in this list needs no .phxlayer migration. (Contrast
    // SocketDataType in GraphModels.cs, which IS ordinal-persisted and is
    // append-only for that reason. Do not carry that rule over to here — and do
    // not carry this one over to there.)
    //
    // AlertBox (V8) is the first COMPILED preset: its onTrigger graph is generated
    // from AlertBoxSettings rather than hand-authored. See WidgetPresets
    // .CompileAlertBoxGraph / RegenerateAlertBox for the compiler and
    // AlertBoxSettings.Detached for the one-way escape hatch.
    //
    // Player (V15) is the iframe preset — a single Player.Embed sink owning the whole
    // widget rect (a cross-origin iframe cannot be composited against the canvas; see
    // PlayerEmbedSinkNode). It is NOT compiled: it carries no settings model at all, so
    // there is nothing here beside AlertBox's. Its two feeds — the songrequest.* queue
    // and a one-shot Twitch clip — are the node's own Source attribute, not a preset each.
    // ★ Deliberately named Player rather than anything web/embed-ish: WebSource already
    // holds that ground in the gallery ("embed URL") and is a URL-fetched IMAGE source,
    // not an iframe.
    public enum WidgetPreset { Image, Video, Text, Audio, WebSource, Particles, Chat, CC, AlertBox, Player }

    /// <summary>
    /// One per-kind media mapping row of an <see cref="AlertBoxSettings"/> — the
    /// author-facing form of a single <c>String.Select</c> Case/Value pair.
    /// <para>
    /// <see cref="Kind"/> is matched against the alert's <c>Args1</c> field, which
    /// Hub's AlertsService fills with its <c>KindLabel</c> (uppercase, e.g.
    /// <c>"GIFT SUB"</c>). The compare is Ordinal and case-SENSITIVE both in
    /// <c>compositor.js evalStringSelect</c> and in the C# mirror, so the spelling
    /// here has to match the label byte for byte — which is why the canonical set
    /// lives in <see cref="AlertBoxSettings.CanonicalKinds"/> rather than being
    /// typed per widget.
    /// </para>
    /// <para>
    /// A BLANK <see cref="Image"/> / <see cref="Sound"/> means "fall through to the
    /// fallback row" — the compiler simply omits that Case, so nothing matches and
    /// <c>String.Select</c>'s Default is emitted. It does NOT mean "silence for
    /// this kind": that reading would burn a select row per kind on every alert box
    /// and would contradict the node's own rule that an empty Case is an
    /// unconfigured row.
    /// </para>
    /// <para>
    /// Paths are RELATIVE to Hub's <c>data/media/</c>. That is a hard requirement
    /// rather than a convention: V7's media resolver refuses a non-relative path
    /// that arrived over a WIRE (leading <c>/</c>, <c>http(s):</c> or <c>data:</c>),
    /// and every path here reaches the loader over a wire. The Inspector warns on
    /// commit; the compiler normalises separators but does not rewrite the value.
    /// </para>
    /// </summary>
    public sealed class AlertBoxKindRow
    {
        [JsonPropertyName("kind")]  public string Kind  { get; set; } = "";
        [JsonPropertyName("image")] public string Image { get; set; } = "";
        [JsonPropertyName("sound")] public string Sound { get; set; } = "";
    }

    /// <summary>
    /// AlertBoxSettings — the author-facing settings of a <see cref="WidgetPreset.AlertBox"/>
    /// widget, and the SOURCE OF TRUTH for its compiled <c>onTrigger</c> graph.
    ///
    /// <para><b>Ownership (Majo's decision, D-V8).</b> While <see cref="Detached"/> is
    /// false these settings ALWAYS regenerate the graph: every settings commit throws
    /// the previous compiled graph away and rebuilds it. Hand edits to that graph are
    /// therefore lost on the next commit — which is why the Inspector states it in a
    /// persistent banner instead of letting an author discover it. The one-way
    /// <see cref="Detached"/> flag is the sanctioned way out: it converts the widget to
    /// a normal hand-editable graph permanently, and once set nothing regenerates
    /// again.</para>
    ///
    /// <para><b>Why a settings model at all</b> rather than reading the values back out
    /// of the graph: the undo stack snapshots the whole layer through
    /// <c>LayerSerializer.Serialize</c>, so anything not on the model is not undoable,
    /// and a round-trip through the graph would make "what did the author configure"
    /// depend on the compiler still being able to parse its own output. Settings in,
    /// graph out, one direction.</para>
    /// </summary>
    public sealed class AlertBoxSettings
    {
        /// <summary>Default bare trigger identifier; the widget trigger becomes <c>onTrigger:alert</c>.</summary>
        public const string DefaultTriggerId = "alert";

        /// <summary>
        /// Default caption: the acting user, and nothing else. <c>{Args2}</c> is expanded by
        /// <c>Text.Render</c>'s own <c>substituteArgs</c> pass, so the caption needs no wire.
        ///
        /// <para><b>★ Why the size metric is NOT in the default.</b> AlertsService always
        /// populates <c>Args3</c>, and for the kinds with no size metric that value is
        /// <c>"0"</c> — a follow has no size at all, and a fresh sub has no cumulative
        /// months. Because the key IS present, <c>substituteArgs</c> substitutes it happily:
        /// no diagnostic, no fallback, just a literal <c>0</c>. A size-bearing default would
        /// therefore print "someone — 0" on every follow of a default install. Authors who
        /// want it add <c>{Args3}</c> themselves; <see cref="MessageFormatTooltip"/> names the
        /// token and says when it is zero.</para>
        ///
        /// <para>An empty caption is legal — <c>Text.Render</c> returns null for empty text and
        /// <c>Image.Combine</c> is null-tolerant, so the alert renders as image-only.</para>
        /// </summary>
        public const string DefaultMessageFormat = "{Args2}";

        /// <summary>
        /// Author-facing help for the Alert Box <c>message</c> field. Lives here rather than in
        /// the Inspector's XAML so the "the default caption must not use the size token, and the
        /// author must still be told the token's name" contract is assertable in one place —
        /// the Inspector sets this string onto the box imperatively.
        /// </summary>
        public const string MessageFormatTooltip =
            "Caption drawn over the alert image. {Args1} = the kind label (FOLLOW / RAID / …), "
          + "{Args2} = the acting user, {Args3} = the size metric — bits, cumulative months, "
          + "raid viewers or tip amount. {Args3} is 0 for kinds that have no size (a follow, or "
          + "a sub with no month count), which is why it is not in the default. "
          + "Leave empty for no caption.";

        /// <summary>
        /// The eventData field carrying the alert kind. AlertsService writes
        /// <c>Args1 = KindLabel(family)</c>, <c>Args2 = user</c>, <c>Args3 = size</c>.
        /// </summary>
        public const string KindArgKey = "Args1";

        /// <summary>
        /// The alert kind labels a compiled Alert Box maps, in the order the rows are
        /// authored and compiled.
        ///
        /// <para><b>★ This list MIRRORS <c>AlertsService.KindLabel</c> and must stay in
        /// step with it.</b> It cannot reference it: <c>KindLabel</c> lives in
        /// <c>Phoenix.Controls.Hub</c>, which references Shared, so a reference back
        /// would invert the dependency direction. The duplication is guarded by a
        /// reflection-based drift test (<c>AlertBoxPresetTests</c>) that walks Hub's real
        /// <c>AlertFamily</c> and fails the moment a family produces a label this list
        /// does not contain — which is the failure mode that matters, because a
        /// mislabelled row does not error, it silently renders the fallback alert
        /// forever.</para>
        ///
        /// <para><b>★ This list is a SUPERSET of what this branch's Hub can emit, on
        /// purpose.</b> On <c>campaign/visualist-rework</c> (branched before Box C landed)
        /// <c>AlertFamily</c> has six members — Follow / Sub / GiftSub / GiftBomb / Cheer /
        /// Raid — and <c>KindLabel</c>'s last arm is <c>_ => "RAID"</c>. Box C's C1 + C20
        /// add Charity / Shoutout / WatchStreak / Tip and turn that arm into an
        /// <c>"ALERT"</c> catch-all. Authoring all ten NOW means the widget needs no second
        /// pass after the merge, and it is safe in both directions because the drift test
        /// asserts CONTAINMENT (every family's label appears here), never equality: four
        /// rows that no family can currently reach are inert, whereas a family with no row
        /// silently renders the fallback forever.</para>
        ///
        /// <para>The catch-all arm — <c>RAID</c> here, <c>ALERT</c> after C20 — is exactly
        /// why the compiled <c>String.Select</c> must carry a Default row
        /// (<see cref="FallbackImage"/> / <see cref="FallbackSound"/>): a select with no
        /// default renders nothing at all for a kind nobody mapped.</para>
        /// </summary>
        public static readonly string[] CanonicalKinds =
        {
            "FOLLOW",
            "SUBSCRIPTION",
            "GIFT SUB",
            "GIFT SUBS",
            "BITS",
            "RAID",
            "CHARITY",
            "SHOUTOUT",
            "WATCH STREAK",
            "TIP",
        };

        /// <summary>
        /// Bare trigger identifier. The compiled graph lands on the widget trigger
        /// <c>onTrigger:&lt;TriggerId&gt;</c> and this is the value the streamer types
        /// into the Alerts tool's per-tier "Trigger Name" field. Hub's
        /// <c>WidgetOwnsTrigger</c> matches BOTH spellings (bare <c>alert</c> and full
        /// <c>onTrigger:alert</c>), so the tool can be configured either way.
        /// </summary>
        [JsonPropertyName("trigger")] public string TriggerId { get; set; } = DefaultTriggerId;

        [JsonPropertyName("message")] public string MessageFormat { get; set; } = DefaultMessageFormat;

        /// <summary>
        /// How long the alert stays on screen, in milliseconds — written to the compiled
        /// trigger's <c>WidgetTimeline.DurationMs</c>, which is what both halves of the
        /// hold actually read: compositor.js clamps it to <c>[2000, 60000]</c> before
        /// holding the trigger, and Hub derives the queue's per-invocation completion
        /// timeout from the same number. Default 4 s.
        ///
        /// <para>The alert's VOLUME is deliberately NOT duplicated here — the TRIGGERS
        /// section already exposes <c>WidgetTrigger.Volume</c> as a per-trigger master and
        /// regeneration leaves it alone, so there is exactly one place to set it.</para>
        /// </summary>
        [JsonPropertyName("durationMs")] public double DurationMs { get; set; } = 4000;

        /// <summary>Default-row image: what an unmapped or blank-mapped kind shows. Relative to <c>data/media/</c>.</summary>
        [JsonPropertyName("fallbackImage")] public string FallbackImage { get; set; } = "";

        /// <summary>Default-row sound. Relative to <c>data/media/</c>.</summary>
        [JsonPropertyName("fallbackSound")] public string FallbackSound { get; set; } = "";

        /// <summary>
        /// Per-kind media rows. The setter HEALS an explicit JSON <c>null</c> back to an
        /// empty list — same philosophy as <c>LayerSerializer.Deserialize</c>'s
        /// <c>layer.Widgets ??= new()</c> and <c>GraphSerializer.MigrateNodes</c>: a field
        /// initializer does not survive a literal <c>"rows": null</c>, and every reader
        /// here (compiler, Inspector, drift test) would then have to defend individually.
        /// Heal once, at the only place the value can enter.
        /// </summary>
        [JsonPropertyName("rows")]
        public List<AlertBoxKindRow> Rows
        {
            get => _rows;
            set => _rows = value ?? new List<AlertBoxKindRow>();
        }
        private List<AlertBoxKindRow> _rows = new();

        /// <summary>
        /// ONE-WAY. Once true the widget's graph is hand-owned and
        /// <c>WidgetPresets.RegenerateAlertBox</c> refuses to touch it — for good. The
        /// settings are kept (read-only) so the widget still reads as "this was an Alert
        /// Box", and no re-attach path exists by design: re-attaching would silently
        /// overwrite the hand edits the detach existed to protect.
        /// </summary>
        [JsonPropertyName("detached")] public bool Detached { get; set; }

        /// <summary>
        /// The widget-trigger name the LAST regeneration installed onto, or <c>""</c> when
        /// nothing has been compiled yet.
        ///
        /// <para><b>★ Why this is persisted rather than recomputed from
        /// <see cref="TriggerId"/>:</b> the compiled trigger's NAME is derived from
        /// <c>TriggerId</c>, so the moment an author renames the id the previous trigger
        /// becomes unreachable from the settings — and it is still sitting on the widget
        /// carrying a full alert chain. Hub's <c>FireVisualTriggerFanOutAsync</c> matches
        /// per widget by trigger name, so the orphan does not merely waste space: whichever
        /// name the Alerts tool is still configured with keeps firing, and the author sees
        /// their rename apparently ignored, with two alert graphs on one widget and no
        /// error anywhere. Recording what we actually installed is the only way
        /// regeneration can clean up after a rename without guessing which triggers it
        /// owns (a heuristic over graph shape would eventually eat a hand-authored
        /// trigger, which is strictly worse).</para>
        ///
        /// <para><b>★ It is also the OWNERSHIP token on the way in.</b>
        /// <c>WidgetPresets.RegenerateAlertBox</c> refuses to write into an existing trigger
        /// unless this field names it (or that trigger has no nodes at all). Without that check
        /// the compiler would adopt a hand-authored <c>onTrigger:alert</c> on the first preset
        /// pick, overwrite it, record it here — and then delete it on the next id rename.</para>
        ///
        /// <para>Round-trips through <c>LayerSerializer</c> like every other settings
        /// field, so it survives save/load and the undo stack.</para>
        /// </summary>
        [JsonPropertyName("compiledTrigger")] public string CompiledTriggerName { get; set; } = "";

        /// <summary>A fresh settings object with one blank row per canonical kind.</summary>
        public static AlertBoxSettings CreateDefault()
        {
            var s = new AlertBoxSettings();
            s.EnsureCanonicalKinds();
            return s;
        }

        /// <summary>
        /// Append a blank row for every canonical kind not already present. Additive and
        /// idempotent: existing rows keep their order, their media and their spelling,
        /// and author-invented kinds (a custom <c>Visual.Trigger</c> whose Args1 is not
        /// an AlertsService label) survive untouched.
        ///
        /// <para>Called from the regeneration entry point rather than at load time so a
        /// widened <c>KindLabel</c> surfaces the new kind's row the first time the author
        /// touches the widget — no .phxlayer migration, no silent gap where the family
        /// fires and there is nowhere to configure it.</para>
        ///
        /// <para>Returns the number of rows appended.</para>
        /// </summary>
        public int EnsureCanonicalKinds()
        {
            Rows ??= new List<AlertBoxKindRow>();
            int added = 0;
            foreach (string kind in CanonicalKinds)
            {
                bool present = false;
                foreach (AlertBoxKindRow r in Rows)
                {
                    if (r is null) continue;
                    if (string.Equals(r.Kind, kind, StringComparison.Ordinal)) { present = true; break; }
                }
                if (present) continue;
                Rows.Add(new AlertBoxKindRow { Kind = kind });
                added++;
            }
            return added;
        }

        /// <summary>
        /// The widget-trigger name the compiled graph installs onto, or <c>null</c> when
        /// <see cref="TriggerId"/> cannot make a valid one.
        ///
        /// <para><b>★ Why this returns null instead of falling back to a default:</b>
        /// <c>WidgetTrigger.Name</c>'s setter SILENTLY NO-OPS on an invalid name and keeps
        /// its previous value — which for a freshly constructed trigger is
        /// <c>"onStartup"</c>. So an unvalidated id would not produce a broken alert
        /// trigger, it would produce a SECOND onStartup that overwrites the widget's idle
        /// graph. Refusing to compile is the only safe answer; the caller logs and bails.
        /// </para>
        /// </summary>
        [JsonIgnore]
        public string? ResolvedTriggerName
        {
            get
            {
                string id = (TriggerId ?? "").Trim();
                if (id.Length == 0) id = DefaultTriggerId;
                string name = "onTrigger:" + id;
                return WidgetTrigger.IsValidName(name) ? name : null;
            }
        }

        /// <summary>True when <paramref name="id"/> is a legal bare trigger identifier.</summary>
        public static bool IsValidTriggerId(string? id)
            => WidgetTrigger.IsValidName("onTrigger:" + ((id ?? "").Trim().Length == 0 ? DefaultTriggerId : (id ?? "").Trim()));

        /// <summary>
        /// Mirror of <c>compositor.js isNonRelativeMediaPath</c> and of
        /// <c>NodeEvaluator.IsNonRelativeMediaPath</c>: a leading <c>/</c>, an
        /// <c>http(s):</c> URL, or a <c>data:</c> URI.
        ///
        /// <para><b>Why a third copy.</b> The engine's twin is <c>private</c> and lives in
        /// <c>Phoenix.Controls.Visualist.Engine</c>, which references Shared — so this type
        /// cannot call it without inverting the dependency. Matching the ENGINE's spelling
        /// (case-insensitive on <c>data:</c>, where the JS is case-sensitive) is deliberate:
        /// this predicate only ever drives an author-facing WARNING, and a warning that is
        /// marginally stricter than the runtime guard can mislabel nothing that would
        /// actually have played — it can only mention one extra pathological spelling.</para>
        ///
        /// <para><b>Why an Alert Box has to care.</b> V7's media resolver is
        /// PROVENANCE-aware: an author who types an absolute path or a CDN URL into a
        /// Path *attribute* keeps working, but a path that arrives over a WIRE and is
        /// non-relative is REFUSED and the loader gets <c>""</c> — because the wired case
        /// is exactly the one a viewer can reach (`!sound https://attacker/x.mp3` →
        /// Args1 → Visual.Arg → String.Select → Audio.Load.Path → the streamer's OBS
        /// fetches an attacker-named URL). A compiled Alert Box wires EVERY one of its
        /// paths, so for this widget the wired rule is the only rule that applies.</para>
        ///
        /// <para>The consequence for the author is silence: a rejected path plays nothing
        /// and shows nothing, with the only trace a browser-side diagnostic they are not
        /// watching. So the Inspector warns at commit time instead — see
        /// <see cref="DescribeMediaPathProblem"/>.</para>
        /// </summary>
        public static bool IsNonRelativeMediaPath(string? path)
        {
            string p = path ?? "";
            if (p.Length == 0) return false;
            if (p[0] == '/') return true;
            if (p.StartsWith("http:",  StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith("data:",  StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// An author-facing reason this media path will not play from a compiled Alert Box,
        /// or <c>null</c> when it looks fine. Blank is fine (an unmapped row).
        ///
        /// <para>TWO distinct failures, deliberately reported by one call because the
        /// author cannot tell them apart from the overlay:</para>
        /// <list type="number">
        /// <item><see cref="IsNonRelativeMediaPath"/> — REFUSED by the wired-path guard.</item>
        /// <item>A Windows-shaped path (<c>C:\…</c>, or any backslash). The guard does NOT
        /// reject these — they do not start with <c>/</c> and carry no scheme — so they
        /// sail through and are then URL-encoded into <c>/media/C%3A%5C…</c>, which 404s.
        /// That is the WORSE of the two: no diagnostic is emitted at all, because from the
        /// resolver's point of view nothing was wrong.</item>
        /// </list>
        /// </summary>
        public static string? DescribeMediaPathProblem(string? path)
        {
            string p = (path ?? "").Trim();
            if (p.Length == 0) return null;
            if (IsNonRelativeMediaPath(p))
                return "must be a path relative to data/media — absolute paths, http(s): URLs "
                     + "and data: URIs are refused on a wired path (an Alert Box wires all of them)";
            if (p.IndexOf('\\') >= 0 || (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0])))
                return "looks like a Windows file path — use a path relative to data/media "
                     + "(e.g. alerts/follow.png); a drive path resolves to a 404 with no diagnostic";
            return null;
        }
    }

    public sealed class LayerWidget
    {
        [JsonPropertyName("id")]        public string                Id        { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("name")]      public string                Name      { get; set; } = "";
        [JsonPropertyName("rect")]      public WidgetRect            Rect      { get; set; } = new();
        [JsonPropertyName("zIndex")]    public int                   ZIndex    { get; set; }
        [JsonPropertyName("preset")]    public WidgetPreset?         Preset    { get; set; }
        // Base64 PNG payload
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

        /// <summary>
        /// Alert Box settings — present only on a <see cref="WidgetPreset.AlertBox"/>
        /// widget, null everywhere else, so an existing .phxlayer round-trips with no new
        /// key (<c>WhenWritingNull</c>).
        ///
        /// <para>These settings OWN the widget's <c>onTrigger:&lt;id&gt;</c> graph while
        /// <c>AlertBoxSettings.Detached</c> is false — see that type's remarks. Widget
        /// duplication and the undo stack both deep-clone through
        /// <c>LayerSerializer</c>, so this rides along automatically; there is no
        /// hand-written clone path to keep in step.</para>
        /// </summary>
        [JsonPropertyName("alertBox")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AlertBoxSettings? AlertBox { get; set; }

        [JsonPropertyName("triggers")]  public List<WidgetTrigger>   Triggers  { get; set; } = new();
    }
}
