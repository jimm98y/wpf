// The folder picker Windows itself shows: IFileOpenDialog with FOS_PICKFOLDERS, as .NET's
// FolderBrowserDialog.RunDialogVista drives it.
//
// There is no comdlg32 entry point for it, and SHBrowseForFolder shows the old tree, so the shell's
// dialog object is the only way to the picker Windows shows every other program. It is reached the
// way WindowsMediaBackend reaches Media Foundation: CoCreateInstance through a plain DllImport, then
// every method a function pointer read out of the object's vtable at its documented slot. No COM
// interface is declared ([ComImport]) and the runtime's COM marshaller is never involved, so nothing
// here ties the port to Windows beyond this file, which only Windows ever enters.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
    internal sealed partial class Win32FileDialogBridge
    {
        private static readonly Guid CLSID_FileOpenDialog = new Guid ("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
        private static readonly Guid IID_IFileOpenDialog = new Guid ("d57c7288-d4ad-4768-be02-9d969532d960");
        private static readonly Guid IID_IShellItem = new Guid ("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private static readonly Guid IID_IFileDialogCustomize = new Guid ("e6fdd21a-163f-4975-9c8c-a69f1ba37034");

        private const uint CLSCTX_INPROC_SERVER = 1;
        private const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40,
                           FOS_PATHMUSTEXIST = 0x800, FOS_FILEMUSTEXIST = 0x1000;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int ERROR_CANCELLED_HR = unchecked ((int) 0x800704C7);

        // IFileDialog vtable slots (IUnknown 0-2, IModalWindow::Show 3, then IFileDialog in order).
        private const int Slot_Show = 3, Slot_SetOptions = 9, Slot_GetOptions = 10, Slot_SetFolder = 12,
                          Slot_SetFileName = 15, Slot_SetTitle = 17, Slot_GetResult = 20;
        // IShellItem::GetDisplayName; IFileDialogCustomize::AddText.
        private const int Slot_GetDisplayName = 5, Slot_AddText = 11;

        [DllImport ("ole32.dll")]
        private static extern int CoCreateInstance (in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr obj);

        [DllImport ("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName (string path, IntPtr bindContext, in Guid iid, out IntPtr item);

        private static unsafe IntPtr* Vtbl (IntPtr o) => *(IntPtr**) o;

        private static unsafe void Release (IntPtr o)
        {
            if (o != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>) Vtbl (o) [2]) (o);
        }

        private static unsafe int QueryInterface (IntPtr o, in Guid iid, out IntPtr result)
        {
            fixed (Guid* g = &iid)
            fixed (IntPtr* r = &result)
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>) Vtbl (o) [0]) (o, g, r);
        }

        private static unsafe int CallString (IntPtr o, int slot, string s)
        {
            fixed (char* p = s)
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>) Vtbl (o) [slot]) (o, p);
        }

        private static unsafe int CallPtr (IntPtr o, int slot, IntPtr arg)
            => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>) Vtbl (o) [slot]) (o, arg);

        /// <summary>The shell's folder picker is there on every Windows this runs on.</summary>
        public bool SupportsFolder => true;

        /// <summary>.NET's FolderBrowserDialog.RunDialogVista: pick-folders over the file system,
        /// starting in the initial directory, the current selection preselected (its parent opened,
        /// its name in the field), and the description either as the title or as a line of text in
        /// the dialog.</summary>
        public unsafe bool ShowFolder (FolderDialogRequest request, out string selectedPath)
        {
            selectedPath = null;
            if (CoCreateInstance (CLSID_FileOpenDialog, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IFileOpenDialog, out IntPtr dialog) < 0
                || dialog == IntPtr.Zero)
                return false;
            try {
                uint options;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>) Vtbl (dialog) [Slot_GetOptions]) (dialog, &options);
                options |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>) Vtbl (dialog) [Slot_SetOptions]) (dialog, options);

                if (!string.IsNullOrEmpty (request.InitialDirectory))
                    SetFolder (dialog, request.InitialDirectory);

                if (!string.IsNullOrEmpty (request.SelectedPath)) {
                    string parent = Path.GetDirectoryName (request.SelectedPath);
                    if (parent == null || !Directory.Exists (parent)) {
                        CallString (dialog, Slot_SetFileName, request.SelectedPath);
                    } else {
                        SetFolder (dialog, parent);
                        CallString (dialog, Slot_SetFileName, Path.GetFileName (request.SelectedPath));
                    }
                }

                if (!string.IsNullOrEmpty (request.Description)) {
                    if (request.UseDescriptionForTitle) {
                        CallString (dialog, Slot_SetTitle, request.Description);
                    } else if (QueryInterface (dialog, IID_IFileDialogCustomize, out IntPtr customize) >= 0 && customize != IntPtr.Zero) {
                        try {
                            fixed (char* p = request.Description)
                                ((delegate* unmanaged[Stdcall]<IntPtr, uint, char*, int>) Vtbl (customize) [Slot_AddText]) (customize, 0, p);
                        } finally {
                            Release (customize);
                        }
                    }
                }

                // Owned by the window the user is in, as the file dialogs are.
                int hr;
                IntPtr cookie = Win32ThemingScope.Enter ();
                try {
                    hr = CallPtr (dialog, Slot_Show, GetActiveWindow ());
                } finally {
                    Win32ThemingScope.Leave (cookie);
                }
                if (hr < 0) {
                    if (hr != ERROR_CANCELLED_HR)
                        Console.Error.WriteLine ($"[folderdialog] IFileOpenDialog::Show failed: 0x{hr:X8}");
                    return false;
                }

                IntPtr item;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>) Vtbl (dialog) [Slot_GetResult]) (dialog, &item) < 0 || item == IntPtr.Zero)
                    return false;
                try {
                    char* name;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, char**, int>) Vtbl (item) [Slot_GetDisplayName]) (item, SIGDN_FILESYSPATH, &name) < 0)
                        return false;
                    selectedPath = new string (name);
                    Marshal.FreeCoTaskMem ((IntPtr) name);
                    return true;
                } finally {
                    Release (item);
                }
            } finally {
                Release (dialog);
            }
        }

        private static void SetFolder (IntPtr dialog, string path)
        {
            if (SHCreateItemFromParsingName (path, IntPtr.Zero, IID_IShellItem, out IntPtr folder) < 0 || folder == IntPtr.Zero)
                return;
            try {
                CallPtr (dialog, Slot_SetFolder, folder);
            } finally {
                Release (folder);
            }
        }
    }
}
