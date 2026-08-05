// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The one PUBLIC seam on the Android backend. Everything else in Composition/Platform is internal,
// but this has to be reachable from outside the engine assembly, because the two halves of the
// Android window mapping are built independently and neither references the other:
//
//   WindowsBase  owns the WPF window handles (MS.Internal.Interop.AndroidWindow) and knows which
//                ANativeWindow currently backs each one.
//   this engine  needs that mapping to build a wgpu surface, and cannot reference WPF at all --
//                DUCE loads this whole assembly reflectively precisely to keep that direction clear.
//
// So AndroidWindow installs itself here (reflectively, as DUCE loads the engine) and the engine
// calls back through AndroidInterop.Resolver. An app head embedding the engine WITHOUT WPF -- the
// Android spike -- can set this itself, or leave it null and pass real ANativeWindow* values as
// handles, which is what the identity fallback in AndroidInterop.GetNativeWindow is for.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu
{
    /// <summary>Public registration point for the Android window-handle mapping (see file header).</summary>
    public static class AndroidPlatform
    {
        /// <summary>Reports a window's top-left corner in device pixels, relative to the activity's
        /// content area. See <see cref="WindowOriginQuery"/>.</summary>
        public delegate void WindowOriginCallback(IntPtr handle, out int x, out int y);

        /// <summary>
        /// Maps a WPF window handle to the ANativeWindow* currently backing it, or IntPtr.Zero when
        /// that window has no live Surface (before surfaceCreated, or while the activity is stopped).
        /// Null means "handles ARE native windows" -- see the file header.
        /// </summary>
        public static Func<IntPtr, IntPtr>? NativeWindowResolver
        {
            get => Composition.Platform.AndroidInterop.Resolver;
            set => Composition.Platform.AndroidInterop.Resolver = value;
        }

        /// <summary>
        /// Reports whether a WPF window handle names an OPAQUE window. Popups are not: they need a
        /// premultiplied-alpha surface so their shadow and rounded corners composite over what is
        /// behind them. Null means "assume opaque". See AndroidInterop.OpaqueQuery.
        /// </summary>
        /// <summary>
        /// Reports a window's top-left corner in device pixels within the activity. Used when a popup
        /// is drawn into its owner's surface rather than its own; see AndroidInterop.OriginQuery.
        /// </summary>
        public static WindowOriginCallback? WindowOriginQuery
        {
            get => Composition.Platform.AndroidInterop.OriginQuery;
            set => Composition.Platform.AndroidInterop.OriginQuery = value;
        }

        public static Func<IntPtr, bool>? WindowOpaqueQuery
        {
            get => Composition.Platform.AndroidInterop.OpaqueQuery;
            set => Composition.Platform.AndroidInterop.OpaqueQuery = value;
        }
    }
}
