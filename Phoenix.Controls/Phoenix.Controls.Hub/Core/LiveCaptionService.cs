using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// LiveCaptionService — reads the live caption text from Windows 11's built-in LiveCaptions
    /// app via UI Automation. The same technique used by SakiRinn/LiveCaptions-Translator,
    /// re-implemented inside Hub so no external GUI app is required.
    ///
    /// Lifecycle:
    ///   - Start() optionally presses Win+Ctrl+L to launch LiveCaptions if it isn't running
    ///     (controlled by AppConfig.LiveCaptionsAutoLaunch).
    ///   - A background poll loop locates the LiveCaptions process, walks UIA to find the
    ///     caption text element, and reads its Name (or ValuePattern). Polls adaptively at
    ///     250 ms while text is changing, slowing to 1000 ms after several unchanged reads.
    ///   - On text change, raises CaptionChanged with the latest caption.
    ///   - Stop() halts the poll loop and releases the cached AutomationElement reference.
    ///
    /// UIA was chosen over Win32 GetWindowText because Windows 11 LiveCaptions renders its
    /// text in a DirectComposition surface that the legacy text APIs can't see.
    /// </summary>
    public sealed class LiveCaptionService : ICaptionSource, IDisposable
    {
        private const string ProcessName       = "LiveCaptions";

        // M47 — adaptive poll cadence. Default fast rate while captions are flowing,
        // slow rate after the same text has been read repeatedly (no one is talking).
        internal const int PollIntervalFastMs  = 250;
        internal const int PollIntervalSlowMs  = 1000;
        internal const int PollSlowThreshold   = 3;     // unchanged reads before slowing
        private  const int DiscoveryDelayMs    = 1500;  // wait after auto-launch for the UI to materialize

        // M88 — give the in-flight UIA RPC time to return before we dispose its element.
        // UIA RPCs are not cancellable from C#, so a too-short wait leaves the call to
        // hit a disposed element with a NullReference / InvalidOperation / ObjectDisposed.
        // 1.5s is the Hub-shutdown budget: long enough for a healthy poll iteration to
        // finish its Task.Delay+UIA round-trip and observe the cancel, short enough that
        // a stuck UIA call doesn't keep the Hub.WinUI process alive past Window.Closed.
        private const int StopDrainTimeoutMs   = 1500;

        // M89 — stale-element detection thresholds. Re-discover when either the element
        // keeps throwing or it keeps returning the same text while the process is alive.
        internal const int RediscoverErrorThreshold      = 3;
        internal const int RediscoverSilentThreshold     = 10;

        // L80 — UIA Subtree scans are slow on cold caches; budget the fast Children pass
        // first and only fall through to the full Subtree walk if it returns nothing in
        // the budgeted window. Keeps the happy path snappy and the recovery path reliable.
        private const int FastFindBudgetMs               = 500;

        private readonly bool _autoLaunch;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        // M48 — _captionElement is touched from the poll task and from Stop() on the
        // calling thread; guard the reference (not the RPC) with this lock.
        private AutomationElement? _captionElement;
        private readonly object _sync = new();

        private string _latest = "";
        private readonly object _lock = new();

        public event Action<string>? CaptionChanged;
        public string LatestCaption { get { lock (_lock) return _latest; } }

        public LiveCaptionService(bool autoLaunchIfNotRunning = false)
        {
            _autoLaunch = autoLaunchIfNotRunning;
        }

        public void Start()
        {
            if (_loop is { IsCompleted: false }) return;
            _cts  = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
            GlobalLogger.Log("LiveCaptionService: started.", "LiveCaption", LogLevel.System);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }

            // M88 — wait longer than the legacy 1s and tolerate a still-running RPC.
            // If the loop hasn't drained, log it at Communication-tier so the user
            // sees the rough shutdown without a crash.
            bool drained = false;
            try { drained = _loop?.Wait(StopDrainTimeoutMs) ?? true; }
            catch (Exception ex)
            {
                GlobalLogger.Log($"LiveCaptionService: drain raised {ex.GetType().Name} — '{ex.Message}'",
                    "LiveCaption", LogLevel.Communication);
            }
            if (!drained)
            {
                GlobalLogger.Log(
                    $"LiveCaptionService: poll loop did not drain in {StopDrainTimeoutMs}ms; releasing UIA reference anyway.",
                    "LiveCaption", LogLevel.Communication);
            }

            // M88 — disposing the UIA reference can race a late-arriving RPC result;
            // swallow the documented exception types instead of crashing teardown.
            try { _cts?.Dispose(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            _cts  = null;
            _loop = null;

            // M48 — null the reference under lock so the poll task (if it survived
            // the drain) sees a consistent snapshot on its next iteration.
            lock (_sync) { _captionElement = null; }

            GlobalLogger.Log("LiveCaptionService: stopped.", "LiveCaption", LogLevel.System);
        }

        public void Dispose() => Stop();

        private async Task PollLoopAsync(CancellationToken ct)
        {
            // M49 — short-circuit on Win10 / pre-22H2. The UIA discovery would otherwise
            // spin forever waiting for a process that will never appear. Mirrors the
            // H59 auto-launch guard pattern.
            if (!IsLiveCaptionsSupported(Environment.OSVersion.Version))
            {
                GlobalLogger.Log(
                    "LiveCaptionService: skipped — Windows 11 22H2 (build 22621) or later required for LiveCaptions UIA discovery.",
                    "LiveCaption", LogLevel.Communication);
                return;
            }

            // First-time launch attempt — only if no LiveCaptions process is already up.
            // H59 — gate the auto-launch behind a Win11 22H2+ check. The Win+Ctrl+L
            // hotkey is mapped to "Lock screen with Bing-of-the-day" on Win10 (and to
            // nothing on early Win11 builds), so firing it there used to trigger an
            // OS-level lock instead of opening LiveCaptions.
            if (_autoLaunch && !IsLiveCaptionsRunning())
            {
                try { PressWinCtrlL(); }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"LiveCaptionService: auto-launch failed: {ex.Message}",
                        "LiveCaption", LogLevel.Communication);
                }
                try { await Task.Delay(DiscoveryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            int unchangedReads     = 0;
            int consecutiveErrors  = 0;
            int consecutiveSameRaw = 0;
            string lastRaw         = "";

            while (!ct.IsCancellationRequested)
            {
                int nextDelayMs = PollIntervalFastMs;

                try
                {
                    // M48 — snapshot the cached element under lock, then use the snapshot
                    // lock-free. Never hold _sync across the UIA RPC (can block 100s of ms).
                    AutomationElement? snapshot;
                    lock (_sync) { snapshot = _captionElement; }

                    if (snapshot is null || !IsElementAlive(snapshot))
                    {
                        snapshot = TryFindCaptionElement();
                        lock (_sync) { _captionElement = snapshot; }

                        // Fresh discovery resets the staleness counters.
                        consecutiveErrors  = 0;
                        consecutiveSameRaw = 0;
                        lastRaw            = "";
                    }

                    if (snapshot is not null)
                    {
                        string text = ReadElementText(snapshot);
                        consecutiveErrors = 0; // a successful read clears the error streak

                        // M89 — track raw read repetition independent of "did we publish";
                        // a stale element keeps returning the same text for many polls.
                        if (string.Equals(text, lastRaw, StringComparison.Ordinal))
                            consecutiveSameRaw++;
                        else
                        {
                            consecutiveSameRaw = 0;
                            lastRaw            = text;
                        }

                        if (!string.IsNullOrEmpty(text))
                        {
                            string prev;
                            bool   changed;
                            lock (_lock)
                            {
                                prev    = _latest;
                                changed = !string.Equals(prev, text, StringComparison.Ordinal);
                                if (changed) _latest = text;
                            }

                            if (changed)
                            {
                                // M47 — text changed: snap back to the fast cadence.
                                unchangedReads = 0;
                                try { CaptionChanged?.Invoke(text); }
                                catch (Exception subEx)
                                {
                                    GlobalLogger.Error("LiveCaption",
                                        "CaptionChanged subscriber threw", subEx);
                                }
                            }
                            else
                            {
                                unchangedReads++;
                            }
                        }
                        else
                        {
                            unchangedReads++;
                        }

                        // M89 — silent staleness: same raw text for many polls AND the
                        // process is still alive (so it isn't simply gone). Drop the
                        // cached element so the next iteration re-discovers it.
                        if (consecutiveSameRaw >= RediscoverSilentThreshold && IsLiveCaptionsRunning())
                        {
                            GlobalLogger.Log(
                                $"LiveCaptionService: caption element appears stale ({consecutiveSameRaw} identical reads); rediscovering.",
                                "LiveCaption", LogLevel.Communication);
                            lock (_sync) { _captionElement = null; }
                            consecutiveSameRaw = 0;
                            lastRaw            = "";
                        }
                    }
                    else
                    {
                        // No caption element yet — the LiveCaptions window may be hidden
                        // or starting up. Stay on the fast cadence so we pick it up promptly.
                        unchangedReads = 0;
                    }
                }
                catch (ElementNotAvailableException ex)
                {
                    consecutiveErrors++;
                    HandleStaleError(ex, consecutiveErrors);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // BH-037 — clean shutdown. Without this, the generic catch logged the
                    // cancellation as a "poll error #N" and bumped the rediscovery counter,
                    // spuriously firing the rediscover branch on every Stop().
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    HandleStaleError(ex, consecutiveErrors);
                }

                // M89 — error-based staleness: trigger rediscovery once we've seen
                // enough consecutive UIA failures. Keeps a one-off blip from churning
                // the cache, but recovers from the language-change / minimize cases.
                if (ShouldRediscover(consecutiveErrors, 0))
                {
                    GlobalLogger.Log(
                        $"LiveCaptionService: {consecutiveErrors} consecutive UIA errors; rediscovering caption element.",
                        "LiveCaption", LogLevel.Communication);
                    lock (_sync) { _captionElement = null; }
                    consecutiveErrors = 0;
                }

                nextDelayMs = NextPollIntervalMs(unchangedReads);

                try { await Task.Delay(nextDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static void HandleStaleError(Exception ex, int consecutiveErrors)
        {
            GlobalLogger.Log(
                $"LiveCaptionService: poll error #{consecutiveErrors} '{ex.Message}'",
                "LiveCaption", LogLevel.Communication);
        }

        // ── Adaptive poll cadence (M47) ──────────────────────────────────────

        /// <summary>
        /// M47 — adaptive poll-rate helper. Returns the fast interval (250 ms) until
        /// <see cref="PollSlowThreshold"/> consecutive reads have been unchanged, then
        /// drops to the slow interval (1000 ms) and stays there. Caller resets the
        /// counter to zero on any text change.
        /// </summary>
        internal static int NextPollIntervalMs(int unchangedReads)
        {
            if (unchangedReads < 0) unchangedReads = 0;
            return unchangedReads >= PollSlowThreshold ? PollIntervalSlowMs : PollIntervalFastMs;
        }

        // ── Stale-element detection (M89) ────────────────────────────────────

        /// <summary>
        /// M89 — pure helper for the stale-element decision. Triggers rediscovery if
        /// the UIA element has thrown <see cref="RediscoverErrorThreshold"/> times in
        /// a row, OR if it has returned identical text for <see cref="RediscoverSilentThreshold"/>
        /// consecutive polls (silent staleness — language change, minimize, etc.).
        /// </summary>
        internal static bool ShouldRediscover(int consecutiveErrors, int consecutiveSameReads)
        {
            if (consecutiveErrors    >= RediscoverErrorThreshold)  return true;
            if (consecutiveSameReads >= RediscoverSilentThreshold) return true;
            return false;
        }

        // ── UIA discovery ────────────────────────────────────────────────────

        private static bool IsLiveCaptionsRunning()
        {
            // L81 — Process objects from GetProcessesByName own a kernel handle each;
            // dispose them deterministically rather than waiting for GC. The leak is
            // small (handle table only) but the call site fires every poll on the
            // slow rediscovery path, so the cumulative count is not zero on long
            // sessions.
            System.Diagnostics.Process[]? procs = null;
            try
            {
                procs = System.Diagnostics.Process.GetProcessesByName(ProcessName);
                return procs.Length > 0;
            }
            catch { return false; }
            finally
            {
                if (procs is not null)
                    foreach (var p in procs) try { p.Dispose(); } catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// H59 / M49 — true on Windows 11 22H2 (build 22621) and later. LiveCaptions
        /// ships with that release; on older builds the Win+Ctrl+L hotkey is either
        /// unbound or mapped to something destructive (lock screen) AND the
        /// LiveCaptions process simply doesn't exist for UIA to find.
        ///
        /// QC40-01 — promoted to public so the Hub.WinUI bootstrapper can pre-flight
        /// the OS check at construction time. Without this, callers outside this
        /// assembly had to duplicate the build-number probe.
        /// </summary>
        public static bool IsLiveCaptionsSupported(Version osVersion)
        {
            if (osVersion is null) return false;
            // Windows 11 reports as 10.x with build >= 22000; LiveCaptions needs >= 22621.
            if (osVersion.Major < 10) return false;
            if (osVersion.Build < 22621) return false;
            return true;
        }

        /// <summary>
        /// Back-compat wrapper around <see cref="IsLiveCaptionsSupported(Version)"/>.
        /// </summary>
        private static bool IsLiveCaptionsSupportedOs()
        {
            try
            {
                var os = Environment.OSVersion;
                if (os.Platform != PlatformID.Win32NT) return false;
                return IsLiveCaptionsSupported(os.Version);
            }
            catch { return false; }
        }

        /// <summary>
        /// Locates the LiveCaptions caption text element. Strategy: find any
        /// AutomationElement whose owning process matches LiveCaptions, then walk its
        /// descendants for a Text/Edit/Document element with non-empty content.
        ///
        /// L80 — root lookup uses a hybrid scan: <see cref="TreeScope.Children"/> first
        /// (fast path; covers the common single-desktop case), and if that returns
        /// nothing within <see cref="FastFindBudgetMs"/> we fall back to the full
        /// <see cref="TreeScope.Subtree"/> walk so virtual desktops and detached
        /// child-window cases still resolve. Combined with M89's rediscovery, stale
        /// finds get refreshed automatically.
        /// </summary>
        private static AutomationElement? TryFindCaptionElement()
        {
            // L81 — same Process-handle leak fix as IsLiveCaptionsRunning above.
            int[] pids;
            System.Diagnostics.Process[]? procs = null;
            try
            {
                procs = System.Diagnostics.Process.GetProcessesByName(ProcessName);
                pids  = procs.Select(p => p.Id).ToArray();
            }
            catch { return null; }
            finally
            {
                if (procs is not null)
                    foreach (var p in procs) try { p.Dispose(); } catch { /* best-effort */ }
            }
            if (pids.Length == 0) return null;

            foreach (int pid in pids)
            {
                AutomationElement? root = FindProcessRoot(pid);
                if (root is null) continue;

                // Prefer Text/Edit/Document controls that already have text. LiveCaptions
                // has a small UIA tree so descendants is fine here.
                var textCondition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));

                AutomationElementCollection? candidates;
                try { candidates = root.FindAll(TreeScope.Descendants, textCondition); }
                catch { candidates = null; }
                if (candidates is null) continue;

                AutomationElement? best = null;
                int bestLen = 0;
                foreach (AutomationElement el in candidates)
                {
                    string txt = ReadElementText(el);
                    if (txt.Length > bestLen) { best = el; bestLen = txt.Length; }
                }
                if (best is not null) return best;
            }
            return null;
        }

        /// <summary>
        /// L80 — fast Children scan first, then full Subtree fallback. Children is
        /// near-instant when LiveCaptions is on the foreground desktop; Subtree is
        /// slower but reaches windows on non-primary virtual desktops or otherwise
        /// nested under a non-direct child of the desktop root.
        ///
        /// QC40-04 — previously, on a fast-path timeout we kicked off a second UIA
        /// RPC (Subtree) while the first (Children) was still in flight, holding
        /// two server slots and racing on the UIA RPC pool. We can't truly cancel
        /// a UIA call from C#, but we can flag the fast task as abandoned via a
        /// linked CTS so its callback never re-introduces an outdated result, and
        /// we can short-circuit a subsequent FindAll on an element the fast task
        /// produces after Subtree has already returned.
        /// </summary>
        private static AutomationElement? FindProcessRoot(int pid)
        {
            var pidCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);

            // Fast path — direct children of the root. We pass a CTS to the task so
            // we can signal "abandon" on timeout. UIA itself isn't cancellable, but
            // the flag stops us from racing two RPCs into the result-handling code.
            using var fastCts = new CancellationTokenSource();
            AutomationElement? fastResult = null;
            var fastTask = Task.Run(() =>
            {
                try
                {
                    var r = AutomationElement.RootElement.FindFirst(TreeScope.Children, pidCondition);
                    // QC40-04 — once we've been abandoned, skip publishing the
                    // result so we don't mutate a shared variable after the caller
                    // has moved on to the Subtree path.
                    if (fastCts.IsCancellationRequested) return null;
                    return r;
                }
                catch { return null; }
            }, fastCts.Token);

            bool fastCompleted;
            try
            {
                fastCompleted = fastTask.Wait(FastFindBudgetMs);
            }
            catch
            {
                fastCompleted = false;
            }
            if (fastCompleted)
            {
                try { fastResult = fastTask.Result; } catch { fastResult = null; }
                if (fastResult is not null) return fastResult;
            }
            else
            {
                // QC40-04 — abandon the fast task before kicking off Subtree so a
                // late-returning Children result is discarded inside the task body.
                try { fastCts.Cancel(); } catch { }

                // QC40-04 — properly observe the abandoned fast task to prevent
                // UnobservedTaskException tearing down the process. If the task
                // body throws AFTER we've timed out, the unawaited exception would
                // otherwise surface on TaskScheduler.UnobservedTaskException at GC
                // time. The continuation reads .Exception (which marks the task
                // as observed) and discards the result we no longer need. Runs
                // synchronously on completion — no extra threadpool churn.
                fastTask.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception; // mark observed
                }, TaskContinuationOptions.ExecuteSynchronously);
            }

            // Slow fallback — full subtree. Reliable; covers virtual desktops and
            // minimised / nested windows that Children misses.
            try
            {
                return AutomationElement.RootElement.FindFirst(TreeScope.Subtree, pidCondition);
            }
            catch { return null; }
        }

        private static bool IsElementAlive(AutomationElement el)
        {
            try { _ = el.Current.ProcessId; return true; }
            catch { return false; }
        }

        private static string ReadElementText(AutomationElement el)
        {
            try
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)
                    && vp is ValuePattern v
                    && !string.IsNullOrEmpty(v.Current.Value))
                    return v.Current.Value;
            }
            catch { }
            try
            {
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern t)
                {
                    string s = t.DocumentRange.GetText(-1);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            try { return el.Current.Name ?? ""; }
            catch { return ""; }
        }

        // ── Win+Ctrl+L key sequence (auto-launch LiveCaptions) ───────────────

        private const byte VK_LWIN    = 0x5B;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_L       = 0x4C;
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP   = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // QC40-05 — one-shot guard for the unsupported-OS warning. Without it, a
        // call site looping over PressWinCtrlL on Win10 would spam SystemLog with
        // the same line every iteration. Interlocked so a future multi-threaded
        // caller can't double-log either.
        private static int _winCtrlLUnsupportedLogged = 0;

        private static void PressWinCtrlL()
        {
            // QC40-05 — defense-in-depth OS guard at the key-event call site.
            // PollLoopAsync already shorts on Win10/pre-22621 via IsLiveCaptionsSupported,
            // but a future refactor (or a direct unit-test call into this method) could
            // bypass that guard and synthesize the chord on a build where Win+Ctrl+L is
            // mapped to "lock screen with Bing-of-the-day" — locking the streamer's
            // workstation mid-stream. Defend at the actual keybd_event site so the
            // dangerous chord can never be synthesized on an unsupported OS, no matter
            // how the call site is reached.
            if (!IsLiveCaptionsSupportedOs())
            {
                if (Interlocked.Exchange(ref _winCtrlLUnsupportedLogged, 1) == 0)
                {
                    GlobalLogger.Log(
                        "LiveCaptionService: refusing to send Win+Ctrl+L on unsupported OS (would trigger lock screen). Further attempts will be silently ignored.",
                        "LiveCaption", LogLevel.Communication);
                }
                return;
            }
            keybd_event(VK_LWIN,    0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            keybd_event(VK_L,       0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            keybd_event(VK_L,       0, KEYEVENTF_KEYUP,   UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP,   UIntPtr.Zero);
            keybd_event(VK_LWIN,    0, KEYEVENTF_KEYUP,   UIntPtr.Zero);
        }
    }
}
