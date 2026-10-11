// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android system clipboard, reached through the head payload the same way printing and
// accessibility are.
//
// Before this file the Android head had no clipboard at all: PlatformClipboard had arms for macOS
// and Wayland and fell through to "unavailable" here, so Clipboard.SetText went to an in-process
// dictionary in MacDataObject and nothing a WPF app copied could be pasted into any other app --
// or vice versa. On a phone, where sharing a snippet between apps is most of what a clipboard is
// for, that is the whole feature missing.
//
// Why a host seam rather than JNI from here: android.content.ClipboardManager needs the Mono.Android
// bindings and an Activity for the Context, and WindowsBase can have neither (see the header of
// AndroidWindow.cs). Everything crossing the seam is a primitive, so the payload never sees a
// WindowsBase-internal type. Its own interface, matching IAndroidPrintHost, so a head that does not
// want a clipboard is not forced to implement one.
//
// Threading: ClipboardManager is main-thread-only on Android, and every caller here arrives on the
// dispatcher thread, which IS the main Looper thread on this head. No marshalling is needed and none
// is done; a call from a background thread is the caller's bug and Android will say so.
//

using System;

namespace MS.Internal.Interop
{
    /// <summary>
    ///  The clipboard operations the Android head payload supplies. Implemented by AndroidHost.
    /// </summary>
    public interface IAndroidClipboardHost
    {
        /// <summary>Empties the clipboard.</summary>
        void Clear();

        /// <summary>Puts plain text on the clipboard, replacing what was there.</summary>
        void SetText(string value);

        /// <summary>The clipboard's plain text, or null when it holds none.</summary>
        string GetText();

        /// <summary>
        ///  Puts bytes on the clipboard under a MIME type, replacing what was there. Returns false
        ///  when the platform cannot carry that type.
        /// </summary>
        /// <remarks>
        ///  Android's clipboard carries a URI for anything that is not text, so an implementation has
        ///  to write the bytes somewhere a content provider can serve them. A head without one
        ///  returns false and only text works, which is still the case that matters.
        /// </remarks>
        bool SetData(string mimeType, byte[] data);

        /// <summary>The clipboard's bytes for a MIME type, or null when it holds none.</summary>
        byte[] GetData(string mimeType);

        /// <summary>True when the clipboard holds something of this MIME type.</summary>
        bool ContainsData(string mimeType);
    }

    /// <summary>
    /// The clipboard backend for Android. Public for the head payload; everything it is handed is a
    /// primitive, so the payload never has to see a WindowsBase-internal type.
    /// </summary>
    // No [SupportedOSPlatform], matching AndroidWindow, AndroidPrint and AndroidAccessibility next
    // door: the public WindowsBase surface carries no platform attributes, and adding one fails
    // ApiCompat against the hand-written reference assembly.
    public static class AndroidClipboard
    {
        /// <summary>MIME type for the text representation, Android's own spelling.</summary>
        public const string TypeString = "text/plain";

        /// <summary>MIME type for the image representation.</summary>
        public const string TypePng = "image/png";

        /// <summary>Set by the head alongside AndroidWindow.Host.</summary>
        public static IAndroidClipboardHost Host { get; set; }

        /// <summary>True once a head is attached that can reach the system clipboard.</summary>
        public static bool IsAvailable => OperatingSystem.IsAndroid() && Host is not null;

        internal static void Clear()
        {
            if (!IsAvailable) return;
            Host.Clear();
        }

        internal static void SetString(string value)
        {
            if (!IsAvailable || value is null) return;
            Host.SetText(value);
        }

        internal static string GetString() => IsAvailable ? Host.GetText() : null;

        internal static bool ContainsString() => IsAvailable && !string.IsNullOrEmpty(Host.GetText());

        internal static void SetData(string mimeType, byte[] data)
        {
            if (!IsAvailable || data is null) return;
            Host.SetData(mimeType, data);
        }

        internal static byte[] GetData(string mimeType) => IsAvailable ? Host.GetData(mimeType) : null;

        internal static bool ContainsData(string mimeType) => IsAvailable && Host.ContainsData(mimeType);
    }
}
