// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Description:
// The StylusPoints a platform pen's measurements become, for the heads that have no StylusDevice.
//
// On Windows a pen's packets reach InkCanvas through its StylusDevice, whose description lists what
// that digitizer measures -- tilt, twist, contact size, the barrel button -- in the units Wisp and
// WM_POINTER define. Every other head drives InkCanvas through the mouse, and the platform touch seam
// carries what the pen measured (MS.Internal.Interop.PenState). This turns the one into the other,
// in Windows' units, so a stroke drawn with an iPad's Pencil carries the same properties a stroke
// drawn on a Surface does.
//

using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using MS.Internal.Interop;

namespace MS.Internal.Ink
{
    internal static class PlatformPenPoints
    {
        // The ranges a Windows pen reports through WM_POINTER, rather than the generic defaults in
        // StylusPointPropertyInfoDefaults. Tilt there is 0..3600, which no real digitizer produces:
        // the pointer stack reports a signed lean, -90..90 degrees, and these are its tenths. The
        // resolution (10 per degree) is the default's, so a value means the same thing either way.
        private static readonly StylusPointPropertyInfo s_x = new StylusPointPropertyInfo(StylusPointProperties.X);
        private static readonly StylusPointPropertyInfo s_y = new StylusPointPropertyInfo(StylusPointProperties.Y);
        private static readonly StylusPointPropertyInfo s_pressure = new StylusPointPropertyInfo(StylusPointProperties.NormalPressure);

        private static readonly StylusPointPropertyInfo s_tiltX =
            new StylusPointPropertyInfo(StylusPointProperties.XTiltOrientation, -900, 900, StylusPointPropertyUnit.Degrees, 10.0f);
        private static readonly StylusPointPropertyInfo s_tiltY =
            new StylusPointPropertyInfo(StylusPointProperties.YTiltOrientation, -900, 900, StylusPointPropertyUnit.Degrees, 10.0f);

        // Twist is a whole turn in tenths of a degree, clockwise, as POINTER_PEN_INFO.rotation is
        // (whose 0..359 the pointer stack scales by the same resolution).
        private static readonly StylusPointPropertyInfo s_twist =
            new StylusPointPropertyInfo(StylusPointProperties.TwistOrientation, 0, 3599, StylusPointPropertyUnit.Degrees, 10.0f);

        // Contact size in HIMETRIC -- hundredths of a millimetre, 1000 to the centimetre -- the unit
        // the default Width/Height info declares and the one Windows reports a contact in.
        private static readonly StylusPointPropertyInfo s_width =
            new StylusPointPropertyInfo(StylusPointProperties.Width, 0, int.MaxValue, StylusPointPropertyUnit.Centimeters, 1000.0f);
        private static readonly StylusPointPropertyInfo s_height =
            new StylusPointPropertyInfo(StylusPointProperties.Height, 0, int.MaxValue, StylusPointPropertyUnit.Centimeters, 1000.0f);

        private static readonly StylusPointPropertyInfo s_barrel =
            new StylusPointPropertyInfo(StylusPointProperties.BarrelButton, 0, 1, StylusPointPropertyUnit.None, 1.0f);

        private const double HimetricPerDip = 2540.0 / 96.0;

        /// <summary>
        ///  The description of a device that measures what <paramref name="pen"/> reports -- and
        ///  nothing it does not, so a finger's stroke stays the plain X, Y, pressure it always was.
        /// </summary>
        internal static StylusPointDescription DescriptionFor(in PenState pen)
        {
            var infos = new List<StylusPointPropertyInfo>(9) { s_x, s_y, s_pressure };
            if (pen.HasTilt)
            {
                infos.Add(s_tiltX);
                infos.Add(s_tiltY);
            }
            if (pen.HasTwist) infos.Add(s_twist);
            if (pen.HasContactSize)
            {
                infos.Add(s_width);
                infos.Add(s_height);
            }

            // Buttons last: StylusPointDescription rejects a button ahead of a value.
            if (pen.BarrelButton.HasValue) infos.Add(s_barrel);
            return new StylusPointDescription(infos);
        }

        /// <summary>
        ///  One point in <paramref name="description"/>, filled from <paramref name="pen"/>.
        /// </summary>
        /// <remarks>
        ///  The description is the STROKE's, fixed at its down, and a measurement can come and go
        ///  within a stroke -- a browser pen whose twist passes through zero, a head that reads a
        ///  property only on some events. A property the description has and this sample lacks
        ///  takes the neutral value (no lean, no turn, no size, released), which is also what a
        ///  Windows digitizer's packet holds for an axis it is not moving. A property the sample has
        ///  and the description lacks is dropped: the stroke has no slot for it, exactly as a
        ///  StylusDevice reformatted to InkCanvas.DefaultStylusPointDescription has none.
        /// </remarks>
        internal static StylusPoint PointFor(Point position, in PenState pen, StylusPointDescription description)
        {
            float pressure = pen.HasPressure ? (float)pen.Pressure : StylusPoint.DefaultPressure;

            System.Collections.ObjectModel.ReadOnlyCollection<StylusPointPropertyInfo> properties = description.GetStylusPointProperties();
            if (properties.Count <= 3)
            {
                return new StylusPoint(position.X, position.Y, pressure, description, null);
            }

            int[] values = new int[properties.Count - 3];
            for (int i = 3; i < properties.Count; i++)
            {
                values[i - 3] = ValueOf(properties[i], pen);
            }
            return new StylusPoint(position.X, position.Y, pressure, description, values);
        }

        private static int ValueOf(StylusPointPropertyInfo info, in PenState pen)
        {
            System.Guid id = info.Id;
            int value = 0;
            if (id == StylusPointProperties.XTiltOrientation.Id)
            {
                if (pen.HasTilt) value = Tenths(System.Math.Clamp(pen.TiltX, -90.0, 90.0));
            }
            else if (id == StylusPointProperties.YTiltOrientation.Id)
            {
                if (pen.HasTilt) value = Tenths(System.Math.Clamp(pen.TiltY, -90.0, 90.0));
            }
            else if (id == StylusPointProperties.TwistOrientation.Id)
            {
                if (pen.HasTwist) value = Tenths(PenGeometry.NormalizeDegrees(pen.Twist)) % 3600;
            }
            else if (id == StylusPointProperties.Width.Id)
            {
                if (pen.HasContactSize) value = (int)System.Math.Round(pen.Width * HimetricPerDip);
            }
            else if (id == StylusPointProperties.Height.Id)
            {
                if (pen.HasContactSize) value = (int)System.Math.Round(pen.Height * HimetricPerDip);
            }
            else if (id == StylusPointProperties.BarrelButton.Id)
            {
                value = pen.BarrelButton == true ? 1 : 0;
            }

            // An app's own DefaultStylusPointDescription may declare a narrower range than ours; the
            // value is kept inside whatever the stroke's description says, as a digitizer's always is.
            if (info.IsButton) return value;
            return System.Math.Clamp(value, info.Minimum, info.Maximum);
        }

        private static int Tenths(double degrees) => (int)System.Math.Round(degrees * 10.0);
    }
}
