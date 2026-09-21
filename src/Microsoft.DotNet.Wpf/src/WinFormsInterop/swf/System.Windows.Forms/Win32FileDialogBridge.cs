// The file browser Windows itself shows, for the Windows head.
//
// Mono's managed dialog is the fallback everywhere, and on X11 it is the only option there has ever
// been. On Windows it is a decade behind what the user is shown by every other application: measured
// side by side, ours is a 555x385 Windows-2000 dialog with a places bar and a "Look in:" combo where
// Windows puts up an 839x497 Explorer window with a navigation pane, a breadcrumb bar, a search box
// and a details view. No amount of laying out our own controls closes that -- it is a different
// program's window.
//
// GetOpenFileNameW / GetSaveFileNameW are the way in, and deliberately NOT IFileDialog: on Vista and
// later comdlg32 forwards these to the same modern picker, so the dialog is identical without a
// single COM interface being declared. That matters here -- the port's rule is that we do not
// hand-author COM bindings that tie it to Windows (see the notes on PTProvider for where the line
// is); a platform-gated DllImport is not that, and costs the other heads nothing because nothing
// outside OperatingSystem.IsWindows() ever reaches it.
//
// FOLDERS ARE NOT DONE HERE, and the reason is the same rule read the other way. The modern folder
// picker is IFileDialog with FOS_PICKFOLDERS and there is no comdlg32 entry point for it;
// SHBrowseForFolder exists and is plain P/Invoke, but it shows the OLD tree, which is not what
// Windows' own FolderBrowserDialog shows any more. Rather than swap one mismatch for another,
// ShowFolder says no and the managed dialog runs.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Windows.Forms
{
    internal sealed class Win32FileDialogBridge : IFileDialogBridge
    {
        /// <summary>The bridge for this platform, or null where there is no common dialog to call.
        /// Used only when nothing else has installed one -- a host that brings its own (the WPF
        /// integration layer does) still wins.</summary>
        internal static readonly IFileDialogBridge Default =
            OperatingSystem.IsWindows() ? new Win32FileDialogBridge() : null;

        private const int MaxPath = 32768;      // multiselect returns a directory plus every name

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        private const int OFN_READONLY = 0x00000001;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        private const int OFN_HIDEREADONLY = 0x00000004;
        private const int OFN_NOCHANGEDIR = 0x00000008;
        private const int OFN_ALLOWMULTISELECT = 0x00000200;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_EXPLORER = 0x00080000;

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

        /// <summary>Why the dialog said no. Zero means the user simply cancelled -- anything else is
        /// this code's fault and worth saying out loud, because a refusal and a cancellation are
        /// indistinguishable from the return value alone.</summary>
        [DllImport("comdlg32.dll")]
        private static extern int CommDlgExtendedError();

        public bool ShowOpen(FileDialogRequest request, out string[] fileNames, out int filterIndex)
            => Show(request, open: true, out fileNames, out filterIndex);

        public bool ShowSave(FileDialogRequest request, out string[] fileNames, out int filterIndex)
            => Show(request, open: false, out fileNames, out filterIndex);

        /// <summary>No. See the note at the top of this file: there is no comdlg32 folder picker,
        /// and the one Windows shows now cannot be reached without declaring COM interfaces. Saying
        /// so here is what keeps the managed tree reachable.</summary>
        public bool SupportsFolder => false;

        public bool ShowFolder(string description, string initialPath, out string selectedPath)
        {
            selectedPath = null;
            return false;
        }

        private static bool Show(FileDialogRequest request, bool open,
                                 out string[] fileNames, out int filterIndex)
        {
            fileNames = null;
            filterIndex = request.FilterIndex;

            IntPtr buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
            try
            {
                // The buffer carries the starting file name in and every chosen name out.
                var initial = new char[MaxPath];
                if (!string.IsNullOrEmpty(request.FileName))
                {
                    int n = Math.Min(request.FileName.Length, MaxPath - 1);
                    request.FileName.CopyTo(0, initial, 0, n);
                }
                Marshal.Copy(initial, 0, buffer, MaxPath);

                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                    hwndOwner = IntPtr.Zero,
                    lpstrFilter = ToNativeFilter(request.Filter),
                    nFilterIndex = Math.Max(1, request.FilterIndex),
                    lpstrFile = buffer,
                    nMaxFile = MaxPath,
                    lpstrInitialDir = request.InitialDirectory,
                    lpstrTitle = request.Title,
                    lpstrDefExt = string.IsNullOrEmpty(request.DefaultExt) ? null
                                                                          : request.DefaultExt.TrimStart('.'),
                    // NOCHANGEDIR because a dialog must not move the application's working directory
                    // out from under it; HIDEREADONLY because WinForms has no such property to carry
                    // the answer back to.
                    Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_HIDEREADONLY,
                };

                if (open)
                {
                    if (request.CheckFileExists) ofn.Flags |= OFN_FILEMUSTEXIST;
                    if (request.Multiselect) ofn.Flags |= OFN_ALLOWMULTISELECT;
                }
                else if (request.OverwritePrompt)
                {
                    ofn.Flags |= OFN_OVERWRITEPROMPT;
                }

                bool ok = open ? GetOpenFileNameW(ref ofn) : GetSaveFileNameW(ref ofn);
                if (!ok)
                {
                    int why = CommDlgExtendedError();
                    if (why != 0)
                        Console.Error.WriteLine($"[filedialog] comdlg32 refused: 0x{why:X4}");
                    return false;                       // 0 is the user cancelling, which is not news
                }

                filterIndex = ofn.nFilterIndex;
                fileNames = ReadNames(buffer, request.Multiselect && open);
                return fileNames.Length > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>WinForms states a filter as "Text|*.txt|All|*.*"; comdlg32 wants the same pairs
        /// separated by NULs and closed by an empty one.</summary>
        private static string ToNativeFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter)) return null;
            var sb = new StringBuilder();
            foreach (string part in filter.Split('|'))
                sb.Append(part).Append('\0');
            sb.Append('\0');
            return sb.ToString();
        }

        /// <summary>One name, or -- for a multiselect -- the directory followed by every file, each
        /// NUL-terminated, the lot closed by an empty one. A single selection comes back in the same
        /// shape as the one-name case even when multiselect was allowed, which is why the count
        /// decides rather than the flag.</summary>
        private static string[] ReadNames(IntPtr buffer, bool multiselect)
        {
            var chars = new char[MaxPath];
            Marshal.Copy(buffer, chars, 0, MaxPath);

            var parts = new System.Collections.Generic.List<string>();
            int start = 0;
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '\0') continue;
                if (i == start) break;                  // the empty string that ends the list
                parts.Add(new string(chars, start, i - start));
                start = i + 1;
            }

            if (parts.Count == 0) return Array.Empty<string>();
            if (!multiselect || parts.Count == 1) return new[] { parts[0] };

            string directory = parts[0];
            var full = new string[parts.Count - 1];
            for (int i = 1; i < parts.Count; i++)
                full[i - 1] = System.IO.Path.Combine(directory, parts[i]);
            return full;
        }
    }
}
