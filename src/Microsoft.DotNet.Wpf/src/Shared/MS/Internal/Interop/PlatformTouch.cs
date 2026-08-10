// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;

namespace MS.Internal.Interop
{
    /// <summary>
    ///  The WPF side of platform touch, implemented by PresentationCore and driven by the windowing
    ///  backends. The fourth sibling of <see cref="PlatformWindow"/>, <see cref="PlatformClipboard"/>
    ///  and <see cref="PlatformDragDrop"/>, and it exists for the same reason: the two halves live in
    ///  different assemblies and the dependency only runs one way. The backends are in WindowsBase;
    ///  <c>TouchDevice</c>, hit-testing and the Touch/Manipulation routed events are in
    ///  PresentationCore, which references WindowsBase and not the reverse. PresentationCore installs
    ///  itself here, exactly as the compositor and drop-target seams do.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   A CONTACT is one finger (or pen tip) from the moment it touches down to the moment it lifts,
    ///   identified by <c>contactId</c>. Ids are the platform's own and need only be unique among the
    ///   contacts alive at one time; a platform that recycles them after a lift is fine.
    ///  </para>
    ///  <para>
    ///   Points are in SCREEN device pixels, matching <see cref="PlatformDragDrop"/> and what
    ///   <c>PointUtil.ScreenToClient</c> expects: off Windows that subtracts the window's client
    ///   screen origin, so a backend adds the same origin to its surface-local coordinates and the
    ///   round trip is exact.
    ///  </para>
    ///  <para>
    ///   Manipulation needs nothing here. <c>TouchDevice</c> is itself an <c>IManipulator</c> and
    ///   promotes its own contacts, so pinch, rotate and inertia follow from delivering contacts
    ///   accurately and no backend has to know manipulation exists. A head with no touch digitizer at
    ///   all -- macOS, whose trackpad reports positions on the TRACKPAD rather than on the window --
    ///   must not fabricate contacts here; it registers an IManipulator of its own instead.
    ///  </para>
    /// </remarks>
    /// <summary>
    ///  What a digitizer measured about a contact beyond where it is.
    /// </summary>
    /// <remarks>
    ///  A struct rather than a widening parameter list because a pen reports several things and will
    ///  report more: pressure and tilt today, twist and the inverted (eraser) end next. Every field
    ///  carries its own "not reported" value, because a finger measures none of them and a stylus on
    ///  a cheap digitizer measures only some -- and a default substituted for a measurement is how
    ///  StylusPoint ends up asserting a tilt nobody sensed.
    /// </remarks>
    internal readonly struct PenState
    {
        /// <summary>0..1, or negative where the device reports no pressure.</summary>
        internal readonly double Pressure;

        /// <summary>
        ///  Tilt from vertical in degrees along each axis, -90..90, or NaN where unreported. X is the
        ///  lean to the user's right, Y the lean towards them -- the browser's tiltX/tiltY, and what
        ///  StylusPointProperties.XTiltOrientation and YTiltOrientation take.
        /// </summary>
        internal readonly double TiltX;
        internal readonly double TiltY;

        internal PenState(double pressure, double tiltX, double tiltY)
        {
            Pressure = pressure;
            TiltX = tiltX;
            TiltY = tiltY;
        }

        /// <summary>A finger: position and nothing else.</summary>
        internal static PenState None => new PenState(-1, double.NaN, double.NaN);

        internal static PenState FromPressure(double pressure) => new PenState(pressure, double.NaN, double.NaN);

        internal bool HasPressure => Pressure >= 0 && Pressure <= 1;
        internal bool HasTilt => !double.IsNaN(TiltX) && !double.IsNaN(TiltY);
    }

    internal interface IPlatformTouchSink
    {
        /// <summary>A contact touched down. Returns true if WPF handled it.</summary>
        /// <param name="pen">What the digitizer measured about the tip; see <see cref="PenState"/>.</param>
        bool TouchDown(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen, uint timestampMs);

        bool TouchMove(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen, uint timestampMs);

        /// <summary>
        ///  A contact lifted.
        /// </summary>
        /// <remarks>
        ///  Pass <see cref="PlatformTouch.NoPosition"/> for both coordinates where the platform's up
        ///  carries none -- wl_touch.up is one such, naming only the contact id. The contact then
        ///  lifts where it was last seen, rather than at whatever placeholder was passed instead.
        /// </remarks>
        bool TouchUp(IntPtr windowHandle, int contactId, int screenX, int screenY, uint timestampMs);

        /// <summary>
        ///  The platform took the contact away -- the compositor started a gesture, the app lost the
        ///  surface, the touch was rejected as a palm. Distinct from an up: no tap or click should
        ///  follow, and any manipulation in progress is abandoned rather than completed.
        /// </summary>
        void TouchCancel(IntPtr windowHandle, int contactId);

        /// <summary>
        ///  Cancels every contact currently alive in a window.
        /// </summary>
        /// <remarks>
        ///  Wayland's wl_touch.cancel names no contact: the compositor is taking the WHOLE sequence,
        ///  typically because it recognised a gesture of its own. The backend has no list of live
        ///  ids to cancel one by one, and the sink does, so the sweep belongs here.
        /// </remarks>
        void TouchCancelAll(IntPtr windowHandle);
    }

    internal static class PlatformTouch
    {
        /// <summary>
        ///  "The platform did not say." Distinct from any real coordinate, including a negative one:
        ///  a window straddling the screen origin genuinely reports negatives.
        /// </summary>
        internal const int NoPosition = int.MinValue;

        /// <summary>Installed by PresentationCore; null until a WPF window exists.</summary>
        internal static IPlatformTouchSink? Sink { get; set; }

        /// <summary>
        ///  True when a touch source has somewhere to deliver to. A backend checks this before doing
        ///  the work of decoding a touch event, and -- more importantly -- to decide whether it must
        ///  still fall back to synthesizing mouse input for a head that has no sink yet.
        /// </summary>
        internal static bool IsAvailable => Sink is not null;

        /// <summary>Cancels every live contact in a window, if anything is listening.</summary>
        internal static void CancelAll(IntPtr windowHandle) => Sink?.TouchCancelAll(windowHandle);
    }
}
