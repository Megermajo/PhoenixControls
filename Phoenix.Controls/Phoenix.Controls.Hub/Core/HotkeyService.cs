using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// System-wide keystroke listener that fires
    /// <c>on_hotkey("Ctrl+Shift+P")</c> blocks via Win32 RegisterHotKey.
    /// Bindings are sourced from every enabled .phx script's
    /// <see cref="ScriptInfo.HotkeyBindings"/> set; the service walks the
    /// ScriptRegistry on Start and on every <see cref="ScriptRegistry.OnChanged"/>
    /// fire to keep registrations in lock-step with the script set.
    ///
    /// Combo grammar: case-insensitive list of modifier names (Ctrl /
    /// Control / Alt / Shift / Win) plus exactly one key, separated by
    /// '+'. Parser normalises to a canonical "Ctrl+Shift+KEY" form so a
    /// declaration of <c>"shift+ctrl+p"</c> matches the Hotkey.<combo>
    /// EventType emitted on dispatch — case + ordering quirks don't drop
    /// matches.
    ///
    /// Lifetime: hidden NativeWindow owns the WM_HOTKEY pump. Off by
    /// default via <see cref="AppConfig.HotkeysEnabled"/> so a
    /// fresh-install Hub doesn't claim arbitrary keystrokes from the OS.
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private readonly ScriptManager _scriptManager;
        private readonly HotkeyWindow _wnd = new();

        // Keyed by combo string (canonicalised). Tracks the Win32 hotkey id
        // currently held for the registration so Unregister can target it precisely.
        // _registered.Value is the id; _usedIds is the reverse set for O(1) collision
        // detection when AllocateId hashes a new combo into the 0xC000..0xFFFF ATOM
        // range. The old monotonic _nextHotkeyId counter could exhaust the 14-bit
        // ATOM range on long sessions that re-resync many times; deriving the id from
        // a SHA-1 hash of the combo makes Register idempotent across resyncs.
        private readonly Dictionary<string, int> _registered = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _usedIds = new();

        // Serializes ALL access to _registered / _usedIds.
        // ResyncBindings (ScriptRegistry.OnChanged thread) and OnHotkeyId (UI/WndProc
        // thread) otherwise mutate + read these collections concurrently — a torn
        // Dictionary read can throw or return a stale id. Held only around the
        // collection mutations; never across the ExecuteOnHotkeyScriptsAsync await.
        private readonly object _bindingsLock = new();

        // ATOM-range bounds (User32 reserves 0xC000..0xFFFF for hotkey + atom ids).
        private const int HotkeyIdMin = 0xC000;
        private const int HotkeyIdMax = 0xFFFF;

        private bool _running;
        private bool _disposed;

        public HotkeyService(ScriptManager scriptManager)
        {
            _scriptManager = scriptManager ?? throw new ArgumentNullException(nameof(scriptManager));
            _wnd.OnHotkeyId += OnHotkeyId;
        }

        /// <summary>
        /// Walks every enabled script's HotkeyBindings, registers each combo
        /// with Win32, and arms the WndProc pump. Idempotent — calls after
        /// the first do a re-sync (drop registrations no longer in scripts,
        /// add new ones) without dropping the message-pump window.
        /// </summary>
        public void Start()
        {
            if (_disposed) return;
            _running = true;
            ResyncBindings();
            ScriptRegistry.Instance.OnChanged += OnScriptsChanged;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { ScriptRegistry.Instance.OnChanged -= OnScriptsChanged; } catch { }
            UnregisterAll();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _wnd.DestroyHandle(); } catch { }
        }

        private void OnScriptsChanged()
        {
            if (!_running) return;
            try { ResyncBindings(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("HotkeyService", "ResyncBindings failed", ex);
            }
        }

        /// <summary>
        /// Diffs the current registrations against the union of every
        /// enabled script's HotkeyBindings. Registers new combos, drops
        /// stale ones. Combos that fail to parse are logged once and
        /// otherwise ignored — a typo in a script's <c>on_hotkey</c>
        /// declaration shouldn't take down the whole service.
        /// </summary>
        private void ResyncBindings()
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in ScriptRegistry.Instance.GetAll())
            {
                if (!info.IsEnabled) continue;
                foreach (var combo in info.HotkeyBindings)
                {
                    string canonical = CanonicalizeCombo(combo);
                    if (!string.IsNullOrEmpty(canonical)) wanted.Add(canonical);
                }
            }

            // Serialize the diff-and-apply against
            // _registered / _usedIds. Register / Unregister / AllocateId reacquire
            // the same (re-entrant) lock, so the whole resync is atomic w.r.t. an
            // OnHotkeyId read on the WndProc thread. No await runs under the lock.
            lock (_bindingsLock)
            {
                // Drop combos no longer present.
                var stale = _registered.Keys.Where(k => !wanted.Contains(k)).ToList();
                foreach (var combo in stale)
                    Unregister(combo);

                // Add combos that aren't yet registered.
                foreach (var combo in wanted)
                {
                    if (_registered.ContainsKey(combo)) continue;
                    Register(combo);
                }
            }
        }

        private void Register(string combo)
        {
            if (!TryParseCombo(combo, out uint mods, out uint vk))
            {
                GlobalLogger.Log($"HotkeyService: unparseable combo '{combo}' — skipped", "HotkeyService", LogLevel.System);
                return;
            }
            int id = AllocateId(combo);
            if (id == 0)
            {
                GlobalLogger.Log($"HotkeyService: no free hotkey id for '{combo}' (ATOM range full)",
                    "HotkeyService", LogLevel.System);
                return;
            }
            // MOD_NOREPEAT (0x4000) prevents Win32 from re-firing
            // WM_HOTKEY at the keyboard auto-repeat rate (~30Hz) while the
            // chord is held. Without it, a held Ctrl+Shift+P dispatches the
            // bound script dozens of times a second.
            uint flags = mods | MOD_NOREPEAT;
            if (!RegisterHotKey(_wnd.Handle, id, flags, vk))
            {
                int err = Marshal.GetLastWin32Error();
                // On ERROR_HOTKEY_ALREADY_REGISTERED (1409) the Win32
                // table has a binding for (mods, vk) on this thread that we don't
                // know about — typically left over from a crashed prior Hub instance,
                // or a stale id we orphaned earlier. Attempt to unregister against
                // the recycled id we just allocated AND against any prior id we
                // remember for this combo (covers the "we re-allocated a different
                // slot but the old one is still held" case), then retry once. We do
                // NOT walk the entire id range — blasting UnregisterHotKey at every
                // 0xC000..0xFFFF slot risks tearing down hotkeys owned by a different
                // process / thread that happened to grab the same combo first.
                if (err == ERROR_HOTKEY_ALREADY_REGISTERED)
                {
                    GlobalLogger.Log(
                        $"HotkeyService: RegisterHotKey('{combo}') hit ERROR_HOTKEY_ALREADY_REGISTERED (1409); attempting recovery.",
                        "HotkeyService", LogLevel.System);
                    TryUnregister(id, $"recovery-pre-retry id={id}");
                    if (_registered.TryGetValue(combo, out int priorId) && priorId != id)
                        TryUnregister(priorId, $"recovery-prior-binding combo='{combo}' priorId={priorId}");
                    if (RegisterHotKey(_wnd.Handle, id, flags, vk))
                    {
                        _registered[combo] = id;
                        GlobalLogger.Log($"HotkeyService: recovered + bound '{combo}' (id={id}) on retry",
                            "HotkeyService", LogLevel.System);
                        return;
                    }
                    int err2 = Marshal.GetLastWin32Error();
                    GlobalLogger.Log(
                        $"HotkeyService: retry after 1409 still failed for '{combo}' (Win32 err {err2}) — leaving combo unbound",
                        "HotkeyService", LogLevel.System);
                    _usedIds.Remove(id);
                    return;
                }
                GlobalLogger.Log($"HotkeyService: RegisterHotKey('{combo}') failed (Win32 err {err})",
                    "HotkeyService", LogLevel.System);
                _usedIds.Remove(id);
                return;
            }
            _registered[combo] = id;
            GlobalLogger.Log($"HotkeyService: bound '{combo}' (id={id})", "HotkeyService", LogLevel.System);
        }

        // Single Unregister call wrapper that surfaces Win32 failures
        // via GlobalLogger rather than swallowing them. The previous shape
        // (`try { UnregisterHotKey(...) } catch { }`) hid the case where the
        // OS still held the binding after we removed it from `_registered`,
        // setting up a stale-binding collision when the same id was recycled.
        // The `reason` string lets the log reader see whether the call came
        // from a planned Unregister (Stop / resync drop) or the 1409 recovery
        // path inside Register.
        private void TryUnregister(int id, string reason)
        {
            bool ok;
            int err = 0;
            try
            {
                ok = UnregisterHotKey(_wnd.Handle, id);
                if (!ok) err = Marshal.GetLastWin32Error();
            }
            catch (Exception ex)
            {
                GlobalLogger.Log($"HotkeyService: UnregisterHotKey threw for id={id} ({reason}): {ex.Message}",
                    "HotkeyService", LogLevel.System);
                return;
            }
            if (!ok)
            {
                GlobalLogger.Log($"HotkeyService: UnregisterHotKey failed (Win32 err {err}) for id={id} ({reason})",
                    "HotkeyService", LogLevel.System);
            }
        }

        /// <summary>
        /// Deterministic id derived from a SHA-1 hash of the combo, mapped into
        /// the ATOM range (0xC000..0xFFFF). On collision we linear-probe to the next free
        /// slot within the same dictionary. Returns 0 if the entire range is exhausted —
        /// in practice this requires >16K simultaneously registered hotkeys.
        /// </summary>
        private int AllocateId(string combo)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combo));
            int seed = ((hash[0] << 8) | hash[1]) & 0x3FFF;
            int start = HotkeyIdMin + seed;

            // Guard the _usedIds linear-probe. The lock
            // is re-entrant — callers (Register, via ResyncBindings) already hold it.
            int range = HotkeyIdMax - HotkeyIdMin + 1;
            lock (_bindingsLock)
            {
                for (int i = 0; i < range; i++)
                {
                    int candidate = HotkeyIdMin + ((start - HotkeyIdMin + i) % range);
                    if (_usedIds.Add(candidate)) return candidate;
                }
            }
            return 0;
        }

        private void Unregister(string combo)
        {
            if (!_registered.TryGetValue(combo, out int id)) return;
            // Don't swallow Win32 failures here. If the OS thinks the
            // binding is still held (e.g. focus was on a process owning the same
            // chord, or the message-pump window handle was already destroyed) we
            // need to know so a recycled id isn't reused on top of a still-bound
            // combo. TryUnregister surfaces the error to GlobalLogger.
            TryUnregister(id, $"planned-unregister combo='{combo}'");
            _registered.Remove(combo);
            _usedIds.Remove(id);
        }

        private void UnregisterAll()
        {
            // Snapshot + drop under _bindingsLock so a
            // shutdown-thread UnregisterAll can't race an OnHotkeyId read / a resync.
            // Re-entrant: Unregister's own _registered / _usedIds access nests safely.
            lock (_bindingsLock)
            {
                foreach (var kv in _registered.ToList())
                    Unregister(kv.Key);
            }
        }

        private void OnHotkeyId(int id)
        {
            // Read _registered under _bindingsLock so a
            // concurrent ResyncBindings mutation on the OnChanged thread can't tear
            // the lookup. The lock is released BEFORE the fire-and-forget dispatch
            // below — we never hold it across the ExecuteOnHotkeyScriptsAsync await.
            string? combo;
            lock (_bindingsLock)
            {
                combo = _registered.FirstOrDefault(kv => kv.Value == id).Key;
            }
            if (string.IsNullOrEmpty(combo)) return;
            // Fire-and-forget — the script runs on the thread pool. The
            // WndProc is on the UI thread; we MUST NOT await here or risk
            // pumping additional WM_HOTKEY messages re-entrantly.
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => _scriptManager.ExecuteOnHotkeyScriptsAsync(combo!),
                "HotkeyService", $"dispatch '{combo}'");
        }

        // ── Combo parsing ────────────────────────────────────────────────

        // Canonical form: "Ctrl+Shift+P" — modifiers in fixed order, single
        // letter / function key in upper case. Used as the key for
        // _registered AND as the suffix on the EventType so the engine's
        // ShouldEnterBlock check matches an exact-string declaration.
        public static string CanonicalizeCombo(string raw)
        {
            if (!TryParseCombo(raw, out uint mods, out uint vk)) return string.Empty;
            var parts = new List<string>();
            if ((mods & MOD_CTRL)  != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT)   != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & MOD_WIN)   != 0) parts.Add("Win");
            parts.Add(VirtualKeyName(vk));
            return string.Join("+", parts);
        }

        public static bool TryParseCombo(string raw, out uint mods, out uint vk)
        {
            mods = 0;
            vk   = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var tokens = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in tokens)
            {
                switch (t.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= MOD_CTRL;  break;
                    case "alt":     mods |= MOD_ALT;   break;
                    case "shift":   mods |= MOD_SHIFT; break;
                    case "win":
                    case "windows": mods |= MOD_WIN;   break;
                    default:
                        if (vk != 0) return false; // more than one non-modifier key
                        vk = ParseVirtualKey(t);
                        break;
                }
            }
            return vk != 0;
        }

        private static uint ParseVirtualKey(string token)
        {
            if (string.IsNullOrEmpty(token)) return 0;
            // Single character A-Z / 0-9 maps to its uppercase byte.
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
            }
            // Function keys F1-F24.
            if (token.Length >= 2 && token[0] is 'F' or 'f' &&
                int.TryParse(token.AsSpan(1), out int fn) &&
                fn >= 1 && fn <= 24)
                return (uint)(0x70 + (fn - 1));
            return token.ToLowerInvariant() switch
            {
                "space"     => 0x20,
                "enter"     => 0x0D,
                "tab"       => 0x09,
                "esc"       => 0x1B,
                "escape"    => 0x1B,
                "left"      => 0x25,
                "up"        => 0x26,
                "right"     => 0x27,
                "down"      => 0x28,
                "insert"    => 0x2D,
                "delete"    => 0x2E,
                "home"      => 0x24,
                "end"       => 0x23,
                "pageup"    => 0x21,
                "pagedown"  => 0x22,
                _           => 0,
            };
        }

        private static string VirtualKeyName(uint vk)
        {
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F).ToString();
            return vk switch
            {
                0x20 => "Space",
                0x0D => "Enter",
                0x09 => "Tab",
                0x1B => "Escape",
                0x25 => "Left",
                0x26 => "Up",
                0x27 => "Right",
                0x28 => "Down",
                0x2D => "Insert",
                0x2E => "Delete",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "PageUp",
                0x22 => "PageDown",
                _    => $"VK{vk:X2}",
            };
        }

        // ── Win32 ────────────────────────────────────────────────────────

        private const int WM_HOTKEY = 0x0312;

        private const uint MOD_ALT     = 0x0001;
        private const uint MOD_CTRL    = 0x0002;
        private const uint MOD_SHIFT   = 0x0004;
        private const uint MOD_WIN     = 0x0008;
        private const uint MOD_NOREPEAT= 0x4000;

        // Win32 error code returned by RegisterHotKey when the
        // (hWnd, id, mods, vk) tuple collides with a binding the OS already
        // tracks for the current thread. Used by the 1409 recovery path.
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Hidden message-pump window. NativeWindow + CreateHandle is the
        // standard pattern for receiving WM_HOTKEY without owning a Form.
        private sealed class HotkeyWindow : NativeWindow
        {
            public event Action<int>? OnHotkeyId;

            public HotkeyWindow()
            {
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    try { OnHotkeyId?.Invoke(id); } catch { }
                }
                base.WndProc(ref m);
            }
        }
    }
}
