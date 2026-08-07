// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;

namespace MS.Internal.Interop
{
    /// <summary>
    ///  The WPF side of a platform drag-and-drop, implemented by PresentationCore and driven by the
    ///  windowing backend. Mirrors <see cref="PlatformClipboard"/>/<see cref="PlatformWindow"/>.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   This exists because the two halves live in different assemblies and the dependency only runs
    ///   one way: the backend is in WindowsBase and the drop logic (hit-testing, the DragEnter/DragOver/
    ///   Drop routed events) is in PresentationCore, which references WindowsBase and not the reverse.
    ///   PresentationCore therefore installs itself here, exactly as the compositor seam does.
    ///  </para>
    ///  <para>
    ///   Points are in SCREEN device pixels, matching what <c>PointUtil.ScreenToClient</c> expects --
    ///   off Windows that subtracts the window's client screen origin, so the backend adds the same
    ///   origin to its surface-local coordinates and the round trip is exact.
    ///  </para>
    ///  <para>
    ///   Effects are <c>DragDropEffects</c> values, passed as int so this file needs no reference to
    ///   PresentationCore's types.
    ///  </para>
    /// </remarks>
    internal interface IPlatformDropTarget
    {
        /// <summary>A drag entered the window. Returns the effect the target chose.</summary>
        /// <param name="mimeTypes">
        ///  The MIME types the drag data can supply, in the platform's own vocabulary
        ///  ("text/uri-list", "text/plain;charset=utf-8", ...). Mapping these onto WPF's
        ///  <c>DataFormats</c> is the implementer's job, since only it knows about them.
        /// </param>
        /// <param name="read">Reads one of <paramref name="mimeTypes"/>, or returns null.</param>
        int DragEnter(IntPtr windowHandle, int screenX, int screenY, string[] mimeTypes,
                      Func<string, byte[]?> read, int allowedEffects);

        int DragOver(IntPtr windowHandle, int screenX, int screenY, int allowedEffects);

        void DragLeave(IntPtr windowHandle);

        /// <summary>The drag was released. Returns the effect that was actually performed.</summary>
        int Drop(IntPtr windowHandle, int screenX, int screenY, int allowedEffects);
    }

    internal static class PlatformDragDrop
    {
        /// <summary>Installed by PresentationCore; null until a WPF window exists.</summary>
        internal static IPlatformDropTarget? Target { get; set; }
    }
}
