using System.Text.Json;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Extracts the first assistant message content from an
    /// OpenAI <c>/v1/chat/completions</c> response (single-shot, non-
    /// streaming). The streaming path uses its own SSE-delta parser; this
    /// helper is for the synchronous JSON-body shape.
    /// </summary>
    /// <remarks>
    /// Response shape:
    /// <code>
    /// { "choices": [{ "message": { "role": "assistant", "content": "…" } }] }
    /// </code>
    /// Returns <c>null</c> if the body is malformed, missing
    /// <c>choices</c>, the array is empty, the first entry has no
    /// <c>message</c>, the message has no <c>content</c>, or the content
    /// is empty / non-string. Vision responses ride this same shape —
    /// the multi-modal parts live on the request side, the answer is
    /// always a plain string.
    /// </remarks>
    public static class AIChatCompletionParser
    {
        public static string? ExtractFirstChoiceContent(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("choices", out var choicesEl)) return null;
                if (choicesEl.ValueKind != JsonValueKind.Array || choicesEl.GetArrayLength() == 0) return null;
                var first = choicesEl[0];
                if (first.ValueKind != JsonValueKind.Object) return null;
                if (!first.TryGetProperty("message", out var msgEl)) return null;
                if (msgEl.ValueKind != JsonValueKind.Object) return null;
                if (!msgEl.TryGetProperty("content", out var contentEl)) return null;
                if (contentEl.ValueKind != JsonValueKind.String) return null;
                string? text = contentEl.GetString();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
