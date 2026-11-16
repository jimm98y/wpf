// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Dispatches clipboard operations to the platform backend, the third sibling of PlatformWindow and
// the same idea: one seam, so a new head is a new backend rather than a new branch at every call
// site.
//
// The type names are deliberately abstract rather than either platform's spelling. macOS wants
// Uniform Type Identifiers ("public.utf8-plain-text"), Wayland and the browser want MIME types
// ("text/plain;charset=utf-8", "text/plain") and Android wants its own MIME spellings, so callers
// name the CONTENT and each backend supplies its own vocabulary.
//
// All six heads are here now. Three of them (browser, iOS, Android) had no backend at all until
// recently and fell through to the "unavailable" answers at the bottom of each method, which meant
// Clipboard.SetText reached MacDataObject's in-process dictionary and stopped: nothing a WPF app
// copied could be pasted into any other application, and nothing copied elsewhere could be pasted
// in. Each of the three has its own file next door, and each is a genuinely different mechanism --
// UIPasteboard through the Objective-C runtime, ClipboardManager through the Android head payload,
// and the browser's asynchronous clipboard bridged to WPF's synchronous API by a cache that the
// paste event fills. None of them is a copy of another.
//

using System;
using System.Collections.Generic;
using System.Text;

namespace MS.Internal.Interop
{
    /// <summary>The clipboard content kinds the system clipboard carries for WPF and WinForms. The bytes:
    /// <see cref="Text"/> and <see cref="Html"/> UTF-8 (HTML a fragment, not Windows' CF_HTML with its
    /// header), <see cref="Rtf"/> the RTF document, <see cref="Png"/> a PNG file, <see cref="FileList"/> a
    /// text/uri-list of file URIs (<see cref="PlatformClipboard.EncodeFileList"/>).</summary>
    public enum ClipboardFormat
    {
        Text,
        Png,
        Html,
        Rtf,
        FileList,
    }

    public static class PlatformClipboard
    {
        /// <summary>True where a system clipboard is reachable.</summary>
        public static bool IsAvailable
        {
            get
            {
                if (OperatingSystem.IsMacOS()) return MacClipboard.IsAvailable;
                if (OperatingSystem.IsIOS()) return UIKitClipboard.IsAvailable;
                if (OperatingSystem.IsAndroid()) return AndroidClipboard.IsAvailable;
                if (OperatingSystem.IsBrowser()) return BrowserClipboard.IsAvailable;
                if (IsWayland) return Wayland.WaylandClipboard.IsAvailable;
                return false;
            }
        }

        // IsAndroid() implies IsLinux(), so the Android arm has to be excluded explicitly or an
        // Android build would take the Wayland path and P/Invoke into a libwayland that is not there.
        private static bool IsWayland => OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid();

        private static string MacType(ClipboardFormat format) => format switch
        {
            ClipboardFormat.Png => MacClipboard.TypePng,
            ClipboardFormat.Html => MacClipboard.TypeHtml,
            ClipboardFormat.Rtf => MacClipboard.TypeRtf,
            _ => MacClipboard.TypeString,
        };

        private static string[] WaylandTypes(ClipboardFormat format) => format switch
        {
            ClipboardFormat.Png => new[] { Wayland.WaylandClipboard.TypePng },
            ClipboardFormat.Html => Wayland.WaylandClipboard.HtmlTypes,
            ClipboardFormat.Rtf => Wayland.WaylandClipboard.RtfTypes,
            ClipboardFormat.FileList => Wayland.WaylandClipboard.UriListTypes,
            _ => Wayland.WaylandClipboard.TextMimeTypes,
        };

        private static string WaylandType(ClipboardFormat format) => WaylandTypes(format)[0];

        /// <summary>Whether the head's clipboard carries this kind at all (the mobile and browser heads
        /// carry text and PNG only).</summary>
        private static bool Carries(ClipboardFormat format)
            => format is ClipboardFormat.Text or ClipboardFormat.Png || OperatingSystem.IsMacOS() || IsWayland;

        private static string UIKitType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? UIKitClipboard.TypePng : UIKitClipboard.TypeString;

        private static string AndroidType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? AndroidClipboard.TypePng : AndroidClipboard.TypeString;

