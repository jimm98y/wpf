// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The headset as the 3D views see it (XrStereo3D): where the eyes are and what they see, where the desktop's
// windows lie in the room, whether a 3D view fills the eyes, and when the eyes were located. Whatever runs the
// headset gives these -- an OpenXR runtime (AndroidXrHost), WebXR (the browser head's window layer), visionOS's
// compositor -- through AppContext, so that a host in any assembly (the app's own, WindowsBase) and this, which
// is compiled into the app, need not see each other's types:
//
//   WpfWebGpu.Xr.EyeView        Func<int, float[]?>     an eye (0 left, 1 right): position (metres, the room's,
//                                                       the desktop in front at -z), turn (a quaternion x y z w),
//                                                       field of view (angles left, right, up, down, radians:
//                                                       left and down negative); null when not known
//   WpfWebGpu.Xr.EyesTime       Func<long>              the display time of the frame the eyes were located for
//   WpfWebGpu.Xr.IsFullscreen   Func<bool>              a 3D view fills the eyes (and its panel can show it)
//   WpfWebGpu.Xr.DesktopToSpace Func<double, double, float[]>   a desktop point (device pixels) in the room
//
// No host, no headset: every question answers "not known".
//

#nullable enable

using System;
using System.Numerics;

namespace MS.Internal.Interop
{
    internal static class XrHeadset
    {
        private static Func<int, float[]?>? s_eyeView;
        private static Func<long>? s_eyesTime;
        private static Func<bool>? s_isFullscreen;
        private static Func<double, double, float[]>? s_desktopToSpace;

        private static Func<int, float[]?>? EyeViewSource => s_eyeView ??= AppContext.GetData("WpfWebGpu.Xr.EyeView") as Func<int, float[]?>;

        /// <summary>Whether a host gives the eyes at all.</summary>
        internal static bool Present => EyeViewSource != null;

        /// <summary>An eye: where, which way, its field of view's tangents (left, right, up, down).</summary>
        internal static bool TryGetEyeView(bool left, out Vector3 position, out Quaternion turn, out Vector4 tangents)
        {
            position = default; turn = Quaternion.Identity; tangents = default;
            if (EyeViewSource?.Invoke(left ? 0 : 1) is not { Length: >= 11 } v) return false;
            position = new Vector3(v[0], v[1], v[2]);
            turn = new Quaternion(v[3], v[4], v[5], v[6]);
            tangents = new Vector4(MathF.Tan(v[7]), MathF.Tan(v[8]), MathF.Tan(v[9]), MathF.Tan(v[10]));
            return true;
        }

        /// <summary>An eye's field of view as angles (left, right, up, down: radians), for what is drawn for it.</summary>
        internal static Vector4 EyeAngles(bool left) =>
            EyeViewSource?.Invoke(left ? 0 : 1) is { Length: >= 11 } v ? new Vector4(v[7], v[8], v[9], v[10]) : default;

        /// <summary>Where both eyes are.</summary>
        internal static bool TryGetEyes(out Vector3 left, out Vector3 right)
        {
            bool known = TryGetEyeView(true, out left, out _, out _);
            known &= TryGetEyeView(false, out right, out _, out _);
            return known;
        }

        internal static long EyesTime => (s_eyesTime ??= AppContext.GetData("WpfWebGpu.Xr.EyesTime") as Func<long>)?.Invoke() ?? 0;

        internal static bool IsFullscreen => (s_isFullscreen ??= AppContext.GetData("WpfWebGpu.Xr.IsFullscreen") as Func<bool>)?.Invoke() ?? false;

        /// <summary>A desktop point (device pixels) in the room: where the window there lies.</summary>
        internal static Vector3 DesktopToSpace(double x, double y) =>
            (s_desktopToSpace ??= AppContext.GetData("WpfWebGpu.Xr.DesktopToSpace") as Func<double, double, float[]>)?.Invoke(x, y) is { Length: >= 3 } p
                ? new Vector3(p[0], p[1], p[2]) : default;
    }
}
