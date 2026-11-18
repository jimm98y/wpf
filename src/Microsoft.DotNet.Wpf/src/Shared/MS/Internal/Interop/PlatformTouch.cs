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
    ///   must not fabricate contacts here; it registers an IManipulator of its own instead. A
    ///   graphics tablet on that same Mac is a different matter: it IS a digitizer, mapped onto the
    ///   screen, and its pen is reported here like any other.
    ///  </para>
    /// </remarks>
    /// <summary>
    ///  What a digitizer measured about a contact beyond where it is.
    /// </summary>
    /// <remarks>
    ///  A struct rather than a widening parameter list because a pen reports several things: pressure,
    ///  tilt, twist, the inverted (eraser) end, the barrel button, and -- for a finger as much as a
    ///  pen -- the size of the contact. Every field carries its own "not reported" value, because a
    ///  finger measures most of them not at all and a stylus on a cheap digitizer measures only some
    ///  -- and a default substituted for a measurement is how StylusPoint ends up asserting a tilt
    ///  nobody sensed.
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

        /// <summary>
        ///  The tool is the INVERTED end of the pen -- the eraser.
        /// </summary>
        /// <remarks>
        ///  A plain bool where the other fields carry a "not reported" value, and deliberately so:
        ///  a head that cannot tell reports false, and every consumer treats "upright" and "no idea"
        ///  identically. A third state would be one nobody could act on differently.
        /// </remarks>
        internal readonly bool IsInverted;

        /// <summary>
        ///  The pen's rotation about its own axis, clockwise, in degrees 0..360, or NaN where
        ///  unreported. Windows' POINTER_PEN_INFO.rotation and StylusPointProperties.TwistOrientation;
        ///  only an art pen or a Pencil Pro has the sensor for it.
        /// </summary>
        internal readonly double Twist;

        /// <summary>
        ///  The size of the contact, in device pixels, or NaN where unreported: the bounding box of
        ///  the ellipse a finger presses, which is what Windows reports as the touch contact
        ///  rectangle and StylusPointProperties.Width and Height. A pen tip has none worth the name,
        ///  and most heads report none for it.
        /// </summary>
        internal readonly double Width;
        internal readonly double Height;

        /// <summary>
        ///  The pen's barrel (side) button is held, or null where the device cannot say -- a finger,
        ///  or a pen the head has no button state for.
        /// </summary>
        /// <remarks>
        ///  Unlike <see cref="IsInverted"/> this one does carry a third state, because the ink path
        ///  acts on it: a StylusPoint description names BarrelButton only for a device that has one,
        ///  as a Windows pen's does, and reporting "released" for a finger would put a button on
        ///  every stroke.
        /// </remarks>
        internal readonly bool? BarrelButton;

        internal PenState(double pressure, double tiltX, double tiltY)
            : this(pressure, tiltX, tiltY, isInverted: false)
        {
        }

        internal PenState(double pressure, double tiltX, double tiltY, bool isInverted)
            : this(pressure, tiltX, tiltY, isInverted, double.NaN, double.NaN, double.NaN, null)
        {
        }

        internal PenState(double pressure, double tiltX, double tiltY, bool isInverted,
                          double twist, double width, double height, bool? barrelButton)
        {
            Pressure = pressure;
            TiltX = tiltX;
            TiltY = tiltY;
            IsInverted = isInverted;
            Twist = twist;
            Width = width;
            Height = height;
            BarrelButton = barrelButton;
        }

        /// <summary>A finger: position and nothing else.</summary>
        internal static PenState None => new PenState(-1, double.NaN, double.NaN);

        internal static PenState FromPressure(double pressure) => new PenState(pressure, double.NaN, double.NaN);

        /// <summary>A finger whose contact area the digitizer measured, and nothing more.</summary>
        internal static PenState Contact(double width, double height)
            => new PenState(-1, double.NaN, double.NaN, false, double.NaN, width, height, null);

        internal bool HasPressure => Pressure >= 0 && Pressure <= 1;
        internal bool HasTilt => !double.IsNaN(TiltX) && !double.IsNaN(TiltY);
        internal bool HasTwist => !double.IsNaN(Twist);
        internal bool HasContactSize => Width > 0 && Height > 0;

        /// <summary>The same measurement with its contact size replaced -- the sink's DIP conversion.</summary>
        internal PenState WithContactSize(double width, double height)
            => new PenState(Pressure, TiltX, TiltY, IsInverted, Twist, width, height, BarrelButton);
    }

    /// <summary>
    ///  One sample the platform folded into a later event: where the contact was and what the
    ///  digitizer measured there, between the previous event and the one that carries it.
    /// </summary>
    /// <remarks>
    ///  A digitizer samples faster than a platform delivers: an Apple Pencil at 240Hz into a 60Hz
    ///  touch stream, an Android stylus into one MotionEvent per frame. The platform keeps what it
    ///  coalesced -- UIEvent.coalescedTouchesForTouch:, MotionEvent's history, getCoalescedEvents()
    ///  -- and so does Windows, which hands ink the whole packet stream of a StylusDevice. Ink drawn
    ///  from the delivered events alone is a polygon through a quarter of the pen's path, and a fast
    ///  curve visibly corners.
    /// </remarks>
    internal readonly struct TouchSample
    {
        /// <summary>Screen device pixels, as every other point on the seam.</summary>
        internal readonly int ScreenX;
        internal readonly int ScreenY;
        internal readonly PenState Pen;
        internal readonly uint TimestampMs;

        internal TouchSample(int screenX, int screenY, in PenState pen, uint timestampMs)
        {
            ScreenX = screenX;
            ScreenY = screenY;
            Pen = pen;
            TimestampMs = timestampMs;
        }
    }

    /// <summary>
    ///  The geometry every head needs to turn what its platform measures into what
    ///  <see cref="PenState"/> carries. Here, once, rather than re-derived per head with its own
    ///  approximation.
    /// </summary>
    internal static class PenGeometry
    {
        /// <summary>
        ///  Resolves a tilt given as an angle from vertical and a direction of lean into the two
        ///  per-axis tilts, in degrees.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   iOS (altitude and azimuth) and Android (AXIS_TILT and AXIS_ORIENTATION) both describe
        ///   the pen in spherical terms; Windows, the browser and Wayland use the per-axis pair, each
        ///   the angle the pen makes with the vertical as seen along one axis. The two are not
        ///   linearly related: a pen leaning 60 degrees towards a corner leans 51 degrees along each
        ///   axis, not 42 -- so scaling the angle by the direction's cosine and sine, the obvious
        ///   shortcut, under-reports every diagonal lean. The exact relation is the Pointer Events
        ///   one, tan(tiltX) = tan(fromVertical) * dirX.
        ///  </para>
        ///  <para>
        ///   atan2 rather than atan of a tangent, so a pen lying flat (90 degrees, an infinite
        ///   tangent) comes out as the +/-90 it is rather than as NaN.
        ///  </para>
        /// </remarks>
        /// <param name="fromVerticalRadians">0 for a pen standing upright, pi/2 for one lying flat.</param>
        /// <param name="directionX">The lean's direction, a unit vector in screen terms: +X right.</param>
        /// <param name="directionY">+Y towards the user (down the screen).</param>
        internal static void TiltFromSpherical(double fromVerticalRadians, double directionX, double directionY,
                                               out double tiltX, out double tiltY)
        {
            double sin = Math.Sin(fromVerticalRadians), cos = Math.Cos(fromVerticalRadians);
            tiltX = Math.Atan2(sin * directionX, cos) * 180.0 / Math.PI;
            tiltY = Math.Atan2(sin * directionY, cos) * 180.0 / Math.PI;
        }

        /// <summary>An angle in degrees, folded into 0..360 the way TwistOrientation counts.</summary>
        internal static double NormalizeDegrees(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return double.NaN;
            double d = degrees % 360.0;
            return d < 0 ? d + 360.0 : d;
        }

        /// <summary>
        ///  The axis-aligned bounding box of a contact ellipse, which is what Windows reports a
        ///  contact's size as (POINTER_TOUCH_INFO.rcContact) and so what StylusPoint Width and
        ///  Height mean.
        /// </summary>
        /// <param name="major">The ellipse's long diameter.</param>
        /// <param name="minor">Its short diameter.</param>
        /// <param name="orientationRadians">The long axis's angle from vertical, clockwise -- Android's
        /// AXIS_ORIENTATION for a finger.</param>
        internal static void EllipseBounds(double major, double minor, double orientationRadians,
                                           out double width, out double height)
        {
            if (double.IsNaN(orientationRadians)) orientationRadians = 0;
            double s = Math.Sin(orientationRadians), c = Math.Cos(orientationRadians);
            width = Math.Sqrt(major * major * s * s + minor * minor * c * c);
            height = Math.Sqrt(major * major * c * c + minor * minor * s * s);
        }
    }

    internal interface IPlatformTouchSink
    {
        /// <summary>A contact touched down. Returns true if WPF handled it.</summary>
        /// <param name="pen">What the digitizer measured about the tip; see <see cref="PenState"/>.</param>
        bool TouchDown(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen, uint timestampMs);

        bool TouchMove(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen, uint timestampMs);

        /// <summary>
        ///  A move that carries the samples the platform coalesced into it.
        /// </summary>
        /// <remarks>
        ///  Still ONE move: the touch and mouse events stay one per platform event, as they are on
        ///  Windows, where the intermediate packets ride along inside the event rather than becoming
        ///  events of their own. A separate method rather than an array on every TouchMove so that
        ///  the common case -- a finger, a platform that coalesces nothing -- allocates nothing.
        /// </remarks>
        /// <param name="coalesced">The samples between the previous move and this one, oldest first,
        /// NOT including this move's own point. Null or empty is a plain move.</param>
        bool TouchMoveCoalesced(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen,
                                uint timestampMs, TouchSample[] coalesced);

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
