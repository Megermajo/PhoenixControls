using System;
using System.Runtime.InteropServices;

namespace Phoenix.Controls.Updater;

/// <summary>
/// Authenticode trust verification for the <c>.phxupdate</c> archive,
/// per <c>redesign-plan/design/project/extras.jsx §11</c>.
///
/// Three outcomes (see <see cref="VerifyResult"/>):
///   <list type="bullet">
///     <item><b>Trusted</b> — file carries an embedded Authenticode signature,
///           the signature is valid, and the certificate chain terminates at a
///           trusted root.</item>
///     <item><b>Unsigned</b> — file has no embedded signature. Today's release
///           pipeline does not yet sign <c>.phxupdate</c> archives, so this is
///           the common case. Caller logs a warning and proceeds.</item>
///     <item><b>Untrusted</b> — file is signed but the signature failed
///           verification (tampering, expired certificate, revoked cert,
///           distrusted root). Caller MUST refuse to apply the update.</item>
///   </list>
///
/// Implementation: P/Invoke into <c>wintrust.dll</c>'s <c>WinVerifyTrust</c>
/// with <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>. This is the same machinery
/// SmartScreen and the OS use for installer/dll trust checks. BCL-only: no
/// new package references needed.
/// </summary>
internal static class Authenticode
{
    public enum VerifyResult
    {
        Trusted,
        Unsigned,
        Untrusted,
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE          = 2;
    private const uint WTD_REVOKE_NONE       = 0;
    private const uint WTD_CHOICE_FILE       = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE  = 2;

    // Selected HRESULTs returned by WinVerifyTrust.
    private const uint TRUST_E_NOSIGNATURE   = 0x800B0100;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_PROVIDER_UNKNOWN     = 0x800B0001;

    /// <summary>
    /// Verifies the Authenticode signature on <paramref name="filePath"/>.
    /// Always non-throwing: returns <see cref="VerifyResult.Untrusted"/> with
    /// a populated <paramref name="detail"/> on any unexpected error so
    /// callers fail closed.
    /// </summary>
    public static VerifyResult Verify(string filePath, out string detail)
    {
        detail = "";
        IntPtr filePtr = IntPtr.Zero;
        IntPtr dataPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct       = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath  = filePath,
                hFile          = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, filePtr, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData      = IntPtr.Zero,
                dwUIChoice          = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice       = WTD_CHOICE_FILE,
                pInfoUnion          = filePtr,
                dwStateAction       = WTD_STATEACTION_VERIFY,
                hWVTStateData       = IntPtr.Zero,
                pwszURLReference    = null,
                dwProvFlags         = 0,
                dwUIContext         = 0,
                pSignatureSettings  = IntPtr.Zero,
            };

            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, dataPtr, fDeleteOld: false);

            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
            uint hr = unchecked((uint)result);

            // Always close the wintrust state, regardless of the verify result.
            // (The state allocated by VERIFY must be released with CLOSE or the
            // wintrust subsystem leaks per call.)
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, dataPtr, fDeleteOld: true);
            WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

            if (result == 0)
            {
                detail = "trusted";
                return VerifyResult.Trusted;
            }
            if (hr == TRUST_E_NOSIGNATURE
                || hr == TRUST_E_SUBJECT_FORM_UNKNOWN
                || hr == TRUST_E_PROVIDER_UNKNOWN)
            {
                detail = $"unsigned (hr=0x{hr:X8})";
                return VerifyResult.Unsigned;
            }
            detail = $"signature verification failed (hr=0x{hr:X8})";
            return VerifyResult.Untrusted;
        }
        catch (DllNotFoundException ex)
        {
            // wintrust.dll missing — only realistic on a non-Windows or
            // heavily-locked-down runtime. Fail closed: caller treats this
            // like Untrusted and refuses the update.
            detail = $"wintrust.dll not available: {ex.Message}";
            return VerifyResult.Untrusted;
        }
        catch (Exception ex)
        {
            detail = $"verify threw: {ex.Message}";
            return VerifyResult.Untrusted;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint   cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint   cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint   dwUIChoice;
        public uint   fdwRevocationChecks;
        public uint   dwUnionChoice;
        public IntPtr pInfoUnion;
        public uint   dwStateAction;
        public IntPtr hWVTStateData;
        public string? pwszURLReference;
        public uint   dwProvFlags;
        public uint   dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
