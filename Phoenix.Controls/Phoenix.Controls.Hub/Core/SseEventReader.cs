using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Byte-level Server-Sent-Events reader. Replaces the prior
    /// <c>StreamReader.ReadLineAsync</c> path inside <c>ai.stream_text</c>
    /// so the parser:
    /// <list type="bullet">
    ///   <item><description>Splits frames on BOTH <c>\n</c> and <c>\r\n</c>
    ///   (the previous reader was tolerant on the line-end side but events
    ///   are framed by <c>\n\n</c> / <c>\r\n\r\n</c> — split on the wrong
    ///   one and Anthropic's multi-line events dropped silently).</description></item>
    ///   <item><description>Buffers partial UTF-8 sequences across socket
    ///   read boundaries so a glyph that straddles a chunk doesn't corrupt
    ///   the surfaced delta. <see cref="UTF8Encoding"/> with
    ///   <c>throwOnInvalidBytes:false</c> + a stateful <see cref="Decoder"/>
    ///   handles the codepoint boundary itself.</description></item>
    ///   <item><description>Surfaces both <c>event:</c> and <c>data:</c>
    ///   fields per event so callers can branch on Anthropic's typed
    ///   frames (<c>event: error</c>, <c>event: message_stop</c>, etc.).
    ///   The legacy parser only inspected <c>data:</c> lines and ignored
    ///   the event name entirely.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Wire format (per WHATWG SSE spec):
    /// <code>
    /// event: error
    /// data: {"type":"error","error":{...}}
    ///
    /// data: {"type":"content_block_delta",...}
    /// </code>
    /// A blank line (LF or CRLF) terminates an event. Multiple <c>data:</c>
    /// lines within one event are concatenated with <c>\n</c> between them.
    /// Lines starting with <c>:</c> are comments (keep-alives) and ignored.
    /// </remarks>
    public sealed class SseEventReader
    {
        // Hard ceiling on the un-terminated carry buffer. SSE endpoints are
        // user-configured (custom translator/AI URLs), so a misbehaving or
        // non-SSE server that never sends a blank-line terminator would grow
        // _carry without bound. Real events are hundreds of bytes; 1M chars
        // (~2 MB UTF-16) is orders of magnitude above any legitimate frame.
        private const int MaxEventChars = 1024 * 1024;

        private readonly Stream _stream;
        private readonly byte[] _readBuf;
        private readonly Decoder _decoder;
        private readonly StringBuilder _carry;

        public SseEventReader(Stream stream, int readBufferSize = 4096)
        {
            _stream = stream;
            _readBuf = new byte[readBufferSize];
            // Stateful UTF-8 decoder — feeding it a partial codepoint at
            // the tail of one read leaves the bytes buffered internally
            // until the next read completes the sequence.
            _decoder = new UTF8Encoding(false, false).GetDecoder();
            _carry = new StringBuilder();
        }

        /// <summary>
        /// Reads the next complete SSE event. Returns <c>null</c> when the
        /// stream closes (end-of-stream with no trailing event pending).
        /// </summary>
        public async Task<SseEvent?> ReadEventAsync(CancellationToken ct)
        {
            while (true)
            {
                // Try to extract a complete event from carry before pulling
                // more bytes off the wire. An "event" here is everything up
                // to the next blank line (LF-LF or CRLF-CRLF / mixed).
                if (TryExtractEvent(out SseEvent? ev)) return ev;

                int n = await _stream.ReadAsync(_readBuf.AsMemory(0, _readBuf.Length), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    // Flush any trailing bytes the decoder is sitting on
                    // (defensive — for well-formed UTF-8 there shouldn't be
                    // anything queued at EOF).
                    char[] flushBuf = new char[16];
                    int flushed = _decoder.GetChars(System.Array.Empty<byte>(), 0, 0, flushBuf, 0, flush: true);
                    if (flushed > 0) _carry.Append(flushBuf, 0, flushed);

                    // EOF — surface any pending event (no trailing blank
                    // line) and then close out.
                    if (_carry.Length > 0)
                    {
                        var tail = ParseEvent(_carry.ToString());
                        _carry.Clear();
                        if (tail != null) return tail;
                    }
                    return null;
                }

                // Decode the freshly-read bytes into characters, appending
                // to carry. The decoder owns any straddling-codepoint state
                // between calls — no manual byte buffering needed.
                int maxChars = _decoder.GetCharCount(_readBuf, 0, n, flush: false);
                if (maxChars > 0)
                {
                    char[] chars = new char[maxChars];
                    int got = _decoder.GetChars(_readBuf, 0, n, chars, 0, flush: false);
                    if (got > 0) _carry.Append(chars, 0, got);
                }

                // Abort rather than buffer forever when the endpoint never
                // terminates an event — the caller's catch surfaces this as a
                // stream error instead of the Hub slowly eating memory.
                if (_carry.Length > MaxEventChars)
                {
                    throw new InvalidDataException(
                        $"SSE event exceeded {MaxEventChars:N0} chars without a blank-line terminator — aborting (misbehaving or non-SSE endpoint).");
                }
            }
        }

        private bool TryExtractEvent(out SseEvent? ev)
        {
            ev = null;
            // Find the first blank-line terminator: \n\n, \r\n\r\n, or
            // mixed (\n\r\n / \r\n\n). Search linearly — events are small
            // (single-event JSON payloads are ~hundreds of bytes typical).
            string buf = _carry.ToString();
            int end = -1;
            int termLen = 0;
            for (int i = 0; i < buf.Length - 1; i++)
            {
                char c0 = buf[i];
                if (c0 != '\n' && c0 != '\r') continue;
                // Match the four possible terminators in priority order.
                if (i + 3 < buf.Length && buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                {
                    end = i; termLen = 4; break;
                }
                if (i + 1 < buf.Length && buf[i] == '\n' && buf[i + 1] == '\n')
                {
                    end = i; termLen = 2; break;
                }
                if (i + 2 < buf.Length && buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\n')
                {
                    end = i; termLen = 3; break;
                }
                if (i + 2 < buf.Length && buf[i] == '\n' && buf[i + 1] == '\r' && buf[i + 2] == '\n')
                {
                    end = i; termLen = 3; break;
                }
            }
            if (end < 0) return false;

            string raw = buf.Substring(0, end);
            _carry.Remove(0, end + termLen);
            ev = ParseEvent(raw);
            return ev != null;
        }

        private static SseEvent? ParseEvent(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string? eventName = null;
            var dataLines = new List<string>();

            // Split on \r\n OR \n. Don't strip \r left over from a CRLF
            // payload before searching for it — handle both styles.
            int start = 0;
            for (int i = 0; i <= raw.Length; i++)
            {
                bool atEnd = i == raw.Length;
                bool atLf = !atEnd && raw[i] == '\n';
                bool atCrLf = !atEnd && raw[i] == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n';
                if (!atEnd && !atLf && !atCrLf) continue;

                string line = raw.Substring(start, i - start);
                if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);

                if (line.Length == 0)
                {
                    // Inner blank line — spec says this ends the event, but
                    // the outer extractor already split on blank lines so
                    // we shouldn't see one here. Treat as no-op.
                }
                else if (line[0] == ':')
                {
                    // Comment / keep-alive — ignore.
                }
                else if (line.StartsWith("event:", System.StringComparison.Ordinal))
                {
                    eventName = StripField(line, 6);
                }
                else if (line.StartsWith("data:", System.StringComparison.Ordinal))
                {
                    dataLines.Add(StripField(line, 5));
                }
                // Other fields (id:, retry:) are ignored — they don't drive
                // our consumer behaviour.

                if (atCrLf) i++; // step past the \n
                start = i + 1;
            }

            if (eventName == null && dataLines.Count == 0) return null;
            string data = dataLines.Count == 1 ? dataLines[0] : string.Join("\n", dataLines);
            return new SseEvent(eventName, data);
        }

        private static string StripField(string line, int prefixLen)
        {
            if (line.Length <= prefixLen) return "";
            int i = prefixLen;
            if (i < line.Length && line[i] == ' ') i++; // spec: single leading space allowed
            return line.Substring(i);
        }
    }

    /// <summary>Parsed SSE event — <see cref="EventName"/> may be null for unnamed events.</summary>
    public readonly record struct SseEvent(string? EventName, string Data);
}
