using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: ai.* command registrations.
    // Two RegisterCommand bodies (ai.prompt / ai.moderate) lifted out of
    // RegisterHubCommands. result.ai_response / result.ai_error /
    // result.ai_flagged / result.ai_category contracts preserved.
    public partial class ScriptManager
    {
        // Provider error bodies (OpenAI / Anthropic / Cerebras /
        // Ollama) routinely echo back the leading and/or trailing characters
        // of the failing API key inside `"error.message"` strings — partial
        // key fingerprints that, when persisted into SystemHistory via
        // GlobalLogger or written to result.ai_error and round-tripped through
        // the Vars table, end up in a SQLite file that ships out with
        // crash dumps and support uploads. Run every body that crosses a log
        // / persist boundary through this filter first.
        //
        // The patterns are deliberately broad: a missed redaction leaks a
        // credential; an over-redaction loses a couple of bytes of debug
        // detail. All keys default to the literal placeholder "[REDACTED]"
        // so logs stay scannable.
        private static readonly Regex _rxJsonKeyField =
            new Regex(@"""(?<k>(?:api[_-]?key|apikey|key|x[-_]api[-_]key|authorization|secret|access[_-]?token|bearer[_-]?token))""\s*:\s*""(?<v>[^""\\]*(?:\\.[^""\\]*)*)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxBearerToken =
            new Regex(@"Bearer\s+[A-Za-z0-9._\-+/=]{20,}",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxAnthropicKey =
            new Regex(@"sk-ant-[A-Za-z0-9_\-]{6,}",
                RegexOptions.Compiled);
        private static readonly Regex _rxOpenAIKey =
            // sk-... with at least 30 chars of base64-ish payload. Tighter than
            // Anthropic's 6 because OpenAI keys are always long; loose enough
            // to catch the `sk-proj-` / `sk-svcacct-` variants too.
            new Regex(@"sk-(?:proj-|svcacct-)?[A-Za-z0-9_\-]{30,}",
                RegexOptions.Compiled);
        // Cerebras keys follow the `csk-...` prefix shape. Same minimum
        // length heuristic as OpenAI.
        private static readonly Regex _rxCerebrasKey =
            new Regex(@"csk-[A-Za-z0-9_\-]{20,}",
                RegexOptions.Compiled);

        /// <summary>
        /// Strips API keys and bearer tokens from a provider
        /// response body before it is logged or persisted. Idempotent
        /// (running the output through it again is a no-op) and
        /// allocation-cheap on the happy path (no matches → returns the
        /// input unchanged after one Regex.IsMatch per pattern).
        /// </summary>
        internal static string RedactSecretsForLog(string? body)
        {
            if (string.IsNullOrEmpty(body)) return body ?? string.Empty;

            string s = body;
            // Order matters: do the high-entropy literal key patterns first so
            // the JSON-key replacement doesn't strip a structure that would
            // then leave a bare key embedded in the surrounding value.
            if (_rxAnthropicKey.IsMatch(s))
                s = _rxAnthropicKey.Replace(s, "sk-ant-[REDACTED]");
            if (_rxOpenAIKey.IsMatch(s))
                s = _rxOpenAIKey.Replace(s, "sk-[REDACTED]");
            if (_rxCerebrasKey.IsMatch(s))
                s = _rxCerebrasKey.Replace(s, "csk-[REDACTED]");
            if (_rxBearerToken.IsMatch(s))
                s = _rxBearerToken.Replace(s, "Bearer [REDACTED]");
            if (_rxJsonKeyField.IsMatch(s))
                s = _rxJsonKeyField.Replace(s, m => $"\"{m.Groups["k"].Value}\":\"[REDACTED]\"");
            return s;
        }

        /// <summary>
        /// Exception-walking overload of
        /// <see cref="RedactSecretsForLog(string)"/>. Walks the InnerException
        /// chain (cap 5, same bound as <see cref="GlobalLogger"/>'s cascade
        /// formatter) and applies the same redaction regex to each leaf's
        /// <c>.Message</c>. Used to build a redacted summary string for the
        /// <c>label</c> arg of <see cref="GlobalLogger.Error"/> so that
        /// AI-provider error bodies returned via exception messages can't
        /// leak partial API-key fingerprints into SystemHistory while still
        /// carrying the un-redacted stack on the in-memory ring buffer.
        /// </summary>
        internal static string RedactSecretsForLog(Exception? ex)
        {
            if (ex == null) return string.Empty;
            var sb = new StringBuilder();
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            int depth = 0;
            Exception? cur = ex;
            while (cur != null && depth < 5 && seen.Add(cur))
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(RedactSecretsForLog(cur.Message));
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Pulls the provider's human-readable failure text out of an AI error
        /// body. Both Anthropic and OpenAI put it at <c>error.message</c>; used
        /// so a guarded shape-miss raises the real reason ("rate limit exceeded")
        /// instead of a bare shape complaint. The message flows through
        /// <see cref="RedactSecretsForLog(Exception?)"/> at the catch site.
        /// </summary>
        private static string DescribeAiApiError(JsonDocument doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString() ?? "unknown error";
            }
            return "no error payload";
        }

        private void RegisterAICommands()
        {
            // ── AI ────────────────────────────────────────────────────────
            // ai.prompt(system, user, model?) — calls OpenAI or Anthropic; stores result.ai_response
            _engine.RegisterCommand("ai.prompt", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string systemPrompt = bound?.GetOrDefault<string>("SystemPrompt", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string userPrompt   = bound?.GetOrDefault<string>("UserPrompt", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string modelArg     = bound?.GetOrDefault<string>("Model", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(userPrompt)) return null;
                string model = !string.IsNullOrWhiteSpace(modelArg)
                    ? modelArg : ConfigManager.Current.DefaultAIModel;
                // max_tokens flows from AppConfig.AiMaxTokens
                // (default 2048) so the cap is tunable per install instead
                // of being hardcoded to 1024.
                int aiMaxTokens = ConfigManager.Current.AiMaxTokens > 0
                    ? ConfigManager.Current.AiMaxTokens : 2048;
                try
                {
                    string response;
                    string stopReason = "";
                    // Provider routing goes through AIProviderRouter
                    // so the prefix rules (claude- with hyphen, not bare
                    // "claude*") match the single source of truth. The bare
                    // .StartsWith("claude", ...) check this replaced would have
                    // mis-routed an attacker-crafted "claudeai" / "claude_x"
                    // model name onto the Anthropic key.
                    var (providerArm, outboundModel) = AIProviderRouter.Resolve(model);
                    if (providerArm == AIProvider.Anthropic)
                    {
                        // Anthropic Messages API
                        string apiKey = ConfigManager.Current.AnthropicKey;
                        var payload = JsonSerializer.Serialize(new
                        {
                            model = outboundModel,
                            max_tokens = aiMaxTokens,
                            system = systemPrompt,
                            messages = new[] { new { role = "user", content = userPrompt } }
                        });
                        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = content };
                        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                        using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                        string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        // An error body (rate-limit, auth failure) has no
                        // content[0].text — the unguarded GetProperty/[0] threw a
                        // shapeless KeyNotFound/IndexOutOfRange into the outer
                        // catch. Guard like the streaming/vision paths and raise
                        // the provider's own error.message through the existing
                        // result.ai_error contract instead.
                        if (doc.RootElement.TryGetProperty("content", out var contentArr)
                            && contentArr.ValueKind == JsonValueKind.Array
                            && contentArr.GetArrayLength() > 0
                            && contentArr[0].TryGetProperty("text", out var textEl)
                            && textEl.ValueKind == JsonValueKind.String)
                        {
                            response = textEl.GetString() ?? "";
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Anthropic response carried no content[0].text (HTTP {(int)resp.StatusCode}): {DescribeAiApiError(doc)}");
                        }
                        // Surface Anthropic's stop_reason
                        // (end_turn / max_tokens / stop_sequence / tool_use)
                        // so scripts can branch on cap-hit vs clean stop.
                        if (doc.RootElement.TryGetProperty("stop_reason", out var srEl) &&
                            srEl.ValueKind == JsonValueKind.String)
                        {
                            stopReason = srEl.GetString() ?? "";
                        }
                    }
                    else
                    {
                        // OpenAI Chat Completions API (fall-through for
                        // OpenAI / Ollama / Cerebras-style outbound models —
                        // ai.prompt is single-shot, so Ollama and Cerebras
                        // arms aren't wired here; that surface lives on
                        // ai.stream_text. Bare-OpenAI scripts still take
                        // this path with outboundModel == model.)
                        string apiKey = ConfigManager.Current.OpenAIApiKey;
                        var payload = JsonSerializer.Serialize(new
                        {
                            model = outboundModel,
                            max_tokens = aiMaxTokens,
                            messages = new[]
                            {
                                new { role = "system",  content = systemPrompt },
                                new { role = "user",    content = userPrompt }
                            }
                        });
                        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions") { Content = content };
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                        using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                        string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        // Same guard as the streaming path: an error body has no
                        // choices[0]. A present choice whose message.content is
                        // null (e.g. a tool-call finish) degrades to "" with
                        // finish_reason still surfaced — only a truly choice-less
                        // body is an error.
                        if (!doc.RootElement.TryGetProperty("choices", out var choicesArr)
                            || choicesArr.ValueKind != JsonValueKind.Array
                            || choicesArr.GetArrayLength() == 0)
                        {
                            throw new InvalidOperationException(
                                $"OpenAI response carried no choices[0] (HTTP {(int)resp.StatusCode}): {DescribeAiApiError(doc)}");
                        }
                        var firstChoice = choicesArr[0];
                        response = firstChoice.TryGetProperty("message", out var msgEl)
                            && msgEl.ValueKind == JsonValueKind.Object
                            && msgEl.TryGetProperty("content", out var contEl)
                            && contEl.ValueKind == JsonValueKind.String
                            ? (contEl.GetString() ?? "")
                            : "";
                        // Surface OpenAI's finish_reason as the
                        // same result.stop_reason var so authored scripts
                        // see one contract across providers (stop / length
                        // / content_filter / tool_calls / function_call).
                        if (firstChoice.TryGetProperty("finish_reason", out var frEl) &&
                            frEl.ValueKind == JsonValueKind.String)
                        {
                            stopReason = frEl.GetString() ?? "";
                        }
                    }
                    await _engine.SetScriptVarAsync("result.ai_response", response);
                    await _engine.SetScriptVarAsync("result.ai_error",    "");
                    _engine.SetLocalResultVar("result.stop_reason", stopReason);
                }
                catch (Exception ex)
                {
                    // Persist a redacted error to the
                    // script var contract, but route the original exception
                    // (with InnerException chain + stack) through
                    // GlobalLogger.Error so the in-memory ring buffer carries
                    // the full diagnostic. The label embeds the redacted
                    // summary so the user-visible row stays scrubbed.
                    string redacted = RedactSecretsForLog(ex);
                    await _engine.SetScriptVarAsync("result.ai_error", redacted);
                    GlobalLogger.Error("Script", $"ai.prompt failed (model={model}): {redacted}", ex);
                }
                return null;
            });

            // ai.generate_image(prompt, model?, size?). Calls
            // OpenAI's /v1/images/generations endpoint (DALL-E family).
            // Synchronous (no streaming surface — the API returns a single
            // image URL once generation completes). Result contract:
            //   * result.ai_image_url    — the generated image URL on success
            //   * result.ai_image_error  — error message on failure
            //   * result.ai_image_done   — "true" once the call completes
            //                              (success or failure), so scripts
            //                              that polled mid-call see a clean
            //                              edge.
            // OpenAI-only for the first slice — Stability / Replicate /
            // Anthropic (no image generation API yet) follow when there's
            // demand. Provider routing follows the same prefix pattern as
            // ai.stream_text but isn't wired here yet (single provider →
            // no need for AIProviderRouter dispatch on this command).
            _engine.RegisterCommand("ai.generate_image", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string prompt = bound?.GetOrDefault<string>("Prompt", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string model  = bound?.GetOrDefault<string>("Model", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string size   = bound?.GetOrDefault<string>("Size", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrWhiteSpace(model)) model = "dall-e-3";
                if (string.IsNullOrWhiteSpace(size))  size  = "1024x1024";

                await _engine.SetScriptVarAsync("result.ai_image_url",   "");
                await _engine.SetScriptVarAsync("result.ai_image_error", "");
                await _engine.SetScriptVarAsync("result.ai_image_done",  "false");

                if (string.IsNullOrEmpty(prompt))
                {
                    await _engine.SetScriptVarAsync("result.ai_image_error", "prompt is empty");
                    await _engine.SetScriptVarAsync("result.ai_image_done",  "true");
                    return null;
                }
                try
                {
                    string apiKey = ConfigManager.Current.OpenAIApiKey;
                    // Request b64_json so the image bytes ride in
                    // the response. The SAS URL OpenAI otherwise returns
                    // expires after one hour (Azure Blob signature), so any
                    // script that persisted result.ai_image_url for later
                    // playback found a dead link the next session. Inlining
                    // the bytes lets us write them straight to the sandbox
                    // and surface a Hub-local path the script can replay
                    // indefinitely. Older endpoints may still respond with
                    // a `url` only; we fall back to a manual download in
                    // that case so the contract degrades gracefully.
                    var payload = JsonSerializer.Serialize(new
                    {
                        model,
                        prompt,
                        n               = 1,
                        size,
                        response_format = "b64_json",
                    });
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations") { Content = content };
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        string redactedBody = RedactSecretsForLog(body);
                        await _engine.SetScriptVarAsync("result.ai_image_error", $"OpenAI HTTP {(int)resp.StatusCode}: {redactedBody}");
                        await _engine.SetScriptVarAsync("result.ai_image_done",  "true");
                        GlobalLogger.Log($"AI Image Generate Failed: HTTP {(int)resp.StatusCode}", "Script", LogLevel.CriticalError);
                        return null;
                    }

                    // Prefer b64_json; download the URL if the
                    // provider ignored response_format. Bytes get written
                    // under the file sandbox so file.* commands can read
                    // them with the same chroot rules.
                    byte[]? imageBytes = null;
                    string? b64 = AIImageResponseParser.ExtractFirstB64Json(body);
                    if (!string.IsNullOrEmpty(b64))
                    {
                        try { imageBytes = System.Convert.FromBase64String(b64); }
                        catch (FormatException fex)
                        {
                            await _engine.SetScriptVarAsync("result.ai_image_error", $"OpenAI b64_json was not valid base64: {fex.Message}");
                            await _engine.SetScriptVarAsync("result.ai_image_done",  "true");
                            return null;
                        }
                    }
                    else
                    {
                        string url = AIImageResponseParser.ExtractFirstUrl(body) ?? "";
                        if (string.IsNullOrEmpty(url))
                        {
                            await _engine.SetScriptVarAsync("result.ai_image_error", "OpenAI response missing data[0].b64_json and data[0].url");
                            await _engine.SetScriptVarAsync("result.ai_image_done",  "true");
                            return null;
                        }
                        // Download immediately — the SAS URL expires after
                        // ~1h. Streams via the shared client so the same
                        // redirect / header-strip rules apply.
                        using var dlReq = new HttpRequestMessage(HttpMethod.Get, url);
                        using var dlResp = await SendWithManualRedirectAsync(dlReq, _engine.ExecutionToken).ConfigureAwait(false);
                        if (!dlResp.IsSuccessStatusCode)
                        {
                            await _engine.SetScriptVarAsync("result.ai_image_error", $"DALL-E URL fetch failed: HTTP {(int)dlResp.StatusCode}");
                            await _engine.SetScriptVarAsync("result.ai_image_done",  "true");
                            GlobalLogger.Log($"AI Image download failed: HTTP {(int)dlResp.StatusCode}", "Script", LogLevel.CriticalError);
                            return null;
                        }
                        imageBytes = await dlResp.Content.ReadAsByteArrayAsync(_engine.ExecutionToken).ConfigureAwait(false);
                    }

                    string savedPath;
                    string sandboxRel;
                    try
                    {
                        (savedPath, sandboxRel) = PersistAiImageBytes(imageBytes!, _engine.ScriptFile);
                        await System.IO.File.WriteAllBytesAsync(savedPath, imageBytes!, _engine.ExecutionToken).ConfigureAwait(false);
                    }
                    catch (Exception wex)
                    {
                        // Persist a short message to the
                        // script var, route the full exception (chain + stack)
                        // through GlobalLogger.Error.
                        await _engine.SetScriptVarAsync("result.ai_image_error", $"Failed to persist DALL-E image: {wex.Message}");
                        await _engine.SetScriptVarAsync("result.ai_image_done",  "true");
                        GlobalLogger.Error("Script", $"ai.generate_image persist failed (model={model})", wex);
                        return null;
                    }

                    // result.ai_image_url stays the public contract — it
                    // now carries a Hub-local sandbox-relative path the
                    // script can hand to file.* commands or the HUD
                    // file proxy. result.ai_image_path is the alias for
                    // scripts that want it spelled out as a path.
                    await _engine.SetScriptVarAsync("result.ai_image_url",  sandboxRel);
                    await _engine.SetScriptVarAsync("result.ai_image_path", sandboxRel);
                    await _engine.SetScriptVarAsync("result.ai_image_done", "true");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // redacted summary to var,
                    // full exception (chain + stack) to GlobalLogger.Error.
                    string redacted = RedactSecretsForLog(ex);
                    await _engine.SetScriptVarAsync("result.ai_image_error", redacted);
                    await _engine.SetScriptVarAsync("result.ai_image_done",  "true");
                    GlobalLogger.Error("Script", $"ai.generate_image failed (model={model}): {redacted}", ex);
                }
                return null;
            });

            // ai.with_tools(systemPrompt, userPrompt, toolsJson, model?).
            // Single-shot OpenAI chat-completions with the `tools` field
            // populated. The model decides whether to answer in plain text
            // or to emit one or more tool_call entries on the assistant
            // message. Result vars are mutually exclusive on a given call:
            //   * result.ai_response   — plain-text answer (when the model
            //                            answered without calling a tool).
            //   * result.ai_tool_calls — JSON array of {id, name, arguments}
            //                            objects (when the model decided
            //                            to call a tool). `arguments` is
            //                            itself a JSON-encoded STRING —
            //                            scripts route it through
            //                            http.parse_json for fields.
            //   * result.ai_error      — failure detail.
            //   * result.ai_done       — flips when the call completes.
            //
            // The Tools arg accepts a JSON-array string of OpenAI tool
            // definitions. Pass-through to the API verbatim — OpenAI's
            // validator is the source of truth for shape errors
            // (returned as result.ai_error inside an HTTP 400 body).
            //
            // First slice is single-shot only. The multi-turn loop where
            // the script returns a tool result and the model continues
            // is a follow-up — once the script has decided what
            // to do with the calls, it would issue a second ai.with_tools
            // passing the prior assistant turn + tool results in a future
            // ToolHistoryVar arg. Authoring that multi-turn shape needs
            // a Convert.JsonArray-builder primitive that doesn't exist
            // yet, so it stays scoped out for now.
            //
            // OpenAI-only for the first slice. Anthropic uses a different
            // tool-call shape (input_schema vs parameters; tool_use
            // content blocks); routes through AIProviderRouter once added.
            _engine.RegisterCommand("ai.with_tools", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string systemPrompt    = bound?.GetOrDefault<string>("SystemPrompt", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string userPrompt      = bound?.GetOrDefault<string>("UserPrompt", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string toolsJson       = bound?.GetOrDefault<string>("Tools", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                string modelArg        = bound?.GetOrDefault<string>("Model", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                // tool_choice / parallel_tool_calls. Optional,
                // both default to "omit" so the API's own defaults stand.
                // tool_choice accepts "auto" | "none" | "any" (Anthropic
                // spelling, normalised to "required" for OpenAI) | a JSON
                // object that explicitly names a tool. parallel_tool_calls
                // accepts "true" / "false" / "" (omit).
                string toolChoiceArg   = bound?.GetOrDefault<string>("ToolChoice", ArgOrEmpty(args, 4)) ?? ArgOrEmpty(args, 4);
                string parallelToolArg = bound?.GetOrDefault<string>("ParallelToolCalls", ArgOrEmpty(args, 5)) ?? ArgOrEmpty(args, 5);
                string model = !string.IsNullOrWhiteSpace(modelArg)
                    ? modelArg
                    : (string.IsNullOrWhiteSpace(ConfigManager.Current.DefaultAIModel) ? "gpt-4o-mini" : ConfigManager.Current.DefaultAIModel);

                await _engine.SetScriptVarAsync("result.ai_response",   "");
                await _engine.SetScriptVarAsync("result.ai_tool_calls", "[]");
                await _engine.SetScriptVarAsync("result.ai_error",      "");
                await _engine.SetScriptVarAsync("result.ai_done",       "false");

                if (string.IsNullOrEmpty(userPrompt))
                {
                    await _engine.SetScriptVarAsync("result.ai_error", "userPrompt is empty");
                    await _engine.SetScriptVarAsync("result.ai_done",  "true");
                    return null;
                }
                // Validate the tools JSON shape early so we surface a clear
                // error rather than letting the API roundtrip wash the
                // message away inside an HTTP 400 body.
                JsonDocument? toolsDoc = null;
                if (!string.IsNullOrWhiteSpace(toolsJson))
                {
                    try
                    {
                        toolsDoc = JsonDocument.Parse(toolsJson);
                        if (toolsDoc.RootElement.ValueKind != JsonValueKind.Array)
                        {
                            await _engine.SetScriptVarAsync("result.ai_error", "Tools must be a JSON array");
                            await _engine.SetScriptVarAsync("result.ai_done",  "true");
                            toolsDoc.Dispose();
                            return null;
                        }
                        // Also validate each tool entry's
                        // function.parameters payload. The parameters
                        // field is itself a JSON SCHEMA object (not a
                        // string), so the test is "is it an object" —
                        // anything else gets rejected at the script
                        // level rather than wasting a round-trip. The
                        // detail error reports the offending tool name
                        // (or index when the name is missing too) so
                        // the script author can fix the call site.
                        int toolIdx = 0;
                        foreach (var tool in toolsDoc.RootElement.EnumerateArray())
                        {
                            if (tool.ValueKind != JsonValueKind.Object)
                            {
                                await _engine.SetScriptVarAsync("result.ai_error", $"Tools[{toolIdx}] must be a JSON object (function definition)");
                                await _engine.SetScriptVarAsync("result.ai_done",  "true");
                                toolsDoc.Dispose();
                                return null;
                            }
                            toolIdx++;
                        }
                    }
                    catch (JsonException ex)
                    {
                        await _engine.SetScriptVarAsync("result.ai_error", $"Tools is not valid JSON (pos {ex.BytePositionInLine}, line {ex.LineNumber}): {ex.Message}");
                        await _engine.SetScriptVarAsync("result.ai_done",  "true");
                        return null;
                    }
                }
                // Validate tool_choice up front. Accept:
                //   ""               → omit (API default)
                //   "auto" | "any" | "none" | "required"
                //                    → OpenAI accepts these as bare strings;
                //                      Anthropic spells "any" / "auto" / "tool".
                //                      We pass through to OpenAI for now (the
                //                      only provider ai.with_tools targets) but
                //                      validate against the union so a future
                //                      Anthropic arm doesn't widen the surface.
                //   { "type":"tool", "name":"..." }
                //   { "type":"function", "function":{"name":"..."} }
                //                    → both Anthropic + OpenAI shapes are
                //                      accepted; the request builder picks
                //                      the right one per provider.
                JsonDocument? toolChoiceDoc = null;
                string toolChoiceLiteral = "";
                if (!string.IsNullOrWhiteSpace(toolChoiceArg))
                {
                    string trimmed = toolChoiceArg.Trim();
                    if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                    {
                        try
                        {
                            toolChoiceDoc = JsonDocument.Parse(trimmed);
                            if (toolChoiceDoc.RootElement.ValueKind != JsonValueKind.Object)
                            {
                                await _engine.SetScriptVarAsync("result.ai_error", "ToolChoice JSON must be an object (e.g. {\"type\":\"function\",\"function\":{\"name\":\"...\"}})");
                                await _engine.SetScriptVarAsync("result.ai_done",  "true");
                                toolChoiceDoc.Dispose();
                                toolsDoc?.Dispose();
                                return null;
                            }
                            if (!toolChoiceDoc.RootElement.TryGetProperty("type", out _))
                            {
                                await _engine.SetScriptVarAsync("result.ai_error", "ToolChoice JSON object must include \"type\"");
                                await _engine.SetScriptVarAsync("result.ai_done",  "true");
                                toolChoiceDoc.Dispose();
                                toolsDoc?.Dispose();
                                return null;
                            }
                        }
                        catch (JsonException ex)
                        {
                            await _engine.SetScriptVarAsync("result.ai_error", $"ToolChoice is not valid JSON: {ex.Message}");
                            await _engine.SetScriptVarAsync("result.ai_done",  "true");
                            toolsDoc?.Dispose();
                            return null;
                        }
                    }
                    else
                    {
                        // Bare string mode. Normalise case but pin to the
                        // accepted union.
                        string lower = trimmed.ToLowerInvariant();
                        if (lower != "auto" && lower != "none" && lower != "any" && lower != "required")
                        {
                            await _engine.SetScriptVarAsync("result.ai_error",
                                $"ToolChoice must be one of \"auto\" | \"none\" | \"any\" | \"required\" | JSON object, got \"{trimmed}\"");
                            await _engine.SetScriptVarAsync("result.ai_done",  "true");
                            toolsDoc?.Dispose();
                            return null;
                        }
                        // OpenAI uses "required" rather than Anthropic's
                        // "any" — translate so authors can use either word.
                        toolChoiceLiteral = lower == "any" ? "required" : lower;
                    }
                }

                // parallel_tool_calls is a boolean. Empty → omit.
                bool? parallelToolCalls = null;
                if (!string.IsNullOrWhiteSpace(parallelToolArg))
                {
                    string s = parallelToolArg.Trim();
                    if (bool.TryParse(s, out bool b)) parallelToolCalls = b;
                    else
                    {
                        await _engine.SetScriptVarAsync("result.ai_error",
                            $"ParallelToolCalls must be \"true\" or \"false\", got \"{s}\"");
                        await _engine.SetScriptVarAsync("result.ai_done",  "true");
                        toolsDoc?.Dispose();
                        toolChoiceDoc?.Dispose();
                        return null;
                    }
                }
                try
                {
                    string apiKey = ConfigManager.Current.OpenAIApiKey;
                    // Hand-build the request body so the tools JSON splices
                    // in directly — System.Text.Json doesn't have a clean
                    // way to pass a "raw JSON" property value through
                    // anonymous-type serialization.
                    var sb = new StringBuilder();
                    sb.Append("{\"model\":");           sb.Append(JsonSerializer.Serialize(model));
                    sb.Append(",\"messages\":[");
                    sb.Append("{\"role\":\"system\",\"content\":"); sb.Append(JsonSerializer.Serialize(systemPrompt ?? ""));
                    sb.Append("},{\"role\":\"user\",\"content\":");  sb.Append(JsonSerializer.Serialize(userPrompt));
                    sb.Append("}]");
                    if (toolsDoc != null)
                    {
                        sb.Append(",\"tools\":");
                        sb.Append(toolsDoc.RootElement.GetRawText());
                    }
                    // Splice tool_choice / parallel_tool_calls when
                    // the script set them; otherwise omit so the API default
                    // applies (OpenAI defaults: tool_choice=auto when tools
                    // present, parallel_tool_calls=true).
                    if (toolChoiceDoc != null)
                    {
                        sb.Append(",\"tool_choice\":");
                        sb.Append(toolChoiceDoc.RootElement.GetRawText());
                    }
                    else if (!string.IsNullOrEmpty(toolChoiceLiteral))
                    {
                        sb.Append(",\"tool_choice\":");
                        sb.Append(JsonSerializer.Serialize(toolChoiceLiteral));
                    }
                    if (parallelToolCalls.HasValue)
                    {
                        sb.Append(",\"parallel_tool_calls\":");
                        sb.Append(parallelToolCalls.Value ? "true" : "false");
                    }
                    sb.Append('}');
                    string payload = sb.ToString();
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions") { Content = content };
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Rich error kind + Retry-After contract.
                        // Body may carry key fingerprints; redact before composing detail.
                        string redactedBody = RedactSecretsForLog(body);
                        string errKind = ClassifyAiErrorKind("OpenAI", (int)resp.StatusCode, redactedBody);
                        string detail  = ComposeAiErrorMessage("OpenAI", (int)resp.StatusCode, redactedBody, errKind);
                        await _engine.SetScriptVarAsync("result.ai_error",      detail);
                        await _engine.SetScriptVarAsync("result.ai_error_kind", errKind);
                        if ((int)resp.StatusCode == 429)
                        {
                            int retryAfterSec = ParseRetryAfterSeconds(resp);
                            await _engine.SetScriptVarAsync("result.ai_retry_after", retryAfterSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        await _engine.SetScriptVarAsync("result.ai_done",  "true");
                        GlobalLogger.Log($"AI Tools Failed: HTTP {(int)resp.StatusCode}", "Script", LogLevel.CriticalError);
                        return null;
                    }
                    var calls = AIToolCallExtractor.Extract(body);
                    if (calls.Count > 0)
                    {
                        await _engine.SetScriptVarAsync("result.ai_tool_calls", AIToolCallExtractor.Serialize(calls));
                    }
                    else
                    {
                        // No tool calls — treat as a plain answer. Empty
                        // string is fine: the script branches on
                        // result.ai_tool_calls != "[]" to decide which
                        // var to read.
                        string answer = AIChatCompletionParser.ExtractFirstChoiceContent(body) ?? "";
                        await _engine.SetScriptVarAsync("result.ai_response", answer);
                    }
                    await _engine.SetScriptVarAsync("result.ai_done", "true");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // redacted to var, full ex (chain + stack) to GlobalLogger.Error.
                    string redacted = RedactSecretsForLog(ex);
                    await _engine.SetScriptVarAsync("result.ai_error", redacted);
                    await _engine.SetScriptVarAsync("result.ai_done",  "true");
                    GlobalLogger.Error("Script", $"ai.with_tools failed (model={model}): {redacted}", ex);
                }
                finally
                {
                    toolsDoc?.Dispose();
                    toolChoiceDoc?.Dispose();
                }
                return null;
            });

            // ai.vision_describe(prompt, imageUrl, model?). Calls
            // OpenAI's chat completions with a multi-modal user message
            // (text + image_url content parts). Single-shot — the result
            // is small enough that streaming buys nothing here. Result
            // contract reuses ai.prompt's vars: result.ai_response carries
            // the answer; result.ai_error any failure detail. OpenAI vision-
            // capable models only for the first slice (gpt-4o, gpt-4o-mini,
            // gpt-4-turbo); Anthropic / Ollama vision arms follow when
            // there's demand — Anthropic's image content shape differs
            // (source.type/url instead of image_url) and would route
            // through AIProviderRouter once added.
            _engine.RegisterCommand("ai.vision_describe", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string prompt   = bound?.GetOrDefault<string>("Prompt", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string imageUrl = bound?.GetOrDefault<string>("ImageUrl", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string model    = bound?.GetOrDefault<string>("Model", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrWhiteSpace(model)) model = "gpt-4o-mini";

                await _engine.SetScriptVarAsync("result.ai_response", "");
                await _engine.SetScriptVarAsync("result.ai_error",    "");

                if (string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(imageUrl))
                {
                    await _engine.SetScriptVarAsync("result.ai_error",
                        string.IsNullOrEmpty(prompt) ? "prompt is empty" : "imageUrl is empty");
                    return null;
                }
                // Reject local-file references. OpenAI's vision API
                // happily accepts `file://` URLs but the server can't read
                // them, so it would silently 400. Worse, with a future
                // self-hosted gateway, a `file://` path could be exfiltrated
                // by an attacker who controls the prompt. Allow only public
                // https://, data:image/...;base64, and Hub's own proxy URLs
                // (http(s)://127.0.0.1 served by HUDServer / the file proxy
                // — handled by the explicit loopback allowance). Any author
                // who wants to send a local image must base64-encode it into
                // a data: URI first; convert.base64_file gives them that
                // surface today.
                if (!IsAcceptableVisionImageUrl(imageUrl))
                {
                    await _engine.SetScriptVarAsync("result.ai_error",
                        "imageUrl must be https://, http://localhost / 127.0.0.1, or a data:image/...;base64 URI. Local file:// paths are rejected; use convert.base64_file to inline a local image as a data URI.");
                    GlobalLogger.Log($"AI Vision rejected imageUrl scheme: {imageUrl.Substring(0, System.Math.Min(60, imageUrl.Length))}", "Script", LogLevel.CriticalError);
                    return null;
                }
                try
                {
                    string apiKey = ConfigManager.Current.OpenAIApiKey;
                    var payload = JsonSerializer.Serialize(new
                    {
                        model,
                        messages = new[]
                        {
                            new
                            {
                                role = "user",
                                content = new object[]
                                {
                                    new { type = "text",      text = prompt },
                                    new { type = "image_url", image_url = new { url = imageUrl } },
                                },
                            },
                        },
                    });
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions") { Content = content };
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Same kind + Retry-After contract.
                        // Redact body before composing detail to keep key fingerprints out of the persisted error.
                        string redactedBody = RedactSecretsForLog(body);
                        string errKind = ClassifyAiErrorKind("OpenAI", (int)resp.StatusCode, redactedBody);
                        string detail  = ComposeAiErrorMessage("OpenAI", (int)resp.StatusCode, redactedBody, errKind);
                        await _engine.SetScriptVarAsync("result.ai_error",      detail);
                        await _engine.SetScriptVarAsync("result.ai_error_kind", errKind);
                        if ((int)resp.StatusCode == 429)
                        {
                            int retryAfterSec = ParseRetryAfterSeconds(resp);
                            await _engine.SetScriptVarAsync("result.ai_retry_after", retryAfterSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        GlobalLogger.Log($"AI Vision Failed: HTTP {(int)resp.StatusCode}", "Script", LogLevel.CriticalError);
                        return null;
                    }
                    string answer = AIChatCompletionParser.ExtractFirstChoiceContent(body) ?? "";
                    if (string.IsNullOrEmpty(answer))
                    {
                        await _engine.SetScriptVarAsync("result.ai_error", "OpenAI response missing choices[0].message.content");
                        return null;
                    }
                    await _engine.SetScriptVarAsync("result.ai_response", answer);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // redacted to var, full ex (chain + stack) to GlobalLogger.Error.
                    string redacted = RedactSecretsForLog(ex);
                    await _engine.SetScriptVarAsync("result.ai_error", redacted);
                    GlobalLogger.Error("Script", $"ai.vision_describe failed (model={model}): {redacted}", ex);
                }
                return null;
            });

            // ai.moderate(text) — stores result.ai_flagged ("true"/"false") + result.ai_category
            _engine.RegisterCommand("ai.moderate", async (args) =>
            {
                string text = _engine.CurrentBoundArgs?.GetOrDefault<string>("Text", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(text)) return null;
                try
                {
                    string apiKey = ConfigManager.Current.OpenAIApiKey;
                    var payload = JsonSerializer.Serialize(new { input = text });
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/moderations") { Content = content };
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await SendWithManualRedirectAsync(req, _engine.ExecutionToken).ConfigureAwait(false);
                    string body = await ReadCappedAsync(resp, _engine.ExecutionToken).ConfigureAwait(false);
                    // HTTP-status check before parsing, mirroring
                    // ai.prompt / ai.generate_image. A 4xx/5xx body is not the
                    // moderation JSON shape, so JsonDocument.Parse would throw
                    // (or worse, parse a confusingly-shaped error object); fail
                    // explicitly with a redacted detail instead. The
                    // body may carry key fingerprints, so redact before it
                    // crosses the persist boundary into result.ai_error.
                    if (!resp.IsSuccessStatusCode)
                    {
                        string redactedBody = RedactSecretsForLog(body);
                        await _engine.SetScriptVarAsync("result.ai_flagged",  "false");
                        await _engine.SetScriptVarAsync("result.ai_category", "");
                        await _engine.SetScriptVarAsync("result.ai_error",    $"OpenAI HTTP {(int)resp.StatusCode}: {redactedBody}");
                        GlobalLogger.Log($"AI Moderate Failed: HTTP {(int)resp.StatusCode}", "Script", LogLevel.CriticalError);
                        return null;
                    }
                    using var doc = JsonDocument.Parse(body);
                    // Guard against a missing/empty "results"
                    // array — indexing [0] on an absent or empty array throws and the
                    // (otherwise-caught) failure left no result vars set. Surface an
                    // explicit error result instead.
                    if (!doc.RootElement.TryGetProperty("results", out var resultsArray)
                        || resultsArray.ValueKind != JsonValueKind.Array
                        || resultsArray.GetArrayLength() == 0)
                    {
                        await _engine.SetScriptVarAsync("result.ai_flagged",  "false");
                        await _engine.SetScriptVarAsync("result.ai_category", "");
                        await _engine.SetScriptVarAsync("result.ai_error",    "moderation response missing results[0]");
                        GlobalLogger.Log("ai.moderate: response missing results[0].", "Script", LogLevel.CriticalError);
                        return null;
                    }
                    var result = resultsArray[0];
                    bool flagged = result.GetProperty("flagged").GetBoolean();
                    // Find the category with the highest score
                    string topCategory = "";
                    double topScore = 0;
                    foreach (var cat in result.GetProperty("category_scores").EnumerateObject())
                    {
                        double s = cat.Value.GetDouble();
                        if (s > topScore) { topScore = s; topCategory = cat.Name; }
                    }
                    await _engine.SetScriptVarAsync("result.ai_flagged",  flagged.ToString().ToLower());
                    await _engine.SetScriptVarAsync("result.ai_category", topCategory);
                    // Clear result.ai_error on the success path so the
                    // node's Error output reads empty after a clean call,
                    // matching the sibling convention (ai.prompt line ~209).
                    await _engine.SetScriptVarAsync("result.ai_error",    "");
                }
                catch (Exception ex)
                {
                    // redacted summary in label, full ex (chain + stack) to GlobalLogger.Error.
                    // Also persist the redacted summary to result.ai_error
                    // so the node's Error output surfaces the failure inline
                    // (mirrors ai.prompt). The un-redacted exception still rides
                    // the GlobalLogger.Error ring buffer for diagnostics.
                    string redacted = RedactSecretsForLog(ex);
                    await _engine.SetScriptVarAsync("result.ai_error", redacted);
                    GlobalLogger.Error("Script", $"ai.moderate failed: {redacted}", ex);
                }
                return null;
            });

            // ai.stream_text(system, user, model?). OpenAI-only
            // for the first slice ("ship one provider at
            // a time"). Streams Server-Sent Events from
            // /v1/chat/completions with stream=true; on each delta:
            //   * accumulates result.ai_response with the cumulative text
            //   * broadcasts AI_CHUNK on Bus so HUD overlays /
            //     other Hub consumers can render the in-flight stream
            //     (e.g. a chat-overlay typing effect)
            // On completion: result.ai_response holds the final text,
            // result.ai_done = "true", result.ai_error = "". On failure:
            // result.ai_error carries the message; result.ai_done still
            // flips so consumers know the stream is closed.
            //
            // Decisions taken:
            //   * Provider first: OpenAI (most-deployed, cleanest streaming
            //     API); Anthropic + Ollama follow later.
            //   * Streaming surface: hybrid — cumulative result.ai_response
            //     for scripts that just want the final, AI_CHUNK bus events
            //     for scripts / overlays that want chunk-by-chunk.
            //   * Conversation memory: NOT in scope yet. The streaming
            //     surface lands first; memory is a follow-up that wraps this
            //     command in a Var pipe-string of prior turns.
            _engine.RegisterCommand("ai.stream_text", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string systemPrompt = bound?.GetOrDefault<string>("SystemPrompt", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string userPrompt   = bound?.GetOrDefault<string>("UserPrompt", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string modelArg     = bound?.GetOrDefault<string>("Model", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                // MemoryVar. When non-empty, names a Var
                // key whose JSON-array-of-{role,content} payload becomes the
                // chat history prepended to this request and is updated
                // after completion. Empty = single-shot (prior behaviour).
                string memoryVar    = bound?.GetOrDefault<string>("MemoryVar", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                if (string.IsNullOrEmpty(userPrompt))
                {
                    await _engine.SetScriptVarAsync("result.ai_error", "userPrompt is empty");
                    await _engine.SetScriptVarAsync("result.ai_done",  "true");
                    return null;
                }
                string model = !string.IsNullOrWhiteSpace(modelArg)
                    ? modelArg
                    : (string.IsNullOrWhiteSpace(ConfigManager.Current.DefaultAIModel) ? "gpt-4o-mini" : ConfigManager.Current.DefaultAIModel);
                // Provider routing routes through AIProviderRouter
                // so prefix rules (claude- with hyphen, ollama/, cerebras/)
                // come from one source of truth. The earlier inline
                // .StartsWith("claude", ...) check would mis-route a
                // crafted "claudeai" model name onto the Anthropic key.
                // Provider routing: claude- prefix → Anthropic,
                // ollama/ prefix → local daemon, cerebras/ prefix →
                // Cerebras OpenAI-wire-compatible API, anything else →
                // OpenAI. All four produce the same downstream surface
                // (cumulative result.ai_response + AI_CHUNK Bus events +
                // result.ai_done sentinel) so swapping providers is just a
                // Model string edit.
                var (provider, providerOutboundModel) = AIProviderRouter.Resolve(model);
                bool isAnthropic = provider == AIProvider.Anthropic;
                bool isOllama    = provider == AIProvider.Ollama;
                bool isCerebras  = provider == AIProvider.Cerebras;

                await _engine.SetScriptVarAsync("result.ai_response", "");
                await _engine.SetScriptVarAsync("result.ai_done",     "false");
                await _engine.SetScriptVarAsync("result.ai_error",    "");

                // Load prior conversation history if MemoryVar is
                // set. Read straight from DB so the lookup works
                // even when the script never wrote {var.<memoryVar>} as a
                // template reference (which would have triggered the engine's
                // preload). Empty / missing / malformed payload → empty list,
                // and the post-stream write replaces it with valid JSON.
                List<ConversationMemory.Turn> priorTurns = new();
                bool useMemory = !string.IsNullOrWhiteSpace(memoryVar);
                if (useMemory)
                {
                    string priorRaw = await DB.Instance.GetVariableAsync(memoryVar, "").ConfigureAwait(false);
                    priorTurns = ConversationMemory.Parse(priorRaw);
                }

                var sb = new StringBuilder();
                // Mid-stream persistence throttle. Writing the cumulative
                // text to the Vars table on every delta is O(chunks) DB
                // round-trips and, combined with a second sb.ToString() for
                // the bus payload, O(response²) string allocation. Coalesce
                // the DB writes to one per window; the local execution var
                // still updates on every delta (SetLocalResultVar) and the
                // AI_CHUNK bus broadcast keeps its per-delta cadence, so
                // overlay / on_bus consumers observe an unchanged stream.
                // Every terminal path persists the exact cumulative text, so
                // the Vars row ends up byte-identical to per-delta writes.
                const int DbPersistWindowMs = 250;
                long lastDbPersistTicks = 0;   // 0 → the first delta persists immediately
                int  lastDbPersistedLen = 0;   // matches the "" initialisation write above
                string cumulative = string.Empty;
                try
                {
                    string label;
                    HttpRequestMessage req;

                    if (isAnthropic)
                    {
                        // Anthropic Messages API streaming. Different SSE
                        // event shape from OpenAI: events are typed
                        // (message_start / content_block_start /
                        // content_block_delta / content_block_stop /
                        // message_delta / message_stop). Text deltas ride
                        // on content_block_delta with delta.type ==
                        // "text_delta" and delta.text carrying the chunk.
                        // System prompt rides the dedicated
                        // `system` field; prior turns go into `messages`
                        // before the new user turn.
                        string apiKey = ConfigManager.Current.AnthropicKey;
                        var anthropicMessages = new List<object>(priorTurns.Count + 1);
                        foreach (var t in priorTurns)
                        {
                            anthropicMessages.Add(new { role = t.Role, content = t.Content });
                        }
                        anthropicMessages.Add(new { role = "user", content = userPrompt });
                        // max_tokens from AppConfig.AiMaxTokens
                        // (default 2048) instead of the previous hardcoded
                        // 4096; same cap as ai.prompt so the single-shot
                        // and streaming paths agree.
                        int streamMaxTokens = ConfigManager.Current.AiMaxTokens > 0
                            ? ConfigManager.Current.AiMaxTokens : 2048;
                        var payload = JsonSerializer.Serialize(new
                        {
                            model,
                            max_tokens = streamMaxTokens,
                            stream     = true,
                            system     = systemPrompt ?? "",
                            messages   = anthropicMessages,
                        });
                        req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                        {
                            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                        };
                        req.Headers.TryAddWithoutValidation("x-api-key",         apiKey);
                        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                        label = "Anthropic";
                    }
                    else if (isOllama)
                    {
                        // Ollama /api/chat streaming. Wire format is NDJSON,
                        // not SSE — each line is a complete JSON object:
                        //   {"model":"...","message":{"role":"assistant",
                        //    "content":"chunk"},"done":false}
                        // The terminator carries `"done":true` and no
                        // message field. No API key — auth is "owns the
                        // loopback port". The "ollama/" prefix on the
                        // model is stripped before forwarding so
                        // authored Model strings stay readable.
                        // Ollama messages array gets the system
                        // prompt first, then prior turns, then the new user
                        // turn. Same shape as OpenAI / Cerebras so swapping
                        // providers doesn't reshape the prompt.
                        // Prefix already stripped by AIProviderRouter.
                        string ollamaModel = providerOutboundModel;
                        string baseUrl = (ConfigManager.Current.OllamaUrl ?? "http://localhost:11434").TrimEnd('/');
                        // Ollama merges system prompt by including it as
                        // the first message with role=system — same shape
                        // as OpenAI. Keep parity so swapping providers
                        // doesn't reshape the prompt.
                        var ollamaMessages = new List<object>(priorTurns.Count + 2)
                        {
                            new { role = "system", content = systemPrompt ?? "" },
                        };
                        foreach (var t in priorTurns)
                        {
                            ollamaMessages.Add(new { role = t.Role, content = t.Content });
                        }
                        ollamaMessages.Add(new { role = "user", content = userPrompt });
                        var payload = JsonSerializer.Serialize(new
                        {
                            model    = ollamaModel,
                            stream   = true,
                            messages = ollamaMessages,
                        });
                        req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/chat")
                        {
                            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                        };
                        label = "Ollama";
                    }
                    else
                    {
                        // OpenAI Chat Completions streaming — line-by-line
                        // SSE with `data: {choices:[{delta:{content:"..."}}]}`
                        // and a sentinel `data: [DONE]`.
                        // Cerebras rides this same parser since its API
                        // is OpenAI-wire-compatible; only the host and
                        // bearer key differ. The "cerebras/" prefix is
                        // stripped before forwarding so authored Model
                        // strings stay readable.
                        string apiKey;
                        string url;
                        string outboundModel;
                        if (isCerebras)
                        {
                            apiKey        = ConfigManager.Current.CerebrasApiKey;
                            url           = "https://api.cerebras.ai/v1/chat/completions";
                            // Prefix already stripped by AIProviderRouter.
                            outboundModel = providerOutboundModel;
                            label         = "Cerebras";
                        }
                        else
                        {
                            apiKey        = ConfigManager.Current.OpenAIApiKey;
                            url           = "https://api.openai.com/v1/chat/completions";
                            outboundModel = providerOutboundModel;
                            label         = "OpenAI";
                        }
                        // Same OpenAI / Cerebras message shape;
                        // memory turns ride between the system prompt and
                        // the new user turn.
                        var openAIMessages = new List<object>(priorTurns.Count + 2)
                        {
                            new { role = "system", content = systemPrompt ?? "" },
                        };
                        foreach (var t in priorTurns)
                        {
                            openAIMessages.Add(new { role = t.Role, content = t.Content });
                        }
                        openAIMessages.Add(new { role = "user", content = userPrompt });
                        // Same AiMaxTokens cap applied to the
                        // OpenAI / Cerebras streaming body so all providers
                        // honour the same per-install ceiling.
                        int openAiMaxTokens = ConfigManager.Current.AiMaxTokens > 0
                            ? ConfigManager.Current.AiMaxTokens : 2048;
                        var payload = JsonSerializer.Serialize(new
                        {
                            model      = outboundModel,
                            stream     = true,
                            max_tokens = openAiMaxTokens,
                            messages   = openAIMessages,
                        });
                        req = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                        };
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    }

                    // Stream via the shared _http instance (AllowAutoRedirect=false)
                    // and follow redirects manually, stripping Authorization / x-api-key /
                    // anthropic-version on cross-host hops so a malicious 3xx can't exfiltrate
                    // the bearer token. The shared client also fixes the per-call socket leak
                    // the previous `new HttpClient(...)` per invocation introduced.
                    using var reqUsing = req;
                    using var resp = await SendStreamingWithManualRedirectAsync(reqUsing, _engine.ExecutionToken, allowLoopback: isOllama).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        // Ollama 404 with body matching `model "X"
                        // not found` is the "model not installed" signal,
                        // distinct from "daemon down" (which surfaces as a
                        // transport-layer HttpRequestException, caught below).
                        // Classify so scripts can branch on result.ai_error_kind.
                        // Same status block parses Retry-After when
                        // the API returned 429 so the script can surface an
                        // accurate cool-down hint via result.ai_retry_after.
                        // Redact err body before composing so key fingerprints don't reach SystemHistory.
                        string redactedErr = RedactSecretsForLog(err);
                        string errorKind = ClassifyAiErrorKind(label, (int)resp.StatusCode, redactedErr);
                        string detail = ComposeAiErrorMessage(label, (int)resp.StatusCode, redactedErr, errorKind);
                        await _engine.SetScriptVarAsync("result.ai_error",      detail);
                        await _engine.SetScriptVarAsync("result.ai_error_kind", errorKind);
                        if ((int)resp.StatusCode == 429)
                        {
                            int retryAfterSec = ParseRetryAfterSeconds(resp);
                            await _engine.SetScriptVarAsync("result.ai_retry_after", retryAfterSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        await _engine.SetScriptVarAsync("result.ai_done",  "true");
                        GlobalLogger.Log($"AI Stream Failed ({label} {model}): {errorKind} HTTP {(int)resp.StatusCode}", "Script", LogLevel.CriticalError);
                        return null;
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync(_engine.ExecutionToken).ConfigureAwait(false);

                    // Collect the terminal stop / finish reason
                    // as the stream completes so scripts can branch on
                    // length-capped vs clean completion.
                    string stopReason = "";
                    // Error payload surfaced by Anthropic's
                    // `event: error` SSE frame. Non-empty means the stream
                    // ended abnormally; we set result.ai_error AND log
                    // through GlobalLogger.Error (no modal dialog per the
                    // repeatable-rejection feedback rule).
                    string streamErrorPayload = "";

                    if (isOllama)
                    {
                        // Ollama emits raw NDJSON, one complete JSON object
                        // per line, with no SSE prefix. StreamReader's
                        // line-by-line tokenizer handles \n / \r\n
                        // identically and the stateful UTF-8 decoder buffers
                        // straddling codepoints across socket reads, so the
                        // legacy reader stays for this provider.
                        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                        bool ollamaDone = false;
                        while (!reader.EndOfStream)
                        {
                            _engine.ExecutionToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(_engine.ExecutionToken).ConfigureAwait(false);
                            if (line == null) break;
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            if (!OllamaStreamParser.TryParseFrame(line, out string? chunk, out bool frameDone)) continue;
                            if (frameDone) ollamaDone = true;
                            if (string.IsNullOrEmpty(chunk))
                            {
                                if (ollamaDone) break;
                                continue;
                            }
                            sb.Append(chunk);
                            cumulative = sb.ToString();
                            long nowTicks = Environment.TickCount64;
                            if (nowTicks - lastDbPersistTicks >= DbPersistWindowMs)
                            {
                                lastDbPersistTicks = nowTicks;
                                lastDbPersistedLen = sb.Length;
                                await _engine.SetScriptVarAsync("result.ai_response", cumulative).ConfigureAwait(false);
                            }
                            else
                            {
                                _engine.SetLocalResultVar("result.ai_response", cumulative);
                            }
                            await Bus.Instance.BroadcastAsync(new BusMessage
                            {
                                Type    = "AI_CHUNK",
                                Source  = "Hub",
                                Target  = "*",
                                Payload = JsonSerializer.Serialize(new { chunk, cumulative }),
                            }).ConfigureAwait(false);
                            if (ollamaDone) break;
                        }
                        // Ollama signals natural close with done=true; no
                        // stop_reason field to surface.
                        if (ollamaDone) stopReason = "stop";
                    }
                    else
                    {
                        // SSE path runs through the byte-level
                        // event reader. Handles CRLF / LF line endings,
                        // multi-byte UTF-8 glyphs straddling socket reads,
                        // and surfaces the SSE event name so Anthropic
                        // `event: error` frames are no longer dropped.
                        var sse = new SseEventReader(stream);
                        while (true)
                        {
                            _engine.ExecutionToken.ThrowIfCancellationRequested();
                            SseEvent? maybeEv = await sse.ReadEventAsync(_engine.ExecutionToken).ConfigureAwait(false);
                            if (maybeEv == null) break;
                            SseEvent ev = maybeEv.Value;
                            string body = ev.Data;
                            if (body == "[DONE]") break;
                            if (string.IsNullOrEmpty(body)) continue;

                            // Anthropic surfaces typed errors as
                            // an `event: error` with the payload on `data:`.
                            // OpenAI / Cerebras don't use `event: error` —
                            // they fail at the HTTP layer (caught above) or
                            // emit an `error` field inside a `data:` chunk
                            // (caught in the JSON parse branch below).
                            if (ev.EventName == "error")
                            {
                                streamErrorPayload = body;
                                break;
                            }

                            string? chunk = null;
                            try
                            {
                                using var doc = JsonDocument.Parse(body);
                                if (isAnthropic)
                                {
                                    if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                                        continue;
                                    string? evType = typeEl.GetString();
                                    if (evType == "content_block_delta")
                                    {
                                        if (!doc.RootElement.TryGetProperty("delta", out var deltaEl)) continue;
                                        if (deltaEl.TryGetProperty("type", out var dTypeEl) &&
                                            dTypeEl.GetString() != "text_delta") continue;
                                        if (deltaEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                                            chunk = textEl.GetString();
                                    }
                                    else if (evType == "message_delta")
                                    {
                                        // Terminal `message_delta`
                                        // carries the final stop_reason
                                        // (end_turn / max_tokens /
                                        // stop_sequence / tool_use).
                                        if (doc.RootElement.TryGetProperty("delta", out var deltaEl) &&
                                            deltaEl.TryGetProperty("stop_reason", out var srEl) &&
                                            srEl.ValueKind == JsonValueKind.String)
                                        {
                                            stopReason = srEl.GetString() ?? "";
                                        }
                                        continue;
                                    }
                                    else if (evType == "error")
                                    {
                                        // Defensive: Anthropic typically
                                        // routes errors via `event: error`,
                                        // but some proxies embed the same
                                        // shape inside an unnamed data
                                        // frame with type=="error".
                                        streamErrorPayload = body;
                                        break;
                                    }
                                    else
                                    {
                                        // message_start, content_block_start
                                        // / _stop, ping, message_stop — no
                                        // text payload.
                                        continue;
                                    }
                                }
                                else
                                {
                                    // A malformed/unexpected chunk lacking
                                    // "choices"/"delta" previously threw KeyNotFoundException — which
                                    // the inner `catch (JsonException)` does NOT catch — aborting the
                                    // ENTIRE stream (and losing all later valid chunks) on one bad
                                    // frame. Use TryGetProperty so a bad chunk is skipped, matching the
                                    // defensive finish_reason/content handling just below.
                                    if (!doc.RootElement.TryGetProperty("choices", out var choices)
                                        || choices.ValueKind != JsonValueKind.Array
                                        || choices.GetArrayLength() == 0) continue;
                                    var firstChoice = choices[0];
                                    // Capture finish_reason if
                                    // present on this chunk (OpenAI puts it
                                    // on the last delta-chunk; null on
                                    // intermediate chunks).
                                    if (firstChoice.TryGetProperty("finish_reason", out var frEl) &&
                                        frEl.ValueKind == JsonValueKind.String)
                                    {
                                        stopReason = frEl.GetString() ?? stopReason;
                                    }
                                    if (!firstChoice.TryGetProperty("delta", out var delta)) continue;
                                    if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                                        chunk = contentEl.GetString();
                                }
                            }
                            catch (JsonException) { continue; }

                            if (string.IsNullOrEmpty(chunk)) continue;

                            sb.Append(chunk);
                            cumulative = sb.ToString();
                            long nowTicks = Environment.TickCount64;
                            if (nowTicks - lastDbPersistTicks >= DbPersistWindowMs)
                            {
                                lastDbPersistTicks = nowTicks;
                                lastDbPersistedLen = sb.Length;
                                await _engine.SetScriptVarAsync("result.ai_response", cumulative).ConfigureAwait(false);
                            }
                            else
                            {
                                _engine.SetLocalResultVar("result.ai_response", cumulative);
                            }
                            await Bus.Instance.BroadcastAsync(new BusMessage
                            {
                                Type    = "AI_CHUNK",
                                Source  = "Hub",
                                Target  = "*",
                                Payload = JsonSerializer.Serialize(new { chunk, cumulative }),
                            }).ConfigureAwait(false);
                        }
                    }

                    // Funnel any provider-emitted error payload to
                    // the same script-result + logger path as transport
                    // failures. No modal dialog (feedback rule:
                    // repeatable-rejection guardrails log, never pop).
                    if (!string.IsNullOrEmpty(streamErrorPayload))
                    {
                        string redactedPayload = RedactSecretsForLog(streamErrorPayload);
                        await _engine.SetScriptVarAsync("result.ai_error", $"{label} stream error: {redactedPayload}");
                        await _engine.SetScriptVarAsync("result.ai_response", cumulative);
                        await _engine.SetScriptVarAsync("result.ai_done", "true");
                        _engine.SetLocalResultVar("result.stop_reason", stopReason);
                        GlobalLogger.Log($"AI Stream Error ({label} {model}): {redactedPayload}", "Script", LogLevel.CriticalError);
                        return null;
                    }

                    await _engine.SetScriptVarAsync("result.ai_response", cumulative);
                    await _engine.SetScriptVarAsync("result.ai_done",     "true");
                    // Surface terminal stop / finish reason.
                    // Local-only (SetLocalResultVar) so it lives on the
                    // script's vars dict without persisting to the Vars
                    // table — same scope as the other AI result.* sentinels.
                    _engine.SetLocalResultVar("result.stop_reason", stopReason);
                    // Append the (user, assistant) pair to the
                    // memory var if one was named. Trimmed to the default
                    // turn cap to bound token cost on long-running streams.
                    if (useMemory)
                    {
                        var nextTurns = ConversationMemory.Append(priorTurns, userPrompt, cumulative, ConversationMemory.DefaultMaxTurns);
                        await _engine.SetScriptVarAsync(memoryVar, ConversationMemory.Serialize(nextTurns));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Persist the coalesced tail so the Vars row carries the
                    // same partial text an uncoalesced per-delta writer would
                    // have left behind at cancel time. Best-effort — the
                    // cancellation must win over a failing flush.
                    if (sb.Length > lastDbPersistedLen)
                    {
                        try { await _engine.SetScriptVarAsync("result.ai_response", cumulative); }
                        catch { /* teardown race */ }
                    }
                    throw;
                }
                catch (HttpRequestException hex)
                {
                    // Flush the coalesced tail first so the persisted partial
                    // matches what per-delta writes would have left behind.
                    if (sb.Length > lastDbPersistedLen)
                    {
                        try { await _engine.SetScriptVarAsync("result.ai_response", cumulative); }
                        catch { /* error contract writes below still run */ }
                    }
                    // Transport-layer failure (connection refused,
                    // DNS, timeout). For Ollama specifically this is the
                    // "daemon not running" signal: the loopback connect
                    // refuses before any HTTP exchange. Surface a kind so
                    // scripts can branch on result.ai_error_kind.
                    string errorKind = isOllama
                        ? "ollama_daemon_down"
                        : "transport";
                    string ollamaHost = (ConfigManager.Current.OllamaUrl ?? "http://localhost:11434");
                    // `label` is scoped inside the try; recompute it here so
                    // the diagnostic message stays accurate regardless of
                    // which provider arm raised.
                    string providerLabel = isOllama ? "Ollama"
                        : isAnthropic ? "Anthropic"
                        : isCerebras ? "Cerebras"
                        : "OpenAI";
                    string msg = isOllama
                        ? $"Ollama daemon not running on {ollamaHost} — start it with `ollama serve`."
                        : $"{providerLabel} transport error: {hex.Message}";
                    await _engine.SetScriptVarAsync("result.ai_error",      msg);
                    await _engine.SetScriptVarAsync("result.ai_error_kind", errorKind);
                    await _engine.SetScriptVarAsync("result.ai_done",  "true");
                    // HttpRequestException stacks are
                    // load-bearing for diagnosing transport faults (DNS / TLS /
                    // refused-connect frames live in InnerException). Carry the
                    // full chain on the log entry; the user-visible label still
                    // names the failing provider + kind.
                    GlobalLogger.Error("Script", $"ai.stream_text transport failed ({providerLabel} {model}): {errorKind} — {hex.Message}", hex);
                }
                catch (Exception ex)
                {
                    // Flush the coalesced tail first so the persisted partial
                    // matches what per-delta writes would have left behind.
                    if (sb.Length > lastDbPersistedLen)
                    {
                        try { await _engine.SetScriptVarAsync("result.ai_response", cumulative); }
                        catch { /* error contract writes below still run */ }
                    }
                    // Surface exception kind for scripts and redact ex.Message for persistence.
                    // The redacted summary still lands in the
                    // script var; the un-redacted exception chain rides on the
                    // GlobalLogger.Error path so SystemHistory's stack trace
                    // stays diagnostic.
                    string redacted = RedactSecretsForLog(ex);
                    string providerLabel = isOllama ? "Ollama"
                        : isAnthropic ? "Anthropic"
                        : isCerebras ? "Cerebras"
                        : "OpenAI";
                    await _engine.SetScriptVarAsync("result.ai_error",      redacted);
                    await _engine.SetScriptVarAsync("result.ai_error_kind", "exception");
                    await _engine.SetScriptVarAsync("result.ai_done",  "true");
                    GlobalLogger.Error("Script", $"ai.stream_text failed ({providerLabel} {model}): {redacted}", ex);
                }
                return null;
            });
        }

        /// <summary>
        /// Vetting for ai.vision_describe imageUrl. Allow only:
        /// <list type="bullet">
        ///   <item><description><c>https://…</c> (any host)</description></item>
        ///   <item><description><c>http://localhost</c> / <c>http://127.0.0.1</c> (Hub-local proxies)</description></item>
        ///   <item><description><c>data:image/…;base64,…</c> inline payloads</description></item>
        /// </list>
        /// Rejects <c>file://</c>, raw filesystem paths, and any other scheme.
        /// Authors that need to ship a local image must base64-encode it first
        /// via the existing convert.base64_file helper and pass a data URI.
        /// </summary>
        internal static bool IsAcceptableVisionImageUrl(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)) return false;
            if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(uri.Scheme, "http",  StringComparison.OrdinalIgnoreCase))
            {
                string host = uri.Host;
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
                if (host == "127.0.0.1" || host == "::1") return true;
                return false;
            }
            return false;
        }

        /// <summary>
        /// Derives the on-disk destination + sandbox-relative path
        /// for a generated DALL-E image. Routes under
        /// <c>{Paths.HubData}/files/ai/&lt;scriptId&gt;/&lt;timestamp&gt;.png</c>.
        /// The "files/" prefix matches the file.* command sandbox so authored
        /// scripts can read the saved image with the standard file.read_bytes
        /// surface. <paramref name="scriptFile"/> is sanitised to a folder-safe
        /// slug (path separators stripped) so a hostile ScriptFile value can't
        /// escape the sandbox.
        /// </summary>
        internal static (string AbsolutePath, string SandboxRelative) PersistAiImageBytes(byte[] bytes, string? scriptFile)
        {
            string slug = "unknown";
            if (!string.IsNullOrWhiteSpace(scriptFile))
            {
                slug = System.IO.Path.GetFileNameWithoutExtension(scriptFile) ?? "unknown";
                // Strip anything that could climb / hop folders.
                foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                    slug = slug.Replace(c, '_');
                if (string.IsNullOrWhiteSpace(slug)) slug = "unknown";
            }
            string hubFiles = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Phoenix.Controls.Shared.Core.Paths.HubData(), "files"));
            string aiDir = System.IO.Path.Combine(hubFiles, "ai", slug);
            System.IO.Directory.CreateDirectory(aiDir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
            string fileName = $"{stamp}.png";
            string absolute = System.IO.Path.Combine(aiDir, fileName);
            string rel = $"ai/{slug}/{fileName}".Replace('\\', '/');
            return (absolute, rel);
        }

        /// <summary>
        /// Classifies a non-success AI response body into a stable
        /// kind string. Lets scripts branch on `result.ai_error_kind`
        /// instead of regex-matching the human-readable message.
        /// </summary>
        internal static string ClassifyAiErrorKind(string label, int status, string body)
        {
            if (string.Equals(label, "Ollama", StringComparison.OrdinalIgnoreCase) && status == 404)
            {
                if (!string.IsNullOrEmpty(body) &&
                    body.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "ollama_model_not_found";
            }
            if (status == 429)       return "rate_limited";
            if (status == 401)       return "auth";
            if (status == 403)       return "forbidden";
            if (status == 404)       return "not_found";
            if (status >= 500)       return "server_error";
            if (status >= 400)       return "client_error";
            return "http_error";
        }

        /// <summary>
        /// Composes a script-facing error message that prepends a
        /// hint when the kind is well-known (Ollama model not installed,
        /// rate limit) and otherwise falls through to the raw HTTP body.
        /// </summary>
        internal static string ComposeAiErrorMessage(string label, int status, string body, string kind)
        {
            return kind switch
            {
                "ollama_model_not_found" =>
                    $"Ollama model not installed. Run `ollama pull <name>` to install it. Raw: {body}",
                "rate_limited" =>
                    $"{label} rate-limited (HTTP 429). Check your account quota or retry after the Retry-After window. Raw: {body}",
                "auth" =>
                    $"{label} authentication failed (HTTP 401). Verify the API key in Hub config. Raw: {body}",
                _ => $"{label} HTTP {status}: {body}",
            };
        }

        /// <summary>
        /// Parses a Retry-After response header. Accepts either an
        /// integer seconds value (per RFC 7231) or an HTTP-date. Returns the
        /// computed delay in seconds (clamped to a sane upper bound so a
        /// hostile server can't ask the script to sleep for days). Returns
        /// 0 when the header is absent or unparseable, signalling "fall
        /// back to existing exponential backoff."
        /// </summary>
        internal static int ParseRetryAfterSeconds(HttpResponseMessage resp)
        {
            const int MaxRetrySeconds = 600; // 10-minute ceiling
            if (resp.Headers.RetryAfter is null) return 0;
            var ra = resp.Headers.RetryAfter;
            if (ra.Delta.HasValue)
            {
                int s = (int)Math.Min(ra.Delta.Value.TotalSeconds, MaxRetrySeconds);
                return Math.Max(0, s);
            }
            if (ra.Date.HasValue)
            {
                TimeSpan delta = ra.Date.Value - DateTimeOffset.UtcNow;
                int s = (int)Math.Min(delta.TotalSeconds, MaxRetrySeconds);
                return Math.Max(0, s);
            }
            return 0;
        }

        /// <summary>
        /// Streaming-aware counterpart to <see cref="SendWithManualRedirectAsync"/>.
        /// Uses <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the returned
        /// <see cref="HttpResponseMessage"/> can be consumed as a live SSE / NDJSON stream,
        /// but still routes through the shared <c>_http</c> client (AllowAutoRedirect=false)
        /// and replays redirects manually, dropping the sensitive header set on cross-host
        /// hops. Same hop cap (<see cref="MaxRedirectHops"/>) and verb semantics as the
        /// non-streaming helper.
        /// </summary>
        private static async Task<HttpResponseMessage> SendStreamingWithManualRedirectAsync(
            HttpRequestMessage initial, CancellationToken ct, bool allowLoopback = false)
        {
            // SSRF gate — same pre-check pattern as the
            // non-streaming sibling. ai.stream_text resolves the provider's
            // URL from authored Model strings, so a future provider-routing
            // path could just as easily reach 127.0.0.1 / 169.254.169.254
            // without this gate. allowLoopback is set true by the Ollama
            // arm (default host http://localhost:11434) so that explicitly-
            // configured local AI path keeps working.
            string? initialUrl = initial.RequestUri?.AbsoluteUri;
            if (string.IsNullOrEmpty(initialUrl))
                throw new HttpRequestException("HTTP streaming request has no RequestUri.");
            var (preOk, preReason) = await UrlImageCache.ValidateUrlForOutboundAsync(initialUrl, ct, allowLoopback).ConfigureAwait(false);
            if (!preOk)
                throw new HttpRequestException($"SSRF gate rejected {initialUrl}: {preReason}");

            // Streaming sibling acquires the same outbound-HTTP gate
            // as SendWithManualRedirectAsync. NOTE: a long-running SSE/NDJSON
            // stream holds its semaphore slot for the entire lifetime of the
            // stream (the response body is consumed by the caller AFTER this
            // helper returns, but the slot is held via the wrapper task
            // below). That is intentional: an unbounded number of concurrent
            // open streaming connections is exactly the saturation case we
            // want to prevent. Scripts that need many parallel streams must
            // raise the cap in a follow-up AppConfig knob rather than
            // bypassing it.
            await _outboundHttpSem.WaitAsync(ct).ConfigureAwait(false);
            HttpResponseMessage? resp = null;
            try
            {
                HttpRequestMessage current = initial;
                resp = await _http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int hops = 0;
                while (IsRedirectStatus(resp.StatusCode) && resp.Headers.Location is not null && hops < MaxRedirectHops)
                {
                    hops++;
                    Uri originalHost = current.RequestUri ?? new Uri("http://_none_/");
                    Uri target = resp.Headers.Location.IsAbsoluteUri
                        ? resp.Headers.Location
                        : new Uri(originalHost, resp.Headers.Location);

                    // Re-validate every redirect target. allowLoopback
                    // propagates: Ollama-arm streams may legitimately 30x to a
                    // sibling localhost endpoint.
                    var (hopOk, hopReason) = await UrlImageCache.ValidateUrlForOutboundAsync(target.AbsoluteUri, ct, allowLoopback).ConfigureAwait(false);
                    if (!hopOk)
                    {
                        resp.Dispose();
                        if (current != initial) current.Dispose();
                        throw new HttpRequestException($"SSRF gate rejected stream redirect to {target}: {hopReason}");
                    }

                    bool crossHost = !string.Equals(originalHost.Host, target.Host, StringComparison.OrdinalIgnoreCase);

                    HttpMethod nextMethod = resp.StatusCode == System.Net.HttpStatusCode.SeeOther
                        ? HttpMethod.Get
                        : current.Method;

                    var next = new HttpRequestMessage(nextMethod, target);
                    foreach (var h in current.Headers)
                    {
                        if (crossHost && _sensitiveCrossHostHeaders.Contains(h.Key))
                        {
                            GlobalLogger.Log(
                                $"AI stream redirect: stripping sensitive header '{h.Key}' on cross-host hop ({originalHost.Host} → {target.Host}).",
                                "Script", LogLevel.Communication);
                            continue;
                        }
                        next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    if (nextMethod != HttpMethod.Get && current.Content is not null)
                    {
                        next.Content = current.Content;
                    }

                    resp.Dispose();
                    if (current != initial) current.Dispose();
                    current = next;
                    resp = await _http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }

                if (current != initial) current.Dispose();
                // Wrap so the semaphore is released when the caller disposes
                // the response (i.e. when the stream is fully consumed or
                // abandoned). HttpResponseMessage owns its content stream,
                // so disposing the wrapper after the caller's `using var
                // resp = ...` closes the stream and releases the slot in
                // the same lexical scope as the read loop.
                var wrapped = new SemaphoreReleasingHttpResponse(resp, _outboundHttpSem);
                resp = null;  // prevent the catch block from releasing twice
                return wrapped;
            }
            catch
            {
                resp?.Dispose();
                // Release can throw ObjectDisposedException
                // if the semaphore was disposed during shutdown; swallow that race.
                try { _outboundHttpSem.Release(); } catch (ObjectDisposedException) { }
                throw;
            }
        }

        /// <summary>
        /// HttpResponseMessage subclass that releases an external
        /// SemaphoreSlim once disposed. Used by SendStreamingWithManualRedirectAsync
        /// so the outbound-HTTP concurrency slot is held for the full lifetime
        /// of a streaming response (header read → caller consumes body → caller
        /// disposes), not just the SendAsync call.
        ///
        /// Implementation note: the wrapper shares the inner response's Content
        /// reference so the caller's `resp.Content.ReadAsStreamAsync(...)` sees
        /// the live SSE/NDJSON byte stream. base.Dispose() disposes that shared
        /// Content; HttpContent.Dispose is idempotent so the explicit
        /// _inner.Dispose() that follows is a no-op on Content (it cleans up
        /// any other inner state, e.g. version trailers). The semaphore is
        /// released exactly once via Interlocked.Exchange so a double-Dispose
        /// (caller's `using` + finalizer) can't over-release the slot count.
        /// </summary>
        private sealed class SemaphoreReleasingHttpResponse : HttpResponseMessage
        {
            private readonly HttpResponseMessage _inner;
            private readonly SemaphoreSlim _sem;
            private int _released;

            public SemaphoreReleasingHttpResponse(HttpResponseMessage inner, SemaphoreSlim sem)
            {
                _inner = inner;
                _sem   = sem;
                // Mirror the inner response so consumers reading
                // StatusCode / Headers / Content see the real values.
                StatusCode     = inner.StatusCode;
                ReasonPhrase   = inner.ReasonPhrase;
                Version        = inner.Version;
                RequestMessage = inner.RequestMessage;
                Content        = inner.Content;
                foreach (var h in inner.Headers)
                    Headers.TryAddWithoutValidation(h.Key, h.Value);
                foreach (var h in inner.TrailingHeaders)
                    TrailingHeaders.TryAddWithoutValidation(h.Key, h.Value);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && System.Threading.Interlocked.Exchange(ref _released, 1) == 0)
                {
                    // base.Dispose disposes the shared Content reference, so
                    // _inner.Dispose's Content cleanup is a no-op. We still
                    // call it for any other state the inner instance holds.
                    base.Dispose(disposing);
                    try { _inner.Dispose(); } catch { /* defensive */ }
                    try { _sem.Release(); }   catch { /* defensive */ }
                    return;
                }
                base.Dispose(disposing);
            }
        }
    }
}
