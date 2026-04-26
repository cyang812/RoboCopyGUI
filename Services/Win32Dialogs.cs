using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RoboCopyGUI.Services;

/// <summary>
/// Win32 IFileOpenDialog wrappers. We prefer these over WinRT
/// <c>FolderPicker</c> / <c>FileOpenPicker</c> because:
///   - WinRT FolderPicker greys out "Select" on the initially shown folder until
///     you re-enter it (a long-standing UX bug for users).
///   - WinRT pickers ignore <c>SuggestedStartLocation</c> if Windows still
///     remembers a different per-process MRU path, which makes the dialog
///     pop open at the dest folder when the user wanted Add Files.
/// </summary>
public static class Win32Dialogs
{
    /// <summary>Show a folder-picker rooted at <paramref name="initialFolder"/> if it exists.
    /// Returns the selected folder path, or <c>null</c> if cancelled.</summary>
    /// <param name="clientGuid">
    /// Optional unique GUID identifying this picker's MRU bucket. Use distinct GUIDs for
    /// source vs destination pickers so Windows doesn't reuse the wrong last-folder.
    /// </param>
    /// <param name="prefillLeafForReselect">
    /// When true, the dialog opens at the *parent* of <paramref name="initialFolder"/> with
    /// the leaf folder name pre-filled in the file-name textbox. This is the right UX when
    /// the user almost certainly wants to confirm the *same* folder again (destination
    /// pickers). It is the wrong UX for source pickers where the user wants to navigate
    /// to a different folder — in that case the leftover leaf in the textbox makes
    /// "Select Folder" search for a non-existent child path. Set this to false for source.
    /// </param>
    public static string? PickFolder(IntPtr hwndOwner, string? initialFolder, Guid? clientGuid = null, bool prefillLeafForReselect = false)
    {
        var dlg = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            if (clientGuid.HasValue)
            {
                Guid g = clientGuid.Value;
                dlg.SetClientGuid(ref g);
            }

            dlg.GetOptions(out FOS opts);
            opts |= FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM | FOS.FOS_PATHMUSTEXIST | FOS.FOS_NOCHANGEDIR;
            dlg.SetOptions(opts);

            if (prefillLeafForReselect && !string.IsNullOrWhiteSpace(initialFolder))
            {
                // Open at the parent and put the leaf name in the textbox so "Select Folder"
                // confirms the existing folder on the first click.
                try
                {
                    string trimmed = initialFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string? parent = Path.GetDirectoryName(trimmed);
                    string leaf = Path.GetFileName(trimmed);
                    if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf) && TrySetFolderRaw(dlg, parent))
                    {
                        dlg.SetFileName(leaf);
                    }
                    else
                    {
                        TrySetFolder(dlg, initialFolder);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "PickFolder: pre-fill leaf failed for {Folder}", initialFolder);
                    TrySetFolder(dlg, initialFolder);
                }
            }
            else
            {
                // Source-style: just land *inside* the saved folder so the user can navigate.
                TrySetFolder(dlg, initialFolder);
            }

            int hr = dlg.Show(hwndOwner);
            if (hr != 0) return null; // user cancelled or error
            dlg.GetResult(out IShellItem item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out IntPtr pszPath);
            try
            {
                return Marshal.PtrToStringUni(pszPath);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pszPath);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dlg);
        }
    }

    /// <summary>Show a file-picker rooted at <paramref name="initialFolder"/>; allows multi-select.</summary>
    public static IReadOnlyList<string> PickFiles(IntPtr hwndOwner, string? initialFolder, Guid? clientGuid = null)
    {
        var dlg = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            if (clientGuid.HasValue)
            {
                Guid g = clientGuid.Value;
                dlg.SetClientGuid(ref g);
            }

            dlg.GetOptions(out FOS opts);
            opts |= FOS.FOS_ALLOWMULTISELECT | FOS.FOS_FILEMUSTEXIST | FOS.FOS_FORCEFILESYSTEM | FOS.FOS_NOCHANGEDIR;
            dlg.SetOptions(opts);

            TrySetFolder(dlg, initialFolder);

            int hr = dlg.Show(hwndOwner);
            if (hr != 0) return Array.Empty<string>();

            dlg.GetResults(out IShellItemArray array);
            try
            {
                array.GetCount(out uint count);
                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    array.GetItemAt(i, out IShellItem item);
                    try
                    {
                        item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out IntPtr pszPath);
                        try
                        {
                            string? p = Marshal.PtrToStringUni(pszPath);
                            if (!string.IsNullOrWhiteSpace(p)) paths.Add(p!);
                        }
                        finally { Marshal.FreeCoTaskMem(pszPath); }
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return paths;
            }
            finally
            {
                Marshal.ReleaseComObject(array);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dlg);
        }
    }

    private static void TrySetFolder(IFileOpenDialog dlg, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            // First try the path as given. SHCreateItemFromParsingName works for UNC
            // paths and existing local folders without needing Directory.Exists, which
            // can be slow/unreliable for network shares.
            if (TrySetFolderRaw(dlg, folder)) return;

            // Otherwise walk up until we find an ancestor we *can* parse (e.g. previous
            // folder may have been deleted/renamed). Cap the walk to avoid an infinite
            // loop on weird path roots.
            string? probe = Path.GetDirectoryName(folder);
            for (int i = 0; i < 16 && !string.IsNullOrEmpty(probe); i++)
            {
                if (TrySetFolderRaw(dlg, probe)) return;
                probe = Path.GetDirectoryName(probe);
            }
            Log.Debug("TrySetFolder: no ancestor of {Folder} could be set.", folder);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TrySetFolder failed for {Folder}", folder);
        }
    }

    /// <summary>Attempt to set the dialog's folder to the exact path given. Returns false on failure.</summary>
    private static bool TrySetFolderRaw(IFileOpenDialog dlg, string folder)
    {
        try
        {
            int hr = SHCreateItemFromParsingName(folder, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem item);
            if (hr != 0 || item is null) return false;
            try { dlg.SetFolder(item); return true; }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TrySetFolderRaw failed for {Folder}", folder);
            return false;
        }
    }

    // ---- COM interop ----

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
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
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [Flags]
    private enum FOS : uint
    {
        FOS_OVERWRITEPROMPT = 0x2,
        FOS_STRICTFILETYPES = 0x4,
        FOS_NOCHANGEDIR = 0x8,
        FOS_PICKFOLDERS = 0x20,
        FOS_FORCEFILESYSTEM = 0x40,
        FOS_ALLNONSTORAGEITEMS = 0x80,
        FOS_NOVALIDATE = 0x100,
        FOS_ALLOWMULTISELECT = 0x200,
        FOS_PATHMUSTEXIST = 0x800,
        FOS_FILEMUSTEXIST = 0x1000,
        FOS_CREATEPROMPT = 0x2000,
        FOS_SHAREAWARE = 0x4000,
        FOS_NOREADONLYRETURN = 0x8000,
        FOS_NOTESTFILECREATE = 0x10000,
        FOS_HIDEMRUPLACES = 0x20000,
        FOS_HIDEPINNEDPLACES = 0x40000,
        FOS_NODEREFERENCELINKS = 0x100000,
        FOS_DONTADDTORECENT = 0x2000000,
        FOS_FORCESHOWHIDDEN = 0x10000000,
        FOS_DEFAULTNOMINIMODE = 0x20000000,
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);
}
