// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Apple Vision Pro as the app's 3D views ask for a headset (XrHeadset): the windows are visionOS's own (the app's
// UIKit window, in the shared space), and a 3D view fills the eyes -- a pinch held on it (a long press), and again
// to put it back -- in an immersive space of the app's (libWpfImmersive, the Swift the visionOS bundle builds:
// wpfrun's BundleVisionOs.cs). Its frames locate the eyes; the app draws the view for them (XrStereo3D), and the
// picture goes to the space with the eyes' number (WpfWebGpu.Xr.SubmitPicture), which presents it with the head's
// pose it was drawn for, so that the compositor moves it to where the head is when it is shown. What the Quest's
// host does (AndroidXrHost), with the compositor's reprojection doing the moving.
//
// The eyes are given in a frame of the desktop's: a plane 1.3 m in front of where the head was when the view went
// to fill the eyes, level, facing it -- where the Quest's host puts its fullscreen view.
//

#nullable enable

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    [System.Runtime.Versioning.SupportedOSPlatform("ios")]
    internal static unsafe class VisionXrHost
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const float FullDistance = 1.3f, FullCentreHeight = -0.05f;

        private static bool s_attached, s_full;
        private static IntPtr s_root;
        private static delegate* unmanaged<delegate* unmanaged<void>, void> s_open;
        private static delegate* unmanaged<void> s_close;
        private static delegate* unmanaged<float*, long> s_eyes;
        private static delegate* unmanaged<IntPtr, int, int, int, long, void> s_submit;

        // the desktop's frame: the head's place and heading when the view began to fill the eyes
        private static bool s_framed;
        private static System.Numerics.Vector3 s_origin;
        private static float s_heading;
        private static double s_rectX, s_rectY, s_rectW, s_rectH, s_metresPerPixel;
        private static readonly float[] s_latest = new float[22];
        private static long s_latestNumber = -1;

        /// <summary>The app's UIKit root view is there: on a Vision, the headset offered to the 3D views.</summary>
        internal static void Attach(IntPtr rootView)
        {
            if (s_attached || !IsVision()) return;
            s_attached = true;
            s_root = rootView;
            IntPtr bundle = Send(objc_getClass("NSBundle"), Sel("mainBundle"));
            string? path = Marshal.PtrToStringUTF8(Send(Send(bundle, Sel("bundlePath")), Sel("UTF8String")));
            if (path == null || !NativeLibrary.TryLoad(System.IO.Path.Combine(path, "Frameworks", "libWpfImmersive.dylib"), out IntPtr lib))
            {
                Console.WriteLine("[xr] visionOS: no libWpfImmersive.dylib in the bundle; no immersive space");
                return;
            }
            s_open = (delegate* unmanaged<delegate* unmanaged<void>, void>)NativeLibrary.GetExport(lib, "wpf_immersive_open");
            s_close = (delegate* unmanaged<void>)NativeLibrary.GetExport(lib, "wpf_immersive_close");
            s_eyes = (delegate* unmanaged<float*, long>)NativeLibrary.GetExport(lib, "wpf_xr_eyes");
            s_submit = (delegate* unmanaged<IntPtr, int, int, int, long, void>)NativeLibrary.GetExport(lib, "wpf_xr_submit");
            if (NativeLibrary.TryGetExport(lib, "wpf_xr_on_spatial", out IntPtr onSpatial))
                ((delegate* unmanaged<delegate* unmanaged<long, int, float, float, float, void>, void>)onSpatial)(&Spatial);

            AppContext.SetData("WpfWebGpu.Xr.EyeView", (Func<int, float[]?>)EyeView);
            AppContext.SetData("WpfWebGpu.Xr.EyesTime", (Func<long>)(() => s_latestNumber));
            AppContext.SetData("WpfWebGpu.Xr.IsFullscreen", (Func<bool>)(() => s_full));
            AppContext.SetData("WpfWebGpu.Xr.DesktopToSpace", (Func<double, double, float[]>)DesktopToSpace);
            // the app's frames draw the picture (the space's are on a thread of their own, the Swift's)
            AppContext.SetData("WpfWebGpu.Xr.CoreDrawsPanel", true);
            AppContext.SetData("WpfWebGpu.Xr.PreparePanel", (Func<int, int, IntPtr>)((w, h) => (IntPtr)1));
            // the picture goes through memory each frame until the GPU hands it over itself: smaller than the Quest's
            AppContext.SetData("WpfWebGpu.Xr.FullHeight", 1440.0);
            AppContext.SetData("WpfWebGpu.Xr.SubmitPicture", (Action<IntPtr, int, int, int, long>)((pixels, w, h, row, number) =>
            {
                if (s_full && number > 0) s_submit(pixels, w, h, row, number);
            }));
            AddPress(rootView);
            (AppContext.GetData("WpfWebGpu.Xr.Core.Enable") as Action)?.Invoke();
            // a check with no hands to pinch with (a simulator's, WPF_XR_AUTO_FULLSCREEN=<seconds>): the first 3D view
            // fills the eyes that long after the start
            if (double.TryParse(Environment.GetEnvironmentVariable("WPF_XR_AUTO_FULLSCREEN"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
                s_auto = new System.Threading.Timer(_ =>
                {
                    IntPtr main = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.dylib"), "_dispatch_main_q");
                    dispatch_async_f(main, IntPtr.Zero, &AutoOnMain);
                }, null, TimeSpan.FromSeconds(seconds), System.Threading.Timeout.InfiniteTimeSpan);
            Console.WriteLine("[xr] visionOS: a pinch held on a 3D view fills the eyes with it");
        }

        // ---- the eyes ----------------------------------------------------------------------------------------

        /// <summary>An eye in the desktop's frame: position, turn, field of view's angles (XrHeadset's layout).</summary>
        private static float[]? EyeView(int eye)
        {
            if (!s_full || s_eyes == null) return null;
            // both eyes of one frame: the left asked first (XrStereo3D), the right takes the same
            if (eye == 0 || s_latestNumber < 0) fixed (float* p = s_latest) s_latestNumber = s_eyes(p);
            if (s_latestNumber < 0) return null;
            if (!s_framed)
            {
                // the desktop's frame from where the head is now: between the eyes, its heading level
                var l = new System.Numerics.Vector3(s_latest[0], s_latest[1], s_latest[2]);
                var r = new System.Numerics.Vector3(s_latest[11], s_latest[12], s_latest[13]);
                s_origin = (l + r) / 2;
                var turn = new System.Numerics.Quaternion(s_latest[3], s_latest[4], s_latest[5], s_latest[6]);
                var ahead = System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, turn);
                s_heading = MathF.Atan2(-ahead.X, -ahead.Z);
                s_framed = true;
            }
            int at = eye == 0 ? 0 : 11;
            var undo = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, -s_heading);
            var position = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(s_latest[at], s_latest[at + 1], s_latest[at + 2]) - s_origin, undo);
            var orientation = undo * new System.Numerics.Quaternion(s_latest[at + 3], s_latest[at + 4], s_latest[at + 5], s_latest[at + 6]);
            return new[] { position.X, position.Y, position.Z, orientation.X, orientation.Y, orientation.Z, orientation.W,
                           s_latest[at + 7], s_latest[at + 8], s_latest[at + 9], s_latest[at + 10] };
        }

        /// <summary>A desktop point in the desktop's frame: the view filling the eyes laid on a plane 1.3 m ahead, as
        /// wide as 80 degrees allow, a little below the eyes (AndroidXrHost's fullscreen panel).</summary>
        private static float[] DesktopToSpace(double x, double y)
        {
            if (s_metresPerPixel <= 0) return new[] { 0f, FullCentreHeight, -FullDistance };
            return new[]
            {
                (float)((x - (s_rectX + s_rectW / 2)) * s_metresPerPixel),
                (float)(FullCentreHeight - (y - (s_rectY + s_rectH / 2)) * s_metresPerPixel),
                -FullDistance,
            };
        }

        // ---- filling the eyes --------------------------------------------------------------------------------

        private static void Toggle(double x, double y)
        {
            if (s_full)
            {
                s_full = false;
                (AppContext.GetData("WpfWebGpu.Xr.Core.EndFullscreen") as Action)?.Invoke();
                (AppContext.GetData("WpfWebGpu.Xr.ExternalTarget") as Func<IntPtr, int, int, bool>)?.Invoke(IntPtr.Zero, 0, 0);
                s_close();
                return;
            }
            if ((AppContext.GetData("WpfWebGpu.Xr.Core.ViewAt") as Func<int, int, double[]?>)?.Invoke((int)x, (int)y) is not { Length: 4 } r
                || r[2] < 8 || r[3] < 8)
            {
                Console.WriteLine($"[xr] no 3D view for fullscreen at ({x:0},{y:0})");
                return;
            }
            (s_rectX, s_rectY, s_rectW, s_rectH) = (r[0], r[1], r[2], r[3]);
            float wide = 2 * FullDistance * MathF.Tan(40 * MathF.PI / 180), high = 2 * FullDistance * MathF.Tan(32 * MathF.PI / 180);
            s_metresPerPixel = Math.Min(wide / r[2], high / r[3]);
            s_framed = false;
            s_full = true;
            Console.WriteLine($"[xr] fullscreen: the 3D view at ({r[0]:0},{r[1]:0}) {r[2]:0}x{r[3]:0}");
            (AppContext.GetData("WpfWebGpu.Xr.Core.BeginFullscreen") as Action<double, double, double, double>)?.Invoke(r[0], r[1], r[2], r[3]);
            s_open(&Closed);
        }

        private static System.Threading.Timer? s_auto;

        [UnmanagedCallersOnly]
        private static void AutoOnMain(IntPtr context)
        {
            try
            {
                // at the first 3D view's centre, as a pinch on it would be
                if ((AppContext.GetData("WpfWebGpu.Xr.Core.FirstView") as Func<double[]?>)?.Invoke() is { Length: 4 } r)
                    Toggle((int)(r[0] + r[2] / 2), (int)(r[1] + r[3] / 2));
                else Console.WriteLine("[xr] fullscreen: no 3D view yet");
            }
            catch (Exception e) { Console.WriteLine("[xr] fullscreen: " + e.Message); }
        }

        [UnmanagedCallersOnly]
        private static void Closed()
        {
            // the space has gone (the person closed it): the view goes back to its window, on the main thread
            if (!s_full) return;
            // dispatch_get_main_queue() is a macro for this symbol's address
            IntPtr main = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.dylib"), "_dispatch_main_q");
            dispatch_async_f(main, IntPtr.Zero, &ClosedOnMain);
        }

        [UnmanagedCallersOnly]
        private static void ClosedOnMain(IntPtr context)
        {
            if (!s_full) return;
            s_full = false;
            (AppContext.GetData("WpfWebGpu.Xr.Core.EndFullscreen") as Action)?.Invoke();
            (AppContext.GetData("WpfWebGpu.Xr.ExternalTarget") as Func<IntPtr, int, int, bool>)?.Invoke(IntPtr.Zero, 0, 0);
        }

        // ---- the hands in the space: one pinch moves the view, two scale and turn it (the Quest's sticks and grip) ----

        private static readonly System.Collections.Generic.Dictionary<long, System.Numerics.Vector3> s_pinches = new();

        [UnmanagedCallersOnly]
        private static void Spatial(long id, int active, float x, float y, float z)
        {
            if (!s_full || !s_framed) { s_pinches.Clear(); return; }
            // where the hand is in the desktop's frame (EyeView's)
            var undo = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, -s_heading);
            var at = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(x, y, z) - s_origin, undo);
            if (active == 0) { s_pinches.Remove(id); return; }
            if (!s_pinches.TryGetValue(id, out var was)) { s_pinches[id] = at; return; }
            var manipulate = AppContext.GetData("WpfWebGpu.Xr.Core.Manipulate") as Action<float, float, float, float, float>;
            if (s_pinches.Count == 1)
            {
                var move = at - was;
                manipulate?.Invoke(0f, 1f, move.X, move.Y, move.Z);
            }
            else if (s_pinches.Count == 2)
            {
                // the other hand where it last was: the span between them, before and now
                System.Numerics.Vector3 other = default;
                foreach (var kv in s_pinches) if (kv.Key != id) other = kv.Value;
                System.Numerics.Vector3 before = was - other, now = at - other;
                float scale = before.Length() > 0.01f ? now.Length() / before.Length() : 1f;
                float turn = MathF.Atan2(-now.Z, now.X) - MathF.Atan2(-before.Z, before.X);
                if (turn > MathF.PI) turn -= 2 * MathF.PI; else if (turn < -MathF.PI) turn += 2 * MathF.PI;
                manipulate?.Invoke(turn, scale, 0f, 0f, 0f);
            }
            s_pinches[id] = at;
        }

        // ---- the pinch held: a long press on the root view -------------------------------------------------------

        private static void AddPress(IntPtr rootView)
        {
            IntPtr cls = objc_getClass("WpfXrPressTarget");
            if (cls == IntPtr.Zero)
            {
                cls = objc_allocateClassPair(objc_getClass("NSObject"), "WpfXrPressTarget", 0);
                class_addMethod(cls, Sel("pressed:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Pressed, "v@:@");
                objc_registerClassPair(cls);
            }
            IntPtr target = Send(Send(cls, Sel("alloc")), Sel("init"));
            IntPtr press = SendPtrPtr(Send(objc_getClass("UILongPressGestureRecognizer"), Sel("alloc")), Sel("initWithTarget:action:"), target, Sel("pressed:"));
            SendDouble(press, Sel("setMinimumPressDuration:"), 0.8);
            SendBool(press, Sel("setCancelsTouchesInView:"), 0);
            SendPtr(rootView, Sel("addGestureRecognizer:"), press);
        }

        [UnmanagedCallersOnly]
        private static void Pressed(IntPtr self, IntPtr sel, IntPtr recognizer)
        {
            const long Began = 1;
            if (SendLong(recognizer, Sel("state")) != Began) return;
            CGPoint at = SendPoint(recognizer, Sel("locationInView:"), s_root);
            double scale = SendDoubleRet(s_root, Sel("contentScaleFactor"));
            try { Toggle(at.X * scale, at.Y * scale); }
            catch (Exception e) { Console.WriteLine("[xr] fullscreen: " + e.Message); }
        }

        private static bool IsVision()
        {
            const long UIUserInterfaceIdiomVision = 6;
            IntPtr device = Send(objc_getClass("UIDevice"), Sel("currentDevice"));
            return device != IntPtr.Zero && SendLong(device, Sel("userInterfaceIdiom")) == UIUserInterfaceIdiomVision;
        }

        [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }

        private static IntPtr Sel(string name) => sel_registerName(name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPtr(IntPtr receiver, IntPtr selector, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendDouble(IntPtr receiver, IntPtr selector, double a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr selector, byte a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDoubleRet(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGPoint SendPoint(IntPtr receiver, IntPtr selector, IntPtr a);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern void dispatch_async_f(IntPtr queue, IntPtr context, delegate* unmanaged<IntPtr, void> work);
    }
}
