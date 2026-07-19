using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// Disables the Windows IME / Text Services Framework input path for the whole
/// process at startup, closing a confirmed WinUI 3 UI-thread freeze.
///
/// <para><b>The bug (diagnosed from two native minidumps).</b> The UI thread
/// wedges in a NESTED message loop inside the Windows text-input framework —
/// <c>msctf!CIMEUIWindowHandler::ImeUIWndProcWorker</c> in one capture,
/// <c>Microsoft.UI.Xaml!TextServicesHost::TimerWindowProc</c> in another —
/// dispatched from the normal WinUI pump (<c>FrameworkApplication::RunDesktop\
/// WindowMessageLoop</c> → <c>DispatchMessageWorker</c>) when an inline
/// <c>TextBox</c> editor gains focus during canvas interaction (Architect node
/// editing under pan/zoom). That IME UI window proc runs its own
/// <c>GetMessage</c> pump which never returns; the app freezes 8–15s and never
/// self-recovers. It is machine-independent (the documented TSF/IME nested-pump
/// hang class: microsoft-ui-xaml #9216, Chromium #328859185) and reproduces
/// regardless of antivirus — the earlier Bitdefender lead was an artefact of
/// that product globally hooking <c>GetMessageW</c>.</para>
///
/// <para><b>The fix.</b> <c>ImmDisableIME((DWORD)-1)</c>, called before the
/// process's first top-level window is created, detaches every thread from the
/// IME message pump the dumps hang in. Phoenix Controls' text fields take
/// identifiers, numbers and command/message text — none of which need IME
/// *composition*. Latin-script keyboards (including German QWERTZ, which types
/// ä/ö/ü/ß as direct keys, not via an IME) are completely unaffected; only
/// CJK-style composition is turned off.</para>
///
/// <para>Gated by <see cref="AppConfig.DisableImeInput"/> (default true), read
/// here via a minimal standalone probe of <c>config.json</c> because the full
/// <c>ConfigManager</c> has not loaded this early in startup. A user who
/// genuinely needs IME composition can set the flag false and relaunch
/// (accepting that the freeze risk returns).</para>
/// </summary>
internal static class ImeGuard
{
    // BOOL ImmDisableIME(DWORD idThread); idThread == (DWORD)-1 → all threads
    // (current and future) in the process. Must be called before the thread's
    // first top-level window receives WM_CREATE.
    [DllImport("imm32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmDisableIME(uint idThread);

    private const uint ImmDisableAllThreads = unchecked((uint)-1);

    /// <summary>
    /// When configured, disable IME/TSF input process-wide. MUST be invoked from
    /// the App constructor (before the splash / main window is created).
    /// Best-effort: never throws, never blocks startup.
    /// </summary>
    internal static void DisableImeInputIfConfigured()
    {
        try
        {
            if (!ShouldDisable())
                return;

            bool ok = ImmDisableIME(ImmDisableAllThreads);
            TryLog(ok
                ? "IME/TSF input disabled process-wide (ImmDisableIME) — closes the WinUI text-input " +
                  "nested-pump freeze. Set AppConfig.DisableImeInput=false to re-enable IME composition."
                : "ImmDisableIME returned false — IME input may still be active (the call likely came " +
                  "after a window was already created).");
        }
        catch (Exception ex)
        {
            TryLog($"ImeGuard: ImmDisableIME failed — {ex.Message}");
        }
    }

    // Minimal, dependency-free probe of the single flag. Defaults to TRUE
    // (disable) on a missing file / unreadable JSON / absent key, so existing
    // installs — whose config.json predates the flag — get the fix by default.
    // Case-insensitive key match so it works whatever casing ConfigManager
    // serialised with.
    private static bool ShouldDisable()
    {
        try
        {
            string path = Paths.AppConfigJson;
            if (!File.Exists(path))
                return true;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return true;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, nameof(AppConfig.DisableImeInput), StringComparison.OrdinalIgnoreCase)
                    && (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False))
                {
                    return prop.Value.GetBoolean();
                }
            }
        }
        catch
        {
            // Any parse/IO error → default on (fix applied).
        }
        return true;
    }

    private static void TryLog(string message)
    {
        try { GlobalLogger.Log(message, "ImeGuard", LogLevel.System); }
        catch { /* logger not up yet — ignore */ }
    }
}
