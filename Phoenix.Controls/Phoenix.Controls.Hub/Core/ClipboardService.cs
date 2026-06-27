using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    ///  — clipboard-update listener. Subscribes to WM_CLIPBOARDUPDATE
    /// on a hidden NativeWindow via Win32 AddClipboardFormatListener, then
    /// fires <see cref="ScriptManager.ExecuteOnClipboardScriptsAsync"/> for
    /// every script that declares an <c>on_clipboard:</c> block. Matches the
    /// hidden-NativeWindow pattern HotkeyService established in .
    ///
    /// Off by default via <see cref="AppConfig.ClipboardWatchEnabled"/> — a
    /// fresh-install Hub never silently observes clipboard contents (privacy
    /// risk if the streamer copies a password while the script is enabled).
    /// </summary>
    public sealed class ClipboardService : IDisposable
    {
        private readonly ScriptManager _scriptManager;
        private readonly ClipboardWindow _wnd = new();
        private bool _running;
        private bool _disposed;

        public ClipboardService(ScriptManager scriptManager)
        {
            _scriptManager = scriptManager ?? throw new ArgumentNullException(nameof(scriptManager));
            _wnd.OnClipboardUpdate += HandleClipboardUpdate;
        }

        public void Start()
        {
            if (_disposed || _running) return;
            if (!AddClipboardFormatListener(_wnd.Handle))
            {
                int err = Marshal.GetLastWin32Error();
                GlobalLogger.Log(
                    $"ClipboardService: AddClipboardFormatListener failed (Win32 err {err})",
                    "ClipboardService", LogLevel.CriticalError);
                return;
            }
            _running = true;
            GlobalLogger.Log("ClipboardService listening (WM_CLIPBOARDUPDATE)", "ClipboardService", LogLevel.System);
        }

        public void Stop()
        {
            if (!_running) return;
            try { RemoveClipboardFormatListener(_wnd.Handle); } catch { }
            _running = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _wnd.DestroyHandle(); } catch { }
        }

        private void HandleClipboardUpdate()
        {
            // QC36-04 — sensitive-content opt-out gates. Password managers / IDEs
            // that copy credentials, OTP codes, or other secret material flag the
            // clipboard payload via either:
            //   (a) the documented "Clipboard Viewer Ignore" format (KeePass /
            //       1Password / Office / VS — atom name registered once, then
            //       checked with IsClipboardFormatAvailable per copy), OR
            //   (b) any clipboard format in the CF_PRIVATEFIRST..CF_PRIVATELAST
            //       range (Microsoft's "this is private, listeners stay out"
            //       reservation). Applications that put private bytes on the
            //       clipboard typically set BOTH a public format AND a private
            //       marker; we drop on the marker regardless of the public
            //       format.
            // If either is present, drop the event before reading any text.
            try
            {
                if (IsClipboardViewerIgnorePresent())
                {
                    GlobalLogger.Log("ClipboardService: skipping payload marked Clipboard Viewer Ignore",
                        "ClipboardService", LogLevel.System);
                    return;
                }
                if (IsAnyPrivateClipboardFormatPresent())
                {
                    GlobalLogger.Log("ClipboardService: skipping payload that advertised a CF_PRIVATEFIRST..CF_PRIVATELAST format",
                        "ClipboardService", LogLevel.System);
                    return;
                }
            }
            catch (Exception ex)
            {
                // [ / P1-24+P1-25] Stay on LogLevel.System (informational —
                // the probe is best-effort and the caller proceeds) but use the
                // Exception-carrying Log overload so the InnerException chain
                // and stack reach the ring buffer. Label names the operation.
                GlobalLogger.Log($"ClipboardService: ignore-format probe failed ({ex.Message}) — proceeding",
                    "ClipboardService", LogLevel.System, ex);
            }

            // Snapshot text on the UI thread (Clipboard.GetText must run STA).
            // The Hub UI thread owns the message pump that fires
            // OnClipboardUpdate, so we're already on the right thread.
            string text = string.Empty;
            try
            {
                if (Clipboard.ContainsText())
                    text = Clipboard.GetText() ?? string.Empty;
            }
            catch (Exception ex)
            {
                // [ / P1-24+P1-25] STA / clipboard-locked failures
                // are recoverable (we pass an empty string downstream) so the
                // entry stays at LogLevel.System; pass the exception through
                // the Log overload to preserve the chain + stack.
                GlobalLogger.Log($"ClipboardService: GetText failed ({ex.Message}) — passing empty string",
                    "ClipboardService", LogLevel.System, ex);
            }

            // QC36-04 — cap payload size so a copied multi-megabyte file blob doesn't
            // balloon the script var table or saturate the bus. The cap is sourced
            // from AppConfig.ClipboardMaxLengthBytes (default 4096 chars) so an
            // operator with a legitimate long-snippet use case can tune it without
            // a recompile. Values <= 0 clamp to the 4096 default.
            // NOTE: cap is measured in DECODED CHARACTERS, not raw bytes; for
            // ASCII-heavy clipboard text the two are equivalent. A future revision
            // could measure UTF-8 bytes if non-Latin payloads start showing up.
            // TODO QC36-04-followup: optional regex-shape credential detection
            // (JWT / Stripe sk_live_ / Slack xoxb- / base32 TOTP seeds) was
            // explicitly deferred from this sweep; revisit when the on_clipboard
            // header gets its first power-user shipping in a sample graph.
            int cfgCap = ConfigManager.Current?.ClipboardMaxLengthBytes ?? DefaultMaxClipboardTextLength;
            int cap    = cfgCap > 0 ? cfgCap : DefaultMaxClipboardTextLength;
            if (text.Length > cap)
            {
                GlobalLogger.Log(
                    $"ClipboardService: truncating clipboard text from {text.Length} to {cap} chars",
                    "ClipboardService", LogLevel.System);
                text = text.Substring(0, cap);
            }

            // Fire-and-forget — ScriptManager runs scripts on the thread pool;
            // we MUST NOT await on the UI thread or risk re-entering the
            // message pump for the next clipboard event before this one's done.
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => _scriptManager.ExecuteOnClipboardScriptsAsync(text),
                "ClipboardService", "dispatch");
        }

        // QC36-04 — fallback cap when AppConfig.ClipboardMaxLengthBytes is unset /
        // non-positive. 4KB matches typical pastebin / IRC URL-shortener limits and
        // is plenty for the on_clipboard text-snippet use case.
        private const int DefaultMaxClipboardTextLength = 4 * 1024;

        // The "Clipboard Viewer Ignore" atom name — Microsoft's documented opt-out
        // signal for clipboard listeners. Atom number varies per session; we resolve
        // it lazily via RegisterClipboardFormat on first use.
        private const string ClipboardViewerIgnoreFormatName = "Clipboard Viewer Ignore";
        private static uint _cachedIgnoreFormat;

        private static bool IsClipboardViewerIgnorePresent()
        {
            if (_cachedIgnoreFormat == 0)
            {
                _cachedIgnoreFormat = RegisterClipboardFormat(ClipboardViewerIgnoreFormatName);
                if (_cachedIgnoreFormat == 0) return false; // registration failed — fail open
            }
            return IsClipboardFormatAvailable(_cachedIgnoreFormat);
        }

        // QC36-04 — CF_PRIVATEFIRST..CF_PRIVATELAST sweep. Win32 reserves the range
        // 0x0200..0x02FF for application-private clipboard formats; a copy event
        // that advertises any of these formats is signalling "this is private —
        // skip". Many password managers / financial apps use this range alongside
        // the "Clipboard Viewer Ignore" atom; checking both maximises our coverage.
        // The sweep is O(256) and only fires on each clipboard change (~1/sec
        // upper bound under normal use), so the cost is negligible vs the leak risk.
        private const uint CF_PRIVATEFIRST = 0x0200;
        private const uint CF_PRIVATELAST  = 0x02FF;

        private static bool IsAnyPrivateClipboardFormatPresent()
        {
            for (uint fmt = CF_PRIVATEFIRST; fmt <= CF_PRIVATELAST; fmt++)
            {
                if (IsClipboardFormatAvailable(fmt)) return true;
            }
            return false;
        }

        // ── Win32 ────────────────────────────────────────────────────────

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        // QC36-04 — IsClipboardFormatAvailable + RegisterClipboardFormat for
        // the Clipboard Viewer Ignore opt-out check and the CF_PRIVATE* sweep.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        private sealed class ClipboardWindow : NativeWindow
        {
            public event Action? OnClipboardUpdate;

            public ClipboardWindow()
            {
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_CLIPBOARDUPDATE)
                {
                    try { OnClipboardUpdate?.Invoke(); } catch { }
                }
                base.WndProc(ref m);
            }
        }
    }
}
