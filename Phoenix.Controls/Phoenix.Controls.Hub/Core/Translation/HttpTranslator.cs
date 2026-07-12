using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core.Translation
{
    /// <summary>
    /// Outcome of the most recent <see cref="HttpTranslator.TranslateAsync"/> call.
    /// Lets callers disambiguate why <c>TranslateAsync</c> returned the original text:
    ///   <see cref="Success"/>     — backend round-tripped (the result may equal the input legitimately).
    ///   <see cref="Timeout"/>     — concurrency slot wait or HttpClient timeout fired; passthrough fallback.
    ///   <see cref="Error"/>       — non-cancellation exception caught; passthrough fallback.
    ///   <see cref="Passthrough"/> — short-circuited before any HTTP call (empty input/target or no endpoint).
    /// </summary>
    public enum TranslationOutcome
    {
        Success,
        Timeout,
        Error,
        Passthrough,
    }

    /// <summary>
    /// HttpTranslator — HTTP-backed translator. Posts to the endpoint configured in
    /// <c>AppConfig.TranslationHttpEndpoint</c>, speaking the request/response shape selected
    /// by <c>AppConfig.TranslationProviderShape</c>:
    ///   "phoenix" — POST {text, target}; optional Bearer key; reads "translated".
    ///               The native shape for custom proxies / user-written adapters, and the
    ///               fallback for unknown shape values.
    ///   "deepl"   — POST {text: [..], target_lang} with a "DeepL-Auth-Key" Authorization
    ///               header; reads translations[0].text.
    ///   "google"  — POST {q, target, format: "text"} with the key appended as a ?key=
    ///               query parameter; reads data.translations[0].translatedText.
    ///   "libre"   — POST {q, target, format: "text"} plus api_key in the body when a key
    ///               is set; reads translatedText.
    /// The endpoint URL always comes from TranslationHttpEndpoint — the shape only changes
    /// body, headers, and response parsing — so self-hosted or regional deployments work by
    /// pointing the endpoint wherever the provider lives.
    /// On any failure (including a response missing the expected field), returns the original
    /// text so live captions never go silent.
    ///
    /// Concurrency: a per-instance <see cref="SemaphoreSlim"/> caps in-flight requests so a flood
    /// of TRANSLATE_REQUEST messages can't exhaust HttpClient sockets (M91).
    /// Lifetime: HttpClient is per-instance (M90) so <see cref="TranslationService.Reload"/> —
    /// which discards the old translator and constructs a new one — can cancel in-flight requests
    /// by calling <see cref="Cancel"/>/<see cref="Dispose"/> on the previous instance.
    ///
    /// Observability (L46): exposes <see cref="InFlightCount"/> and <see cref="LastTranslationOutcome"/>
    /// so a UI/HUD layer can render an in-flight indicator and tell apart "translator returned
    /// the same string" from "translator timed out and we passed the original through."
    ///
    /// Reset hook (L47): <see cref="ClearPending"/> drops every tracked in-flight request, surfacing
    /// a <see cref="TranslationOutcome.Timeout"/> outcome to awaiting callers (via cancellation of
    /// the per-call linked CTS). Intended to be invoked by the WebSocket layer when its connection
    /// resets so a flaky reconnect doesn't strand pending entries until the HttpClient 5s timeout.
    /// </summary>
    public sealed class HttpTranslator : ITranslator, IDisposable
    {
        // ── M91 concurrency cap ────────────────────────────────────────────────
        // Small per-instance gate — prevents TRANSLATE_REQUEST flooding from
        // exhausting HttpClient connections. Excess callers wait briefly for a
        // slot; on timeout we fall back to passthrough rather than throw.
        private const int MaxConcurrentRequests = 8;
        private static readonly TimeSpan SlotWaitTimeout = TimeSpan.FromSeconds(2);
        private readonly SemaphoreSlim _gate = new(MaxConcurrentRequests, MaxConcurrentRequests);

        // ── M90 per-instance HttpClient + cancellation ────────────────────────
        // Static HttpClient outlives the translator instance and breaks Reload's
        // cancellation guarantee. A per-instance client + linked CTS lets
        // Reload/Dispose abort in-flight SendAsync calls deterministically.
        private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(5);
        private readonly HttpClient _http = new() { Timeout = HttpRequestTimeout };
        private CancellationTokenSource _cts = new();
        private readonly object _ctsLock = new();
        private int _disposed;

        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _shape;

        // ── L46 / L47 in-flight tracking ──────────────────────────────────────
        // Tracks every active TranslateAsync call by an internal monotonic id so
        // ClearPending() can cancel them and stale entries can be lazily evicted
        // on the next access. The SemaphoreSlim only counts slot occupancy — it
        // doesn't give us a handle to the linked CTS we need to cancel from the
        // outside, hence this parallel registry.
        private readonly ConcurrentDictionary<long, PendingEntry> _pending = new();
        private long _nextPendingId;
        private int _inFlight;
        private int _lastOutcome = (int)TranslationOutcome.Passthrough;

        private sealed class PendingEntry
        {
            public CancellationTokenSource Linked { get; }
            public DateTimeOffset StartedAt { get; }
            public PendingEntry(CancellationTokenSource linked, DateTimeOffset startedAt)
            {
                Linked  = linked;
                StartedAt = startedAt;
            }
        }

        public string Name => "http";

        /// <summary>Number of TranslateAsync calls currently in flight (slot wait + HTTP request).</summary>
        public int InFlightCount => Volatile.Read(ref _inFlight);

        /// <summary>Outcome of the most recent <see cref="TranslateAsync"/> completion.</summary>
        public TranslationOutcome LastTranslationOutcome => (TranslationOutcome)Volatile.Read(ref _lastOutcome);

        /// <param name="endpoint">Backend URL from <c>AppConfig.TranslationHttpEndpoint</c>.</param>
        /// <param name="apiKey">Optional key from <c>AppConfig.TranslationApiKey</c>; transport depends on shape.</param>
        /// <param name="shape">Provider shape from <c>AppConfig.TranslationProviderShape</c>
        /// ("phoenix" | "deepl" | "google" | "libre"); unknown values behave as "phoenix".</param>
        public HttpTranslator(string endpoint, string apiKey, string shape = "phoenix")
        {
            _endpoint = endpoint ?? "";
            _apiKey   = apiKey ?? "";
            _shape    = (shape ?? "phoenix").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Cancels every in-flight request and rotates the cancellation source so
        /// subsequent calls run against a fresh generation. Safe to call from any
        /// thread; called by Dispose and any future config-reload hook.
        /// </summary>
        public void Cancel()
        {
            CancellationTokenSource old;
            lock (_ctsLock)
            {
                old = _cts;
                _cts = new CancellationTokenSource();
            }
            try { old.Cancel(); } catch { /* best-effort */ }
            old.Dispose();
        }

        /// <summary>
        /// L47 — Drops every tracked in-flight request, cancelling each per-call
        /// linked CTS so awaiting <see cref="TranslateAsync"/> callers unblock with
        /// a <see cref="TranslationOutcome.Timeout"/> outcome instead of stalling
        /// for the full HttpClient timeout. Intended for the WebSocket layer to
        /// call when its connection resets — pending responses for the dropped
        /// socket will never arrive, so leaving entries in place causes the next
        /// render to wait out the 5s HttpClient timeout per stranded request.
        ///
        /// Returns the number of pending entries that were cleared.
        /// </summary>
        public int ClearPending()
        {
            int cleared = 0;
            // Snapshot keys so we can safely mutate the dictionary while iterating.
            foreach (var key in System.Linq.Enumerable.ToArray(_pending.Keys))
            {
                if (_pending.TryRemove(key, out var entry))
                {
                    cleared++;
                    try { entry.Linked.Cancel(); } catch { /* best-effort */ }
                }
            }
            return cleared;
        }

        /// <summary>
        /// L47 TTL pass — drops any pending entry older than the HttpClient timeout
        /// so a wedged backend can't keep <see cref="InFlightCount"/> elevated
        /// forever. Called lazily on the next TranslateAsync entry; safe to call
        /// from any thread.
        /// </summary>
        private void EvictStalePending()
        {
            var cutoff = DateTimeOffset.UtcNow - HttpRequestTimeout - TimeSpan.FromSeconds(1);
            foreach (var kv in _pending)
            {
                if (kv.Value.StartedAt < cutoff && _pending.TryRemove(kv.Key, out var entry))
                {
                    try { entry.Linked.Cancel(); } catch { /* best-effort */ }
                }
            }
        }

        public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
        {
            // Fail fast if disposed. Without this, a
            // TranslateAsync racing Dispose() would touch the disposed _gate / _http
            // and throw ObjectDisposedException. A disposed translator passes the
            // original text through (captions never go silent).
            if (Volatile.Read(ref _disposed) != 0)
            {
                Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Passthrough);
                return text ?? "";
            }

            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(targetLanguage))
            {
                Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Passthrough);
                return text ?? "";
            }
            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Passthrough);
                return text;
            }

            // L47 — sweep stale entries before adding new ones so a wedged backend
            // can't keep InFlightCount elevated indefinitely.
            EvictStalePending();

            // L46 — bump the public counter as soon as we commit to the call so a
            // UI poll right before WaitAsync sees the request in flight.
            Interlocked.Increment(ref _inFlight);

            try
            {
                // Wait for a concurrency slot, but bound the wait so a stuck
                // backend can't pile up callers indefinitely. On timeout, return the
                // original text (passthrough) and log a Communication-level message.
                bool slotAcquired;
                try
                {
                    slotAcquired = await _gate.WaitAsync(SlotWaitTimeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Timeout);
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    // Dispose() raced us and disposed _gate
                    // after the upfront _disposed check passed. Degrade to passthrough
                    // rather than throwing on a disposed member.
                    Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Passthrough);
                    return text;
                }

                if (!slotAcquired)
                {
                    GlobalLogger.Log(
                        $"HttpTranslator: concurrency cap ({MaxConcurrentRequests}) saturated — returning original text.",
                        "Translation", LogLevel.Communication);
                    Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Timeout);
                    return text;
                }

                // Snapshot the current generation CTS so a Reload/Cancel mid-flight
                // doesn't race the linked-source creation.
                CancellationTokenSource generationCts;
                lock (_ctsLock) generationCts = _cts;

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, generationCts.Token);

                // L47 — register this call so ClearPending() can cancel it from outside.
                long pendingId = Interlocked.Increment(ref _nextPendingId);
                var entry = new PendingEntry(linked, DateTimeOffset.UtcNow);
                _pending[pendingId] = entry;

                try
                {
                    // Request/response contract per AppConfig.TranslationProviderShape —
                    // see BuildRequest/ParseTranslated for the per-provider wire formats.
                    using var req = BuildRequest(text, targetLanguage);

                    using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Error);
                        return text;
                    }

                    string respJson = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(respJson);
                    string? translated = ParseTranslated(doc.RootElement);
                    if (translated != null)
                    {
                        Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Success);
                        return translated;
                    }
                    // Response was well-formed JSON but the expected field is missing or
                    // mis-typed — a provider error as far as we're concerned, though the
                    // original text still passes through (captions never go silent).
                    Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Error);
                    return text;
                }
                catch (OperationCanceledException)
                {
                    // L46 — distinguish caller-cancellation from
                    // ClearPending/HttpClient timeout. Caller's own ct cancelling
                    // is propagated; everything else is treated as a Timeout
                    // (since the entry was either swept by ClearPending or hit
                    // the HttpClient 5s ceiling).
                    if (ct.IsCancellationRequested)
                    {
                        Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Timeout);
                        throw;
                    }
                    Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Timeout);
                    return text;
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"HttpTranslator: error '{ex.Message}' — returning original text.",
                        "Translation", LogLevel.Communication);
                    Volatile.Write(ref _lastOutcome, (int)TranslationOutcome.Error);
                    return text;
                }
                finally
                {
                    _pending.TryRemove(pendingId, out _);
                    try { _gate.Release(); } catch { /* disposed */ }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        // ── Provider request/response shapes ──────────────────────────────────
        // The endpoint URL is always AppConfig.TranslationHttpEndpoint; the shape
        // only decides body, headers, and (for Google) the ?key= query parameter.

        /// <summary>
        /// Builds the outbound POST for the configured provider shape.
        ///   phoenix: { "text": "<source>", "target": "<lang>" }          + Bearer key header
        ///   deepl:   { "text": ["<source>"], "target_lang": "<LANG>" }   + DeepL-Auth-Key header
        ///   google:  { "q": "<source>", "target": "<lang>", "format": "text" } + ?key= on the URL
        ///   libre:   { "q": "<source>", "target": "<lang>", "format": "text", "api_key"? }
        /// Unknown shapes fall back to phoenix.
        /// </summary>
        private HttpRequestMessage BuildRequest(string text, string targetLanguage)
        {
            string body;
            HttpRequestMessage req;
            switch (_shape)
            {
                case "deepl":
                    // DeepL v2 /translate: text is an array, target_lang is uppercase,
                    // and the key rides a provider-specific Authorization scheme.
                    body = JsonSerializer.Serialize(new
                    {
                        text = new[] { text },
                        target_lang = targetLanguage.ToUpperInvariant(),
                    });
                    req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(_apiKey))
                        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _apiKey);
                    return req;

                case "google":
                    // Google Cloud Translation v2: the key travels as a query parameter,
                    // not a header. Preserve any query the configured endpoint already has.
                    body = JsonSerializer.Serialize(new
                    {
                        q = text,
                        target = targetLanguage,
                        format = "text",
                    });
                    string uri = _endpoint;
                    if (!string.IsNullOrEmpty(_apiKey))
                        uri += (uri.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(_apiKey);
                    return new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };

                case "libre":
                    // LibreTranslate: api_key rides in the JSON body — only when set, so
                    // keyless self-hosted instances don't get a spurious empty field.
                    body = string.IsNullOrEmpty(_apiKey)
                        ? JsonSerializer.Serialize(new { q = text, target = targetLanguage, format = "text" })
                        : JsonSerializer.Serialize(new { q = text, target = targetLanguage, format = "text", api_key = _apiKey });
                    return new HttpRequestMessage(HttpMethod.Post, _endpoint)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };

                default: // "phoenix" — and the use-time fallback for unknown shape values.
                    body = JsonSerializer.Serialize(new { text, target = targetLanguage });
                    req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(_apiKey))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    return req;
            }
        }

        /// <summary>
        /// Extracts the translated string from the provider response, or null when the
        /// expected field is missing or mis-typed. Callers map null to
        /// <see cref="TranslationOutcome.Error"/> and pass the original text through.
        ///   phoenix: { "translated": "..." }
        ///   deepl:   { "translations": [ { "text": "..." } ] }
        ///   google:  { "data": { "translations": [ { "translatedText": "..." } ] } }
        ///   libre:   { "translatedText": "..." }
        /// </summary>
        private string? ParseTranslated(JsonElement root)
        {
            switch (_shape)
            {
                case "deepl":
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("translations", out var dArr) &&
                        dArr.ValueKind == JsonValueKind.Array && dArr.GetArrayLength() > 0 &&
                        dArr[0].ValueKind == JsonValueKind.Object &&
                        dArr[0].TryGetProperty("text", out var dText) &&
                        dText.ValueKind == JsonValueKind.String)
                        return dText.GetString();
                    return null;

                case "google":
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("data", out var gData) &&
                        gData.ValueKind == JsonValueKind.Object &&
                        gData.TryGetProperty("translations", out var gArr) &&
                        gArr.ValueKind == JsonValueKind.Array && gArr.GetArrayLength() > 0 &&
                        gArr[0].ValueKind == JsonValueKind.Object &&
                        gArr[0].TryGetProperty("translatedText", out var gText) &&
                        gText.ValueKind == JsonValueKind.String)
                        return gText.GetString();
                    return null;

                case "libre":
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("translatedText", out var lText) &&
                        lText.ValueKind == JsonValueKind.String)
                        return lText.GetString();
                    return null;

                default: // "phoenix" — and the use-time fallback for unknown shape values.
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("translated", out var pText) &&
                        pText.ValueKind == JsonValueKind.String)
                        return pText.GetString();
                    return null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { ClearPending(); } catch { /* best-effort */ }
            try { Cancel(); } catch { /* best-effort */ }
            try { _http.Dispose(); } catch { /* best-effort */ }
            try { _gate.Dispose(); } catch { /* best-effort */ }
        }
    }
}
