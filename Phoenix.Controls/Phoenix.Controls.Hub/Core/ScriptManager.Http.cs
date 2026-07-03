using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: http.* + api.call command registrations.
    // Seven RegisterCommand bodies (http.get / api.call / http.post /
    // http.put / http.patch / http.delete / http.parse_json) lifted out
    // of RegisterHubCommands. result.http_status / result.http_body /
    // result.http_error contracts are byte-for-byte preserved.
    public partial class ScriptManager
    {
        private void RegisterHttpCommands()
        {
            // ── HTTP ─────────────────────────────────────────────────────────
            // H11 / H31 / H32 — All http.* commands route through the shared _http
            // client, honor the script's CancellationToken, and cap response bodies.
            // Per-request headers go on HttpRequestMessage rather than HttpClient.DefaultRequestHeaders
            // so concurrent calls don't see one another's auth tokens.

            // http.get(url, headers?) — stores result.http_status, result.http_body, result.http_error
            _engine.RegisterCommand("http.get", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string url     = bound?.GetOrDefault<string>("Url", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string headers = bound?.GetOrDefault<string>("Headers", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(url)) return null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyHeadersToRequest(req, headers);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.http_status", ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                    await _engine.SetScriptVarAsync("result.http_body",   body);
                    await _engine.SetScriptVarAsync("result.http_error",  "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Redact key fingerprints from the exception
                    // message before persisting / logging. Mirrors the AI
                    // command path (ScriptManager.AI.cs RedactSecretsForLog).
                    string redacted = RedactSecretsForLog(ex.Message);
                    await _engine.SetScriptVarAsync("result.http_error", redacted);
                    GlobalLogger.Log($"HTTP GET Failed: {redacted}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // api.call(url) — minimal HTTP GET. Stores response body in result.api_response
            // and result.api_error on failure. Output Response socket on the API.Call node
            // resolves to {result.api_response} via ScriptExporter.ResolveOutputFromNode.
            _engine.RegisterCommand("api.call", async (args) =>
            {
                string url = _engine.CurrentBoundArgs?.GetOrDefault<string>("Url", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(url)) return null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.api_response", body);
                    await _engine.SetScriptVarAsync("result.api_error",    "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Redact key fingerprints before persisting.
                    string redacted = RedactSecretsForLog(ex.Message);
                    await _engine.SetScriptVarAsync("result.api_response", "");
                    await _engine.SetScriptVarAsync("result.api_error",    redacted);
                    GlobalLogger.Error("Script", $"api.call({url}) failed: {redacted}", ex);
                }
                return null;
            });

            // http.post(url, body, content_type?, headers?)
            _engine.RegisterCommand("http.post", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string url         = bound?.GetOrDefault<string>("Url", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string body        = bound?.GetOrDefault<string>("Body", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string contentType = bound?.GetOrDefault<string>("ContentType", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                string headers     = bound?.GetOrDefault<string>("Headers", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                if (string.IsNullOrEmpty(url)) return null;
                if (string.IsNullOrEmpty(body) && bound == null) return null;  // legacy: required body
                try
                {
                    string ct = !string.IsNullOrWhiteSpace(contentType) ? contentType : "application/json";
                    using var content = new StringContent(body ?? string.Empty, Encoding.UTF8, ct);
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    ApplyHeadersToRequest(req, headers);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string respBody = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.http_status",   ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                    await _engine.SetScriptVarAsync("result.http_body",     respBody);
                    await _engine.SetScriptVarAsync("result.http_error",    "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Redact key fingerprints before persisting.
                    string redacted = RedactSecretsForLog(ex.Message);
                    await _engine.SetScriptVarAsync("result.http_error", redacted);
                    GlobalLogger.Log($"HTTP POST Failed: {redacted}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // http.put(url, body, content_type?, headers?)
            _engine.RegisterCommand("http.put", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string url         = bound?.GetOrDefault<string>("Url", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string body        = bound?.GetOrDefault<string>("Body", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string contentType = bound?.GetOrDefault<string>("ContentType", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                string headers     = bound?.GetOrDefault<string>("Headers", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                if (string.IsNullOrEmpty(url)) return null;
                if (string.IsNullOrEmpty(body) && bound == null) return null;
                try
                {
                    string ct = !string.IsNullOrWhiteSpace(contentType) ? contentType : "application/json";
                    using var content = new StringContent(body ?? string.Empty, Encoding.UTF8, ct);
                    using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                    ApplyHeadersToRequest(req, headers);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string respBody = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.http_status",   ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                    await _engine.SetScriptVarAsync("result.http_body",     respBody);
                    await _engine.SetScriptVarAsync("result.http_error",    "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Redact key fingerprints before persisting.
                    string redacted = RedactSecretsForLog(ex.Message);
                    await _engine.SetScriptVarAsync("result.http_error", redacted);
                    GlobalLogger.Log($"HTTP PUT Failed: {redacted}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // http.patch(url, body, content_type?, headers?) — symmetric with http.put.
            // L27 follow-up: PatchHandler now exists in ExporterRegistry, so the template
            // can be safely registered upstream.
            _engine.RegisterCommand("http.patch", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string url         = bound?.GetOrDefault<string>("Url", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string body        = bound?.GetOrDefault<string>("Body", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string contentType = bound?.GetOrDefault<string>("ContentType", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                string headers     = bound?.GetOrDefault<string>("Headers", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                if (string.IsNullOrEmpty(url)) return null;
                if (string.IsNullOrEmpty(body) && bound == null) return null;
                try
                {
                    string ct = !string.IsNullOrWhiteSpace(contentType) ? contentType : "application/json";
                    using var content = new StringContent(body ?? string.Empty, Encoding.UTF8, ct);
                    using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
                    ApplyHeadersToRequest(req, headers);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string respBody = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.http_status",   ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                    await _engine.SetScriptVarAsync("result.http_body",     respBody);
                    await _engine.SetScriptVarAsync("result.http_error",    "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Redact key fingerprints before persisting.
                    string redacted = RedactSecretsForLog(ex.Message);
                    await _engine.SetScriptVarAsync("result.http_error", redacted);
                    GlobalLogger.Log($"HTTP PATCH Failed: {redacted}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // http.delete(url, body?, content_type?, headers?) — body+headers optional
            _engine.RegisterCommand("http.delete", async (args) =>
            {
                string url = _engine.CurrentBoundArgs?.GetOrDefault<string>("Url", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(url)) return null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                    // Symmetric verb support: accept optional Body/ContentType/Headers
                    // to match http.post / http.put. Manifest declares only Url; the
                    // optional trailing args stay raw-args side-channel.
                    if (args.Length >= 2 && !string.IsNullOrEmpty(args[1]))
                    {
                        string ct = args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "application/json";
                        req.Content = new StringContent(args[1], Encoding.UTF8, ct);
                    }
                    ApplyHeadersToRequest(req, args.Length >= 4 ? args[3] : "");
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string respBody = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.http_status", ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                    await _engine.SetScriptVarAsync("result.http_body",   respBody);
                    await _engine.SetScriptVarAsync("result.http_error",  "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Redact key fingerprints before persisting.
                    string redacted = RedactSecretsForLog(ex.Message);
                    await _engine.SetScriptVarAsync("result.http_error", redacted);
                    GlobalLogger.Log($"HTTP DELETE Failed: {redacted}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // http.parse_json(json, path) — dot-path extraction, stores result in result.json_value
            // Example: http.parse_json("{\"temp\":22}", "temp")  →  result.json_value = "22"
            // JSON keys may contain literal dots (e.g. "user.name"). The previous
            // tokenizer split on every '.', collapsing that into two segments. Now `\.`
            // (backslash-dot) escapes a literal dot inside a path segment so the right
            // key is addressable. Backslash-backslash (`\\`) escapes a literal backslash.
            _engine.RegisterCommand("http.parse_json", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string json = bound?.GetOrDefault<string>("Json", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string path = bound?.GetOrDefault<string>("Path", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path)) return null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var element = doc.RootElement;
                    foreach (var segment in TokenizeJsonPath(path))
                    {
                        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var child))
                            element = child;
                        else if (int.TryParse(segment, out int idx) && element.ValueKind == JsonValueKind.Array)
                            element = element[idx];
                        else { element = default; break; }
                    }
                    string result = element.ValueKind == JsonValueKind.Undefined ? ""
                        : element.ValueKind == JsonValueKind.String ? element.GetString() ?? ""
                        : element.ToString();
                    await _engine.SetScriptVarAsync("result.json_value", result);
                    await _engine.SetScriptVarAsync("result.json_error", "");
                }
                catch (Exception ex)
                {
                    // On parse failure clear the stale value and surface the message
                    // on result.json_error so the ParseJson node's Error output socket
                    // is detectable downstream (mirrors the http.* error contract).
                    await _engine.SetScriptVarAsync("result.json_value", "");
                    await _engine.SetScriptVarAsync("result.json_error", ex.Message);
                    GlobalLogger.Log($"http.parse_json failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

        }
    }
}
