using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Phoenix.Controls.Shared.WinUI.Services;

/// <summary>
/// COM-interop wrapper around Win32 IFileOpenDialog / IFileSaveDialog.
/// WinUI 3's <c>FileOpenPicker.SuggestedStartLocation</c> only accepts the
/// <c>PickerLocationId</c> enum (Documents / Pictures / Music / …); there is
/// no API to seed an arbitrary folder. Architect / Visualist need pickers
/// that land at the project's <c>data/logic/</c> + <c>data/layers/</c>
/// directories, which forces the legacy COM dialog. The dialog still
/// uses the modern Vista+ shell UI — only the surfacing API differs.
/// </summary>
/// <remarks>
///  <b>STA-thread requirement.</b> Both <see cref="PickSingleFile"/>
/// and <see cref="PickSaveFile"/> instantiate Common Item Dialog COM objects
/// (<c>FileOpenDialogClass</c> / <c>FileSaveDialogClass</c>) and invoke
/// <c>IFileDialog.Show</c>, which require a Single-Threaded Apartment
/// caller. Calling from an MTA worker (default thread-pool thread) will
/// either throw <c>InvalidCastException</c> on the COM activation or
/// silently misbehave (modal never paints, no completion event). All
/// pillar entry points already run on the WinUI UI dispatcher, which is
/// STA by construction — this guard exists so background-task callers
/// fail loudly in Debug instead of presenting a frozen dialog in
/// Release. Wrap any background invocation in
/// <c>DispatcherQueue.TryEnqueue</c> (or equivalent) to marshal back to
/// the UI thread before calling.
/// </remarks>
public static class CustomFilePicker
{
    [Conditional("DEBUG")]
    private static void AssertSTA()
    {
        //  One-line STA guard. The dialog COM objects are
        // apartment-bound; running from MTA produces hard-to-diagnose
        // failures (silent no-show, COM cast exceptions). Fail loudly
        // in Debug so the caller learns to dispatch to the UI thread.
        Debug.Assert(
            Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "CustomFilePicker must be invoked from an STA thread (WinUI UI dispatcher). " +
            "Marshal background-thread callers via DispatcherQueue.TryEnqueue.");
    }

    private const int S_OK = 0;
    private const uint ERROR_CANCELLED = 0x800704C7;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [Flags]
    private enum FOS : uint
    {
        FOS_OVERWRITEPROMPT  = 0x00000002,
        FOS_PATHMUSTEXIST    = 0x00000800,
        FOS_FILEMUSTEXIST    = 0x00001000,
        FOS_NOREADONLYRETURN = 0x00008000,
        FOS_DONTADDTORECENT  = 0x02000000,
        FOS_FORCEFILESYSTEM  = 0x00000040,
        FOS_ALLOWMULTISELECT = 0x00000200,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass { }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialogClass { }

    // IFileDialog inherits from IModalWindow. Method order matters for the
    // COM vtable layout, so list IModalWindow's Show first, then IFileDialog
    // members in declaration order. The dialog-type-specific subinterfaces
    // (IFileOpenDialog / IFileSaveDialog) extend with extra methods we don't
    // need; QueryInterface to IFileDialog is enough for our use cases.
    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr hwndOwner);

