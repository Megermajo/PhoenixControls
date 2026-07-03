using System;
using System.IO;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Sandbox release-note: every file.* command below resolves the
    // user-supplied path against a fixed sandbox root — Paths.HubData/files.
    // Absolute paths and any ../-bearing path that escapes the root are rejected
    // and surface result.file_error without touching disk. Hub's port-18080
    // server is the unauthenticated remote-arrival surface for these commands
    // via webhooks; widening the sandbox lets a webhook-driven script read
    // config.json (API keys) or overwrite phoenix_v3.db. Don't move the root
    // and don't expose an unsandboxed escape hatch.
    //
    // Partial split: file.* command registrations.
    // Two RegisterCommand bodies (read_text / write_text) lifted out of
    // RegisterHubCommands. result.file_content / result.file_error contract
    // is preserved byte-for-byte.
    public partial class ScriptManager
    {
        // Resolved once at static init. Paths.HubData() returns the
        // Hub's data directory (solution-anchored in dev, AppBase-relative in
        // shipped builds); we tack on "/files" for the script sandbox so user
        // scripts can never reach sibling folders (logic/, layers/, config.json).
        private static readonly string s_fileSandboxRoot = Path.GetFullPath(
            Path.Combine(Paths.HubData(), "files"));

        /// <summary>
        /// Chroot a script-supplied path under <see cref="s_fileSandboxRoot"/>.
        /// Returns <c>true</c> + a fully-qualified path that is guaranteed to live
        /// inside the sandbox. Returns <c>false</c> for empty / null input, absolute
        /// paths (drive-rooted or UNC), and any input whose normalised form escapes
        /// the root via <c>..</c> traversal. The sandbox dir itself is created
        /// on demand so a fresh install doesn't reject the first write.
        /// </summary>
        private static bool TryResolveSafeFilePath(string userPath, out string fullPath)
        {
            fullPath = "";
            if (string.IsNullOrWhiteSpace(userPath)) return false;
            // Reject absolute paths up-front. Path.IsPathRooted catches drive
            // letters ("C:\…"), drive-relative paths ("C:foo"), and rooted
            // forward-slash paths ("/foo"). It also catches UNC paths
            // ("\\server\share\…"), which is what we want.
            if (Path.IsPathRooted(userPath)) return false;
            // Defence-in-depth — also reject the explicit UNC prefix on
            // platforms where IsPathRooted's behaviour might shift.
            if (userPath.StartsWith("\\\\", StringComparison.Ordinal) ||
                userPath.StartsWith("//",   StringComparison.Ordinal)) return false;

            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine(s_fileSandboxRoot, userPath));
            }
            catch
            {
                return false;
            }

            // GetFullPath normalises out the .. segments; if the result still
            // sits under the sandbox root (with the separator boundary) we're
            // safe. The equality clause permits referring to the root folder
            // itself, which has no security impact since the file ops all
            // need a file name.
            string rootWithSep = s_fileSandboxRoot + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(combined, s_fileSandboxRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            try { Directory.CreateDirectory(s_fileSandboxRoot); } catch { /* surfaced on the file op */ }
            fullPath = combined;
            return true;
        }

        private void RegisterFileCommands()
        {
            // P3 — file.read_text(path) — reads UTF-8 contents into result.file_content;
            // exception messages (FileNotFound, UnauthorizedAccess, IO) land in result.file_error
            // with Content cleared. Path is resolved under the sandbox root
            // (Paths.HubData/files); absolute paths and ../-escapes are rejected.
            _engine.RegisterCommand("file.read_text", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string path = bound?.GetOrDefault<string>("Path", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(path))
                {
                    await _engine.SetScriptVarAsync("result.file_content", "");
                    await _engine.SetScriptVarAsync("result.file_error",   "Path is empty.");
                    return null;
                }
                if (!TryResolveSafeFilePath(path, out string resolved))
                {
                    await _engine.SetScriptVarAsync("result.file_content", "");
                    await _engine.SetScriptVarAsync("result.file_error",   "Path is outside the script file sandbox.");
                    GlobalLogger.Log($"file.read_text({path}) BLOCKED: outside sandbox '{s_fileSandboxRoot}'", "Script", LogLevel.CriticalError);
                    return null;
                }
                try
                {
                    string content = await System.IO.File.ReadAllTextAsync(resolved, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.file_content", content);
                    await _engine.SetScriptVarAsync("result.file_error",   "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.file_content", "");
                    await _engine.SetScriptVarAsync("result.file_error",   ex.Message);
                    GlobalLogger.Log($"file.read_text({path}) failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // P3 — file.write_text(path, content, append?) — writes UTF-8. Creates
            // parent directories on demand (mirrors File.AppendAllText behaviour even
            // for the overwrite path so users don't have to MkDir manually). Append=true
            // appends; otherwise the file is replaced. Path is resolved
            // under the sandbox root.
            _engine.RegisterCommand("file.write_text", async (args) =>
            {
                var bound  = _engine.CurrentBoundArgs;
                string path    = bound?.GetOrDefault<string>("Path", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string content = bound?.GetOrDefault<string>("Content", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                bool append    = (bound != null && bound.ContainsKey("Append"))
                              ? bound.Get<bool>("Append")
                              : (args.Length >= 3 && ScriptEngine.ParseTruthy(args[2]));
                if (string.IsNullOrWhiteSpace(path))
                {
                    await _engine.SetScriptVarAsync("result.file_error", "Path is empty.");
                    return null;
                }
                if (!TryResolveSafeFilePath(path, out string resolved))
                {
                    await _engine.SetScriptVarAsync("result.file_error", "Path is outside the script file sandbox.");
                    GlobalLogger.Log($"file.write_text({path}) BLOCKED: outside sandbox '{s_fileSandboxRoot}'", "Script", LogLevel.CriticalError);
                    return null;
                }
                try
                {
                    string? dir = System.IO.Path.GetDirectoryName(resolved);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    if (append)
                        await System.IO.File.AppendAllTextAsync(resolved, content, _engine.ExecutionToken).ConfigureAwait(false);
                    else
                        await System.IO.File.WriteAllTextAsync(resolved, content, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.file_error", "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.file_error", ex.Message);
                    GlobalLogger.Log($"file.write_text({path}) failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // file.read_json(path). Mirrors file.read_text but
            // additionally validates that the file content is parseable JSON.
            // On parse failure the raw content still goes to result.file_content
            // (so a script can inspect it) and result.file_error carries the
            // parser message — same "load + report" contract as read_text. We
            // intentionally do NOT lift JSON keys into the local var dict here:
            // the engine's dict semantics aren't nailed down yet (per the
            // legacy TODO note), so consumers route result.file_content into
            // http.parse_json or a future json.* surface when one lands.
            _engine.RegisterCommand("file.read_json", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string path = bound?.GetOrDefault<string>("Path", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(path))
                {
                    await _engine.SetScriptVarAsync("result.file_content", "");
                    await _engine.SetScriptVarAsync("result.file_error",   "Path is empty.");
                    return null;
                }
                if (!TryResolveSafeFilePath(path, out string resolved))
                {
                    await _engine.SetScriptVarAsync("result.file_content", "");
                    await _engine.SetScriptVarAsync("result.file_error",   "Path is outside the script file sandbox.");
                    GlobalLogger.Log($"file.read_json({path}) BLOCKED: outside sandbox '{s_fileSandboxRoot}'", "Script", LogLevel.CriticalError);
                    return null;
                }
                try
                {
                    string content = await System.IO.File.ReadAllTextAsync(resolved, _engine.ExecutionToken).ConfigureAwait(false);
                    string parseError = "";
                    try { using var doc = System.Text.Json.JsonDocument.Parse(content); }
                    catch (System.Text.Json.JsonException jex) { parseError = jex.Message; }
                    await _engine.SetScriptVarAsync("result.file_content", content);
                    await _engine.SetScriptVarAsync("result.file_error",   parseError);
                    GlobalLogger.Log(
                        parseError.Length == 0
                            ? $"file.read_json({path}) → {content.Length} chars (valid JSON)"
                            : $"file.read_json({path}) → {content.Length} chars (parse error: {parseError})",
                        "Script", LogLevel.Communication);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.file_content", "");
                    await _engine.SetScriptVarAsync("result.file_error",   ex.Message);
                    GlobalLogger.Log($"file.read_json({path}) failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // file.write_json(path, content, append?). Validates the supplied
            // content is parseable JSON BEFORE the disk write — a malformed
            // payload returns the parser error in result.file_error and the
            // file is left untouched, so a buggy script can't leave a
            // half-written / corrupt JSON file behind. Append mode appends
            // payload + newline (canonical JSON Lines shape — one record
            // per line; the file as a whole is then JSON Lines, not single
            // JSON object).
            _engine.RegisterCommand("file.write_json", async (args) =>
            {
                var bound  = _engine.CurrentBoundArgs;
                string path    = bound?.GetOrDefault<string>("Path", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string content = bound?.GetOrDefault<string>("Content", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                bool append    = (bound != null && bound.ContainsKey("Append"))
                              ? bound.Get<bool>("Append")
                              : (args.Length >= 3 && ScriptEngine.ParseTruthy(args[2]));
                if (string.IsNullOrWhiteSpace(path))
                {
                    await _engine.SetScriptVarAsync("result.file_error", "Path is empty.");
                    return null;
                }
                if (!TryResolveSafeFilePath(path, out string resolved))
                {
                    await _engine.SetScriptVarAsync("result.file_error", "Path is outside the script file sandbox.");
                    GlobalLogger.Log($"file.write_json({path}) BLOCKED: outside sandbox '{s_fileSandboxRoot}'", "Script", LogLevel.CriticalError);
                    return null;
                }
                try { using var doc = System.Text.Json.JsonDocument.Parse(content); }
                catch (System.Text.Json.JsonException jex)
                {
                    await _engine.SetScriptVarAsync("result.file_error", "Invalid JSON payload: " + jex.Message);
                    GlobalLogger.Log($"file.write_json({path}) rejected: invalid JSON ({jex.Message})", "Script", LogLevel.CriticalError);
                    return null;
                }
                try
                {
                    string? dir = System.IO.Path.GetDirectoryName(resolved);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    if (append)
                        await System.IO.File.AppendAllTextAsync(resolved, content + Environment.NewLine, _engine.ExecutionToken).ConfigureAwait(false);
                    else
                        await System.IO.File.WriteAllTextAsync(resolved, content, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.file_error", "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.file_error", ex.Message);
                    GlobalLogger.Log($"file.write_json({path}) failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });
        }
    }
}
