// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// On Windows a TextBox's Copy reaches the system clipboard even with no WPF host around the form:
// the driver writes CF_UNICODETEXT through user32. Before, only a WindowsFormsHost lent it a way out,
// so Copy in a plain WinForms dialog -- the unhandled-exception dialog's details -- stayed in process.
//
// This one touches the real clipboard, so it puts back the text that was there, and does not run at
// all when the clipboard holds something other than text (that could not be put back).
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class SystemClipboardTests
    {
        private const uint CF_UNICODETEXT = 13;

        [Fact]
        public void CopyInAReadOnlyTextBox_ReachesTheWindowsClipboard()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows clipboard");
            Assert.SkipUnless(SWF.XplatUIWebGpu.GetInstance() != null && SWF.XplatUIWebGpu.ClipboardBridge is SWF.Win32ClipboardBridge,
                "a host installed a clipboard of its own");
            bool hadText = IsClipboardFormatAvailable(CF_UNICODETEXT);
            Assert.SkipUnless(OnlyText(), "the clipboard holds something besides plain text, which could not be put back");
            string? before = hadText ? Read() : null;
            try
            {
                using var form = new SWF.Form { ClientSize = new Size(300, 200) };
                var box = new SWF.TextBox { Multiline = true, ReadOnly = true, Width = 280, Height = 150,
                    Text = "System.InvalidCastException " + Guid.NewGuid() };
                form.Controls.Add(box);
                form.CreateControl();
                box.SelectAll();
                box.Copy();
                Assert.Equal(box.Text, Read());
            }
            finally
            {
                var bridge = SWF.XplatUIWebGpu.ClipboardBridge!;
                if (before != null) bridge.SetText(before);
                else bridge.Clear();
            }
        }

        /// <summary>Nothing on the clipboard but plain text (CF_TEXT, CF_OEMTEXT, CF_UNICODETEXT, CF_LOCALE).</summary>
        private static bool OnlyText()
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f))
                    if (f != 1 && f != 7 && f != 13 && f != 16) return false;
                return true;
            }
            finally { CloseClipboard(); }
        }

        private static string? Read()
        {
            for (int i = 0; i < 10 && !OpenClipboard(IntPtr.Zero); i++) Thread.Sleep(20);
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }

        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    }
}
