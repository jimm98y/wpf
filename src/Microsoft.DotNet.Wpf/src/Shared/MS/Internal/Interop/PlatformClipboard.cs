// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Dispatches clipboard operations to the platform backend, the third sibling of PlatformWindow and
// the same idea: one seam, so a new head is a new backend rather than a new branch at every call
// site.
//
// The type names are deliberately abstract rather than either platform's spelling. macOS wants
// Uniform Type Identifiers ("public.utf8-plain-text") and Wayland wants MIME types
// ("text/plain;charset=utf-8"), so callers name the CONTENT and each backend supplies its own
// vocabulary.
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
                if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
                    return Wayland.WaylandClipboard.IsAvailable;
                return false;
            }
        }

        private static bool IsWayland => OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid();

        private static string MacType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? MacClipboard.TypePng : MacClipboard.TypeString;

        private static string WaylandType(ClipboardFormat format)
            => format == ClipboardFormat.Png ? Wayland.WaylandClipboard.TypePng : Wayland.WaylandClipboard.TypeString;

        internal static void Clear()
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.Clear();
            else if (IsWayland) Wayland.WaylandClipboard.Clear();
        }

        internal static void SetString(string value)
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.SetString(value);
            else if (IsWayland) Wayland.WaylandClipboard.SetString(value);
        }

        internal static string GetString()
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.GetString();
            if (IsWayland) return Wayland.WaylandClipboard.GetString();
            return null;
        }

        internal static bool ContainsString()
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.ContainsString();
            if (IsWayland) return Wayland.WaylandClipboard.ContainsString();
            return false;
        }

        internal static void SetData(ClipboardFormat format, byte[] data)
        {
            if (OperatingSystem.IsMacOS()) MacClipboard.SetData(MacType(format), data);
            else if (IsWayland) Wayland.WaylandClipboard.SetData(WaylandType(format), data);
        }

        internal static byte[] GetData(ClipboardFormat format)
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.GetData(MacType(format));
            if (IsWayland) return Wayland.WaylandClipboard.GetData(WaylandType(format));
            return null;
        }

        internal static bool ContainsData(ClipboardFormat format)
        {
            if (OperatingSystem.IsMacOS()) return MacClipboard.ContainsData(MacType(format));
            if (IsWayland) return Wayland.WaylandClipboard.ContainsData(WaylandType(format));
            return false;
        }
    }
}
