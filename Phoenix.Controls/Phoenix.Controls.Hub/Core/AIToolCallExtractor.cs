using System.Collections.Generic;
using System.Text.Json;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    ///  — extracts the assistant's tool-call list from an OpenAI
    /// chat-completions response when the model chose to call a tool.
    /// Sibling helper to <see cref="AIChatCompletionParser"/>: that one
    /// returns the assistant's text content; this one returns the
    /// structured tool calls. The two paths are mutually exclusive on
    /// any given response (OpenAI sets <c>content</c> = null when
    /// <c>tool_calls</c> is populated, and vice versa).
    /// </summary>
    /// <remarks>
    /// Response shape when the model calls a tool:
    /// <code>
    /// { "choices":[{
    ///     "message":{
    ///       "role":"assistant",
    ///       "content":null,
    ///       "tool_calls":[
    ///         { "id":"call_abc",
    ///           "type":"function",
    ///           "function":{
    ///             "name":"get_weather",
    ///             "arguments":"{\"location\":\"Seattle\"}" }}
    ///       ] },
    ///     "finish_reason":"tool_calls" }] }
    /// </code>
    /// The <c>arguments</c> field is itself a JSON-encoded STRING (not a
    /// parsed object). The extractor surfaces it verbatim so the script
    /// can route it through <c>http.parse_json</c> if it wants the
    /// individual fields.
    /// </remarks>
    public static class AIToolCallExtractor
    {
        public readonly record struct ToolCall(string Id, string Name, string Arguments);

        /// <summary>
        /// Extracts the tool-call list. Returns an empty list when the
        /// response had no tool calls (the script should then look at
        /// <see cref="AIChatCompletionParser.ExtractFirstChoiceContent"/>
        /// for a text answer instead). Returns an empty list on malformed
        /// input — the caller falls through to the error path which is
        /// the right surface for that case.
        /// </summary>
        public static List<ToolCall> Extract(string? responseBody)
        {
            var result = new List<ToolCall>();
            if (string.IsNullOrWhiteSpace(responseBody)) return result;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("choices", out var choicesEl)) return result;
                if (choicesEl.ValueKind != JsonValueKind.Array || choicesEl.GetArrayLength() == 0) return result;
                var first = choicesEl[0];
                if (first.ValueKind != JsonValueKind.Object) return result;
                if (!first.TryGetProperty("message", out var msgEl)) return result;
                if (msgEl.ValueKind != JsonValueKind.Object) return result;
                if (!msgEl.TryGetProperty("tool_calls", out var toolCallsEl)) return result;
                if (toolCallsEl.ValueKind != JsonValueKind.Array) return result;

                foreach (var tc in toolCallsEl.EnumerateArray())
                {
                    if (tc.ValueKind != JsonValueKind.Object) continue;
                    string id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? (idEl.GetString() ?? "") : "";
                    if (!tc.TryGetProperty("function", out var fnEl) || fnEl.ValueKind != JsonValueKind.Object) continue;
                    string name = fnEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? (nameEl.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    string arguments = fnEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String
                        ? (argsEl.GetString() ?? "") : "";

                    // QC37-06 — `arguments` is a JSON-encoded STRING per the
                    // OpenAI contract, but smaller models (gpt-4o-mini, GPT-3.5)
                    // occasionally emit malformed JSON or get truncated mid-args
                    // when finish_reason == "length". The string is still shipped
                    // verbatim (the script may want the raw bytes for forensics)
                    // but we surface a Communication-tier warning so authors can
                    // detect the silent path. Empty arguments is normal for
                    // zero-arg tools, not a warning case.
                    if (!string.IsNullOrEmpty(arguments))
                    {
                        try
                        {
                            using var argDoc = JsonDocument.Parse(arguments);
                            // Parses cleanly — happy path.
                        }
                        catch (JsonException pex)
                        {
                            GlobalLogger.Log(
                                $"AI tool_call '{name}' has malformed arguments JSON: {pex.Message}",
                                "AI", LogLevel.Communication);
                        }
                    }
                    result.Add(new ToolCall(id, name, arguments));
                }
            }
            catch (JsonException)
            {
                // malformed body → empty list. Caller surfaces the error.
            }
            return result;
        }

        /// <summary>
        /// Serialises a tool-call list to the canonical JSON shape that
        /// <c>ai.with_tools</c> writes into <c>result.ai_tool_calls</c>.
        /// Each entry is <c>{id, name, arguments}</c> — a flat shape the
        /// script can iterate without unwrapping the OpenAI envelope.
        /// </summary>
        public static string Serialize(IReadOnlyList<ToolCall> calls)
        {
            var array = new List<Dictionary<string, string>>(calls.Count);
            foreach (var c in calls)
            {
                array.Add(new Dictionary<string, string>
                {
                    ["id"]        = c.Id,
                    ["name"]      = c.Name,
                    ["arguments"] = c.Arguments,
                });
            }
            return JsonSerializer.Serialize(array);
        }
    }
}
