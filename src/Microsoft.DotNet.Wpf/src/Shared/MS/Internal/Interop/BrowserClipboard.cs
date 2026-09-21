// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser system clipboard, the WebAssembly sibling of MacClipboard and UIKitClipboard.
//
// Before this file the browser head had no clipboard: PlatformClipboard fell through to
// "unavailable", so Clipboard.SetText reached an in-process dictionary in MacDataObject and a WPF
// app in a tab could neither copy anything out nor paste anything in. Of the three heads that were
// missing a clipboard this is the one where it shows most: everything AROUND the tab is another
// application to paste into.
//
// The interesting half of the design is in browser-window.js, next to the code it depends on --
// read the comment above installClipboardListeners for why a synchronous WPF API can be served by
// an asynchronous platform one at all, and for the one case (reading with no paste gesture) that no
// browser allows anybody to implement.
//
// The JS lives in browser-window.js rather than in a module of its own so that no new file has to
// be deployed: the SDK and every path-referencing wasm sample already copy that one, and printing
// already set the precedent that platform services beyond windowing belong in it.
//

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    /// <summary>Access to the browser clipboard, callable from any WPF assembly.</summary>
    [SupportedOSPlatform("browser")]
    public static partial class BrowserClipboard
    {
        /// <summary>MIME type for the text representation, the browser's own spelling.</summary>
        public const string TypeString = "text/plain";

        /// <summary>MIME type for the image representation.</summary>
        public const string TypePng = "image/png";

        /// <summary>True when running in a browser, where the JS bridge is usable.</summary>
        /// <remarks>
        ///  Matching MacClipboard: the platform is the condition. Whether the host page actually
        ///  registered the module is not knowable from here, so every call below is guarded instead
        ///  -- a head that forgot the setModuleImports line gets no clipboard rather than a crash on
        ///  the first Ctrl+C.
        /// </remarks>
        public static bool IsAvailable => OperatingSystem.IsBrowser();

        public static void Clear()
        {
            if (!IsAvailable) return;
            try { Js.Clear(); } catch (JSException) { }
        }

        public static void SetString(string value)
        {
            if (!IsAvailable || value is null) return;
            try { Js.SetText(value); } catch (JSException) { }
        }

        public static string GetString()
        {
            if (!IsAvailable) return null;

            try
            {
                string text = Js.GetText();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (JSException)
            {
                return null;
            }
        }

        public static bool ContainsString()
        {
            if (!IsAvailable) return false;
            try { return Js.HasText(); } catch (JSException) { return false; }
        }

        public static void SetData(string type, byte[] data)
        {
            if (!IsAvailable || data is null) return;

            // Only PNG crosses the seam. The browser clipboard is typed by MIME and the other things
            // WPF can put on it have no web type at all, so silently accepting them would produce a
            // clipboard entry no application (this one included) could ever read back.
            if (!string.Equals(type, TypePng, StringComparison.Ordinal)) return;

            try { Js.SetPng(data); } catch (JSException) { }
        }

        public static byte[] GetData(string type)
        {
            if (!IsAvailable) return null;
            if (!string.Equals(type, TypePng, StringComparison.Ordinal)) return null;

            try
            {
                string base64 = Js.GetPng();
                if (string.IsNullOrEmpty(base64)) return null;
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
            catch (JSException)
            {
                return null;
            }
        }

        public static bool ContainsData(string type)
        {
            if (!IsAvailable || !string.Equals(type, TypePng, StringComparison.Ordinal)) return false;
            try { return Js.HasPng(); } catch (JSException) { return false; }
        }

        /// <summary>The browser-window.js exports, bound the same way BrowserWindow binds its own.</summary>
        private static partial class Js
        {
            private const string Module = "wpfBrowserWindow";

            [JSImport("clipboardClear", Module)]
            internal static partial void Clear();

            [JSImport("clipboardSetText", Module)]
            internal static partial void SetText(string value);

            [JSImport("clipboardGetText", Module)]
            internal static partial string GetText();

            [JSImport("clipboardHasText", Module)]
            internal static partial bool HasText();

            [JSImport("clipboardSetPng", Module)]
            internal static partial void SetPng([JSMarshalAs<JSType.Array<JSType.Number>>] byte[] data);

            [JSImport("clipboardGetPng", Module)]
            internal static partial string GetPng();

            [JSImport("clipboardHasPng", Module)]
            internal static partial bool HasPng();
        }
    }
}