        private static string BrowserType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? BrowserClipboard.TypePng : BrowserClipboard.TypeString;

        public static void Clear()
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.Clear();
            else if (OperatingSystem.IsIOS()) UIKitClipboard.Clear();
            else if (OperatingSystem.IsAndroid()) AndroidClipboard.Clear();
            else if (OperatingSystem.IsBrowser()) BrowserClipboard.Clear();
            else if (IsWayland) Wayland.WaylandClipboard.Clear();
        }

        public static void SetString(string value)
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.SetString(value);
            else if (OperatingSystem.IsIOS()) UIKitClipboard.SetString(value);
            else if (OperatingSystem.IsAndroid()) AndroidClipboard.SetString(value);
            else if (OperatingSystem.IsBrowser()) BrowserClipboard.SetString(value);
            else if (IsWayland) Wayland.WaylandClipboard.SetString(value);
        }

        public static string GetString()
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.GetString();
            if (OperatingSystem.IsIOS()) return UIKitClipboard.GetString();
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.GetString();
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.GetString();
            if (IsWayland) return Wayland.WaylandClipboard.GetString();
            return null;
        }

        public static bool ContainsString()
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.ContainsString();
            if (OperatingSystem.IsIOS()) return UIKitClipboard.ContainsString();
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.ContainsString();
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.ContainsString();
            if (IsWayland) return Wayland.WaylandClipboard.ContainsString();
            return false;
        }

        public static void SetData(ClipboardFormat format, byte[] data)
        {
            if (format != ClipboardFormat.Text && format != ClipboardFormat.Png)
            {
                SetFormats(new[] { new KeyValuePair<ClipboardFormat, byte[]>(format, data) });
                return;
            }
            if (OperatingSystem.IsMacOS()) MacClipboard.SetData(MacType(format), data);
            else if (OperatingSystem.IsIOS()) UIKitClipboard.SetData(UIKitType(format), data);
            else if (OperatingSystem.IsAndroid()) AndroidClipboard.SetData(AndroidType(format), data);
            else if (OperatingSystem.IsBrowser()) BrowserClipboard.SetData(BrowserType(format), data);
            else if (IsWayland) Wayland.WaylandClipboard.SetData(WaylandType(format), data);
        }

        public static byte[] GetData(ClipboardFormat format)
        {
            if (!Carries(format)) return null;
            if (format == ClipboardFormat.FileList)
            {
                if (OperatingSystem.IsMacOS()) return MacClipboard.GetFilePaths() is { } paths ? EncodeFileList(paths) : null;
                return Wayland.WaylandClipboard.GetData(Wayland.WaylandClipboard.TypeUriList);
            }
            if (OperatingSystem.IsMacOS()) return MacClipboard.GetData(MacType(format));
            if (OperatingSystem.IsIOS()) return UIKitClipboard.GetData(UIKitType(format));
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.GetData(AndroidType(format));
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.GetData(BrowserType(format));
            if (IsWayland) return Wayland.WaylandClipboard.GetData(WaylandType(format));
            return null;
        }

        public static bool ContainsData(ClipboardFormat format)
        {
            if (!Carries(format)) return false;
            if (format == ClipboardFormat.FileList && OperatingSystem.IsMacOS()) return MacClipboard.GetFilePaths() is not null;
            if (OperatingSystem.IsMacOS()) return MacClipboard.ContainsData(MacType(format));
            if (OperatingSystem.IsIOS()) return UIKitClipboard.ContainsData(UIKitType(format));
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.ContainsData(AndroidType(format));
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.ContainsData(BrowserType(format));
            if (IsWayland) return Wayland.WaylandClipboard.ContainsData(WaylandType(format));
            return false;
        }
    
        /// <summary>
        /// Replaces the clipboard with several kinds at once -- what a copy offers (text and RTF from a
        /// rich text box, text and HTML from a grid, a file list). Written one kind at a time, each write
        /// replaced the one before. The heads whose clipboards carry one kind (mobile, browser) get the
        /// text if there is some, else the image.
        /// </summary>
        public static void SetFormats(IReadOnlyList<KeyValuePair<ClipboardFormat, byte[]>> items)
        {
            if (items == null || items.Count == 0) { Clear(); return; }
            if (OperatingSystem.IsMacOS())
            {
                var typed = new List<(string, byte[])>();
                string[] files = null;
                foreach (KeyValuePair<ClipboardFormat, byte[]> item in items)
                {
                    if (item.Value == null) continue;
                    if (item.Key == ClipboardFormat.FileList) files = DecodeFileList(item.Value);
                    else typed.Add((MacType(item.Key), item.Value));
                }
                MacClipboard.SetMany(typed, files);
                return;
            }
            if (IsWayland)
            {
                var typed = new List<(string[], byte[])>();
                foreach (KeyValuePair<ClipboardFormat, byte[]> item in items)
                    if (item.Value != null) typed.Add((WaylandTypes(item.Key), item.Value));
                Wayland.WaylandClipboard.OfferMany(typed);
                return;
            }
            byte[] text = null, png = null;
            foreach (KeyValuePair<ClipboardFormat, byte[]> item in items)
            {
                if (item.Key == ClipboardFormat.Text) text ??= item.Value;
                else if (item.Key == ClipboardFormat.Png) png ??= item.Value;
            }
            if (text != null) SetString(Encoding.UTF8.GetString(text));
            else if (png != null) SetData(ClipboardFormat.Png, png);
            else Clear();
        }

        /// <summary>The files on the clipboard, as paths, or null.</summary>
        public static string[] GetFileList()
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.GetFilePaths();
            byte[] list = IsWayland ? GetData(ClipboardFormat.FileList) : null;
            return list == null ? null : DecodeFileList(list);
        }

        /// <summary>The fragment of Windows' CF_HTML ("Version:0.9 StartHTML:... StartFragment:..."), which
        /// is what an application puts under "HTML Format"; a string without that header is the HTML.</summary>
        public static string HtmlFragment(string cfHtml)
        {
            if (cfHtml == null || !cfHtml.StartsWith("Version:", StringComparison.Ordinal)) return cfHtml;
            int start = HeaderOffset(cfHtml, "StartFragment:"), end = HeaderOffset(cfHtml, "EndFragment:");
            if (start < 0 || end < start) { start = HeaderOffset(cfHtml, "StartHTML:"); end = HeaderOffset(cfHtml, "EndHTML:"); }
            byte[] bytes = Encoding.UTF8.GetBytes(cfHtml);   // the offsets are UTF-8 byte offsets
            if (start < 0 || end < start || end > bytes.Length) return cfHtml;
            return Encoding.UTF8.GetString(bytes, start, end - start);
        }

        private static int HeaderOffset(string cfHtml, string key)
        {
            int i = cfHtml.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return -1;
            int j = i + key.Length, k = j;
            while (k < cfHtml.Length && char.IsDigit(cfHtml[k])) k++;
            return int.TryParse(cfHtml.AsSpan(j, k - j), out int v) ? v : -1;
        }

        /// <summary>An HTML fragment as Windows' CF_HTML, which applications parse "HTML Format" as.</summary>
        public static string ToCfHtml(string fragment)
        {
            const string Header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string Pre = "<html><body>\r\n<!--StartFragment-->", Post = "<!--EndFragment-->\r\n</body></html>";
            fragment ??= string.Empty;
            int startHtml = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, Header, 0, 0, 0, 0));
            int startFragment = startHtml + Encoding.UTF8.GetByteCount(Pre);
            int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
            int endHtml = endFragment + Encoding.UTF8.GetByteCount(Post);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, Header, startHtml, endHtml, startFragment, endFragment) + Pre + fragment + Post;
        }

        /// <summary>Paths as a text/uri-list (RFC 2483): file URIs, one per line, CRLF.</summary>
        public static byte[] EncodeFileList(string[] paths)
        {
            var sb = new StringBuilder();
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                sb.Append(new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri).Append("\r\n");
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>The local paths of a text/uri-list's file URIs (comments and other schemes skipped).</summary>
        public static string[] DecodeFileList(byte[] uriList)
        {
            var paths = new List<string>();
            foreach (string raw in Encoding.UTF8.GetString(uriList).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (Uri.TryCreate(line, UriKind.Absolute, out Uri uri) && uri.IsFile) paths.Add(uri.LocalPath);
            }
            return paths.Count > 0 ? paths.ToArray() : null;
        }
    }
}
