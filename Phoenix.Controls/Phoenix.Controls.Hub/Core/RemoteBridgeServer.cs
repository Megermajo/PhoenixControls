using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Viewer-roadmap Slice 0 — Hub-hosted REST + WebSocket bridge for
    /// <c>Phoenix.Controls.Viewer</c>. Lives on a dedicated port (default 18082)
    /// so it never mixes with the trusted-loopback <c>Bus</c> (18081) or
    /// <c>HUDServer</c> (18080); see Viewer_Roadmap.md §"Architecture summary"
    /// for the four invariants that govern this surface.
    ///
    /// Endpoint shape:
    ///   • <c>POST /pair/begin</c>        → <see cref="HandlePairBeginAsync"/>
    ///   • <c>POST /pair/complete</c>     → <see cref="HandlePairCompleteAsync"/>
    ///   • <c>GET  /api/version</c>       → schema + hubVersion
    ///   • <c>GET  /api/snapshot</c>      → bootstrap blob (auth)
    ///   • <c>GET  /api/db/tables</c>     → User_* + System table names (auth)
    ///   • <c>GET  /api/db/table/{name}</c> → table contents (auth)
    ///   • <c>POST /api/db/row</c>        → insert (auth, User_* only)
    ///   • <c>PATCH /api/db/cell</c>      → update cell (auth, User_* only)
    ///   • <c>DELETE /api/db/row</c>      → delete row (auth, User_* only)
    ///   • <c>POST /api/scripts/{file}/enable</c> (auth)
    ///   • <c>POST /api/process/{name}/{start|stop}</c> (auth)
    ///   • <c>POST /api/giveaway/draw</c> (auth, atomic — Slice 3)
    ///   • <c>WS   /ws</c>                → push fanout (bearer in subprotocol)
    /// </summary>
    public sealed class RemoteBridgeServer : IAsyncDisposable, IDisposable
    {
        public const int    SchemaVersion = 1;
        public const string HubVersion    = "0.1.0";

        private readonly RemoteAuthManager _auth;
        private readonly RemoteSessionRegistry _sessions;

        // Per-IP sliding-window rate limit on /pair/complete. A single
        // 6-digit code has ~1M brute-force space; without throttling a LAN
        // attacker can burn through the keyspace inside the TTL window. Cap at
        // 10 attempts per minute per source IP; on exceed return 429 so the
        // Viewer surfaces "Too many attempts, slow down" rather than silently
        // failing.
        private const int PairCompleteMaxAttemptsPerMinute = 10;
        private static readonly TimeSpan PairCompleteWindow = TimeSpan.FromMinutes(1);
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _pairCompleteAttempts = new();
        private readonly object _pairCompleteSweepLock = new();
        private DateTime _pairCompleteLastSweep = DateTime.MinValue;

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private string? _activePrefix;

        public bool IsRunning => _listener is { IsListening: true };
        public string? ActivePrefix => _activePrefix;
        public RemoteAuthManager Auth => _auth;
        public RemoteSessionRegistry Sessions => _sessions;

        public RemoteBridgeServer() : this(new RemoteAuthManager(), new RemoteSessionRegistry()) { }

        public RemoteBridgeServer(RemoteAuthManager auth, RemoteSessionRegistry sessions)
        {
            _auth     = auth     ?? throw new ArgumentNullException(nameof(auth));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;
            await _auth.EnsureSchemaAsync().ConfigureAwait(false);

            string scheme = string.IsNullOrWhiteSpace(ConfigManager.Current.RemoteTlsCertPath) ? "http" : "https";
            string host   = string.IsNullOrWhiteSpace(ConfigManager.Current.RemoteBindHost)
                              ? "127.0.0.1"
                              : ConfigManager.Current.RemoteBindHost;
            int    port   = ConfigManager.Current.RemotePort > 0 ? ConfigManager.Current.RemotePort : 18082;
            string prefix = $"{scheme}://{host}:{port}/";

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                GlobalLogger.Log(
                    $"RemoteBridgeServer: failed to bind '{prefix}': {ex.Message}. " +
                    "Try a different RemoteBindHost / RemotePort, or run `netsh http add urlacl` for the URL.",
                    "RemoteBridge", LogLevel.CriticalError);
                _listener = null;
                _cts.Dispose();
                _cts = null;
                return;
            }

            _activePrefix = prefix;
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            GlobalLogger.Log(
                $"RemoteBridgeServer listening on {prefix}",
                "RemoteBridge", LogLevel.System);

            // Pairing IDs and bearer tokens travel over this socket
            // verbatim. Loopback-only deployments are safe (no network egress);
            // any non-loopback bind without HTTPS leaks the bearer to anyone
            // sniffing the LAN/Wi-Fi segment.
            //
            // AppConfig.RemotePairingRequiresHttps (added by this
            // sprint, default true) now hard-refuses to start when this would
            // be unsafe. Operator can flip the toggle off to permit plaintext-on-
            // LAN (NOT recommended); otherwise they must configure
            // RemoteTlsCertPath or restrict RemoteBindHost to loopback. The
            // refused-start path tears down the listener+cts we provisioned
            // above so the IsRunning probe reflects the actual state.
            bool isHttp = string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase);
            bool isLoopback = string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                              || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(host, "::1", StringComparison.Ordinal);
            if (isHttp && !isLoopback)
            {
                if (ConfigManager.Current.RemotePairingRequiresHttps)
                {
                    GlobalLogger.Log(
                        $"RemoteBridgeServer: refusing to start on '{prefix}' — non-loopback HTTP bind blocked by AppConfig.RemotePairingRequiresHttps=true. " +
                        "Configure AppConfig.RemoteTlsCertPath to a PFX cert (and `netsh http add sslcert` for the URL) to enable HTTPS, " +
                        "OR restrict RemoteBindHost to 127.0.0.1 for loopback-only, " +
                        "OR (NOT recommended) set RemotePairingRequiresHttps=false to permit plaintext-on-LAN.",
                        "RemoteBridge", LogLevel.CriticalError);

                    // Tear down what we just provisioned. The accept task DID
                    // start a few lines above (so cancelling _cts is what makes
                    // its loop unwind); await its completion before disposing
                    // the CTS to avoid the ObjectDisposedException race.
                    try { _cts.Cancel(); }     catch { }
                    try { _listener.Stop(); }  catch { }
                    try { _listener.Close(); } catch { }
                    _listener = null;
                    var refusedTask = _acceptTask;
                    _acceptTask = null;
                    if (refusedTask is not null)
                    {
                        try { await refusedTask.ConfigureAwait(false); }
                        catch { /* shutdown best-effort */ }
                    }
                    try { _cts.Dispose(); } catch { }
                    _cts = null;
                    _activePrefix = null;
                    return;
                }

                GlobalLogger.Log(
                    $"RemoteBridgeServer: SECURITY WARNING — bound to non-loopback host '{host}' over plaintext HTTP " +
                    "(AppConfig.RemotePairingRequiresHttps=false explicitly opted in). " +
                    "Pairing codes and bearer tokens will travel unencrypted on this segment. " +
                    "Configure AppConfig.RemoteTlsCertPath to a PFX cert and run `netsh http add sslcert` " +
                    "for the URL to enable HTTPS, or restrict RemoteBindHost to 127.0.0.1 for loopback-only.",
                    "RemoteBridge", LogLevel.CriticalError);
            }
            else if (isHttp)
            {
                GlobalLogger.Log(
                    "RemoteBridgeServer: running over plaintext HTTP on loopback — safe for " +
                    "same-host pairing but Viewer-over-LAN requires TLS (set AppConfig.RemoteTlsCertPath).",
                    "RemoteBridge", LogLevel.System);
            }
        }

        public async Task StopAsync()
        {
            if (_cts != null) try { _cts.Cancel(); } catch { }
            if (_listener != null)
            {
                try { _listener.Stop(); }   catch { }
                try { _listener.Close(); }  catch { }
                _listener = null;
            }
            try { await _sessions.CloseAllAsync("server stopping").ConfigureAwait(false); } catch { }
            if (_acceptTask != null)
            {
                try { await _acceptTask.ConfigureAwait(false); } catch { }
                _acceptTask = null;
            }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _activePrefix = null;
            GlobalLogger.Log("RemoteBridgeServer stopped.", "RemoteBridge", LogLevel.System);
        }

        /// <summary>
        /// DisposeAsync is the supported teardown for WinUI/STA
        /// callers. <see cref="StopAsync"/> awaits a WS accept loop and a
        /// session-broadcast drain, both of which schedule continuations on the
        /// captured SynchronizationContext; calling <c>.GetAwaiter().GetResult()</c>
        /// on the UI thread deadlocks the dispatch loop. Prefer
        /// <c>await server.DisposeAsync()</c>.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch { /* shutdown is best-effort */ }
        }

        /// <summary>
        /// Synchronous <see cref="IDisposable.Dispose"/> compatibility
        /// shim. The previous implementation was
        /// <c>StopAsync().GetAwaiter().GetResult()</c>, a WinUI deadlock surface
        /// whenever the call originated on the UI thread (Hub shutdown via the
        /// pillar-launcher path does exactly that). Fire-and-forget the drain
        /// on a background task instead — the listener stops synchronously
        /// before yielding, so the port unbinds even though session cleanup
        /// continues asynchronously. Production callers should prefer
        /// <see cref="DisposeAsync"/>.
        /// </summary>
        public void Dispose()
        {
            // Stop the listener synchronously so the port is released even if
            // the background drain stalls; the rest of StopAsync (session
            // close, accept-loop await, cts cleanup) runs on a worker so we
            // don't pump messages on the caller's SyncContext.
            try
            {
                if (_cts != null) try { _cts.Cancel(); } catch { }
                if (_listener != null)
                {
                    try { _listener.Stop(); }  catch { }
                    try { _listener.Close(); } catch { }
                    _listener = null;
                }
            }
            catch { }

            // Background-drain the remainder. SafeRunAsync routes faults to
            // GlobalLogger.Error rather than leaving an unobserved Task.
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => DrainAfterDisposeAsync(),
                "RemoteBridge", "Dispose drain");
        }

        private async Task DrainAfterDisposeAsync()
        {
            try { await _sessions.CloseAllAsync("server stopping").ConfigureAwait(false); } catch { }
            if (_acceptTask != null)
            {
                try { await _acceptTask.ConfigureAwait(false); } catch { }
                _acceptTask = null;
            }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _activePrefix = null;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener is { IsListening: true })
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    GlobalLogger.Error("RemoteBridge", "accept loop exception", ex);
                    continue;
                }
                // Route per-request dispatch through SafeRunAsync so an unhandled
                // fault inside DispatchAsync surfaces in the log instead of being
                // dropped on the floor as an unobserved Task fault.
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => DispatchAsync(ctx, ct),
                    "RemoteBridge", "request dispatch");
            }
        }

        private async Task DispatchAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                if (ctx.Request.IsWebSocketRequest)
                {
                    await HandleWebSocketAsync(ctx, ct).ConfigureAwait(false);
                    return;
                }

                string method = ctx.Request.HttpMethod ?? "";
                string path   = ctx.Request.Url?.AbsolutePath ?? "/";

                // Public endpoints — no bearer required.
                if (method == "GET" && path == "/api/version")     { await WriteVersionAsync(ctx).ConfigureAwait(false); return; }
                if (method == "POST" && path == "/pair/begin")     { await HandlePairBeginAsync(ctx).ConfigureAwait(false); return; }
                if (method == "POST" && path == "/pair/complete")  { await HandlePairCompleteAsync(ctx).ConfigureAwait(false); return; }

                // Everything else is authed.
                string? deviceId = await AuthorizeAsync(ctx).ConfigureAwait(false);
                if (deviceId is null) { Write(ctx, 401, "Unauthorized"); return; }

                if (method == "GET"    && path == "/api/snapshot")       { await HandleSnapshotAsync(ctx, deviceId).ConfigureAwait(false); return; }
                if (method == "GET"    && path == "/api/db/tables")       { await HandleDbTablesAsync(ctx, deviceId).ConfigureAwait(false); return; }
                if (method == "GET"    && path.StartsWith("/api/db/table/", StringComparison.Ordinal))
                                                                          { await HandleDbTableContentsAsync(ctx, deviceId, path.Substring("/api/db/table/".Length)).ConfigureAwait(false); return; }
                if (method == "POST"   && path == "/api/db/row")          { await HandleDbInsertAsync(ctx, deviceId).ConfigureAwait(false); return; }
                if (method == "PATCH"  && path == "/api/db/cell")         { await HandleDbCellAsync(ctx, deviceId).ConfigureAwait(false); return; }
                if (method == "DELETE" && path == "/api/db/row")          { await HandleDbDeleteRowAsync(ctx, deviceId).ConfigureAwait(false); return; }
                if (method == "POST"   && path.StartsWith("/api/scripts/", StringComparison.Ordinal) && path.EndsWith("/enable", StringComparison.Ordinal))
                                                                          { await HandleScriptEnableAsync(ctx, deviceId, path).ConfigureAwait(false); return; }
                if (method == "POST"   && path.StartsWith("/api/process/", StringComparison.Ordinal))
                                                                          { await HandleProcessLifecycleAsync(ctx, deviceId, path).ConfigureAwait(false); return; }
                if (method == "POST"   && path == "/api/giveaway/draw")   { await HandleGiveawayDrawAsync(ctx, deviceId).ConfigureAwait(false); return; }

                Write(ctx, 404, "Not Found");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RemoteBridge", "dispatch exception", ex);
                try { Write(ctx, 500, "Internal Server Error"); } catch { }
            }
        }

        // ── Public endpoints ──────────────────────────────────────────────

        private static async Task WriteVersionAsync(HttpListenerContext ctx)
        {
            await WriteJsonAsync(ctx, 200, new { schema = SchemaVersion, hubVersion = HubVersion }).ConfigureAwait(false);
        }

        private async Task HandlePairBeginAsync(HttpListenerContext ctx)
        {
            var grant = _auth.BeginPairing();
            await WriteJsonAsync(ctx, 200, new
            {
                pairingId = grant.PairingId,
                code      = grant.Code,
                expiresAt = grant.ExpiresAt.ToString("O"),
            }).ConfigureAwait(false);
        }

        private async Task HandlePairCompleteAsync(HttpListenerContext ctx)
        {
            // Per-IP rate limit. Apply BEFORE body parse so a
            // body-less brute-force loop doesn't get a free pass on the
            // single-use grant CAS.
            string remoteIp = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            if (!TryRegisterPairCompleteAttempt(remoteIp))
            {
                GlobalLogger.Log(
                    $"RemoteBridge: /pair/complete throttled — {remoteIp} exceeded " +
                    $"{PairCompleteMaxAttemptsPerMinute}/min.",
                    "RemoteBridge", LogLevel.CriticalError);
                Write(ctx, 429, "Too Many Requests");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx).ConfigureAwait(false);
            if (body is null) { Write(ctx, 400, "Bad Request"); return; }
            string pairingId    = body.Value.TryGetProperty("pairingId", out var p)    ? p.GetString() ?? "" : "";
            string code         = body.Value.TryGetProperty("code", out var c)         ? c.GetString() ?? "" : "";
            string deviceLabel  = body.Value.TryGetProperty("deviceLabel", out var dl) ? dl.GetString() ?? "" : "";

            string? token = await _auth.CompletePairingAsync(pairingId, code, deviceLabel).ConfigureAwait(false);
            if (token is null) { Write(ctx, 401, "pairing rejected"); return; }

            int sep = token.IndexOf(':');
            string deviceId = sep > 0 ? token.Substring(0, sep) : token;
            await WriteJsonAsync(ctx, 200, new { deviceId, token }).ConfigureAwait(false);
        }

        /// <summary>
        /// Sliding-window rate-limit gate for <c>/pair/complete</c>.
        /// Returns <c>true</c> when the attempt is within budget and has been
        /// recorded; returns <c>false</c> when the IP has exceeded
        /// <see cref="PairCompleteMaxAttemptsPerMinute"/> in the most recent
        /// <see cref="PairCompleteWindow"/>. Per-IP buckets are pruned lazily on
        /// each attempt (own bucket) plus a coarse global sweep every minute so
        /// the dictionary stays bounded under churn.
        /// </summary>
        private bool TryRegisterPairCompleteAttempt(string remoteIp)
        {
            DateTime now = DateTime.UtcNow;
            DateTime cutoff = now - PairCompleteWindow;
            var bucket = _pairCompleteAttempts.GetOrAdd(remoteIp, _ => new Queue<DateTime>());
            lock (bucket)
            {
                while (bucket.Count > 0 && bucket.Peek() < cutoff)
                    bucket.Dequeue();
                if (bucket.Count >= PairCompleteMaxAttemptsPerMinute)
                {
                    MaybeSweepAttemptBuckets(now);
                    return false;
                }
                bucket.Enqueue(now);
            }
            MaybeSweepAttemptBuckets(now);
            return true;
        }

        private void MaybeSweepAttemptBuckets(DateTime now)
        {
            // Drop wholly-expired buckets every minute. Cheap walk — the map
            // is keyed by remote-IP so cardinality stays small in practice.
            if ((now - _pairCompleteLastSweep) < PairCompleteWindow) return;
            if (!Monitor.TryEnter(_pairCompleteSweepLock)) return;
            try
            {
                if ((now - _pairCompleteLastSweep) < PairCompleteWindow) return;
                _pairCompleteLastSweep = now;
                DateTime cutoff = now - PairCompleteWindow;
                foreach (var kv in _pairCompleteAttempts)
                {
                    lock (kv.Value)
                    {
                        while (kv.Value.Count > 0 && kv.Value.Peek() < cutoff)
                            kv.Value.Dequeue();
                        if (kv.Value.Count == 0)
                            _pairCompleteAttempts.TryRemove(kv.Key, out _);
                    }
                }
            }
            finally { Monitor.Exit(_pairCompleteSweepLock); }
        }

        // ── Authed endpoints ──────────────────────────────────────────────

        private async Task HandleSnapshotAsync(HttpListenerContext ctx, string deviceId)
        {
            // Scripts
            var scripts = ScriptRegistry.Instance.GetAll()
                .Select(s => new { file = s.FileName, enabled = ScriptRegistry.Instance.IsEnabled(s.FileName) })
                .ToList();

            // Layers — id + active state from the registry. Thin projection so the
            // Viewer doesn't need to know Layer's full shape.
            var layers = LayerRegistry.Instance.GetRegisteredLayerIds()
                .Select(id => new { id, active = LayerRegistry.Instance.IsLayerActive(id) })
                .ToList();

            var dbTables = await DB.Instance.GetUserTablesAsync().ConfigureAwait(false);
            var dbBucket = new
            {
                User   = dbTables.Where(t => t.StartsWith("User_", StringComparison.OrdinalIgnoreCase)).ToList(),
                System = dbTables.Where(t => !t.StartsWith("User_", StringComparison.OrdinalIgnoreCase)).ToList(),
            };

            var pairedDevices = await DB.Instance.ListPairedDevicesAsync().ConfigureAwait(false);
            var caller = pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);

            await WriteJsonAsync(ctx, 200, new
            {
                schema      = SchemaVersion,
                hubVersion  = HubVersion,
                Scripts     = scripts,
                Layers      = layers,
                DbTables    = dbBucket,
                PairedDevices = new
                {
                    self        = caller is null ? null : new { caller.DeviceId, caller.Label },
                    otherCount  = caller is null ? pairedDevices.Count : pairedDevices.Count - 1,
                },
            }).ConfigureAwait(false);
        }

        private async Task HandleDbTablesAsync(HttpListenerContext ctx, string deviceId)
        {
            var tables = await DB.Instance.GetUserTablesAsync().ConfigureAwait(false);
            var split = new
            {
                User   = tables.Where(t => t.StartsWith("User_", StringComparison.OrdinalIgnoreCase)).ToList(),
                System = tables.Where(t => !t.StartsWith("User_", StringComparison.OrdinalIgnoreCase)).ToList(),
            };
            await WriteJsonAsync(ctx, 200, split).ConfigureAwait(false);
        }

        private async Task HandleDbTableContentsAsync(HttpListenerContext ctx, string deviceId, string tableName)
        {
            if (!IsSafeIdentifier(tableName)) { Write(ctx, 400, "invalid table"); return; }
            try
            {
                var dt = await DB.Instance.GetTableDataAsync(tableName).ConfigureAwait(false);
                var cols = new List<string>();
                foreach (System.Data.DataColumn c in dt.Columns) cols.Add(c.ColumnName);
                var rows = new List<Dictionary<string, string>>();
                foreach (System.Data.DataRow r in dt.Rows)
                {
                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var col in cols)
                        row[col] = r[col]?.ToString() ?? "";
                    rows.Add(row);
                }
                await WriteJsonAsync(ctx, 200, new { columns = cols, rows }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Write(ctx, 404, ex.Message);
            }
        }

        private async Task HandleDbInsertAsync(HttpListenerContext ctx, string deviceId)
        {
            var body = await ReadBodyAsJsonAsync(ctx).ConfigureAwait(false);
            if (body is null) { Write(ctx, 400, "Bad Request"); return; }
            string table = body.Value.TryGetProperty("table", out var tEl) ? tEl.GetString() ?? "" : "";
            if (!IsUserTable(table))
            {
                await RemoteAuditLogger.LogAsync(deviceId, "INSERT", table, "", null, null, RemoteAuditLogger.ResultRejected).ConfigureAwait(false);
                Write(ctx, 403, "table is not a User_* table"); return;
            }
            if (!body.Value.TryGetProperty("values", out var valsEl) || valsEl.ValueKind != JsonValueKind.Object)
            {
                Write(ctx, 400, "values object required"); return;
            }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in valsEl.EnumerateObject())
                values[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
            try
            {
                long rowid = await DB.Instance.InsertUserRowAsync(table, values).ConfigureAwait(false);
                string after = JsonSerializer.Serialize(values);
                await RemoteAuditLogger.LogAsync(deviceId, "INSERT", table, rowid.ToString(), null, after, RemoteAuditLogger.ResultOk).ConfigureAwait(false);
                await BroadcastDbChangedAsync(table, "INSERT", rowid).ConfigureAwait(false);
                await WriteJsonAsync(ctx, 200, new { rowid }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RemoteAuditLogger.LogAsync(deviceId, "INSERT", table, "", null, JsonSerializer.Serialize(values), RemoteAuditLogger.ResultFailed).ConfigureAwait(false);
                Write(ctx, 500, ex.Message);
            }
        }

        private async Task HandleDbCellAsync(HttpListenerContext ctx, string deviceId)
        {
            var body = await ReadBodyAsJsonAsync(ctx).ConfigureAwait(false);
            if (body is null) { Write(ctx, 400, "Bad Request"); return; }
            string table  = body.Value.TryGetProperty("table",  out var tEl) ? tEl.GetString() ?? "" : "";
            string column = body.Value.TryGetProperty("column", out var cEl) ? cEl.GetString() ?? "" : "";
            long   rowId  = body.Value.TryGetProperty("rowid",  out var rEl) && rEl.TryGetInt64(out var rid) ? rid : 0;
            string value  = body.Value.TryGetProperty("value",  out var vEl) ? vEl.GetString() ?? "" : "";

            if (!IsUserTable(table))
            {
                await RemoteAuditLogger.LogAsync(deviceId, "UPDATE", table, rowId.ToString(), null, null, RemoteAuditLogger.ResultRejected).ConfigureAwait(false);
                Write(ctx, 403, "table is not a User_* table"); return;
            }
            try
            {
                string before = await DB.Instance.GetCellAsync(table, rowId, column).ConfigureAwait(false);
                await DB.Instance.SetCellAsync(table, rowId, column, value).ConfigureAwait(false);
                await RemoteAuditLogger.LogAsync(deviceId, "UPDATE", table, $"{rowId}/{column}", before, value, RemoteAuditLogger.ResultOk).ConfigureAwait(false);
                await BroadcastDbChangedAsync(table, "UPDATE", rowId).ConfigureAwait(false);
                Write(ctx, 200, "ok");
            }
            catch (Exception ex)
            {
                await RemoteAuditLogger.LogAsync(deviceId, "UPDATE", table, $"{rowId}/{column}", null, value, RemoteAuditLogger.ResultFailed).ConfigureAwait(false);
                Write(ctx, 500, ex.Message);
            }
        }

        private async Task HandleDbDeleteRowAsync(HttpListenerContext ctx, string deviceId)
        {
            var body = await ReadBodyAsJsonAsync(ctx).ConfigureAwait(false);
            if (body is null) { Write(ctx, 400, "Bad Request"); return; }
            string table = body.Value.TryGetProperty("table", out var tEl) ? tEl.GetString() ?? "" : "";
            long   rowId = body.Value.TryGetProperty("rowid", out var rEl) && rEl.TryGetInt64(out var rid) ? rid : 0;
            if (!IsUserTable(table))
            {
                await RemoteAuditLogger.LogAsync(deviceId, "DELETE", table, rowId.ToString(), null, null, RemoteAuditLogger.ResultRejected).ConfigureAwait(false);
                Write(ctx, 403, "table is not a User_* table"); return;
            }
            try
            {
                await DB.Instance.DeleteRowAsync(table, rowId).ConfigureAwait(false);
                await RemoteAuditLogger.LogAsync(deviceId, "DELETE", table, rowId.ToString(), null, null, RemoteAuditLogger.ResultOk).ConfigureAwait(false);
                await BroadcastDbChangedAsync(table, "DELETE", rowId).ConfigureAwait(false);
                Write(ctx, 200, "ok");
            }
            catch (Exception ex)
            {
                await RemoteAuditLogger.LogAsync(deviceId, "DELETE", table, rowId.ToString(), null, null, RemoteAuditLogger.ResultFailed).ConfigureAwait(false);
                Write(ctx, 500, ex.Message);
            }
        }

        private async Task HandleScriptEnableAsync(HttpListenerContext ctx, string deviceId, string path)
        {
            // Path shape: /api/scripts/{file}/enable
            string after = path.Substring("/api/scripts/".Length);
            int slash = after.LastIndexOf('/');
            if (slash <= 0) { Write(ctx, 400, "Bad Request"); return; }
            string fileName = after.Substring(0, slash);

            var body = await ReadBodyAsJsonAsync(ctx).ConfigureAwait(false);
            if (body is null) { Write(ctx, 400, "Bad Request"); return; }
            bool enabled = body.Value.TryGetProperty("enabled", out var eEl) && eEl.ValueKind == JsonValueKind.True;

            ScriptRegistry.Instance.SetEnabled(fileName, enabled);
            await RemoteAuditLogger.LogAsync(deviceId, "SCRIPT_TOGGLE", "Scripts", fileName, null, enabled ? "enabled" : "disabled", RemoteAuditLogger.ResultOk).ConfigureAwait(false);
            Write(ctx, 200, "ok");
        }

        private async Task HandleProcessLifecycleAsync(HttpListenerContext ctx, string deviceId, string path)
        {
            // Path shape: /api/process/{name}/{start|stop}
            string after = path.Substring("/api/process/".Length);
            int slash = after.LastIndexOf('/');
            if (slash <= 0) { Write(ctx, 400, "Bad Request"); return; }
            string name = after.Substring(0, slash);
            string verb = after.Substring(slash + 1);

            if (string.Equals(name, "Hub", StringComparison.OrdinalIgnoreCase))
            {
                await RemoteAuditLogger.LogAsync(deviceId, "PROCESS_" + verb.ToUpperInvariant(), "Processes", name, null, null, RemoteAuditLogger.ResultRejected).ConfigureAwait(false);
                Write(ctx, 403, "Hub itself is not toggleable"); return;
            }
            try
            {
                if (string.Equals(verb, "start", StringComparison.OrdinalIgnoreCase))
                    ProcessManager.Instance.CreateProcess(name, name);
                else if (string.Equals(verb, "stop", StringComparison.OrdinalIgnoreCase))
                    ProcessManager.Instance.TerminateProcess(name);
                else
                {
                    Write(ctx, 400, "verb must be start or stop"); return;
                }
                await RemoteAuditLogger.LogAsync(deviceId, "PROCESS_" + verb.ToUpperInvariant(), "Processes", name, null, null, RemoteAuditLogger.ResultOk).ConfigureAwait(false);
                Write(ctx, 200, "ok");
            }
            catch (Exception ex)
            {
                await RemoteAuditLogger.LogAsync(deviceId, "PROCESS_" + verb.ToUpperInvariant(), "Processes", name, null, null, RemoteAuditLogger.ResultFailed).ConfigureAwait(false);
                Write(ctx, 500, ex.Message);
            }
        }

        private async Task HandleGiveawayDrawAsync(HttpListenerContext ctx, string deviceId)
        {
            // Unlike its siblings (HandleGiveawayEnterAsync,
            // HandleDbInsertAsync, …), the body here had no try/catch — a DB fault
            // (CreateUserTable / GetTableData / SetCell) escaped as an unhandled
            // exception in the request pump instead of an audited 500. Wrap the body to
            // match the sibling pattern: audit-log ResultFailed + Write(500, message).
            try
            {
                // Slice 3 — atomic draw + claim. Bind tables on first call so an empty
                // database works; the entry/prize tables are User_* so they are remotely
                // editable by this same Viewer.
                await DB.Instance.CreateUserTableAsync("User_GiveawayEntries",
                    new List<(string, string)>
                    {
                        ("Username", "TEXT"),
                        ("EnteredAt", "TEXT"),
                        ("Weight",    "INTEGER"),
                        ("Claimed",   "INTEGER"),
                    }).ConfigureAwait(false);
                await DB.Instance.CreateUserTableAsync("User_GiveawayPrizes",
                    new List<(string, string)>
                    {
                        ("Name",       "TEXT"),
                        ("Awarded",    "INTEGER"),
                        ("WinnerUser", "TEXT"),
                    }).ConfigureAwait(false);

                // Pull unclaimed entries with weight >= 1; weighted random pick.
                var entries = await DB.Instance.GetTableDataAsync("User_GiveawayEntries").ConfigureAwait(false);
                var pool = new List<(long rowid, string user, int weight)>();
                foreach (System.Data.DataRow r in entries.Rows)
                {
                    long rid    = Convert.ToInt64(r["rowid"]);
                    string user = r["Username"]?.ToString() ?? "";
                    int weight  = r.Table.Columns.Contains("Weight") && int.TryParse(r["Weight"]?.ToString(), out var w) ? w : 1;
                    int claim   = r.Table.Columns.Contains("Claimed") && int.TryParse(r["Claimed"]?.ToString(), out var c) ? c : 0;
                    if (weight <= 0 || claim != 0 || string.IsNullOrEmpty(user)) continue;
                    pool.Add((rid, user, weight));
                }
                if (pool.Count == 0)
                {
                    await RemoteAuditLogger.LogAsync(deviceId, "GIVEAWAY_DRAW", "User_GiveawayEntries", "", null, null, RemoteAuditLogger.ResultRejected).ConfigureAwait(false);
                    Write(ctx, 409, "no eligible entries"); return;
                }

                int totalWeight = 0;
                foreach (var p in pool) totalWeight += p.weight;
                int pick = System.Security.Cryptography.RandomNumberGenerator.GetInt32(totalWeight);
                (long rowid, string user, int weight) winner = pool[0];
                int running = 0;
                foreach (var p in pool)
                {
                    running += p.weight;
                    if (pick < running) { winner = p; break; }
                }

                // Atomic-as-it-can-be: mark Claimed=1 with the engine's already-
                // serialized DB connection. Two concurrent /draw requests
                // would each pick from the snapshot; the second's Claimed write
                // doesn't undo the first, so both winners are recorded — but only
                // one row goes Claimed=1 per draw. DB.cs's _lock is the
                // serialization point.
                await DB.Instance.SetCellAsync("User_GiveawayEntries", winner.rowid, "Claimed", "1").ConfigureAwait(false);
                await RemoteAuditLogger.LogAsync(deviceId, "GIVEAWAY_DRAW", "User_GiveawayEntries", winner.rowid.ToString(), null, winner.user, RemoteAuditLogger.ResultOk).ConfigureAwait(false);
                await BroadcastDbChangedAsync("User_GiveawayEntries", "UPDATE", winner.rowid).ConfigureAwait(false);
                await WriteJsonAsync(ctx, 200, new { winner = winner.user, rowid = winner.rowid }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RemoteAuditLogger.LogAsync(deviceId, "GIVEAWAY_DRAW", "User_GiveawayEntries", "", null, null, RemoteAuditLogger.ResultFailed).ConfigureAwait(false);
                Write(ctx, 500, ex.Message);
            }
        }

        // ── WebSocket ────────────────────────────────────────────────────

        private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "";
            if (path != "/ws") { Write(ctx, 404, "Not Found"); return; }

            string? bearer = ExtractBearerFromSubprotocol(ctx.Request.Headers["Sec-WebSocket-Protocol"]);
            string? deviceId = bearer is null ? null : await _auth.VerifyTokenAsync(bearer).ConfigureAwait(false);
            if (deviceId is null) { Write(ctx, 401, "Unauthorized"); return; }

            HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: bearer is null ? null : "bearer." + bearer); }
            catch (Exception ex) { GlobalLogger.Error("RemoteBridge", "WS accept failed", ex); return; }

            var socket = wsCtx.WebSocket;
            var session = new RemoteSession(socket);
            _sessions.Add(deviceId, session);

            try
            {
                // Greet so the Viewer sees a known prelude before push fanout starts.
                await SendJsonAsync(session, new { type = "VIEWER_HELLO", deviceId, schema = SchemaVersion }, ct).ConfigureAwait(false);

                var buf = new byte[16384];
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var res = await socket.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    // Viewer only listens on this socket; we ignore inbound for now.
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch { /* peer dropped */ }
            finally
            {
                _sessions.Remove(deviceId, session);
                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch { try { socket.Abort(); } catch { } }
                try { session.SendLock.Dispose(); } catch { }
            }
        }

        private static string? ExtractBearerFromSubprotocol(string? header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            // Header is comma-separated subprotocols; we ship the bearer as
            // "bearer.{token}". Pull out the first matching entry.
            foreach (var entry in header.Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.StartsWith("bearer.", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 7)
                    return trimmed.Substring(7);
            }
            return null;
        }

        // ── Public push helpers (called by Hub's bus / chat / process feeds) ──

        public Task BroadcastLiveFeedAsync(Log entry) =>
            _sessions.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type    = "VIEWER_LIVEFEED",
                level   = entry.Level.ToString(),
                source  = entry.Source,
                message = entry.Message,
                ts      = DateTime.UtcNow.ToString("O"),
            }));

        public Task BroadcastChatAsync(string username, string message) =>
            _sessions.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type     = "VIEWER_CHAT",
                username,
                message,
                ts       = DateTime.UtcNow.ToString("O"),
            }));

        public Task BroadcastProcessStateAsync(string processId, string state) =>
            _sessions.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type      = "VIEWER_PROCESS_STATE",
                processId,
                state,
                ts        = DateTime.UtcNow.ToString("O"),
            }));

        private Task BroadcastDbChangedAsync(string table, string op, long rowid) =>
            _sessions.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type  = "VIEWER_DB_CHANGED",
                table,
                op,
                rowid,
            }));

        // ── Helpers ──────────────────────────────────────────────────────

        private static bool IsUserTable(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && name.StartsWith("User_", StringComparison.OrdinalIgnoreCase)
            && IsSafeIdentifier(name);

        private static bool IsSafeIdentifier(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");

        private async Task<string?> AuthorizeAsync(HttpListenerContext ctx)
        {
            string? auth = ctx.Request.Headers["Authorization"];
            if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return null;
            string token = auth.Substring("Bearer ".Length).Trim();
            return await _auth.VerifyTokenAsync(token).ConfigureAwait(false);
        }

        private static async Task<JsonElement?> ReadBodyAsJsonAsync(HttpListenerContext ctx)
        {
            try
            {
                using var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
                string body = await sr.ReadToEndAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body)) return null;
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch { return null; }
        }

        private static void Write(HttpListenerContext ctx, int status, string body)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                byte[] buf = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = buf.Length;
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                ctx.Response.Close();
            }
            catch { }
        }

        private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload);
                byte[] buf  = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode      = status;
                ctx.Response.ContentType     = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch { }
        }

        /// <summary>
        /// Per-socket SendAsync timeout. A stalled Viewer (paused
        /// devtools, network half-close) would otherwise pin <c>SendAsync</c>
        /// forever holding the per-session <see cref="RemoteSession.SendLock"/>.
        /// </summary>
        private static readonly TimeSpan SocketSendTimeout = TimeSpan.FromSeconds(5);

        private static async Task SendJsonAsync(RemoteSession session, object payload, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(payload);
            byte[] buf  = Encoding.UTF8.GetBytes(json);
            CancellationTokenSource? linked = null;
            try
            {
                await session.SendLock.WaitAsync(ct).ConfigureAwait(false);
                if (session.Socket.State == WebSocketState.Open)
                {
                    linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(SocketSendTimeout);
                    try
                    {
                        await session.Socket.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout fired — abort the socket so callers' next
                        // Snapshot() drops it instead of dead-locking on the
                        // SendLock again on the next broadcast.
                        try { session.Socket.Abort(); } catch { }
                        GlobalLogger.Log(
                            $"RemoteBridge: aborted WS send after {SocketSendTimeout.TotalSeconds:0}s stall.",
                            "RemoteBridge", LogLevel.CriticalError);
                    }
                }
            }
            catch { }
            finally
            {
                try { linked?.Dispose(); } catch { }
                try { session.SendLock.Release(); } catch { }
            }
        }
    }
}
