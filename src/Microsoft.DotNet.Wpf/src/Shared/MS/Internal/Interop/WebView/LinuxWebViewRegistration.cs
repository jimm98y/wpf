// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// How the Wayland windowing layer hands its subsurface plumbing to the web-view seam.
//
// Same mechanism as AndroidWebViewRegistration, and for the same structural reason -- read that file
// for the full rationale. In short: the seam is link-compiled into three assemblies, only one of
// which references WindowsBase, so anything declared alongside the seam would exist as three
// distinct types; AppContext data is process-wide and therefore common to all three, and delegates
// over primitives are BCL types every copy can call.
//
// What differs from Android, and makes this the easier of the two: the registrant is OUR OWN code.
// The Wayland layer in WindowsBase registers itself at start-up, so an application does nothing.
//
// The split follows what each side can actually reach:
//
//   * The SEAM drives WPE WebKit. That is plain P/Invoke to libWPEWebKit, which the seam is allowed
//     to do (the JNI prohibition that shapes the Android head has no analogue here).
//   * The WAYLAND LAYER owns the wl_subsurface: creating it needs wl_compositor and
//     wl_subcompositor, which WaylandDisplay binds and which live in WindowsBase. It also owns
//     attaching the buffers WPE exports, because that is a wl_surface operation on a surface it
//     created.
//
// The parent surface itself needs no plumbing: on this head the window handle IS the wl_surface*
// and it is stable for the window's life (see WaylandWindow), so it arrives in Create unchanged.
//

using System;

namespace MS.Internal.Interop.WebView
{
    /// <summary>The commands <see cref="LinuxWebViewRegistration"/> dispatches to the Wayland layer.</summary>
    internal static class LinuxWebViewCommands
    {
        /// <summary>
        /// (IntPtr parentSurface, int x, int y, int width, int height) -> int id, or 0.
        /// Creates a wl_surface, makes it a wl_subsurface of the parent, and places it above.
        /// </summary>
        internal const string CreateSubsurface = "createSubsurface";

        /// <summary>(id, x, y, width, height) -> null. Device pixels, relative to the parent.</summary>
        internal const string MoveSubsurface = "moveSubsurface";

        /// <summary>(id, bool) -> null. A hidden subsurface attaches a null buffer.</summary>
        internal const string SetSubsurfaceVisible = "setSubsurfaceVisible";

        /// <summary>
        /// (id, IntPtr wlBuffer, int width, int height) -> null. Attach, damage and commit a frame
        /// the engine exported. Committing is the Wayland layer's job because the surface is its.
        /// </summary>
        internal const string AttachBuffer = "attachBuffer";

        /// <summary>(id) -> null.</summary>
        internal const string DestroySubsurface = "destroySubsurface";

        /// <summary>() -> IntPtr. The process's wl_display*, which WPE's FDO backend needs.</summary>
        internal const string GetDisplay = "getDisplay";
    }

    /// <summary>
    /// The bridge to the Wayland windowing layer. Absent off Linux, and absent until that layer has
    /// come up -- <see cref="IsAvailable"/> is how the backend tells.
    /// </summary>
    internal static class LinuxWebViewRegistration
    {
        /// <summary>
        /// The AppContext key the Wayland layer sets. Spelled out rather than derived from a type
        /// name, so renaming a class here cannot silently break a build that registers elsewhere.
        /// </summary>
        internal const string InvokeKey = "MS.Internal.Interop.WebView.Linux.Invoke";

        internal static bool IsAvailable => Resolve() is not null;

        /// <summary>
        /// Read the registration. Deliberately not cached, for the reason
        /// AndroidWebViewRegistration.Resolve documents: caching the first non-null answer would
        /// silently ignore a later re-registration for the life of the process.
        /// </summary>
        private static Func<string, object[], object> Resolve() =>
            AppContext.GetData(InvokeKey) as Func<string, object[], object>;

        internal static object Invoke(string command, params object[] args)
        {
            Func<string, object[], object> invoke = Resolve();

            if (invoke is null)
            {
                throw new PlatformNotSupportedException(
                    "The Wayland windowing layer has not registered its web-view support. This is " +
                    "expected only on a Linux head that is not running under Wayland.");
            }

            return invoke(command, args ?? Array.Empty<object>());
        }
    }
}
