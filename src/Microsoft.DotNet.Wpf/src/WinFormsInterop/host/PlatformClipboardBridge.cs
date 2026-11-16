// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system clipboard for the driver where there is no user32: the head's own (NSPasteboard on
// macOS, the Wayland selection, UIPasteboard, Android's ClipboardManager, the browser's), through
// WindowsBase's PlatformClipboard -- the same backends WPF's Clipboard uses.
//
// Only a WindowsFormsHost used to connect the driver to a system clipboard off Windows (through WPF's
// Clipboard). A WinForms application on its own -- Application.Run(new MainForm()) on a Mac -- had the
// process-local store and nothing else: Ctrl+C in a text box pasted nowhere else, and Ctrl+V brought
// in nothing another application had copied.
//

extern alias WindowsBase;

using System.Collections.Generic;
using PlatformClipboard = WindowsBase::MS.Internal.Interop.PlatformClipboard;
using ClipboardFormat = WindowsBase::MS.Internal.Interop.ClipboardFormat;

namespace System.Windows.Forms
{
    internal sealed class PlatformClipboardBridge : IClipboardBridge
    {
        internal static bool IsAvailable
        {
            get
            {
                try { return PlatformClipboard.IsAvailable; }
                catch { return false; }
            }
        }

        public bool TryGetText(out string text)
        {
            text = null;
            try { text = PlatformClipboard.GetString(); } catch { }
            return text != null;
        }

        public void SetText(string text)
        {
            try { PlatformClipboard.SetString(text); } catch { }
        }

        public bool ContainsText()
        {
            try { return PlatformClipboard.ContainsString(); } catch { return false; }
        }

        public void Clear()
        {
            try { PlatformClipboard.Clear(); } catch { }
        }

        public void SetFormats(string text, byte[] png, string html, string rtf, string[] files)
        {
            var items = new List<KeyValuePair<ClipboardFormat, byte[]>>();
            if (text != null) items.Add(new(ClipboardFormat.Text, Text.Encoding.UTF8.GetBytes(text)));
            if (html != null) items.Add(new(ClipboardFormat.Html, Text.Encoding.UTF8.GetBytes(html)));
            if (rtf != null) items.Add(new(ClipboardFormat.Rtf, Text.Encoding.UTF8.GetBytes(rtf)));
            if (png != null) items.Add(new(ClipboardFormat.Png, png));
            if (files is { Length: > 0 }) items.Add(new(ClipboardFormat.FileList, PlatformClipboard.EncodeFileList(files)));
            try { PlatformClipboard.SetFormats(items); } catch { }
        }

        public bool Contains(ClipKind kind)
        {
            try { return PlatformClipboard.ContainsData(ToFormat(kind)); }
            catch { return false; }
        }

        public byte[] GetPng() => Get(ClipboardFormat.Png);

        public string GetHtml() => Get(ClipboardFormat.Html) is { } b ? Text.Encoding.UTF8.GetString(b) : null;

        public string GetRtf() => Get(ClipboardFormat.Rtf) is { } b ? Text.Encoding.UTF8.GetString(b) : null;

        public string[] GetFiles()
        {
            try { return PlatformClipboard.GetFileList(); }
            catch { return null; }
        }

        // Windows' CF_HTML, one implementation for WPF and WinForms (WindowsBase's)
        internal static string HtmlFragment(string cfHtml) => PlatformClipboard.HtmlFragment(cfHtml);
        internal static string ToCfHtml(string fragment) => PlatformClipboard.ToCfHtml(fragment);

        private static byte[] Get(ClipboardFormat format)
        {
            try { return PlatformClipboard.GetData(format); }
            catch { return null; }
        }

        private static ClipboardFormat ToFormat(ClipKind kind) => kind switch
        {
            ClipKind.Png => ClipboardFormat.Png,
            ClipKind.Html => ClipboardFormat.Html,
            ClipKind.Rtf => ClipboardFormat.Rtf,
            ClipKind.Files => ClipboardFormat.FileList,
            _ => ClipboardFormat.Text,
        };
    }
}
