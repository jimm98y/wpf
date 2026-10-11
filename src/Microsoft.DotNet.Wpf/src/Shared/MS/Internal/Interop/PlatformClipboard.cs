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

namespace MS.Internal.Interop
{
    /// <summary>The clipboard content kinds WPF's data object bridges.</summary>
    internal enum ClipboardFormat
    {
        Text,
        Png,
    }

    internal static class PlatformClipboard
    {
        /// <summary>True where a system clipboard is reachable.</summary>
        internal static bool IsAvailable
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

        private static string MacType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? MacClipboard.TypePng : MacClipboard.TypeString;

        private static string WaylandType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? Wayland.WaylandClipboard.TypePng : Wayland.WaylandClipboard.TypeString;

        private static string UIKitType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? UIKitClipboard.TypePng : UIKitClipboard.TypeString;

        private static string AndroidType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? AndroidClipboard.TypePng : AndroidClipboard.TypeString;

        private static string BrowserType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? BrowserClipboard.TypePng : BrowserClipboard.TypeString;

        internal static void Clear()
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.Clear();
            else if (OperatingSystem.IsIOS()) UIKitClipboard.Clear();
            else if (OperatingSystem.IsAndroid()) AndroidClipboard.Clear();
            else if (OperatingSystem.IsBrowser()) BrowserClipboard.Clear();
            else if (IsWayland) Wayland.WaylandClipboard.Clear();
        }

        internal static void SetString(string value)
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.SetString(value);
            else if (OperatingSystem.IsIOS()) UIKitClipboard.SetString(value);
            else if (OperatingSystem.IsAndroid()) AndroidClipboard.SetString(value);
            else if (OperatingSystem.IsBrowser()) BrowserClipboard.SetString(value);
            else if (IsWayland) Wayland.WaylandClipboard.SetString(value);
        }

        internal static string GetString()
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.GetString();
            if (OperatingSystem.IsIOS()) return UIKitClipboard.GetString();
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.GetString();
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.GetString();
            if (IsWayland) return Wayland.WaylandClipboard.GetString();
            return null;
        }

        internal static bool ContainsString()
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.ContainsString();
            if (OperatingSystem.IsIOS()) return UIKitClipboard.ContainsString();
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.ContainsString();
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.ContainsString();
            if (IsWayland) return Wayland.WaylandClipboard.ContainsString();
            return false;
        }

        internal static void SetData(ClipboardFormat format, byte[] data)
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.SetData(MacType(format), data);
            else if (OperatingSystem.IsIOS()) UIKitClipboard.SetData(UIKitType(format), data);
            else if (OperatingSystem.IsAndroid()) AndroidClipboard.SetData(AndroidType(format), data);
            else if (OperatingSystem.IsBrowser()) BrowserClipboard.SetData(BrowserType(format), data);
            else if (IsWayland) Wayland.WaylandClipboard.SetData(WaylandType(format), data);
        }

        internal static byte[] GetData(ClipboardFormat format)
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.GetData(MacType(format));
            if (OperatingSystem.IsIOS()) return UIKitClipboard.GetData(UIKitType(format));
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.GetData(AndroidType(format));
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.GetData(BrowserType(format));
            if (IsWayland) return Wayland.WaylandClipboard.GetData(WaylandType(format));
            return null;
        }

        internal static bool ContainsData(ClipboardFormat format)
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.ContainsData(MacType(format));
            if (OperatingSystem.IsIOS()) return UIKitClipboard.ContainsData(UIKitType(format));
            if (OperatingSystem.IsAndroid()) return AndroidClipboard.ContainsData(AndroidType(format));
            if (OperatingSystem.IsBrowser()) return BrowserClipboard.ContainsData(BrowserType(format));
            if (IsWayland) return Wayland.WaylandClipboard.ContainsData(WaylandType(format));
            return false;
        }
    }
}
