// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Apple Pencil: the eraser it does not have, and the measurements a UITouch carries.
//
// A Windows pen erases with its other END, and WPF learns it from StylusDevice.Inverted; Wacom, the
// browser and Android all report that end. An Apple Pencil has no second end. iPadOS gives the
// eraser through UIPencilInteraction instead: a double tap on the Pencil's flat side (or a squeeze
// on a Pencil Pro) asks the app to switch tools, and the user's own setting -- Settings > Apple
// Pencil -- says what the gesture means. "Switch between current tool and eraser" is the default.
//
// So the eraser here is a STATE the app keeps, toggled by that gesture, and every Pencil contact
// reports IsInverted while it is on. That is exactly what the rest of WPF needs: InkCanvas switches
// to EditingModeInverted at the down, as it would for a Surface pen turned over.
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>
    ///  The pen-or-eraser state a Pencil's tool-switch gesture drives. Pure, so it is tested off iOS.
    /// </summary>
    internal sealed class PencilToolState
    {
        // UIPencilPreferredAction: what the user chose in Settings for the double tap or squeeze.
        internal const int Ignore = 0;
        internal const int SwitchEraser = 1;
        internal const int SwitchPrevious = 2;

        /// <summary>The Pencil is the eraser now.</summary>
        internal bool EraserActive { get; private set; }

        // Whether the eraser was ever the tool, which is what "switch to the previous tool" needs:
        // with only the pen and the eraser to choose between, the previous tool of a pen that has
        // never been anything else is nothing.
        private bool _eraserUsed;

        /// <summary>
        ///  The gesture happened, and the user's setting says <paramref name="preferredAction"/>.
        ///  Returns whether the tool changed.
        /// </summary>
        /// <remarks>
        ///  "Switch eraser" toggles between the tool and the eraser, Apple's own description of it.
        ///  "Switch previous" goes back to the other tool -- from the eraser to the pen, and from the
        ///  pen to the eraser only if the eraser was the tool before. Every other choice (the colour
        ///  palette, the ink attributes, a system shortcut, nothing) is for a palette WPF does not
        ///  have, and leaves the tool alone.
        /// </remarks>
        internal bool Apply(int preferredAction)
        {
            switch (preferredAction)
            {
                case SwitchEraser:
                    Set(!EraserActive);
                    return true;
                case SwitchPrevious:
                    if (!EraserActive && !_eraserUsed) return false;
                    Set(!EraserActive);
                    return true;
                default:
                    return false;
            }
        }

        private void Set(bool eraser)
        {
            EraserActive = eraser;
            if (eraser) _eraserUsed = true;
        }
    }

    /// <summary>
    ///  UIPencilInteraction on the WPF views, and the conversions from a Pencil UITouch's angles.
    /// </summary>
    internal static unsafe class UIKitPencil
    {
        private static readonly PencilToolState s_tool = new PencilToolState();
        private static IntPtr s_delegate;

        /// <summary>The Pencil is the eraser now (main thread).</summary>
        internal static bool EraserActive => s_tool.EraserActive;

        /// <summary>
        ///  Gives a view a UIPencilInteraction, so the Pencil's double tap and squeeze reach WPF.
        /// </summary>
        /// <remarks>
        ///  Every WPF view, not just the main one: UIKit delivers the gesture to the interaction of
        ///  the view the Pencil was last used over, and an InkCanvas in a dialog is in a view of its
        ///  own. A device with no Pencil support simply never calls the delegate.
        /// </remarks>
        internal static void Attach(IntPtr view)
        {
            try
            {
                if (view == IntPtr.Zero) return;
                IntPtr cls = objc_getClass("UIPencilInteraction");
                IntPtr del = Delegate();
                if (cls == IntPtr.Zero || del == IntPtr.Zero) return;

                IntPtr interaction = Send(Send(cls, Sel("alloc")), Sel("init"));
                if (interaction == IntPtr.Zero) return;

                // The delegate is weak in UIKit; the one instance is held for the life of the process.
                SendVoidPtr(interaction, Sel("setDelegate:"), del);
                SendVoidPtr(view, Sel("addInteraction:"), interaction);
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS pencil interaction failed: {e.Message}");
            }
        }

        /// <summary>
        ///  A Pencil's two angles as the per-axis tilt the seam takes, in degrees.
        /// </summary>
        /// <param name="altitude">From the screen: 0 lying flat, pi/2 upright (UITouch.altitudeAngle).</param>
        /// <param name="azimuth">The direction the Pencil leans, in the view: 0 along +x, increasing
        /// clockwise on a y-down screen (UITouch azimuthAngleInView:).</param>
        internal static void Tilt(double altitude, double azimuth, out double tiltX, out double tiltY)
            => PenGeometry.TiltFromSpherical(Math.PI / 2 - altitude, Math.Cos(azimuth), Math.Sin(azimuth), out tiltX, out tiltY);

        /// <summary>
        ///  A Pencil Pro's barrel roll (UITouch.rollAngle, radians) as twist, or NaN for a Pencil
        ///  without the sensor -- which reports a roll of exactly zero.
        /// </summary>
        /// <remarks>
        ///  Zero is ambiguous, a real reading as well as the "no sensor" one; it is treated as the
        ///  latter, and a Pro rolled to exactly zero then reports no twist on that sample, which the
        ///  ink path records as the zero it was.
        /// </remarks>
        internal static double TwistFromRoll(double rollRadians)
            => rollRadians == 0 || double.IsNaN(rollRadians) ? double.NaN : PenGeometry.NormalizeDegrees(rollRadians * 180.0 / Math.PI);

        // ---- the delegate ----

        private static IntPtr Delegate()
        {
            if (s_delegate != IntPtr.Zero) return s_delegate;

            IntPtr cls = objc_getClass("WpfPencilDelegate");
            if (cls == IntPtr.Zero)
            {
                IntPtr nsObject = objc_getClass("NSObject");
                if (nsObject == IntPtr.Zero) return IntPtr.Zero;
                cls = objc_allocateClassPair(nsObject, "WpfPencilDelegate", UIntPtr.Zero);
                if (cls == IntPtr.Zero) return IntPtr.Zero;

                // iPadOS 12.1 to 17.4 call this one.
                class_addMethod(cls, Sel("pencilInteractionDidTap:"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&DidTapImp, "v@:@");
                // 17.5 and later call these instead when the delegate has them, and add the squeeze.
                class_addMethod(cls, Sel("pencilInteraction:didReceiveTap:"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidReceiveTapImp, "v@:@@");
                class_addMethod(cls, Sel("pencilInteraction:didReceiveSqueeze:"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidReceiveSqueezeImp, "v@:@@");

                IntPtr protocol = objc_getProtocol("UIPencilInteractionDelegate");
                if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);
                objc_registerClassPair(cls);
            }

            s_delegate = Send(Send(cls, Sel("alloc")), Sel("init"));
            return s_delegate;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidTapImp(IntPtr self, IntPtr sel, IntPtr interaction) => Gesture("preferredTapAction");

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidReceiveTapImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr tap) => Gesture("preferredTapAction");

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidReceiveSqueezeImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr squeeze)
        {
            try
            {
                // A squeeze has phases; it switches the tool once, when it ends, as Apple's own apps
                // do. UIPencilInteractionPhase: 0 began, 1 changed, 2 ended, 3 cancelled.
                if (squeeze == IntPtr.Zero || SendNInt(squeeze, Sel("phase")) != 2) return;
                Gesture("preferredSqueezeAction");
            }
            catch { }
        }

        private static void Gesture(string preference)
        {
            // Never let a managed exception unwind into UIKit.
            try
            {
                IntPtr cls = objc_getClass("UIPencilInteraction");
                if (cls == IntPtr.Zero) return;
                IntPtr selector = Sel(preference);

                // The preference is a CLASS property (the user's, not the interaction's). The squeeze
                // one exists from 17.5 only, and a squeeze arrives only there -- asked anyway, so an
                // older system's missing selector is a no-op rather than an exception.
                if (!SendBoolPtr(cls, Sel("respondsToSelector:"), selector)) return;
                s_tool.Apply((int)SendNInt(cls, selector));
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS pencil gesture failed: {e.Message}");
            }
        }

        // ---- interop ----

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
    }
}
