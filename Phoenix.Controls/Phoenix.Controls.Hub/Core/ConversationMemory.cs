using System.Collections.Generic;
using System.Text.Json;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Stores AI chat history as a JSON-array-of-<c>{role,content}</c> string
    /// inside a Var.  — the conversation memory wrapper for
    /// <c>ai.stream_text</c>. Authors point a stream call at a memory var
    /// (any Var key) and the streaming kernel transparently
    /// prepends prior turns to the request and appends the new exchange
    /// after completion.
    /// </summary>
    /// <remarks>
    /// Storage shape:
    /// <code>
    /// [{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]
    /// </code>
    /// The empty/missing case parses to an empty list — first call is a
    /// no-op for the prepend, then writes back the inaugural turn.
    /// <para>
    /// Trimming: <see cref="MaxTurns"/> caps the number of role+content
    /// pairs retained. Once the history exceeds the cap, the OLDEST
    /// pairs are discarded (FIFO) so the most recent context is what
    /// the model sees. The system prompt is owned by <c>ai.stream_text</c>'s
    /// <c>SystemPrompt</c> arg, NOT the memory var, so it never falls
    /// out of the window.
    /// </para>
    /// </remarks>
    public static class ConversationMemory
    {
        /// <summary>Default cap on retained turns when the script doesn't specify one. 50 user+assistant pairs (~100 messages) keeps token cost bounded for long-running streams while still giving the model meaningful context.</summary>
        public const int DefaultMaxTurns = 50;

        public readonly record struct Turn(string Role, string Content);

        /// <summary>
        /// Parses a Var payload to an ordered list of turns.
        /// Empty / null / malformed JSON returns an empty list rather
        /// than throwing — the caller treats first-touch as the inaugural
        /// turn.
        /// </summary>
        public static List<Turn> Parse(string? rawJson)
        {
            var turns = new List<Turn>();
            if (string.IsNullOrWhiteSpace(rawJson)) return turns;
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return turns;
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    string role = entry.TryGetProperty("role", out var rEl) && rEl.ValueKind == JsonValueKind.String
                        ? (rEl.GetString() ?? "") : "";
                    string content = entry.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String
                        ? (cEl.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(role)) continue;
                    turns.Add(new Turn(role, content));
                }
            }
            catch (JsonException)
            {
                // Stale / corrupted memory var → clean slate. The next
                // write replaces the bad payload entirely.
            }
            return turns;
        }

        /// <summary>
        /// Returns a new list with the (user, assistant) pair appended
        /// and FIFO-trimmed to <paramref name="maxTurns"/>. The original
        /// list is not mutated.
        /// </summary>
        /// <remarks>
        /// QC37-03 — eviction happens in turn-PAIR units, not entry units.
        /// The previous implementation trimmed at the raw entry index, so
        /// once the history exceeded the cap an assistant message could be
        /// left as the first turn (with the matching user message dropped).
        /// Anthropic's Messages API hard-400s on "roles must alternate
        /// starting with user" the next time that history is sent, killing
        /// the 51st turn against Claude.
        /// <para>
        /// Strategy: walk the FIFO and drop entries in pairs, always
        /// preserving the user→assistant alternation invariant. A non-pair
        /// stray at the head (corrupt prior state) is dropped first so the
        /// cap re-aligns to clean pairs.
        /// </para>
        /// </remarks>
        public static List<Turn> Append(List<Turn> existing, string userPrompt, string assistantResponse, int maxTurns)
        {
            // QC37-10 — null-guard. Callers that forget to seed `existing`
            // from Parse() (which always returns a non-null list) used to
            // NRE on `.Count` / `.AddRange`. Treat null the same as an
            // empty history; the first call simply seeds the inaugural
            // user/assistant pair.
            var seed = existing ?? new List<Turn>(0);
            var next = new List<Turn>(seed.Count + 2);
            next.AddRange(seed);
            next.Add(new Turn("user",      userPrompt      ?? ""));
            next.Add(new Turn("assistant", assistantResponse ?? ""));
            int cap = maxTurns > 0 ? maxTurns : DefaultMaxTurns;
            // Cap counts role+content PAIRS (user+assistant). Two entries
            // per pair. Trim oldest pairs first; never split a pair.
            int maxEntries = cap * 2;
            while (next.Count > maxEntries)
            {
                // If the head isn't a "user" entry (legacy / corrupt
                // history), drop the stray first so the next pair-drop
                // realigns to a user→assistant boundary.
                if (next[0].Role != "user")
                {
                    next.RemoveAt(0);
                    continue;
                }
                // Standard case — drop the (user, assistant) pair at the
                // head. If the second entry isn't "assistant" (again,
                // corrupt history), still drop both so we don't loop
                // forever; the surfaced history stays alternating.
                int drop = next.Count >= 2 ? 2 : 1;
                next.RemoveRange(0, drop);
            }
            return next;
        }

        /// <summary>Serialises a turn list to the canonical JSON-array form stored in the Var.</summary>
        public static string Serialize(IReadOnlyList<Turn> turns)
        {
            // Hand-roll the array since record struct projection through
            // System.Text.Json's source generators would need ctor wiring.
            var array = new List<Dictionary<string, string>>(turns.Count);
            foreach (var t in turns)
            {
                array.Add(new Dictionary<string, string>
                {
                    ["role"]    = t.Role,
                    ["content"] = t.Content,
                });
            }
            return JsonSerializer.Serialize(array);
        }
    }
}
