using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Hub.Core
{
    public class ScriptInfo
    {
        public string    FileName        { get; set; } = "";
        public bool      IsEnabled       { get; set; } = true;
        public DateTime? LastRunAt       { get; set; }
        public double?   LastCpuPercent  { get; set; }
        // Actual CPU time consumed by the most recent script run, in
        // milliseconds. The Script panel renders this directly — the older
        // LastCpuPercent reading was being shaped into a fudged TimeSpan
        // (percent × 10 ms) which gave readings off by orders of magnitude
        // for short-lived scripts. Recorded alongside LastCpuPercent by
        // ScriptManager.TimedExecuteAsync / ExecuteEventTriggerScriptsAsync.
        public double?   LastCpuMs       { get; set; }
        public long?     LastMemKb       { get; set; }

        /// <summary>
        /// Cached file content. Populated lazily on first read and cleared
        /// whenever the watcher's Refresh fires for this file.
        /// Hot paths (on_chat / on_event / on_webhook scans) read this rather than
        /// re-opening every .phx for every event.
        /// </summary>
        public string?   Content         { get; set; }

        /// <summary>Absolute path on disk. Set during Load/Refresh.</summary>
        public string    FullPath        { get; set; } = "";

        /// <summary>
        /// Header index built when Content is loaded — fast O(1) "does this script
        /// have an on_chat / on_webhook(name) / on_state_change(name) block?".
        /// </summary>
        public bool             HasChatHeader      { get; set; }
        public bool             HasStartupHeader   { get; set; }
        public HashSet<string>  EventTypes         { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string>  WebhookNames       { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string>  StateChangeNames   { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string>  BusEventTypes      { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // WS.Server event-trigger node. Same shape as WebhookNames:
        // each on_websocket("name"): block declares a path the WebSocketServerService
        // exposes at /ws/<name>; matching scripts fire when a client message lands.
        public HashSet<string>  WebSocketNames     { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // System.Hotkey event-trigger. Each on_hotkey("Ctrl+Shift+P"):
        // block declares a system-wide keystroke that HotkeyService binds via
        // Win32 RegisterHotKey at script-load time. Unbound on script disable /
        // reload so the OS never holds a hotkey for a script that isn't
        // currently subscribed.
        public HashSet<string>  HotkeyBindings     { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // System.Clipboard event-trigger. Bool flag rather than a
        // name set: clipboard updates are global (the OS doesn't differentiate
        // by handler), so the script either subscribes via `on_clipboard:` or
        // it doesn't. ClipboardService fires every script whose flag is set
        // on each WM_CLIPBOARDUPDATE.
        public bool             HasClipboardHeader { get; set; }
        // OBS WS v5 event-type subscriptions. Each on_obs("EventType"):
        // block declares an OBS event the containing script wants to react to.
        // ObsWebSocketClient pushes inbound events through
        // ScriptManager.DispatchObsEvent which fans across every script whose
        // ObsEventTypes set contains the matched name.
        public HashSet<string>  ObsEventTypes      { get; set; } = new(StringComparer.Ordinal);

        // Live processes — set ONLY on synthetic process-instance entries registered
        // by ProcessInstanceManager (FileName = "process::<instanceId>"). A non-null
        // InstanceId marks an instance: its Content is the process template, and
        // ScopedVars are the instance's start params (keyed "param.<name>") merged
        // UNDER event vars on every run. Normal file-script entries leave both null
        // and behave exactly as before.
        public string?                              InstanceId { get; set; }
        public IReadOnlyDictionary<string, string>? ScopedVars { get; set; }
    }

    // Persisted DTO — superset of the old Dictionary<string, bool> format
    internal class PersistedScriptState
    {
        public bool      IsEnabled  { get; set; } = true;
        public DateTime? LastRunAt  { get; set; }
    }

    public class ScriptRegistry
    {
        private static ScriptRegistry? _instance;
        public static ScriptRegistry Instance => _instance ??= new ScriptRegistry();

        private readonly ConcurrentDictionary<string, ScriptInfo> _scripts = new();

        // Live-process instances — synthetic ScriptInfos for every STARTED process
        // instance, owned by ProcessInstanceManager. WhereEnabled yields from this
        // alongside _scripts so every registry-driven event family fans out to live
        // instances for free. Deliberately NOT touched by LoadScripts / Refresh:
        // process templates live under data/logic/processes/<graph>/ (a subfolder the
        // non-recursive *.phx loader never sees) and only run per-instance, never as
        // standalone top-level scripts.
        private readonly ConcurrentDictionary<string, ScriptInfo> _processInstances = new(StringComparer.Ordinal);

        private string _statesPath  = "";
        private string _logicPath   = "";

        // LoadScripts can now be invoked concurrently — the
        // ScriptManager ctor kicks an async copy off the thread pool, and the
        // bootstrapper still issues its own explicit call once the resolved
        // logic path is known. Two concurrent `_scripts.Clear()` + populate
        // passes would leave the dictionary in an undefined intermediate
        // state; serialise them here so either call is safe to make in any
        // order without callers needing to coordinate.
        private readonly object _loadLock = new();

        // Serialises every write to script_states.json and makes it atomic
        // (temp-file + rename). PersistStates() used to be a bare File.WriteAllText
        // with no lock, called synchronously from RecordExecution on EVERY script
        // execution. Under a chat/event flood, independent fire-and-forget dispatch
        // tasks (chat / bus / webhook / scheduler) each landed in RecordExecution at
        // the same instant, so two File.WriteAllText calls raced on the same path and
        // the loser threw "The process cannot access the file … being used by another
        // process." Serialising the write kills the write-vs-write race; the temp+rename
        // makes a concurrent reader (LoadPersistedStates) see whole-old or whole-new,
        // never a torn file.
        private readonly object _persistLock = new();

        // Debounce for the hot-path flush. script_states.json only persists IsEnabled +
        // LastRunAt — LastRunAt is a cosmetic "last ran" timestamp that does not need a
        // synchronous disk write on every single execution. RecordExecution now schedules
        // a coalesced write ~750 ms after the last execution, collapsing a burst of N
        // executions into one write. Enable/disable toggles (SetEnabled) still flush
        // immediately so a user-visible state change survives an instant crash.
        private System.Threading.Timer? _persistTimer;
        private const int PersistDebounceMs = 750;

        // Coalesces the OnChanged raise from the per-execution hot path.
        // RecordExecution used to raise OnChanged synchronously on EVERY script
        // execution; each raise triggers an O(N-scripts) UI-dispatch rebuild of
        // the Scripts panel — many times a second on a busy stream, for what is
        // a counter/timestamp change. Executions now open (or mark dirty) a
        // short window and the one-shot timer raises once per window; a call
        // landing inside an open window makes the fire callback re-arm, so a
        // burst always ends with a trailing raise that reflects the final
        // state. Deliberately arm-on-first (not re-arm-on-every-call): a
        // trailing-edge debounce would starve the panel indefinitely under a
        // sustained execution flood, whereas this shape raises at most once per
        // interval and still converges. Structural raises (LoadScripts /
        // Refresh / SetEnabled / process-instance register) stay synchronous —
        // only the execution-stats path is coalesced.
        private readonly object _changedRaiseLock = new();
        private System.Threading.Timer? _changedRaiseTimer;
        private bool _changedRaiseArmed;
        private bool _changedRaisePending;
        private const int ChangedRaiseCoalesceMs = 300;

        public event Action? OnChanged;

        public void LoadScripts(string logicPath)
        {
            // Serialise concurrent LoadScripts invocations. The lock
            // is briefly held across the full populate so a reader iterating
            // _scripts.Values mid-load sees the prior state, not a half-cleared
            // one. ConcurrentDictionary makes the per-entry writes safe; the
            // lock makes the global "clear + repopulate" sequence atomic.
            lock (_loadLock)
            {
                LoadScriptsLocked(logicPath);
            }
        }

        private void LoadScriptsLocked(string logicPath)
        {
            _logicPath  = logicPath;
            _statesPath = Path.Combine(logicPath, "script_states.json");
            var saved = LoadPersistedStates();

            _scripts.Clear();
            if (!Directory.Exists(logicPath)) return;

            foreach (var file in Directory.GetFiles(logicPath, "*.phx"))
            {
                string name = Path.GetFileName(file);
                saved.TryGetValue(name, out var s);

                // Cold-start warmup. Eagerly populate Content
                // and the header index here so the first dispatch post-startup is
                // a dictionary lookup, not a synchronous WaitForFileStable +
                // File.ReadAllText round-trip on the chat / event hot path. If
                // the read fails (locked file, missing, malformed encoding), we
                // leave Content == null so GetContent[Async] retains its lazy
                // fallback and dispatch still works once the file settles.
                string? eagerContent = null;
                try
                {
                    eagerContent = ReadAllTextStable(file);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error(
                        "ScriptRegistry",
                        $"read failed for '{name}' during LoadScripts",
                        ex);
                }

                var info = new ScriptInfo
                {
                    FileName  = name,
                    FullPath  = file,
                    IsEnabled = s?.IsEnabled ?? true,
                    LastRunAt = s?.LastRunAt,
                    Content   = eagerContent,
                };
                if (eagerContent != null)
                    BuildHeaderIndex(info, eagerContent);

                _scripts[name] = info;
            }

            // Duplicate header-name detection. Two .phx files
            // sharing the same on_webhook / on_websocket / on_hotkey name
            // collide on dispatch (silent fan-out for webhooks, silent drop for
            // websocket/hotkey first-match paths); warn at registry-load time
            // so the operator can rename one.
            DetectDuplicateHeaderNames();

            SafeEvent.Raise(OnChanged, "ScriptRegistry", "OnChanged");
        }

        /// <summary>Refreshes using the last-known logic path set by LoadScripts().</summary>
        public void Refresh() => Refresh(_logicPath);

        public void Refresh(string logicPath)
        {
            if (!Directory.Exists(logicPath)) return;

            var saved = LoadPersistedStates();
            var onDisk = new HashSet<string>();

            foreach (var file in Directory.GetFiles(logicPath, "*.phx"))
            {
                string name = Path.GetFileName(file);
                onDisk.Add(name);

                // Build a fresh ScriptInfo with Content + header index FULLY populated
                // BEFORE publishing into _scripts. Refresh runs on LogicWatcher's
                // debounce timer thread; the dispatch hot path (WhereEnabled →
                // ExecuteOn*ScriptsAsync) reads _scripts.Values from arbitrary
                // threads. The previous shape cleared the prior entry's Content
                // and HashSets in place and relied on DetectDuplicateHeaderNames
                // (or the dispatch-side defensive GetContent in WhereEnabled) to
                // repopulate them. A chat / event burst landing inside that window
                // hit the synchronous GetContent → WaitForFileStable (bounded to
                // ~1.5 s per locked file) on the dispatch thread, filling the
                // chat-script semaphore until the watcher caught up. Now the
                // watcher thread takes the WaitForFileStable hit and the swap is
                // atomic via ConcurrentDictionary's indexer set — readers either
                // see the prior fully-built entry or the new fully-built entry,
                // never a half-cleared one.
                // Distinguish a successful read (even of a legitimately empty
                // .phx) from a transient read failure (locked file / OneDrive /
                // permission). On failure we must NOT publish an empty Content +
                // empty header index — that wipes the prior entry's EventTypes /
                // WebhookNames / etc., and because Content == "" is non-null,
                // WhereEnabled / GetContent never retry, so every trigger for
                // that script silently stops firing until the next clean read.
                string freshContent = "";
                bool readOk = false;
                try { freshContent = ReadAllTextStable(file); readOk = true; }
                catch (Exception ex)
                {
                    GlobalLogger.Error(
                        "ScriptRegistry",
                        $"read failed for '{name}' during Refresh",
                        ex);
                }

                ScriptInfo? prior = _scripts.TryGetValue(name, out var existing) ? existing : null;
                saved.TryGetValue(name, out var s);
                var fresh = new ScriptInfo
                {
                    FileName       = name,
                    FullPath       = file,
                    // User-toggled IsEnabled survives across refreshes (prior wins);
                    // new-on-disk entries fall back to persisted state, then to true.
                    IsEnabled      = prior?.IsEnabled ?? s?.IsEnabled ?? true,
                    LastRunAt      = prior?.LastRunAt ?? s?.LastRunAt,
                    LastCpuPercent = prior?.LastCpuPercent,
                    LastCpuMs      = prior?.LastCpuMs,
                    LastMemKb      = prior?.LastMemKb,
                    // On read failure carry the prior Content (null if this is a
                    // brand-new entry, which keeps the lazy GetContent retry path
                    // alive); only adopt the fresh string when the read succeeded.
                    Content        = readOk ? freshContent : prior?.Content,
                };
                if (readOk)
                {
                    BuildHeaderIndex(fresh, freshContent);
                }
                else if (prior != null)
                {
                    // Preserve the last good header index so dispatch predicates
                    // keep matching while the file is transiently unreadable.
                    fresh.HasChatHeader      = prior.HasChatHeader;
                    fresh.HasStartupHeader   = prior.HasStartupHeader;
                    fresh.HasClipboardHeader = prior.HasClipboardHeader;
                    fresh.EventTypes         = prior.EventTypes;
                    fresh.WebhookNames       = prior.WebhookNames;
                    fresh.StateChangeNames   = prior.StateChangeNames;
                    fresh.BusEventTypes      = prior.BusEventTypes;
                    fresh.WebSocketNames     = prior.WebSocketNames;
                    fresh.HotkeyBindings     = prior.HotkeyBindings;
                    fresh.ObsEventTypes      = prior.ObsEventTypes;
                }
                _scripts[name] = fresh;
            }

            // Snapshot the keys to remove into a local list before mutating the
            // dictionary. Iterating _scripts.Keys (a point-in-time snapshot) while
            // TryRemove modifies the collection is benign, but a concurrent Refresh
            // / add landing between the snapshot and a TryRemove leaves a stale
            // observable window; materialising the delete set first keeps the
            // mirror-the-on-disk-files invariant tight.
            var keysToRemove = new List<string>();
            foreach (var key in _scripts.Keys)
            {
                if (!onDisk.Contains(key))
                    keysToRemove.Add(key);
            }
            foreach (var key in keysToRemove)
            {
                _scripts.TryRemove(key, out _);
            }

            // Generalized duplicate scan covers webhook + websocket + hotkey.
            DetectDuplicateHeaderNames();

            SafeEvent.Raise(OnChanged, "ScriptRegistry", "OnChanged");
        }

        /// <summary>
        /// Returns the cached .phx contents, lazily reading from disk on first access.
        /// Applies WaitForFileStable so partial writes from Architect's exporter don't
        /// reach the engine. Header index is populated alongside.
        /// </summary>
        public async Task<string> GetContentAsync(string fileName)
        {
            if (!_scripts.TryGetValue(fileName, out var info))
            {
                // Live-process instance ("process::<id>") isn't a file in _scripts —
                // serve its template body from the instance registry so the fan-out
                // run path gets content.
                var inst = ResolveInstance(fileName);
                return inst?.Content ?? "";
            }
            if (info.Content != null) return info.Content;
            string path = info.FullPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { info.Content = ""; return ""; }

            string content;
            try { content = await ReadAllTextStableAsync(path).ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("ScriptRegistry", $"read failed for '{fileName}'", ex);
                info.Content = "";
                return "";
            }
            info.Content = content;
            BuildHeaderIndex(info, content);
            return content;
        }

        /// <summary>
        /// Synchronous alternative for hot paths that already block (e.g. registry init);
        /// callers that can `await` should prefer GetContentAsync.
        /// </summary>
        public string GetContent(string fileName)
        {
            if (!_scripts.TryGetValue(fileName, out var info))
            {
                var inst = ResolveInstance(fileName);
                return inst?.Content ?? "";
            }
            if (info.Content != null) return info.Content;
            string path = info.FullPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { info.Content = ""; return ""; }
            try { info.Content = ReadAllTextStable(path); }
            catch { info.Content = ""; }
            if (!string.IsNullOrEmpty(info.Content))
                BuildHeaderIndex(info, info.Content);
            return info.Content;
        }

        // Pre-compiled regexes for header detection. Compiled instances are reused
        // across all scripts.
        private static readonly Regex _rxOnChat       = new(@"^on_chat[^_a-zA-Z]", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxOnStartup    = new(@"^on_startup\b",    RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxOnEvent      = new(@"^on_event\(""?([^""\)]+)""?\)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxOnWebhook    = new(@"^on_webhook\(""?([^""\)]+)""?\)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxOnStateChange= new(@"^on_state_change\(""?([^""\)]+)""?\)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxOnBus        = new(@"^on_bus\(""?([^""\)]+)""?\)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);
        // WS.Server header detection. Same shape as on_webhook.
        private static readonly Regex _rxOnWebsocket  = new(@"^on_websocket\(""?([^""\)]+)""?\)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);
        // System.Hotkey header detection. Group 1 carries the
        // keystroke string (e.g. "Ctrl+Shift+P"); HotkeyService parses it
        // into Win32 modifier flags + virtual-key at registration time.
        private static readonly Regex _rxOnHotkey     = new(@"^on_hotkey\(""?([^""\)]+)""?\)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);
        // System.Clipboard header detection. No payload — fires
        // on every clipboard update for any script that opts in.
        private static readonly Regex _rxOnClipboard  = new(@"^on_clipboard\s*:",                RegexOptions.Multiline | RegexOptions.Compiled);
        // OBS event header detection. Group 1 carries the bare OBS
        // event name (e.g. "CurrentProgramSceneChanged"); ObsWebSocketClient
        // forwards the matching events through ScriptManager.DispatchObsEvent.
        private static readonly Regex _rxOnObs        = new(@"^on_obs\(""?([^""\)]+)""?\)\s*:",  RegexOptions.Multiline | RegexOptions.Compiled);

        private static void BuildHeaderIndex(ScriptInfo info, string content)
        {
            // Build-fresh + atomic-swap; was in-place
            // Clear()+Add() racing readers. These typed sets are read concurrently
            // from dispatch threads (WhereEnabled predicates calling .Contains(),
            // DetectDuplicateHeaderNames enumerating them); an in-place Clear()
            // mid-read tore the set / threw InvalidOperationException. Each set is
            // built into a fresh local with the SAME comparer as its field
            // declaration (ObsEventTypes is Ordinal; the rest OrdinalIgnoreCase),
            // then published by whole-reference assignment — reference writes are
            // atomic so readers always see a complete old-or-new set. The bool
            // flags are atomic single-word writes and stay assigned in place.
            var eventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _rxOnEvent.Matches(content))       eventTypes.Add(m.Groups[1].Value.Trim());
            var webhookNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _rxOnWebhook.Matches(content))     webhookNames.Add(m.Groups[1].Value.Trim());
            var stateChangeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _rxOnStateChange.Matches(content)) stateChangeNames.Add(m.Groups[1].Value.Trim());
            var busEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _rxOnBus.Matches(content))         busEventTypes.Add(m.Groups[1].Value.Trim());
            var webSocketNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _rxOnWebsocket.Matches(content))   webSocketNames.Add(m.Groups[1].Value.Trim());
            var hotkeyBindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _rxOnHotkey.Matches(content))      hotkeyBindings.Add(m.Groups[1].Value.Trim());
            var obsEventTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in _rxOnObs.Matches(content))         obsEventTypes.Add(m.Groups[1].Value.Trim());

            // Publish: bool flags first (atomic), then whole-reference set swaps.
            info.HasChatHeader      = _rxOnChat.IsMatch(content);
            info.HasStartupHeader   = _rxOnStartup.IsMatch(content);
            info.HasClipboardHeader = _rxOnClipboard.IsMatch(content);
            info.EventTypes       = eventTypes;
            info.WebhookNames     = webhookNames;
            info.StateChangeNames = stateChangeNames;
            info.BusEventTypes    = busEventTypes;
            info.WebSocketNames   = webSocketNames;
            info.HotkeyBindings   = hotkeyBindings;
            info.ObsEventTypes    = obsEventTypes;
        }

        /// <summary>
        /// Block briefly until the file is no longer being written. Mirrors
        /// LogicWatcher.WaitForFileStable; duplicated here to avoid a circular
        /// reference between this class and the watcher.
        /// </summary>
        private static void WaitForFileStable(string path, int maxAttempts = 30, int delayMs = 50)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return;
                }
                catch (IOException) { Thread.Sleep(delayMs); }
                catch (UnauthorizedAccessException) { Thread.Sleep(delayMs); }
                catch (Exception ex) { GlobalLogger.Error("ScriptRegistry", $"unexpected error waiting for file '{path}'", ex); return; }
            }
        }

        private static async Task WaitForFileStableAsync(string path, int maxAttempts = 30, int delayMs = 50)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return;
                }
                catch (IOException) { await Task.Delay(delayMs).ConfigureAwait(false); }
                catch (UnauthorizedAccessException) { await Task.Delay(delayMs).ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("ScriptRegistry", $"unexpected error waiting for file '{path}'", ex); return; }
            }
        }

        // ReadAllTextStable / Async — race-tolerant read for .phx files that
        // Architect's exporter may be in the middle of overwriting.
        //
        // WaitForFileStable + File.ReadAllText has a race window: the wait
        // returns the moment the file is unheld, then File.ReadAllText opens
        // again with FileShare.Read — if Architect's next save lands in that
        // window (FileShare.None on the writer side) the read fails with
        // "The process cannot access the file because it is being used by
        // another process." Two-step fix: open with FileShare.ReadWrite so
        // we tolerate a concurrent writer that opens FileShare.ReadWrite
        // (the common case for cooperative writers), AND retry the read
        // itself on IOException so a transient overlap with an exclusive
        // writer rides through. Final attempt re-throws so the caller's
        // existing catch still logs the failure.
        // internal so ProcessTemplateRegistry reuses the same race-tolerant read
        // for templates Architect's exporter may be mid-overwrite of.
        internal static string ReadAllTextStable(string path, int maxAttempts = 5, int delayMs = 50)
        {
            WaitForFileStable(path);
            for (int i = 0; i < maxAttempts - 1; i++)
            {
                try { return ReadAllTextShared(path); }
                catch (IOException) { Thread.Sleep(delayMs); }
            }
            return ReadAllTextShared(path);
        }

        private static async Task<string> ReadAllTextStableAsync(string path, int maxAttempts = 5, int delayMs = 50)
        {
            await WaitForFileStableAsync(path).ConfigureAwait(false);
            for (int i = 0; i < maxAttempts - 1; i++)
            {
                try { return await ReadAllTextSharedAsync(path).ConfigureAwait(false); }
                catch (IOException) { await Task.Delay(delayMs).ConfigureAwait(false); }
            }
            return await ReadAllTextSharedAsync(path).ConfigureAwait(false);
        }

        private static string ReadAllTextShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        private static async Task<string> ReadAllTextSharedAsync(string path)
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var sr = new StreamReader(fs);
            return await sr.ReadToEndAsync().ConfigureAwait(false);
        }

        // Generalized header-collision detector. Was
        // `DetectDuplicateWebhookNames` (webhook-only) — now scans every
        // routed header where same-name collisions matter:
        //   * on_webhook(name)   — single-dispatch surface; collision = silent
        //     duplicate fan-out, surprising for the script author.
        //   * on_websocket(name) — previously first-match-wins, now all-match
        //     fan-out; either way, a same-name collision should
        //     surface so the user can decide intentional-vs-typo.
        //   * on_hotkey(combo)   — first-match-wins kept; collision
        //     silently drops the loser, exactly the case the warning catches.
        //
        // Tier: Communication, not CriticalError — collisions are an authoring
        // warning, not a Hub fault. The pre-fix log used CriticalError for
        // webhook; we keep that for webhook to preserve the existing
        // operator-visible alert level on a header that has shipped longest,
        // and use Communication for the newer headers. This is the smallest
        // behavior-change footprint that still covers the new surfaces.
        //
        // on_clipboard: deliberately NOT included — multiple on_clipboard
        // blocks are by design (parallel fan-out to every subscriber, per
        // ExecuteOnClipboardScriptsAsync, ScriptManager line 1466).
        //
        // on_state_change(name): not included for now — state changes already
        // fan out by design (mirrors on_clipboard), and no regression has been
        // flagged there.
        private void DetectDuplicateHeaderNames()
        {
            // Each tuple: (header label for log, accessor for the HashSet on
            // ScriptInfo, log level). Tier varies per-header per the rationale
            // above.
            var checks = new (string Label, Func<ScriptInfo, IEnumerable<string>> Accessor, LogLevel Level)[]
            {
                ("on_webhook",   info => info.WebhookNames,    LogLevel.CriticalError),
                ("on_websocket", info => info.WebSocketNames,  LogLevel.Communication),
                ("on_hotkey",    info => info.HotkeyBindings,  LogLevel.Communication),
            };

            // Populate header indexes first so the accessors see the latest content.
            foreach (var info in _scripts.Values)
            {
                if (info.Content == null) GetContent(info.FileName);
            }

            foreach (var (label, accessor, level) in checks)
            {
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var info in _scripts.Values)
                {
                    // safe to enumerate concurrently:
                    // BuildHeaderIndex now atomic-swaps whole set references, so
                    // accessor(info) yields a stable snapshot the watcher can't
                    // Clear() out from under us mid-iteration.
                    foreach (var name in accessor(info))
                    {
                        if (seen.TryGetValue(name, out var first) && first != info.FileName)
                        {
                            GlobalLogger.Log(
                                $"{label}(\"{name}\") declared in BOTH '{first}' AND '{info.FileName}' — collision will affect dispatch (see header semantics). Rename one if unintentional.",
                                "ScriptRegistry", level);
                        }
                        else
                        {
                            seen[name] = info.FileName;
                        }
                    }
                }
            }
        }

        // Back-compat shim. Older code paths called
        // DetectDuplicateWebhookNames directly; forward to the generalized
        // detector so callers don't have to be touched in this sweep.
        // Internal so the test project can poke it via InternalsVisibleTo.
        internal void DetectDuplicateWebhookNames() => DetectDuplicateHeaderNames();

        /// <summary>
        /// Convenience predicate for hot paths: enumerate all enabled scripts whose
        /// header index says they care about the given filter.
        /// </summary>
        public IEnumerable<ScriptInfo> WhereEnabled(Func<ScriptInfo, bool> match)
        {
            foreach (var s in _scripts.Values)
            {
                if (!s.IsEnabled) continue;
                // Never take the synchronous GetContent → WaitForFileStable
                // (up to ~1.5 s on a locked file) hit on the dispatch hot path.
                // LoadScripts / Refresh eagerly populate Content for every script;
                // a null here means a transient read failure left this brand-new
                // entry uncached. Skip it (it has no header index to match against
                // anyway) and let the next watcher Refresh repopulate it.
                if (s.Content == null)
                {
                    continue;
                }
                if (match(s)) yield return s;
            }
            // Live-process fan-out — every STARTED instance is just another
            // ScriptInfo whose Content is the process template. The same header
            // predicate selects it, and the engine's EventType discriminator gates
            // the right block inside, so chat / on_event / on_obs / on_bus /
            // on_state_change / on_clipboard / startup all reach live instances with
            // no per-family change. Schedules bypass WhereEnabled and are driven by
            // ProcessInstanceManager's per-instance timers instead.
            foreach (var s in _processInstances.Values)
            {
                if (!s.IsEnabled) continue;
                if (s.Content == null) continue;
                if (match(s)) yield return s;
            }
        }

        // ── Live-process instance registry (owned by ProcessInstanceManager) ──────

        /// <summary>
        /// Publishes a synthetic ScriptInfo for a started process instance so the
        /// WhereEnabled fan-out reaches it. <paramref name="templateContent"/> is the
        /// process template; <paramref name="scopedVars"/> are the instance's start
        /// params (keyed "param.&lt;name&gt;"). Re-registering the same id replaces it.
        /// </summary>
        public void RegisterProcessInstance(string instanceId, string templateContent,
                                            IReadOnlyDictionary<string, string> scopedVars)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            var info = new ScriptInfo
            {
                FileName   = $"process::{instanceId}",
                FullPath   = "",
                IsEnabled  = true,
                Content    = templateContent ?? "",
                InstanceId = instanceId,
                ScopedVars = scopedVars,
            };
            BuildHeaderIndex(info, info.Content);
            _processInstances[instanceId] = info;
            SafeEvent.Raise(OnChanged, "ScriptRegistry", "OnChanged");
        }

        /// <summary>Removes a started instance's synthetic ScriptInfo (idempotent).</summary>
        public void UnregisterProcessInstance(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            if (_processInstances.TryRemove(instanceId, out _))
                SafeEvent.Raise(OnChanged, "ScriptRegistry", "OnChanged");
        }

        /// <summary>The synthetic ScriptInfo for a live instance, or null.</summary>
        public ScriptInfo? GetProcessInstance(string instanceId)
            => _processInstances.TryGetValue(instanceId, out var info) ? info : null;

        /// <summary>Maps a "process::&lt;id&gt;" FileName to its instance ScriptInfo, or null.</summary>
        private ScriptInfo? ResolveInstance(string? fileName)
        {
            const string prefix = "process::";
            if (fileName == null || !fileName.StartsWith(prefix, StringComparison.Ordinal)) return null;
            return GetProcessInstance(fileName.Substring(prefix.Length));
        }

        public bool IsEnabled(string fileName)
        {
            return !_scripts.TryGetValue(fileName, out var info) || info.IsEnabled;
        }

        public void RecordExecution(string fileName, double cpuPercent = 0, long memKb = 0, double cpuMs = 0)
        {
            if (_scripts.TryGetValue(fileName, out var info))
            {
                // UTC for DST-safe persistence. Field stays DateTime? so
                // existing JSON state files round-trip unchanged; only the captured
                // wall-clock kind flips local→UTC. UI surfaces should ToLocalTime()
                // before display.
                info.LastRunAt      = DateTime.UtcNow;
                info.LastCpuPercent = cpuPercent;
                info.LastCpuMs      = cpuMs;
                info.LastMemKb      = memKb;
                // Debounced — coalesce a flood of per-execution flushes into one write.
                SchedulePersist();
                // Coalesced — see the _changedRaiseLock field block. The stats
                // written above are visible to whichever raise fires next; the
                // trailing raise guarantees the panel converges on the final
                // post-burst state.
                ScheduleChangedRaise();
            }
        }

        /// <summary>
        /// Opens a coalescing window for the hot-path OnChanged raise (or marks an
        /// already-open window dirty). See the <see cref="_changedRaiseLock"/> field
        /// block for the full rationale. Safe to call from any thread — RecordExecution
        /// lands here from concurrent chat / event / webhook dispatch flows.
        /// </summary>
        private void ScheduleChangedRaise()
        {
            lock (_changedRaiseLock)
            {
                if (_changedRaiseArmed)
                {
                    // Window already open — the fire callback re-arms for us so
                    // this call's stats still get a trailing raise.
                    _changedRaisePending = true;
                    return;
                }
                _changedRaiseArmed = true;
                // Singleton-lifetime timer; never explicitly disposed (the registry
                // lives for the whole Hub process). Mirrors _persistTimer.
                _changedRaiseTimer ??= new System.Threading.Timer(
                    _ => FireCoalescedChanged(), null, Timeout.Infinite, Timeout.Infinite);
                try { _changedRaiseTimer.Change(ChangedRaiseCoalesceMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { _changedRaiseArmed = false; /* shutdown race — a lost UI refresh is harmless */ }
            }
        }

        private void FireCoalescedChanged()
        {
            lock (_changedRaiseLock)
            {
                if (_changedRaisePending)
                {
                    // Executions landed while this window was open — keep it armed
                    // and fire again one interval later so the panel picks up the
                    // final post-burst state. Sustained floods therefore raise at
                    // most once per interval instead of once per execution.
                    _changedRaisePending = false;
                    try { _changedRaiseTimer?.Change(ChangedRaiseCoalesceMs, Timeout.Infinite); }
                    catch (ObjectDisposedException) { _changedRaiseArmed = false; }
                }
                else
                {
                    _changedRaiseArmed = false;
                }
            }
            // Raise outside the lock so a slow subscriber can't block the
            // scheduling path RecordExecution runs on.
            SafeEvent.Raise(OnChanged, "ScriptRegistry", "OnChanged");
        }

        public void SetEnabled(string fileName, bool enabled)
        {
            if (_scripts.TryGetValue(fileName, out var info))
            {
                info.IsEnabled = enabled;
                // Immediate — a user-visible enable/disable must survive an instant crash.
                PersistStatesNow();
                SafeEvent.Raise(OnChanged, "ScriptRegistry", "OnChanged");
            }
        }

        public IEnumerable<ScriptInfo> GetAll() => _scripts.Values;

        private Dictionary<string, PersistedScriptState> LoadPersistedStates()
        {
            try
            {
                if (!string.IsNullOrEmpty(_statesPath) && File.Exists(_statesPath))
                {
                    var json = File.ReadAllText(_statesPath);

                    // Try new format first
                    var newFormat = JsonSerializer.Deserialize<Dictionary<string, PersistedScriptState>>(json);
                    if (newFormat != null) return newFormat;
                }
            }
            catch
            {
                // Fall through: try legacy format below
            }

            // Migrate legacy Dictionary<string, bool> format
            try
            {
                if (!string.IsNullOrEmpty(_statesPath) && File.Exists(_statesPath))
                {
                    var json = File.ReadAllText(_statesPath);
                    var legacy = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                    if (legacy != null)
                    {
                        var migrated = new Dictionary<string, PersistedScriptState>();
                        foreach (var kv in legacy)
                            migrated[kv.Key] = new PersistedScriptState { IsEnabled = kv.Value };
                        return migrated;
                    }
                }
            }
            catch { }

            return new Dictionary<string, PersistedScriptState>();
        }

        /// <summary>
        /// Coalesced flush — (re)arms a one-shot timer so a burst of RecordExecution
        /// calls collapses to a single write ~<see cref="PersistDebounceMs"/> ms after
        /// the last one. The timer callback runs the serialized atomic write below.
        /// Used by the per-execution hot path; SetEnabled flushes immediately instead.
        /// </summary>
        private void SchedulePersist()
        {
            if (string.IsNullOrEmpty(_statesPath)) return;
            lock (_persistLock)
            {
                // Singleton-lifetime timer; never explicitly disposed (the registry
                // lives for the whole Hub process). Arming it Infinite/Infinite at
                // creation and re-Change-ing on each call gives debounce semantics.
                _persistTimer ??= new System.Threading.Timer(_ => PersistStatesNow(), null, Timeout.Infinite, Timeout.Infinite);
                try { _persistTimer.Change(PersistDebounceMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { /* shutdown race — last flush lost is harmless */ }
            }
        }

        /// <summary>
        /// Serialized, atomic write of script_states.json. The snapshot+serialize runs
        /// lock-free (ConcurrentDictionary enumeration is safe); only the file I/O is
        /// serialized under <see cref="_persistLock"/> and written via a temp file then
        /// <see cref="File.Move(string,string,bool)"/> so the swap is atomic and a
        /// concurrent reader never sees a partial file. This is the fix for the
        /// "being used by another process" IOException flood under load.
        /// </summary>
        private void PersistStatesNow()
        {
            try
            {
                if (string.IsNullOrEmpty(_statesPath)) return;
                var states = new Dictionary<string, PersistedScriptState>();
                foreach (var kv in _scripts)
                    states[kv.Key] = new PersistedScriptState
                    {
                        IsEnabled = kv.Value.IsEnabled,
                        LastRunAt = kv.Value.LastRunAt
                    };
                string json = JsonSerializer.Serialize(states);
                lock (_persistLock)
                {
                    string tmp = _statesPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _statesPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                // Mirrors GraphSerializer.SaveGraphAsync's logging shape — a silent
                // catch here used to drop disk-full / file-locked / OneDrive-conflict
                // failures, leaving the user to discover that an Enable/Disable toggle
                // had reverted only on next launch. Don't rethrow: this runs from
                // in-place UI toggles and a debounce-timer thread where a throw would
                // surprise the caller; logging is the right resolution.
                GlobalLogger.Error(
                    "ScriptRegistry",
                    $"PersistStates failed for '{_statesPath}'",
                    ex);
            }
        }
    }
}