        // IFileDialog
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid([In] ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // R57 — IFileOpenDialog extends IFileDialog with multi-result accessors. COM
    // interop doesn't inherit a base ComImport interface's vtable, so the full
    // IModalWindow + IFileDialog method list is re-declared in order, then the
    // two IFileOpenDialog-specific members. We only call GetResults.
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr hwndOwner);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid([In] ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    // R57 — minimal IShellItemArray. The methods before GetCount are reserved as
    // empty vtable slots (we never call them) so the GetCount / GetItemAt
    // offsets line up with the native vtable.
    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler();              // slot 0 — unused
        void GetPropertyStore();           // slot 1 — unused
        void GetPropertyDescriptionList(); // slot 2 — unused
        void GetAttributes();              // slot 3 — unused
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems();                  // last slot — unused
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    /// <summary>
    /// Show a single-file Open dialog seeded at <paramref name="defaultFolder"/>.
    /// Returns the selected absolute path or null if the user cancelled.
    /// Filters render in the dialog's file-type combo (e.g.
    /// <c>("Phoenix Graph", ".phxg")</c>).
    /// </summary>
    public static string? PickSingleFile(
        IntPtr hwnd,
        string? defaultFolder,
        IEnumerable<(string label, string extension)> filters)
    {
        AssertSTA();
        IFileDialog? dialog = null;
        try
        {
            dialog = (IFileDialog)new FileOpenDialogClass();
            dialog.SetOptions(FOS.FOS_FILEMUSTEXIST | FOS.FOS_PATHMUSTEXIST
                              | FOS.FOS_NOREADONLYRETURN | FOS.FOS_FORCEFILESYSTEM);
            ApplyFilters(dialog, filters);
            ApplyDefaultFolder(dialog, defaultFolder);

            int hr = dialog.Show(hwnd);
            if ((uint)hr == ERROR_CANCELLED || hr != S_OK) return null;

            //  Wrap the result IShellItem in try/finally so a throw
            // from GetDisplayName can't leak the COM ref. (Pre-fix: a
            // GetDisplayName exception bypassed ReleaseComObject entirely.)
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (COMException ex) when ((uint)ex.HResult == ERROR_CANCELLED)
        {
            return null;
        }
        finally
        {
            if (dialog is not null) Marshal.FinalReleaseComObject(dialog);
        }
    }

    /// <summary>
    /// R57 — show a multi-select Open dialog seeded at <paramref
    /// name="defaultFolder"/>. Returns every selected absolute path (empty when
    /// the user cancelled). Same filter shape as <see cref="PickSingleFile"/>.
    /// </summary>
    public static IReadOnlyList<string> PickMultipleFiles(
        IntPtr hwnd,
        string? defaultFolder,
        IEnumerable<(string label, string extension)> filters)
    {
        AssertSTA();
        object? comObj = null;
        try
        {
            comObj = new FileOpenDialogClass();
            var dialog = (IFileDialog)comObj;
            dialog.SetOptions(FOS.FOS_FILEMUSTEXIST | FOS.FOS_PATHMUSTEXIST
                              | FOS.FOS_NOREADONLYRETURN | FOS.FOS_FORCEFILESYSTEM
                              | FOS.FOS_ALLOWMULTISELECT);
            ApplyFilters(dialog, filters);
            ApplyDefaultFolder(dialog, defaultFolder);

            int hr = dialog.Show(hwnd);
            if ((uint)hr == ERROR_CANCELLED || hr != S_OK) return Array.Empty<string>();

            // QueryInterface to IFileOpenDialog for the multi-result accessor.
            var openDialog = (IFileOpenDialog)comObj;
            openDialog.GetResults(out var array);
            try
            {
                array.GetCount(out uint count);
                var results = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    array.GetItemAt(i, out var item);
                    try
                    {
                        item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                        if (!string.IsNullOrEmpty(path)) results.Add(path);
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return results;
            }
            finally { Marshal.ReleaseComObject(array); }
        }
        catch (COMException ex) when ((uint)ex.HResult == ERROR_CANCELLED)
        {
            return Array.Empty<string>();
        }
        finally
        {
            if (comObj is not null) Marshal.FinalReleaseComObject(comObj);
        }
    }

    /// <summary>
    /// Show a Save dialog seeded at <paramref name="defaultFolder"/> with
    /// <paramref name="suggestedFileName"/> pre-filled. The first filter's
    /// extension is also set as the default so a user typing a bare name
    /// gets the right suffix appended.
    /// </summary>
    public static string? PickSaveFile(
        IntPtr hwnd,
        string? defaultFolder,
        string? suggestedFileName,
        IEnumerable<(string label, string extension)> filters)
    {
        AssertSTA();
        IFileDialog? dialog = null;
        try
        {
            dialog = (IFileDialog)new FileSaveDialogClass();
            dialog.SetOptions(FOS.FOS_OVERWRITEPROMPT | FOS.FOS_PATHMUSTEXIST
                              | FOS.FOS_NOREADONLYRETURN | FOS.FOS_FORCEFILESYSTEM);
            var filterList = filters.ToList();
            ApplyFilters(dialog, filterList);
            ApplyDefaultFolder(dialog, defaultFolder);

            if (filterList.Count > 0)
                dialog.SetDefaultExtension(filterList[0].extension.TrimStart('.'));
            if (!string.IsNullOrEmpty(suggestedFileName))
                dialog.SetFileName(suggestedFileName);

            int hr = dialog.Show(hwnd);
            if ((uint)hr == ERROR_CANCELLED || hr != S_OK) return null;

            //  Wrap the result IShellItem in try/finally — see
            // sibling PickSingleFile for rationale.
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (COMException ex) when ((uint)ex.HResult == ERROR_CANCELLED)
        {
            return null;
        }
        finally
        {
            if (dialog is not null) Marshal.FinalReleaseComObject(dialog);
        }
    }

    private static void ApplyFilters(IFileDialog dialog, IEnumerable<(string label, string extension)> filters)
    {
        var array = filters.Select(f => new COMDLG_FILTERSPEC
        {
            pszName = f.label,
            pszSpec = f.extension.StartsWith("*", StringComparison.Ordinal) ? f.extension : "*" + f.extension,
        }).ToArray();
        if (array.Length == 0) return;
        dialog.SetFileTypes((uint)array.Length, array);
        dialog.SetFileTypeIndex(1);
    }

    private static void ApplyDefaultFolder(IFileDialog dialog, string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            // Materialise the folder so the picker doesn't fall back to the
            // user's MRU when a fresh install hasn't created data/logic/ yet.
            Directory.CreateDirectory(folder);
        }
        catch { return; }

        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out var item);
            //  Wrap in try/finally so a throw from SetFolder
            // (e.g. UAC restriction on the parsed path) can't leak the
            // IShellItem COM ref.
            try
            {
                // SetFolder forces every open to land here regardless of MRU —
                // matches Majo's "standard folder is the logic/layers folder"
                // expectation. SetDefaultFolder would defer to the MRU after
                // first use, which drifts.
                dialog.SetFolder(item);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch { /* best-effort — picker still shows, just at default location */ }
    }
}
