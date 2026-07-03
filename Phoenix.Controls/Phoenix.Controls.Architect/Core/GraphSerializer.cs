using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Serializes System.Drawing.Color as a single ARGB integer so it survives a
    /// System.Text.Json round-trip (all Color properties are read-only getters and
    /// cannot be set by the default deserializer, which would silently yield Color.Empty).
    /// </summary>
    /// <remarks>
    /// The writer for the legacy <c>{"R":…,"G":…,"B":…,"A":…}</c>
    /// object form was dropped, but the READER stays lenient — a 0.9.7-era report from Majo
    /// surfaced legacy <c>.phxg</c> files where every socket loaded with
    /// <see cref="Color.Empty"/> (the Read returned Empty for
    /// any non-Number token), which then cascaded through
    /// <c>NodeRegistry.DataTypeFromColorPublic</c> → <c>SocketDataType.Any</c>
    /// → wires repainted as the default gray "Any" pipe → user-visible
    /// "piping not taken over properly" symptom. Reading the legacy form is
    /// non-destructive (the next Save writes back the new integer form), so
    /// restoring the reader path is a one-way migration with no risk to the
    /// canonical wire format.
    /// </remarks>
    internal sealed class ColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return Color.FromArgb(reader.GetInt32());

            // Hex-string form — supports hand-authored `.phxg` payloads
            // that use `"Color": "#FF8800"` or `"#AARRGGBB"`. Previously the
            // reader fell through to `reader.Skip()` and the socket loaded as
            // Color.Empty, then degraded to SocketDataType.Any during
            // MigrateNodes — same symptom as the legacy {R,G,B,A} regression.
            if (reader.TokenType == JsonTokenType.String)
            {
                string s = reader.GetString() ?? string.Empty;
                if (TryParseHexColor(s, out var parsed)) return parsed;
                // A non-empty string that isn't a
                // valid #RRGGBB/#AARRGGBB hex degrades the socket to Color.Empty
                // → SocketDataType.Any during MigrateNodes, silently changing the
                // socket's type. Log the parse failure (rare hand-edit typo) so
                // it isn't invisible. Empty/blank strings are left unlogged.
                if (!string.IsNullOrWhiteSpace(s))
                    GlobalLogger.Log(
                        $"Socket color '{s}' is not a valid #RRGGBB/#AARRGGBB hex — socket loads untyped (Any).",
                        "GraphSerializer", LogLevel.Communication);
                return Color.Empty;
            }

            // Legacy {R,G,B,A} object form — used by older .phxg files.
            // Without this branch the reader returned Color.Empty and every
            // downstream socket palette degraded to "Any" gray.
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                int a = 255, r = 0, g = 0, b = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var name = reader.GetString();
                    reader.Read();
                    int v = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : 0;
                    switch (name)
                    {
                        case "A": case "a": a = v; break;
                        case "R": case "r": r = v; break;
                        case "G": case "g": g = v; break;
                        case "B": case "b": b = v; break;
                    }
                }
                return Color.FromArgb(a, r, g, b);
            }

            reader.Skip();
            return Color.Empty;
        }

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.ToArgb());

        /// <summary>
        /// Parse a CSS-style <c>#RRGGBB</c> or <c>#AARRGGBB</c> hex string into
        /// a <see cref="Color"/>. Accepts an optional leading '#'; rejects any
        /// other length or non-hex character. Six-digit form assumes alpha=255.
        /// </summary>
        private static bool TryParseHexColor(string s, out Color color)
        {
            color = Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            ReadOnlySpan<char> span = s.AsSpan();
            if (span[0] == '#') span = span[1..];
            if (span.Length != 6 && span.Length != 8) return false;

            int a = 255, r, g, b;
            int idx = 0;
            if (span.Length == 8)
            {
                if (!TryParseHexByte(span.Slice(idx, 2), out a)) return false; idx += 2;
            }
            if (!TryParseHexByte(span.Slice(idx, 2), out r)) return false; idx += 2;
            if (!TryParseHexByte(span.Slice(idx, 2), out g)) return false; idx += 2;
            if (!TryParseHexByte(span.Slice(idx, 2), out b)) return false;
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        private static bool TryParseHexByte(ReadOnlySpan<char> two, out int value)
            => int.TryParse(two, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public static class GraphSerializer
    {
        // Forward-compatibility surface for the .phxg JSON wire format.
        // Bump this constant and add a migration step in MigrateNodes (keyed off
        // the loaded version) when the schema changes in a non-trivial way. The
        // model class (Graph) intentionally has no Version field so the
        // Shared assembly stays version-agnostic; the version lives at the
        // serializer layer as a top-level JSON property "_schemaVersion".
        private const int CurrentSchemaVersion = 1;
        private const string SchemaVersionPropertyName = "_schemaVersion";

        /// <summary>
        /// Fired when LoadGraph fails to parse a .phxg file. Subscribers (MainView)
        /// surface a ContentDialog so the user knows their file is malformed and the
        /// returned empty graph isn't a silent data loss event waiting to be saved
        /// over the broken file. Args: (filePath, exception).
        /// </summary>
        public static event Action<string, Exception>? OnLoadFailed;

        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters    = { new ColorJsonConverter() }
        };

        public static async Task SaveGraphAsync(Graph graph, string filePath)
        {
            // Refuse to overwrite a file that was loaded from a future
            // schema version. Without this gate, any autosave/explicit-save on a
            // read-only placeholder would silently clobber the newer .phxg with
            // the empty placeholder payload. Surface via GlobalLogger (callers
            // also see the throw) — UI layers may attach a one-shot dialog at
            // their level for the canvas-paint case if desired.
            if (graph?.IsReadOnly == true)
            {
                var ex = new InvalidOperationException(
                    $"Refusing to save '{Path.GetFileName(filePath)}': graph was loaded read-only " +
                    "(forward-compat schema). Open the file in a newer Architect or discard the buffer.");
                GlobalLogger.Error("GraphSerializer", ex.Message, ex);
                throw ex;
            }

            try
            {
                // Offload the synchronous JSON serialize + rotate work
                // to the ThreadPool so a large graph doesn't hitch the UI thread.
                // The previous shape did SerializeToNode + ToJsonString + the
                // rotation chain inline; only the final File.WriteAllTextAsync
                // was awaited. For sizeable .phxg files that left the UI frozen
                // long enough to drop frames during autosave.
                string tempPath = filePath + ".tmp";
                string json     = await Task.Run(() =>
                {
                    // Serialize to a JsonNode so we can inject "_schemaVersion" at the
                    // top level without modifying Graph (model lives in Shared
                    // and ripples across the whole suite).
                    var node = JsonSerializer.SerializeToNode(graph, _options) as JsonObject
                               ?? new JsonObject();
                    node[SchemaVersionPropertyName] = CurrentSchemaVersion;
                    string s = node.ToJsonString(_options);

                    // Three-slot rolling backup chain.
                    // Rotate BEFORE the destination is overwritten by the new content
                    // so the pyramid pushes down by one. Best-effort: a failed rotation
                    // only logs (the foreground save still proceeds because the user's
                    // intent is to write the new content).
                    RotateBackupChain(filePath);
                    return s;
                }).ConfigureAwait(false);

                bool tempWritten = false;
                bool moved       = false;
                try
                {
                    // Atomic temp-then-rename so a power loss / crash mid-write
                    // can't truncate the destination .phxg. The .phxg file feeds
                    // the LogicWatcher → Hub script engine; a truncated graph
                    // would silently broadcast a half-baked program to Hub.
                    await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
                    tempWritten = true;
                    File.Move(tempPath, filePath, overwrite: true);
                    moved = true;
                }
                finally
                {
                    // If the Move never fired but the .tmp landed (e.g.
                    // the WriteAllTextAsync succeeded and then Move threw because
                    // the destination was locked by another process), the .tmp
                    // would otherwise linger on disk forever — clutter for the
                    // user, no recovery value (the same write will succeed next
                    // Save). Only clean up the .tmp when the Move didn't fire;
                    // a successful Move consumes the .tmp by definition.
                    if (tempWritten && !moved)
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                        catch { /* best-effort cleanup */ }
                    }
                }
            }
            catch (Exception ex)
            {
                // Mirror LoadGraph's logging shape and rethrow so call sites see
                // the failure. SaveCommand wraps this in a try/catch + MessageBox
                // already; fire-and-forget call sites attach an OnlyOnFaulted
                // continuation that routes through GlobalLogger.Error too.
                GlobalLogger.Error("GraphSerializer", $"SaveGraph failed for {Path.GetFileName(filePath)}", ex);
                throw;
            }
        }

        /// <summary>
        /// Async equivalent of <see cref="LoadGraph"/>. Offloads the entire load
        /// pipeline (file read, schema-version inspection, NormalizeAttributesInJson
        /// rewrite, JsonSerializer.Deserialize, MigrateNodes) to a thread-pool
        /// worker so the UI thread is free during file open. Same return shape
        /// and same OnLoadFailed semantics — wraps the synchronous path so
        /// non-UI callers (EventPairCrossFileSync) get the same behaviour.
        ///
        /// PERF: previously ArchitectViewModel.Open
        /// called LoadGraph synchronously on the UI thread; for a sizeable .phxg
        /// that was hundreds of ms of canvas freeze every open.
        /// </summary>
        public static Task<Graph> LoadGraphAsync(string filePath)
            => Task.Run(() => LoadGraph(filePath));

        public static Graph LoadGraph(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return new Graph();
                string json = File.ReadAllText(filePath);

                // Inspect the top-level "_schemaVersion" before binding to the
                // model. JsonNode lets us peek without losing the original payload.
                int loadedVersion = 0; // 0 = legacy (field absent)
                bool hasVersionField = false;
                try
                {
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && root.TryGetPropertyValue(SchemaVersionPropertyName, out var versionNode)
                                     && versionNode is JsonValue v
                                     && v.TryGetValue(out int parsed))
                    {
                        // A malformed / negative
                        // _schemaVersion would otherwise slip past the upper-bound
                        // check below (parsed > Current is false for negatives) and
                        // mis-route migration as if it were a known legacy version.
                        // Clamp negatives to legacy (0) + warn; overflow values that
                        // don't fit int already fail TryGetValue and stay legacy.
                        if (parsed < 0)
                        {
                            GlobalLogger.Log(
                                $"Graph '{Path.GetFileName(filePath)}' has invalid {SchemaVersionPropertyName}={parsed} — treating as legacy (0).",
                                "GraphSerializer", LogLevel.Communication);
                            loadedVersion   = 0;
                            hasVersionField = false;
                        }
                        else
                        {
                            loadedVersion   = parsed;
                            hasVersionField = true;
                        }
                    }
                }
                catch
                {
                    // Malformed JSON — fall through to the binder below, which will
                    // raise the same error and end up in the outer catch.
                }

                if (loadedVersion > CurrentSchemaVersion)
                {
                    // The previous "return new Graph()" path silently
                    // dropped the user back to an empty canvas; the next autosave
                    // would then overwrite the future-format .phxg with the empty
                    // payload, destroying the newer file. Returning a placeholder
                    // tagged IsReadOnly lets the canvas display the "this file is
                    // too new" state while SaveGraphAsync refuses to write.
                    GlobalLogger.Error(
                        "GraphSerializer",
                        $"Graph schema version {loadedVersion} exceeds known version {CurrentSchemaVersion} — opened read-only (file: {Path.GetFileName(filePath)})");
                    return new Graph
                    {
                        Name = $"[read-only — newer schema v{loadedVersion}] {Path.GetFileNameWithoutExtension(filePath)}",
                        IsReadOnly = true
                    };
                }

                // Normalize node `Attributes` payloads BEFORE binding.
                // Three sub-bugs handled at the load boundary so call sites don't need
                // defensive code:
                //   * JSON int/float/bool/null values for what the model declares as
                //     a Dictionary<string,string> — the binder would reject those, or
                //     downstream `int.TryParse(attr, ...)` would fail when the same
                //     value was authored as bare-number vs. quoted-string.
                //   * Duplicate keys — System.Text.Json silently keeps the last value;
                //     authors deserve a log so they can correct the .phxg.
                //   * Present-but-null entries — a JSON `"Foo": null` survives parse
                //     as a string-null (TryGetValue returns true with raw == null),
                //     which breaks the `out var raw` assumption nearly every reader
                //     makes.
                //
                // PERF NOTE — an earlier attempt skipped this step when
                // hasVersionField && loadedVersion == CurrentSchemaVersion, on the
                // theory that schema-v1 files always came from JsonSerializer.Serialize.
                // BugFixSweep18_GraphHardening_Tests.B13_Load_NumericAttribute_NormalizesToString
                // proves the assumption wrong: a hand-authored v1 .phxg can contain
                // bare numeric attribute values, and the contract is that the loader
                // coerces them to strings regardless of schema version. Keep the
                // normalization unconditional.
                json = NormalizeAttributesInJson(json, Path.GetFileName(filePath));

                var graph = JsonSerializer.Deserialize<Graph>(json, _options) ?? new Graph();

                if (!hasVersionField)
                {
                    GlobalLogger.Log(
                        "Loading legacy graph (no schema version) — running migration",
                        "GraphSerializer",
                        LogLevel.Communication);
                }
                // Always run MigrateNodes for any known version (≤ current). It is
                // idempotent and load-bearing for several callers — Save/Load
                // round-trips, attribute-key normalization ("Case A"),
                // and post-edit dangling-link pruning all depend on it
                // executing on every Load. The version field provides
                // forward-compat refusal (above) — not an excuse to skip migration.
                //
                // Recurse into nested macros + processes-in-processes.
                // Pre-fix the LoadGraph path stopped at the top-level Macros / Processes
                // lists — a macro defined inside another macro (or a process inside a
                // process) silently skipped migration entirely. Walk the tree so every
                // embedded Graph instance gets the same migration pass.
                MigrateNodesRecursive(graph);

                // Single invocation of the wildcard cascade here so freshly
                // loaded graphs always pick up Reroute → wildcard propagation without
                // depending on the canvas, ViewModel, or sub-graph windows to remember
                // to call it. Cascade is idempotent, so callers that still invoke it
                // (Canvas / sub-graph editor / clipboard) remain no-ops.
                NodeRegistry.ResolveWildcardCascade(graph);

                return graph;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("GraphSerializer", $"LoadGraph failed for {Path.GetFileName(filePath)}", ex);
                // Notify any UI listener so the user sees the failure rather than
                // silently receiving an empty graph that could overwrite the broken
                // .phxg on next save. Listener exceptions are swallowed here so a
                // bad subscriber can't promote a parse error to a process crash.
                try { OnLoadFailed?.Invoke(filePath, ex); } catch { }
                return new Graph();
            }
        }

        /// <summary>
        /// Pre-binding JSON normalization for node `Attributes` payloads.
        ///
        /// Walks the raw JSON via <see cref="Utf8JsonReader"/> and emits a rewritten JSON
        /// stream via <see cref="Utf8JsonWriter"/>. Inside each <c>Nodes[*].Attributes</c>
        /// object:
        /// <list type="bullet">
        ///   <item>Numeric / bool values are coerced to canonical invariant strings.</item>
        ///   <item>Null values become <c>""</c> (preserves the <c>TryGetValue</c> contract).</item>
        ///   <item>Object/array values are JSON-stringified + a Communication warning is logged.</item>
        ///   <item>Duplicate keys are dropped (last-wins) + a Communication warning is logged.</item>
        /// </list>
        /// Outside Attributes objects: passthrough verbatim.
        ///
        /// Why a hand-rolled walker instead of <see cref="JsonNode"/>: <c>JsonObject</c>
        /// preserves duplicate-key tokens at parse time but throws
        /// <see cref="ArgumentException"/> the moment a dictionary view is materialized
        /// (enumeration, indexer access on the duplicate). The reader/writer pass treats
        /// the duplicates as a stream and decides what to keep without ever building a
        /// dictionary view.
        /// </summary>
        internal static string NormalizeAttributesInJson(string json, string fileNameForLog)
        {
            byte[] inputBytes;
            try { inputBytes = System.Text.Encoding.UTF8.GetBytes(json); }
            catch { return json; }

            using var output = new MemoryStream(inputBytes.Length);
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            {
                var reader = new Utf8JsonReader(inputBytes);

                // State for "are we inside a Nodes[*].Attributes object?". When inside,
                // we BUFFER the canonicalized (key → value) pairs into a Dictionary
                // (which is last-wins by indexer assignment) instead of writing through
                // to the Utf8JsonWriter. On EndObject we flush the buffer in-order. This
                // is what makes duplicate-key behavior last-wins to match standard JSON.
                int attrsDepth = -1;
                Dictionary<string, string>? attrsBuffer = null;
                List<string>? attrsKeyOrder = null;
                HashSet<string>? attrsDupesLogged = null;
                string? activeNodeTitle = null;
                string? pendingAttrKey = null;
                var nameStack = new Stack<string>();

                try
                {
                    while (reader.Read())
                    {
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.StartObject:
                            {
                                if (nameStack.Count > 0 && nameStack.Peek() == "Attributes")
                                {
                                    // Enter Attributes-buffering mode.
                                    attrsDepth = reader.CurrentDepth;
                                    attrsBuffer = new Dictionary<string, string>(StringComparer.Ordinal);
                                    attrsKeyOrder = new List<string>();
                                    attrsDupesLogged = new HashSet<string>(StringComparer.Ordinal);
                                    writer.WriteStartObject();
                                }
                                else
                                {
                                    writer.WriteStartObject();
                                }
                                break;
                            }

                            case JsonTokenType.EndObject:
                            {
                                if (attrsBuffer != null && reader.CurrentDepth == attrsDepth)
                                {
                                    // Flush the buffered (key, last-wins value) pairs in
                                    // first-seen-key order. Keys re-asserted later updated
                                    // their value via the dictionary indexer.
                                    foreach (var key in attrsKeyOrder!)
                                    {
                                        writer.WritePropertyName(key);
                                        writer.WriteStringValue(attrsBuffer[key]);
                                    }
                                    attrsBuffer = null;
                                    attrsKeyOrder = null;
                                    attrsDupesLogged = null;
                                    attrsDepth = -1;
                                    pendingAttrKey = null;
                                }
                                writer.WriteEndObject();
                                if (nameStack.Count > 0) nameStack.Pop();
                                break;
                            }

                            case JsonTokenType.StartArray:
                                writer.WriteStartArray();
                                break;

                            case JsonTokenType.EndArray:
                                writer.WriteEndArray();
                                if (nameStack.Count > 0) nameStack.Pop();
                                break;

                            case JsonTokenType.PropertyName:
                            {
                                string name = reader.GetString() ?? "";
                                nameStack.Push(name);

                                if (attrsBuffer != null && reader.CurrentDepth == attrsDepth + 1)
                                {
                                    // Key inside Attributes: stage for buffering. Don't
                                    // emit yet — the value token below populates the
                                    // buffer, which is flushed at EndObject.
                                    if (attrsBuffer.ContainsKey(name) && attrsDupesLogged!.Add(name))
                                    {
                                        GlobalLogger.Log(
                                            $"GraphSerializer ({fileNameForLog}): duplicate attribute key '{name}' on node '{activeNodeTitle ?? "(unknown)"}' — last-wins applied.",
                                            "GraphSerializer",
                                            LogLevel.Communication);
                                    }
                                    if (!attrsBuffer.ContainsKey(name))
                                        attrsKeyOrder!.Add(name);
                                    pendingAttrKey = name;
                                }
                                else
                                {
                                    writer.WritePropertyName(name);
                                }
                                break;
                            }

                            case JsonTokenType.String:
                            {
                                string s = reader.GetString() ?? string.Empty;
                                if (nameStack.Count > 0 && nameStack.Peek() == "Title" &&
                                    attrsBuffer == null)
                                {
                                    activeNodeTitle = s;
                                }
                                if (pendingAttrKey != null && attrsBuffer != null)
                                {
                                    attrsBuffer[pendingAttrKey] = s;
                                }
                                else
                                {
                                    writer.WriteStringValue(s);
                                }
                                if (nameStack.Count > 0) nameStack.Pop();
                                pendingAttrKey = null;
                                break;
                            }

                            case JsonTokenType.Number:
                            {
                                if (pendingAttrKey != null && attrsBuffer != null)
                                {
                                    // Coerce numeric to invariant-string form for
                                    // the string-only Dictionary<string,string> model.
                                    string s;
                                    if (reader.TryGetInt64(out long ln))
                                        s = ln.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    else if (reader.TryGetDouble(out double dn))
                                        s = dn.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    else
                                        s = reader.GetString() ?? "0";
                                    attrsBuffer[pendingAttrKey] = s;
                                }
                                else
                                {
                                    // Outside Attributes — pass through.
                                    if (reader.TryGetInt64(out long ln)) writer.WriteNumberValue(ln);
                                    else if (reader.TryGetDouble(out double dn)) writer.WriteNumberValue(dn);
                                    else writer.WriteRawValue(reader.GetString() ?? "0");
                                }
                                if (nameStack.Count > 0) nameStack.Pop();
                                pendingAttrKey = null;
                                break;
                            }

                            case JsonTokenType.True:
                            case JsonTokenType.False:
                            {
                                bool b = reader.TokenType == JsonTokenType.True;
                                if (pendingAttrKey != null && attrsBuffer != null)
                                    attrsBuffer[pendingAttrKey] = b ? "true" : "false";
                                else
                                    writer.WriteBooleanValue(b);
                                if (nameStack.Count > 0) nameStack.Pop();
                                pendingAttrKey = null;
                                break;
                            }

                            case JsonTokenType.Null:
                            {
                                if (pendingAttrKey != null && attrsBuffer != null)
                                {
                                    // Present-but-null becomes empty string so the
                                    // `out var raw` contract every existing reader uses
                                    // never sees null.
                                    attrsBuffer[pendingAttrKey] = string.Empty;
                                }
                                else
                                {
                                    writer.WriteNullValue();
                                }
                                if (nameStack.Count > 0) nameStack.Pop();
                                pendingAttrKey = null;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Graceful degradation: a normalization failure here
                    // must NOT take the whole graph down. Re-throwing propagated to
                    // LoadGraph's outer catch, which returns an empty Graph() — losing
                    // every node/link in the file, with the risk that the user then
                    // saves over the (recoverable) .phxg with the empty one. Instead,
                    // log the failure with the original exception attached and return
                    // the un-normalized JSON so the graph still binds through
                    // JsonSerializer.Deserialize (with whatever attribute shapes were
                    // present) and loads with a warning rather than vanishing.
                    GlobalLogger.Error(
                        "GraphSerializer",
                        $"({fileNameForLog}) attribute normalization failed — loading un-normalized graph",
                        ex);
                    return json;
                }
            }

            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }

        /// <summary>
        /// Walks <paramref name="graph"/> and every nested
        /// macro / process sub-graph, calling <see cref="MigrateNodes"/> on each.
        /// Pre-fix the LoadGraph path migrated the top-level Macros / Processes
        /// once, but a macro embedded inside another macro's inner graph
        /// silently skipped migration. Cycles are impossible here — macros own
        /// their inner graphs by composition, not by reference — but a visited
        /// set is kept for cheap insurance against pathological hand-edited .phxg
        /// shapes.
        /// </summary>
        // Sanity cap so a pathological hand-authored .phxg with a
        // cycle that somehow defeats the visited set (e.g. recursively-cloned
        // sub-graphs that all carry distinct object identity) can't blow the
        // call stack. 32 levels far exceeds any realistic macro/process
        // nesting — Architect's editor surfaces a flat parent / sub-graph
        // breadcrumb that practically caps at 4-5 in real graphs.
        private const int MaxMigrationRecursionDepth = 32;

        private static void MigrateNodesRecursive(Graph graph)
        {
            if (graph == null) return;
            var visited = new HashSet<Graph>();
            MigrateNodesRecursiveCore(graph, visited, depth: 0);
        }

        private static void MigrateNodesRecursiveCore(Graph graph, HashSet<Graph> visited, int depth)
        {
            if (graph == null) return;
            if (!visited.Add(graph)) return;
            if (depth >= MaxMigrationRecursionDepth)
            {
                GlobalLogger.Log(
                    $"GraphSerializer: migration recursion cap ({MaxMigrationRecursionDepth}) hit — deeper nested sub-graphs skipped.",
                    "GraphSerializer", LogLevel.Communication);
                return;
            }

            MigrateNodes(graph);

            if (graph.Macros != null)
            {
                foreach (var macro in graph.Macros)
                    if (macro?.Graph != null)
                        MigrateNodesRecursiveCore(macro.Graph, visited, depth + 1);
            }

            if (graph.Processes != null)
            {
                foreach (var proc in graph.Processes)
                    if (proc?.Graph != null)
                        MigrateNodesRecursiveCore(proc.Graph, visited, depth + 1);
            }
        }

        /// <summary>
        /// Reorder a node's sockets so each type
        /// group (inputs, then outputs) reads in template order, with any
        /// non-template / placeholder sockets kept in their original relative
        /// order appended after the template ones in their group. Preserves the
        /// Socket instances (so links, which resolve by Id, are untouched) and
        /// only rewrites the list when the order actually changed.
        /// </summary>
        private static void ReorderSocketsToTemplate(Node node, NodeTemplate tmpl)
        {
            if (node.Sockets is null || node.Sockets.Count < 2) return;

            static List<Socket> OrderByTemplate(IEnumerable<Socket> sockets, IReadOnlyList<string> templateOrder)
            {
                var idx = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < templateOrder.Count; i++)
                    if (!idx.ContainsKey(templateOrder[i])) idx[templateOrder[i]] = i;
                int orig = 0;
                return sockets
                    .Select(s => (s, orig: orig++, tpl: idx.TryGetValue(s.Name ?? string.Empty, out var t) ? t : int.MaxValue))
                    .OrderBy(e => e.tpl == int.MaxValue ? 1 : 0) // template sockets first, extras after
                    .ThenBy(e => e.tpl)                          // template order
                    .ThenBy(e => e.orig)                         // stable for extras / duplicate names
                    .Select(e => e.s)
                    .ToList();
            }

            var inputs  = OrderByTemplate(node.Sockets.Where(s => s.Type == SocketType.Input),
                                          tmpl.Inputs.Select(t => t.Item1).ToList());
            var outputs = OrderByTemplate(node.Sockets.Where(s => s.Type == SocketType.Output),
                                          tmpl.Outputs.Select(t => t.Item1).ToList());

            var rebuilt = new List<Socket>(node.Sockets.Count);
            rebuilt.AddRange(inputs);
            rebuilt.AddRange(outputs);
            if (rebuilt.Count != node.Sockets.Count) return; // defensive — never drop sockets
            if (rebuilt.SequenceEqual(node.Sockets)) return; // already ordered — no churn
            node.Sockets.Clear();
            node.Sockets.AddRange(rebuilt);
        }

        /// <summary>
        /// Adds any sockets or attributes that are present in the current NodeRegistry template
        /// but missing from a saved node (e.g. because the template was updated after the graph
        /// was last saved). Also resizes the node to fit the new socket count.
        /// </summary>
        private static void MigrateNodes(Graph graph)
        {
            const int headerH   = 24;
            const int spacing   = 22;
            const int nodeWidth = 200;

            // A JSON literal `"Attributes": null` or `"Sockets": null` overrides
            // the field initializer and produces a null collection that downstream code
            // (DataType re-sync, intra-node id collision repair, template back-fill, the
            // dangling-link sweep, MacroSyncHashGate.BuildSignature) NREs on. Heal once
            // here so no later pass needs to defend.
            //
            // Extend the same healing path to top-level Graph collections —
            // `"Nodes": null` / `"Links": null` / `"Macros": null` / `"Processes": null`
            // in a hand-edited .phxg would otherwise NRE in the very first foreach over
            // `graph.Nodes` below. The model class's field initializers don't survive a
            // JSON null literal (System.Text.Json explicitly assigns null in that case).
            graph.Nodes     ??= new List<Node>();
            graph.Links     ??= new List<Link>();
            graph.Macros    ??= new List<Macro>();
            graph.Processes ??= new List<Process>();
            foreach (var node in graph.Nodes)
            {
                node.Attributes ??= new Dictionary<string, string>();
                node.Sockets    ??= new List<Socket>();
            }

            // Live-processes migration — upgrade the legacy fire-and-forget
            // process nodes to the live-instance model. Process.Spawn → Process.Start
            // and Process.Terminate → Process.Stop have identical socket/attr shapes,
            // so the template re-sync below is a no-op; we only retitle, so the node
            // binds to the new template and re-exports as process.start / process.stop.
            // (The old templates stay registered + HiddenFromPalette as a backstop.)
            // Runs per-graph in the migration recursion, so nested process/macro
            // bodies upgrade too. Idempotent: titles already upgraded are left alone.
            foreach (var node in graph.Nodes)
            {
                if (node.Title == "Process.Spawn")          node.Title = "Process.Start";
                else if (node.Title == "Process.Terminate") node.Title = "Process.Stop";
            }

            // Adjacency prebuild. The downstream
            // collision-repair + dangling-link sweep both used to walk
            // graph.Links inside an outer loop over graph.Nodes (or vice
            // versa), giving O(N·L) load behaviour on the migration pass.
            // Build the (NodeId, SocketId) → List<Link> index once here and
            // hand it through; the consumers do O(1) lookups against it
            // instead of re-scanning the full link list per node/per link.
            //
            // The map is invalidated as soon as either side mutates
            // FromSocketId / ToSocketId — RepairIntraNodeSocketIdCollisions
            // does exactly that. So we use it to find the affected links,
            // rewire them, and don't try to keep the index live across the
            // template-back-fill loop. The dangling-link sweep at the end
            // builds its OWN socket-membership index then, since template
            // back-fill mutates each node's socket list.
            var linksByEndpoint = BuildLinksByEndpoint(graph);

            // Intra-node socket-id collision repair.
            // Hand-edited or copy-pasted .phxg payloads can produce two sockets on the
            // same node sharing an Id. The link resolver matches by `(NodeId, SocketId)`
            // pairs and walks links in graph order, so the "second" link to an
            // ambiguous id silently snaps to whichever socket comes first in iteration
            // — mis-routing data without complaint.
            //
            // The repair: for each node, walk sockets in declaration order. The first
            // occurrence of a given Id keeps that Id; subsequent duplicates get
            // renamed to `{originalId}_dup1`, `{originalId}_dup2`, etc. Links that
            // reference the original id are walked in graph-order and the Nth link
            // (zero-indexed) gets pointed at the Nth socket occurrence — which means
            // the 0th link stays on the original Id and the 1st link gets rewired to
            // `{id}_dup1`, etc. Cross-node duplicate ids are NOT renamed (Ids are
            // scoped per node, by design — renaming across nodes would just churn
            // data without fixing a real ambiguity).
            RepairIntraNodeSocketIdCollisions(graph, linksByEndpoint);

            // Re-sync DataType from Color for every socket — ensures previously-saved nodes pick up
            // any corrections to the DataTypeFromColor mapping (e.g. ColNumber → Int, not Float).
            // Visualist-only palette types (Image / Color / Scalar / Vector2-4 / Audio)
            // ride on custom socket colors that DataTypeFromColor doesn't know about, so
            // unconditionally re-deriving downgrades them to Any on every load. Skip the
            // overwrite when the color-derived type would be Any but the persisted DataType
            // is more specific — that way Architect's six core palette colors keep healing
            // and Visualist's richer types survive the round-trip.
            foreach (var node in graph.Nodes)
            {
                if (node.Sockets == null) continue;
                foreach (var s in node.Sockets)
                {
                    var derived = NodeRegistry.DataTypeFromColorPublic(s.Color);
                    if (derived == SocketDataType.Any && s.DataType != SocketDataType.Any) continue;
                    s.DataType = derived;
                }
            }

            // The legacy CaseA/CaseB → Case A/Case B migration for
            // Logic.Switch / State.Switch was removed. Majo green-lit the pull
            // after confirming no personal graphs still carry the old keys; the
            // current templates have used the spaced names since before the
            // in-tree goldens existed. If a future out-of-tree .phxg turns up
            // with the old shape, restore the migration block from this
            // commit's parent.

            // Bus.Send / Bus.Broadcast / Twitch.SendChat output-socket rename:
            // legacy graphs stored the flow continuation socket as "Sent"; templates
            // now expose it as "Done" so all action-node continuations converge on
            // the same name. Rename the persisted socket in
            // place — preserving its Id keeps existing wires intact, so the later
            // template back-fill sees a socket already named "Done" and skips the
            // remove-and-readd path that would orphan the links.
            var sentToDoneTitles = new HashSet<string> { "Bus.Send", "Bus.Broadcast", "Twitch.SendChat" };
            foreach (var node in graph.Nodes)
            {
                if (!sentToDoneTitles.Contains(node.Title)) continue;
                foreach (var s in node.Sockets)
                {
                    if (s.Type == SocketType.Output && s.Name == "Sent")
                        s.Name = "Done";
                }
            }

            // DB.Increment restructure (2026-06-08): the node moved from a hybrid
            // (TableName attribute + Key/Row sockets) to the DB.SetCell socket shape
            // (TableName + RowId + Column input sockets, Amount in place of Value) so it
            // gets the databank dropdown pickers and matches the other DB nodes. Rename
            // the persisted sockets IN PLACE — preserving each socket's Id keeps existing
            // wires (e.g. RowId fed from a DB.FindRow) intact, so the template back-fill
            // below sees sockets already named Column/RowId and skips the remove-and-readd
            // path that would orphan the links (same approach as the Sent→Done rename
            // above). The inline pill VALUE lives in node.Attributes keyed by socket name,
            // so move those across too; the new TableName socket is added by the back-fill
            // and binds to the existing TableName attribute by name.
            foreach (var node in graph.Nodes)
            {
                if (node.Title != "DB.Increment") continue;
                foreach (var s in node.Sockets)
                {
                    if (s.Type != SocketType.Input) continue;
                    if (s.Name == "Key")      s.Name = "Column";
                    else if (s.Name == "Row") s.Name = "RowId";
                }
                if (node.Attributes.TryGetValue("Key", out var keyVal))
                {
                    if (!node.Attributes.ContainsKey("Column")) node.Attributes["Column"] = keyVal;
                    node.Attributes.Remove("Key");
                }
                if (node.Attributes.TryGetValue("Row", out var rowVal))
                {
                    if (!node.Attributes.ContainsKey("RowId")) node.Attributes["RowId"] = rowVal;
                    node.Attributes.Remove("Row");
                }
            }

            foreach (var node in graph.Nodes)
            {
                // DB.FetchRow: synthesize per-column output sockets
                // matching KnownColumns. Runs before the generic template
                // back-fill below so the user-declared columns are present
                // before the back-fill walks the socket list.
                if (node.Title == "DB.FetchRow")
                {
                    NodeRegistry.EnsureFetchRowColumnSockets(node, graph);
                }

                // Dynamic event nodes: variable sockets are saved state, not template drift — preserve them.
                if (node.Title == "Event.Trigger" || node.Title == "Event.Executor" || node.Title == "Event.Return")
                {
                    // Repair BEFORE ensure: strip corrupted sockets (duplicate
                    // placeholders, wrong-side placeholders, Output-channel junk on
                    // Event.Return) that graphs saved under the old polarity-heal /
                    // Return-as-Executor sync bugs still carry — the ensure step below
                    // only ever ADDS, so without this pass a corrupted node stays
                    // broken on every load (wire-drops reject as same-side, stacked
                    // placeholder hit-bands make the node un-editable).
                    PlaceholderActivator.RepairEventNodeShape(graph, node);
                    NodeRegistry.EnsureEventNodePlaceholders(node);

                    int inRow = 0, outRow = 0;
                    foreach (var s in node.Sockets)
                    {
                        if (s.Name == "Flow") { s.Offset = new Point(s.Offset.X, headerH + 6); continue; }
                        if (s.Type == SocketType.Input)  { inRow++;  s.Offset = new Point(s.Offset.X, headerH + 6 + inRow  * spacing); }
                        else                             { outRow++; s.Offset = new Point(s.Offset.X, headerH + 6 + outRow * spacing); }
                    }

                    int rows = node.Sockets.Count(s => s.Name != "Flow") + 1;
                    node.Size = new Size(node.Size.Width, headerH + 14 + rows * spacing);
                    continue;
                }

                // Visual.Trigger: dynamic variable inputs — same preservation approach as Event.*
                if (node.Title == "Visual.Trigger")
                {
                    if (!node.Sockets.Exists(s => s.IsPlaceholder && s.Type == SocketType.Input && s.Name == "+ variable"))
                        node.Sockets.Add(new Socket
                        {
                            Name = "+ variable", Type = SocketType.Input,
                            Color = NodeRegistry.ColString, IsPlaceholder = true
                        });

                    int inRow = 0;
                    foreach (var s in node.Sockets)
                    {
                        if (s.Name == "Flow") { s.Offset = new Point(-6, headerH + 6); continue; }
                        if (s.Name == "Done") { s.Offset = new Point(nodeWidth - 14, headerH + 6); continue; }
                        if (s.Type == SocketType.Input) { inRow++; s.Offset = new Point(-6, headerH + 6 + inRow * spacing); }
                    }

                    int rows = node.Sockets.Count(s => s.Type == SocketType.Input && s.Name != "Flow") + 1;
                    node.Size = new Size(node.Size.Width, headerH + 14 + rows * spacing);
                    continue;
                }

                // Macro.Call: sockets are rebuilt from the macro definition, not the template — skip migration
                if (node.Title == "Macro.Call") continue;

                // Macro.Entry / Macro.Exit carry user-defined parameter
                // sockets that aren't in their templates. The post-recursion sweep would
                // strip them as "non-template inputs/outputs" and break the macro export's
                // parameter binding (golden MacroSimple regressed). Their socket layout is
                // managed at macro-edit time, not via template back-fill.
                if (node.Title == "Macro.Entry" || node.Title == "Macro.Exit") continue;

                var tmpl = NodeRegistry.GetTemplate(node.Title);
                if (tmpl == null)
                {
                    // A node whose template is no
                    // longer registered (e.g. a plugin/category was removed) is
                    // skipped here — and the dangling-link sweep below then prunes
                    // its wires. Pre-fix that happened silently, so a missing
                    // template quietly dropped sockets + links with no trace.
                    // Surface a Communication-level warning so partial migration
                    // is visible. (No modal — per the repeatable-rejection rule.)
                    GlobalLogger.Log(
                        $"Migration skipped node '{node.Title}' (id {node.Id}) — no registered template; " +
                        "its template-managed sockets/links may be pruned. A plugin or node type may be missing.",
                        "GraphSerializer", LogLevel.Communication);
                    continue;
                }

                // Remove sockets that no longer exist in the template (non-placeholder only)
                var templateInputNames  = tmpl.Inputs.Select(t => t.Item1).ToHashSet();
                var templateOutputNames = tmpl.Outputs.Select(t => t.Item1).ToHashSet();
                node.Sockets.RemoveAll(s => !s.IsPlaceholder
                    && ((s.Type == SocketType.Input  && !templateInputNames.Contains(s.Name))
                     || (s.Type == SocketType.Output && !templateOutputNames.Contains(s.Name))));

                // Pre-build name sets for the
                // existing input/output sockets so the missing-socket back-fill below
                // does O(1) HashSet lookups instead of O(M) `node.Sockets.Any(...)`
                // scans per template entry. Each set is kept current as sockets are
                // inserted/added so the "already present" check stays correct.
                var existingInputNames  = new HashSet<string>(
                    node.Sockets.Where(s => s.Type == SocketType.Input).Select(s => s.Name),
                    StringComparer.Ordinal);
                var existingOutputNames = new HashSet<string>(
                    node.Sockets.Where(s => s.Type == SocketType.Output).Select(s => s.Name),
                    StringComparer.Ordinal);

                // Add any missing input sockets
                for (int i = 0; i < tmpl.Inputs.Count; i++)
                {
                    var (name, color) = tmpl.Inputs[i];
                    if (existingInputNames.Add(name))
                    {
                        node.Sockets.Insert(i, new Socket
                        {
                            Name   = name,
                            Type   = SocketType.Input,
                            Color  = color,
                            Offset = new Point(-6, headerH + 6 + i * spacing)
                        });
                    }
                }

                // Add any missing output sockets
                for (int i = 0; i < tmpl.Outputs.Count; i++)
                {
                    var (name, color) = tmpl.Outputs[i];
                    if (existingOutputNames.Add(name))
                    {
                        node.Sockets.Add(new Socket
                        {
                            Name   = name,
                            Type   = SocketType.Output,
                            Color  = color,
                            Offset = new Point(nodeWidth - 14, headerH + 6 + i * spacing)
                        });
                    }
                }

                // Add any missing Attributes (properties) from template defaults
                foreach (var kv in tmpl.DefaultProperties)
                    node.Attributes.TryAdd(kv.Key, kv.Value);

                // Snap socket Y offsets to current template order so old graphs heal when a
                // template gains/reorders sockets (otherwise stale offsets overlap visually —
                // System.GetTime/GetDate had two sockets sharing the same Y).
                for (int i = 0; i < tmpl.Inputs.Count; i++)
                {
                    var name = tmpl.Inputs[i].Item1;
                    var s = node.Sockets.Find(x => x.Type == SocketType.Input && x.Name == name);
                    if (s != null)
                        s.Offset = new Point(s.Offset.X, headerH + 6 + i * spacing);
                }
                for (int i = 0; i < tmpl.Outputs.Count; i++)
                {
                    var name = tmpl.Outputs[i].Item1;
                    var s = node.Sockets.Find(x => x.Type == SocketType.Output && x.Name == name);
                    if (s != null)
                        s.Offset = new Point(s.Offset.X, headerH + 6 + i * spacing);
                }

                // Resize node if the new socket count requires more height
                int maxSockets = Math.Max(tmpl.Inputs.Count, tmpl.Outputs.Count);
                int newH = headerH + 14 + maxSockets * spacing;
                if (node.Size.Height < newH)
                    node.Size = new Size(node.Size.Width, newH);

                // Backfill socket Description from the current template registry so
                // older .phxg files pick up freshly-authored socket-
                // level help text. Only writes when the persisted Description is empty
                // — a future per-graph override (canvas-side rename / authoring tool)
                // would set Description directly and survive this pass.
                if (tmpl.SocketDescriptions.Count > 0)
                {
                    foreach (var s in node.Sockets)
                    {
                        if (!string.IsNullOrEmpty(s.Description)) continue;
                        if (tmpl.SocketDescriptions.TryGetValue(s.Name, out var desc) && !string.IsNullOrEmpty(desc))
                            s.Description = desc;
                    }
                }

                // Rebuild the socket list in
                // template order. The "add missing input" path above uses
                // Sockets.Insert(templateInputIndex, …) against the MIXED
                // input/output list, so a back-filled socket can land among the
                // wrong type and render in the wrong row order under WinUI
                // (which rows by list-iteration order, not the WinForms-era
                // Offset.Y the snap above corrects). Socket INSTANCES are
                // preserved (links resolve by Id) — only their order changes.
                ReorderSocketsToTemplate(node, tmpl);
            }

            // Sweep dangling links left over from the socket-removal pass
            // above. RemoveAll deletes any link whose endpoints no longer resolve
            // to a real socket on the corresponding node. Done in a single pass
            // AFTER socket migration so we don't iterate-while-mutating sockets.
            // Without this, downstream code (validator, exporter) silently treats
            // the dangling link as if it carries data — producing scripts that
            // reference deleted sockets.
            //
            // Fold the per-link `graph.Nodes.Find(...)` +
            // `Sockets.All(...)` scans (O(L · (N + S)) total) into one O(N + N·S)
            // prebuild + O(L) sweep. The pre-fix shape dominated load time for
            // any sizeable .phxg; the prebuild is unconditional because it's
            // cheap and the dangling-sweep alone already justifies it.
            var nodesById = new Dictionary<string, Node>(graph.Nodes.Count, StringComparer.Ordinal);
            foreach (var n in graph.Nodes)
            {
                if (!string.IsNullOrEmpty(n.Id)) nodesById[n.Id] = n;
            }
            var socketIdsByNode = new Dictionary<string, HashSet<string>>(graph.Nodes.Count, StringComparer.Ordinal);
            foreach (var n in graph.Nodes)
            {
                if (string.IsNullOrEmpty(n.Id)) continue;
                var ids = new HashSet<string>(n.Sockets.Count, StringComparer.Ordinal);
                foreach (var s in n.Sockets) if (!string.IsNullOrEmpty(s.Id)) ids.Add(s.Id);
                socketIdsByNode[n.Id] = ids;
            }
            graph.Links.RemoveAll(link =>
            {
                if (!nodesById.ContainsKey(link.FromNodeId)) return true;
                if (!nodesById.ContainsKey(link.ToNodeId))   return true;
                if (!socketIdsByNode.TryGetValue(link.FromNodeId, out var fromIds)
                    || !fromIds.Contains(link.FromSocketId)) return true;
                if (!socketIdsByNode.TryGetValue(link.ToNodeId, out var toIds)
                    || !toIds.Contains(link.ToSocketId))     return true;
                return false;
            });

            // Migration adds/removes sockets, renames socket Ids (the
            // collision repair above), and rewires links. Every one of those is a
            // structural change, so invalidate Graph's id-indexed caches now —
            // otherwise the per-paint lookup self-heals once-per-cache-miss for the
            // rest of the session, churning CPU on every canvas paint until the next
            // legit MarkStructuralChange landing.
            graph.MarkStructuralChange();
        }

        /// <summary>
        /// Build a (NodeId, SocketId) → List&lt;Link&gt;
        /// index over <see cref="Graph.Links"/>. Each link contributes one
        /// entry under its From-endpoint and one under its To-endpoint, so a
        /// link touching either side is reachable via either key. Used by
        /// the migration pass to avoid the O(N · L) re-scan that
        /// <see cref="RepairIntraNodeSocketIdCollisions"/> previously paid
        /// per duplicated socket id.
        /// </summary>
        private static Dictionary<(string NodeId, string SocketId), List<Link>>
            BuildLinksByEndpoint(Graph graph)
        {
            var map = new Dictionary<(string, string), List<Link>>(graph.Links.Count * 2);
            foreach (var link in graph.Links)
            {
                Add((link.FromNodeId, link.FromSocketId), link);
                Add((link.ToNodeId,   link.ToSocketId),   link);
            }
            return map;

            void Add((string NodeId, string SocketId) key, Link link)
            {
                if (string.IsNullOrEmpty(key.NodeId) || string.IsNullOrEmpty(key.SocketId)) return;
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<Link>(2);
                    map[key] = list;
                }
                list.Add(link);
            }
        }
        /// <summary>
        /// Three-slot rolling backup metadata.
        /// One entry per existing .phxg.bakN beside the active <c>.phxg</c>;
        /// returned in newest-first order. <see cref="LastWriteUtc"/> is
        /// surfaced by Architect's File → Restore previous version… menu so
        /// the user can pick by timestamp rather than slot index.
        /// </summary>
        public sealed record BackupCandidate(string Path, int Slot, DateTime LastWriteUtc);

        /// <summary>
        /// List the existing <c>.phxg.bak1 / bak2 / bak3</c> entries beside the
        /// given .phxg file. Returns at most three results, newest-slot-first
        /// (bak1 → bak2 → bak3). Missing slots are skipped silently. Used by
        /// the "Restore previous version…" menu in MainView. Best-effort: any
        /// IO failure returns an empty list so the menu can fall back to a
        /// "no backups available" hint without crashing.
        /// </summary>
        public static System.Collections.Generic.List<BackupCandidate> ListBackups(string phxgPath)
        {
            var results = new System.Collections.Generic.List<BackupCandidate>();
            if (string.IsNullOrEmpty(phxgPath)) return results;
            try
            {
                for (int slot = 1; slot <= 3; slot++)
                {
                    string p = phxgPath + ".bak" + slot;
                    if (!File.Exists(p)) continue;
                    DateTime ts;
                    try { ts = File.GetLastWriteTimeUtc(p); }
                    catch { ts = DateTime.MinValue; }
                    results.Add(new BackupCandidate(p, slot, ts));
                }
            }
            catch { /* best-effort */ }
            return results;
        }

        /// <summary>
        /// Restore one of the <c>.phxg.bakN</c>
        /// candidates as the new active .phxg, bumping the rolling chain so
        /// the current state slides into <c>bak1</c> first (the same shape
        /// <see cref="SaveGraphAsync"/> creates). Returns true when the
        /// restore succeeded. Caller is expected to reload the file
        /// afterwards via <see cref="LoadGraph"/> / <see cref="LoadGraphAsync"/>.
        /// </summary>
        public static bool RestoreBackup(string phxgPath, string backupPath)
        {
            if (string.IsNullOrEmpty(phxgPath) || string.IsNullOrEmpty(backupPath)) return false;
            if (!File.Exists(backupPath)) return false;
            try
            {
                // CRITICAL: snapshot the backup payload BEFORE rotating
                // the chain. The user-facing restore menu lets the user pick any
                // .bakN slot; if we rotate first, the rotation slides
                // phxg→bak1, bak1→bak2, bak2→bak3 (dropping bak3), which moves
                // the file at `backupPath` to a different slot — or deletes it
                // entirely if the user picked bak3. The subsequent File.Copy
                // would then either fail or silently grab the wrong snapshot.
                // Reading into memory first decouples the payload from the
                // path so the rotation can safely shuffle the chain.
                byte[] payload = File.ReadAllBytes(backupPath);
                RotateBackupChain(phxgPath);
                File.WriteAllBytes(phxgPath, payload);
                GlobalLogger.Log(
                    $"Restored '{Path.GetFileName(phxgPath)}' from '{Path.GetFileName(backupPath)}'.",
                    "GraphSerializer", LogLevel.System);
                return true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("GraphSerializer",
                    $"RestoreBackup failed: {Path.GetFileName(backupPath)} → {Path.GetFileName(phxgPath)}", ex);
                return false;
            }
        }

        /// <summary>
        /// Slide <c>file.phxg → file.phxg.bak1</c>, <c>bak1 → bak2</c>,
        /// <c>bak2 → bak3</c>. The oldest slot (<c>bak3</c>) is replaced if
        /// it exists. No-op when the .phxg file does not exist yet (first save
        /// of a brand-new graph). Best-effort — any IO failure is logged and
        /// the foreground save still proceeds because the user's intent is to
        /// write fresh content; the rotation is a defence-in-depth backstop.
        ///
        /// Rotate in reverse order so a mid-flight failure can never
        /// destroy a slot that hasn't been promoted yet. Pre-fix the chain ran
        /// bak2→bak3 first then bak1→bak2; if either Move threw after deleting
        /// the destination slot, the source content was already gone from its
        /// previous home. Doing bak2→bak3 first and bak1→bak2 second is
        /// still the correct destination order — but each individual promotion
        /// is now guarded so we never delete-then-fail-move. We use File.Copy
        /// + File.Delete (instead of File.Move) so the original file is only
        /// dropped after the Copy lands; if the Copy throws, the source slot
        /// is intact and the next save will re-attempt cleanly.
        /// </summary>
        private static void RotateBackupChain(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                string bak1 = filePath + ".bak1";
                string bak2 = filePath + ".bak2";
                string bak3 = filePath + ".bak3";

                // bak2 → bak3 (oldest slot; drops bak3 only AFTER the copy lands).
                PromoteSlot(bak2, bak3);
                // bak1 → bak2.
                PromoteSlot(bak1, bak2);
                // current .phxg → bak1 (the active file is about to be replaced
                // atomically by the .tmp rename, so we want a Copy not a Move
                // — leaving the active file intact for the .tmp.Move to land on).
                File.Copy(filePath, bak1, overwrite: true);
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"GraphSerializer: backup-chain rotation skipped for '{Path.GetFileName(filePath)}': {ex.Message}",
                    "GraphSerializer", LogLevel.System);
            }
        }

        /// <summary>
        /// Promote one backup slot — copy <paramref name="src"/> to
        /// <paramref name="dst"/> via a <c>.tmp</c> stage, then File.Replace to
        /// land it atomically. Only after the Replace lands do we delete
        /// <paramref name="src"/>. If any step throws, both <paramref name="src"/>
        /// and <paramref name="dst"/> remain in their previous state — no slot
        /// is lost mid-flight.
        /// </summary>
        private static void PromoteSlot(string src, string dst)
        {
            if (!File.Exists(src)) return;
            string stage = dst + ".tmp";
            try { if (File.Exists(stage)) File.Delete(stage); } catch { }
            try
            {
                File.Copy(src, stage, overwrite: true);
                // File.Move with overwrite is atomic on NTFS for same-volume
                // moves — equivalent to MoveFileEx with MOVEFILE_REPLACE_EXISTING.
                // If dst doesn't exist, Move acts as a rename; if it does, it's
                // replaced in one step. Either way, stage is consumed.
                File.Move(stage, dst, overwrite: true);
                // Only drop the source slot AFTER the destination is durable.
                try { File.Delete(src); } catch { /* dst is safe; src cleanup is best-effort */ }
            }
            catch
            {
                // Cleanup the half-baked stage so the next rotation isn't
                // tripped up by a leftover .tmp.
                try { if (File.Exists(stage)) File.Delete(stage); } catch { }
                throw;
            }
        }

        private static readonly HashSet<string> _columnValueNodes =
            new() { "DB.GetCell", "DB.SetCell", "DB.FindRow", "DB.InsertRow" };

        public static async Task ApplyColumnTypesAsync(Graph graph)
        {
            // Collect unique table names from DB nodes that have resolvable literal attributes.
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in graph.Nodes)
            {
                if (!_columnValueNodes.Contains(node.Title)) continue;
                string table = StripQuotes(node.Attributes.GetValueOrDefault("TableName", ""));
                if (!string.IsNullOrEmpty(table) && !table.Contains('{'))
                    tableNames.Add(table);
            }

            // Fetch column types per table (one DB round-trip each).
            var schemaCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tableNames)
            {
                try { schemaCache[table] = await Phoenix.Controls.Shared.Services.DB.Instance.GetTableColumnTypesAsync(table).ConfigureAwait(false); }
                catch { /* DB offline — skip */ }
            }

            foreach (var node in graph.Nodes)
            {
                if (!_columnValueNodes.Contains(node.Title)) continue;
                string table  = StripQuotes(node.Attributes.GetValueOrDefault("TableName", ""));
                string column = StripQuotes(node.Attributes.GetValueOrDefault("Column", ""));
                if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column)) continue;
                if (table.Contains('{') || column.Contains('{')) continue;
                if (!schemaCache.TryGetValue(table, out var colTypes)) continue;
                if (!colTypes.TryGetValue(column, out string? sqlType)) continue;

                Color valueColor = NodeRegistry.ColumnTypeToSocketColor(sqlType ?? "TEXT");
                NodeRegistry.ApplyColumnTypeToNode(node, valueColor);

                // Find the socket we just updated and propagate its type through reroutes.
                // Both DB.GetCell and DB.SetCell expose the typed cell socket as "Value";
                // only the direction differs (GetCell outputs it, the others consume it).
                // (Previously `node.Title == "DB.GetCell" ? "Value" : "Value"` — a no-op
                // ternary that masked the fact that the name is shared.)
                const string socketName = "Value";
                var direction = node.Title == "DB.GetCell" ? Phoenix.Controls.Shared.Models.SocketType.Output
                                                           : Phoenix.Controls.Shared.Models.SocketType.Input;
                var updatedSocket = node.Sockets.Find(s => s.Name == socketName && s.Type == direction);
                if (updatedSocket != null)
                    PropagateSocketTypeChange(graph, updatedSocket, node, new HashSet<string>());
            }
        }

        private static void PropagateSocketTypeChange(
            Graph graph,
            Socket changedSocket,
            Node ownerNode,
            HashSet<string> visited)
        {
            if (!visited.Add(changedSocket.Id)) return;

            bool isOutput = changedSocket.Type == Phoenix.Controls.Shared.Models.SocketType.Output;

            if (isOutput)
            {
                // Walk links where changedSocket is the source.
                foreach (var link in graph.Links)
                {
                    if (link.FromNodeId != ownerNode.Id || link.FromSocketId != changedSocket.Id) continue;
                    var targetNode = graph.Nodes.Find(n => n.Id == link.ToNodeId);
                    if (targetNode == null) continue;

                    if (targetNode.Title == "Flow.Reroute")
                    {
                        foreach (var s in targetNode.Sockets)
                        {
                            s.Color    = changedSocket.Color;
                            s.DataType = changedSocket.DataType;
                        }
                        var rerouteOut = targetNode.Sockets.Find(s => s.Type == Phoenix.Controls.Shared.Models.SocketType.Output);
                        if (rerouteOut != null)
                            PropagateSocketTypeChange(graph, rerouteOut, targetNode, visited);
                    }
                }
            }
            else
            {
                // Input socket: walk links where changedSocket is the destination and update upstream reroutes.
                foreach (var link in graph.Links)
                {
                    if (link.ToNodeId != ownerNode.Id || link.ToSocketId != changedSocket.Id) continue;
                    var sourceNode = graph.Nodes.Find(n => n.Id == link.FromNodeId);
                    if (sourceNode?.Title != "Flow.Reroute") continue;

                    foreach (var s in sourceNode.Sockets)
                    {
                        s.Color    = changedSocket.Color;
                        s.DataType = changedSocket.DataType;
                    }
                    var rerouteIn = sourceNode.Sockets.Find(s => s.Type == Phoenix.Controls.Shared.Models.SocketType.Input);
                    if (rerouteIn != null)
                        PropagateSocketTypeChange(graph, rerouteIn, sourceNode, visited);
                }
            }
        }

        private static string StripQuotes(string s)
            => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

        /// <summary>
        /// Intra-node socket-id collision repair. See the call-site
        /// comment in <see cref="MigrateNodes"/> for the full rationale and link-rewire
        /// semantics. This method walks each node's sockets in declaration order,
        /// renames duplicates to <c>{originalId}_dup{N}</c>, and rewrites the Nth
        /// matching link endpoint to point at the Nth occurrence's new id. Logs a
        /// Communication-tier warning per affected node.
        ///
        /// Accepts a prebuilt
        /// <c>(NodeId, SocketId) → List&lt;Link&gt;</c> adjacency map so the per-
        /// duplicated-id rewire can do a single bucket lookup instead of a full
        /// scan over <see cref="Graph.Links"/>. Per-link order within the bucket
        /// matches insertion order (the BuildLinksByEndpoint walk visits links
        /// in graph.Links order), preserving the "Nth-link → Nth-occurrence"
        /// rewire semantics the original code relied on.
        /// </summary>
        private static void RepairIntraNodeSocketIdCollisions(
            Graph graph,
            Dictionary<(string NodeId, string SocketId), List<Link>> linksByEndpoint)
        {
            foreach (var node in graph.Nodes)
            {
                // First pass: identify which socket Ids appear more than once on this node.
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var s in node.Sockets)
                {
                    if (string.IsNullOrEmpty(s.Id)) continue;
                    counts[s.Id] = counts.TryGetValue(s.Id, out var c) ? c + 1 : 1;
                }

                var duplicatedIds = counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
                if (duplicatedIds.Count == 0) continue;

                // Second pass per duplicated id: rename socket occurrences and rewire links.
                foreach (var origId in duplicatedIds)
                {
                    // Collect socket occurrences in declaration order.
                    var occurrences = new List<Socket>();
                    foreach (var s in node.Sockets)
                        if (s.Id == origId) occurrences.Add(s);

                    // Rename occurrences 1..N (zero-th keeps original id).
                    for (int i = 1; i < occurrences.Count; i++)
                        occurrences[i].Id = $"{origId}_dup{i}";

                    // Walk only the links touching (node.Id, origId) — the
                    // adjacency bucket preserves graph-insertion order, so
                    // the Nth matching link maps to the Nth occurrence.
                    if (!linksByEndpoint.TryGetValue((node.Id, origId), out var bucket))
                    {
                        // No links touch the duplicated socket — log + continue.
                        GlobalLogger.Log(
                            $"GraphSerializer: node '{node.Title}' had {occurrences.Count} sockets sharing id '{origId}' — renamed duplicates to '_dupN' (no incoming links to rewire).",
                            "GraphSerializer",
                            LogLevel.Communication);
                        continue;
                    }

                    int matchIndex = 0;
                    foreach (var link in bucket)
                    {
                        bool fromMatch = link.FromNodeId == node.Id && link.FromSocketId == origId;
                        bool toMatch   = link.ToNodeId   == node.Id && link.ToSocketId   == origId;
                        if (!fromMatch && !toMatch) continue;

                        // The 0th matching link stays on the original id (no change);
                        // the Nth matching link points at the Nth occurrence's new id.
                        if (matchIndex >= 1 && matchIndex < occurrences.Count)
                        {
                            string newId = occurrences[matchIndex].Id;
                            if (fromMatch) link.FromSocketId = newId;
                            if (toMatch)   link.ToSocketId   = newId;
                        }
                        matchIndex++;
                    }

                    GlobalLogger.Log(
                        $"GraphSerializer: node '{node.Title}' had {occurrences.Count} sockets sharing id '{origId}' — renamed duplicates to '_dupN' and rewired matching links by index order.",
                        "GraphSerializer",
                        LogLevel.Communication);
                }
            }
        }
    }
}
