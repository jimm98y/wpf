// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser's headset (WebXR, browser-window.js) as the app's 3D views ask for one (XrHeadset): the eyes as
// the session's latest frame has them, where the page's windows lie in the room, and a 3D view filling the eyes
// -- B or Y on a controller, the view under its ray -- drawn by the app into a canvas of its own (XrStereo3D,
// XrExternalPanel), each eye shown its half; the sticks and grips turn, scale and move it meanwhile. What an
// OpenXR headset's host does (AndroidXrHost), with the session in the page: the frames are the page's, so the
// app draws the panel on its own frame (WpfWebGpu.Xr.CoreDrawsPanel) and the page shows the latest.
//
// The app's 3D side is compiled into the app, this into WindowsBase: they meet through AppContext
// (WpfWebGpu.Xr.* this gives, WpfWebGpu.Xr.Core.* the app's side gives).
//

#nullable enable

using System;
using System.Runtime.InteropServices.JavaScript;

namespace MS.Internal.Interop
{
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    internal static partial class BrowserXrHost
    {
        private static bool s_offered, s_full;
        private static long s_shown = -1;

        /// <summary>A WebXR session began: the headset offered to the 3D views, and they put in their stereo mode.</summary>
        internal static void Started()
        {
            if (!s_offered)
            {
                s_offered = true;
                AppContext.SetData("WpfWebGpu.Xr.EyeView", (Func<int, float[]?>)(eye =>
                    Js.XrEyeView(eye) is { Length: >= 11 } v ? Array.ConvertAll(v, d => (float)d) : null));
                AppContext.SetData("WpfWebGpu.Xr.EyesTime", (Func<long>)(() => (long)Js.XrEyesTime()));
                AppContext.SetData("WpfWebGpu.Xr.IsFullscreen", (Func<bool>)(() => s_full));
                AppContext.SetData("WpfWebGpu.Xr.DesktopToSpace", (Func<double, double, float[]>)((x, y) =>
                    Array.ConvertAll(Js.XrDesktopToSpace(x, y), d => (float)d)));
                // the page's frames: the app draws the fullscreen panel on its own, into this canvas
                AppContext.SetData("WpfWebGpu.Xr.CoreDrawsPanel", true);
                AppContext.SetData("WpfWebGpu.Xr.PreparePanel", (Func<int, int, IntPtr>)((w, h) => (IntPtr)Js.XrPanelCanvas(w, h)));
                AppContext.SetData("WpfWebGpu.Xr.PanelDrawn", (Action<long>)(number =>
                {
                    if (!s_full) return;
                    s_shown = number;
                    Js.XrFullscreen(true, number);
                }));
            }
            (AppContext.GetData("WpfWebGpu.Xr.Core.Enable") as Action)?.Invoke();
        }

        /// <summary>The session ended: a view filling the eyes goes back to its window.</summary>
        internal static void Ended()
        {
            if (s_full) Toggle(-1, -1);
        }

        /// <summary>B or Y: the 3D view under the ray (desktop device pixels) fills the eyes, or goes back.</summary>
        internal static void Toggle(int x, int y)
        {
            if (s_full)
            {
                s_full = false;
                s_shown = -1;
                Js.XrFullscreen(false, -1);
                (AppContext.GetData("WpfWebGpu.Xr.Core.EndFullscreen") as Action)?.Invoke();
                (AppContext.GetData("WpfWebGpu.Xr.ExternalTarget") as Func<IntPtr, int, int, bool>)?.Invoke(IntPtr.Zero, 0, 0);
                return;
            }
            if ((AppContext.GetData("WpfWebGpu.Xr.Core.ViewAt") as Func<int, int, double[]?>)?.Invoke(x, y) is not { Length: 4 } r
                || r[2] < 8 || r[3] < 8)
            {
                // the rest of the app: out of the session, back to the page (as the headset's own app goes back to its panel)
                Console.WriteLine($"[xr] no 3D view at ({x},{y}): back to the page");
                Js.XrEnd();
                return;
            }
            Console.WriteLine($"[xr] fullscreen: the 3D view at ({r[0]:0},{r[1]:0}) {r[2]:0}x{r[3]:0}");
            (AppContext.GetData("WpfWebGpu.Xr.Core.BeginFullscreen") as Action<double, double, double, double>)?.Invoke(r[0], r[1], r[2], r[3]);
            s_full = true;
            Js.XrFullscreen(true, -1);
        }

        /// <summary>The sticks and grips in fullscreen: the model turned (radians), scaled and moved (metres).</summary>
        internal static void Manipulate(double turn, double scale, double x, double y, double z)
        {
            if (!s_full) return;
            (AppContext.GetData("WpfWebGpu.Xr.Core.Manipulate") as Action<float, float, float, float, float>)?.Invoke((float)turn, (float)scale, (float)x, (float)y, (float)z);
        }

        private static partial class Js
        {
            private const string Module = "wpfBrowserWindow";

            [JSImport("xrEyeView", Module)]
            [return: JSMarshalAs<JSType.Array<JSType.Number>>]
            internal static partial double[]? XrEyeView(int eye);

            [JSImport("xrEyesTime", Module)]
            internal static partial double XrEyesTime();

            [JSImport("xrDesktopToSpace", Module)]
            [return: JSMarshalAs<JSType.Array<JSType.Number>>]
            internal static partial double[] XrDesktopToSpace(double x, double y);

            [JSImport("xrPanelCanvas", Module)]
            internal static partial int XrPanelCanvas(int width, int height);

            [JSImport("xrEnd", Module)]
            internal static partial void XrEnd();

            [JSImport("xrFullscreen", Module)]
            internal static partial void XrFullscreen(bool on, double shown);
        }
    }
}
