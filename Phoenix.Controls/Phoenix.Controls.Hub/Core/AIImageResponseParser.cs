using System.Text.Json;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    ///  — extracts the first image URL from an OpenAI
    /// <c>/v1/images/generations</c> response. Lifted to a static
    /// helper so the response shape contract has a unit-testable
    /// surface independent of the live HTTP path.
    /// </summary>
    /// <remarks>
    /// OpenAI's image-generation response shape:
    /// <code>
    /// { "created": 1715000000,
    ///   "data": [{ "url": "https://oaidalleapiprodscus.blob.core.windows.net/…" }] }
    /// </code>
    /// The <c>data</c> array can carry multiple entries when <c>n &gt; 1</c>;
    /// this command always passes <c>n = 1</c> so the first entry is the
    /// answer. Returns <c>null</c> if the body is malformed, missing
    /// the <c>data</c> array, the array is empty, or the first entry
    /// has no <c>url</c> field.
    /// </remarks>
    public static class AIImageResponseParser
    {
        public static string? ExtractFirstUrl(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
                if (dataEl.ValueKind != JsonValueKind.Array || dataEl.GetArrayLength() == 0) return null;
                var first = dataEl[0];
                if (first.ValueKind != JsonValueKind.Object) return null;
                if (!first.TryGetProperty("url", out var urlEl)) return null;
                if (urlEl.ValueKind != JsonValueKind.String) return null;
                string? url = urlEl.GetString();
                return string.IsNullOrEmpty(url) ? null : url;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// QC37-13 — extracts the first entry's <c>b64_json</c> payload when
        /// the request set <c>response_format = "b64_json"</c>. Returns
        /// <c>null</c> on the same set of failure cases as <see cref="ExtractFirstUrl"/>
        /// (malformed body, missing data array, empty array, missing b64
        /// field, non-string value). Pairs with a future download-and-host
        /// fix for the 1-hour SAS-expiry issue on DALL-E URLs (QC37-04).
        /// </summary>
        public static string? ExtractFirstB64Json(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
                if (dataEl.ValueKind != JsonValueKind.Array || dataEl.GetArrayLength() == 0) return null;
                var first = dataEl[0];
                if (first.ValueKind != JsonValueKind.Object) return null;
                if (!first.TryGetProperty("b64_json", out var b64El)) return null;
                if (b64El.ValueKind != JsonValueKind.String) return null;
                string? b64 = b64El.GetString();
                return string.IsNullOrEmpty(b64) ? null : b64;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
