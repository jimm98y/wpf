// The Windows clipboard for the driver: text through user32 (OpenClipboard, SetClipboardData with
// CF_UNICODETEXT), the way Windows' own edit control copies. No COM.
//
// The driver's text went to the system only through a bridge a WPF host installed (WPF's Clipboard).
// A WinForms window with no WindowsFormsHost around it -- an application's own dialog, the
// unhandled-exception dialog -- had none, so Copy put the text in the process's private store and
// nothing reached the clipboard: Ctrl+C in the "Exception details" box did nothing anyone could see.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Windows.Forms
{
    internal sealed class Win32ClipboardBridge : IClipboardBridge
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        // The clipboard's owner: EmptyClipboard makes the window that opened it the owner, and
        // SetClipboardData fails when that is no window at all. A message-only window per process.
        private static IntPtr s_owner;
        private static readonly object s_lock = new object();

        private static IntPtr Owner()
        {
            lock (s_lock)
            {
                if (s_owner == IntPtr.Zero)
                    s_owner = CreateWindowExW(0, "STATIC", "WpfWebGpu clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return s_owner;
            }
        }

        /// <summary>Another application may hold the clipboard for a moment: WinForms retries ten
        /// times, 100 ms apart; a shorter wait does here.</summary>
        private static bool Open()
        {
            IntPtr owner = Owner();
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(owner)) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        public void SetText(string text)
        {
            if (!Open()) return;
            try
            {
                EmptyClipboard();
                int bytes = (text.Length + 1) * 2;
                IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (mem == IntPtr.Zero) return;
                IntPtr p = GlobalLock(mem);
                if (p == IntPtr.Zero) { GlobalFree(mem); return; }
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                    Marshal.WriteInt16(p, text.Length * 2, 0);
                }
                finally { GlobalUnlock(mem); }
                if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero) GlobalFree(mem);   // the system owns it once set
            }
            finally { CloseClipboard(); }
        }

        public bool TryGetText(out string text)
        {
            text = null;
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !Open()) return false;
            try
            {
                IntPtr mem = GetClipboardData(CF_UNICODETEXT);
                if (mem == IntPtr.Zero) return false;
                IntPtr p = GlobalLock(mem);
                if (p == IntPtr.Zero) return false;
                try
                {
                    int max = (int)Math.Min((ulong)GlobalSize(mem) / 2, int.MaxValue);
                    int n = 0;
                    while (n < max && Marshal.ReadInt16(p, n * 2) != 0) n++;
                    text = Marshal.PtrToStringUni(p, n);
                    return true;
                }
                finally { GlobalUnlock(mem); }
            }
            finally { CloseClipboard(); }
        }

        public bool ContainsText() => IsClipboardFormatAvailable(CF_UNICODETEXT);

        public void Clear()
        {
            if (!Open()) return;
            try { EmptyClipboard(); }
            finally { CloseClipboard(); }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr mem);
    }
}
