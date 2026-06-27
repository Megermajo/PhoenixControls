using System;
using System.Text.Json;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Parses one frame of Ollama's <c>/api/chat</c> streaming response
    /// (NDJSON — each line is a complete JSON object). Lifted to a static
    /// helper so the routing + parsing contract is unit-testable without
    /// standing up a live HTTP transport.
    /// </summary>
    /// <remarks>
    /// Ollama frames take one of two shapes:
    /// <list type="bullet">
    ///   <item><description>
    ///     Mid-stream — <c>{"model":"…","message":{"role":"assistant",
    ///     "content":"chunk"},"done":false}</c>. <see cref="TryParseFrame"/>
    ///     surfaces <paramref name="chunk"/> = "chunk", <paramref name="done"/> = false.
    ///   </description></item>
    ///   <item><description>
    ///     Terminator — <c>{"done":true,"total_duration":…}</c> with no
    ///     message field. <paramref name="chunk"/> = null,
    ///     <paramref name="done"/> = true.
    ///   </description></item>
    /// </list>
    /// Be defensive about a future Ollama build merging the last chunk and
    /// the done sentinel into a single frame: the parser surfaces both
    /// fields independently rather than treating them as mutually exclusive.
    /// </remarks>
    public static class OllamaStreamParser
    {
        /// <summary>
        /// Parses a single NDJSON frame body. Returns <c>true</c> if the
        /// frame parsed; <c>false</c> if the body was empty or malformed
        /// JSON. <paramref name="chunk"/> is null when the frame carries
        /// no message content (e.g. the terminator); callers should treat
        /// null + done=true as the natural close.
        /// </summary>
        public static bool TryParseFrame(string body, out string? chunk, out bool done)
        {
            chunk = null;
            done = false;
            if (string.IsNullOrWhiteSpace(body)) return false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.Object &&
                    msgEl.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind == JsonValueKind.String)
                {
                    chunk = contentEl.GetString();
                }
                if (doc.RootElement.TryGetProperty("done", out var doneEl) &&
                    doneEl.ValueKind == JsonValueKind.True)
                {
                    done = true;
                }
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
