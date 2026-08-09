// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Touch off Windows: a TouchDevice the windowing backends can drive.
//
// WPF's only TouchDevice is StylusTouchDeviceBase, which sits on Wisp and WM_POINTER and exists on
// Windows alone. Every other head therefore had no touch at all: UIKitWindow and AndroidWindow
// synthesized a single contact into mouse moves plus wheel notches, which is why a finger could
// scroll a ScrollViewer and do nothing else. No TouchDown, no second contact, and -- because
// TouchDevice is what promotes itself to a manipulator -- no pinch, rotate or inertia anywhere.
//
// This is the other implementation: one PlatformTouchDevice per live contact, driven through the
// MS.Internal.Interop.PlatformTouch seam by whichever backend owns the digitizer. Everything above
// it is WPF's own machinery, unchanged, so a head that delivers contacts accurately gets the Touch
// events, ScrollViewer panning, Manipulation and inertia for free.
//

using MS.Internal;
using MS.Internal.Interop;
using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Media;

namespace System.Windows.Input
{
    /// <summary>
    ///  One touch contact, from the moment it goes down until it lifts or is cancelled.
    /// </summary>
    internal sealed class PlatformTouchDevice : TouchDevice
    {
        // The position in the coordinates of the source's root visual, which is what WPF hit-tests
        // and reports against. Converted once per report, at the seam.
        private Point _position;

        // Contact pressure, 0..1, or -1 where the device does not report any. Pens do; fingers on
        // most digitizers do not, and inventing a value would make StylusPoint.PressureFactor lie.
        private double _pressure = -1;

        internal PlatformTouchDevice(int deviceId) : base(deviceId)
        {
        }

        internal bool IsDown { get; private set; }

        internal void SetSource(PresentationSource source) => SetActiveSource(source);

        /// <summary>The most recent position, in the root visual's coordinates.</summary>
        internal void SetPosition(Point position, double pressure)
        {
            _position = position;
            _pressure = pressure;
        }

        public override TouchPoint GetTouchPoint(IInputElement relativeTo)
        {
            Point point = _position;

            // relativeTo == null means "the root", which is the space _position is already in.
            if (relativeTo is not null)
            {
                PresentationSource source = ActiveSource;
                if (source is null)
                {
                    return new TouchPoint(this, point, default, TouchActionOf(IsDown));
                }

                point = InputElement.TranslatePoint(_position, source.RootVisual, (DependencyObject)relativeTo);
            }

            // A contact has an area, not a point, and WPF reports it as a rect. Nothing off Windows
            // gives a real contact ellipse today, so the rect is the point itself: honest about the
            // fact that the size is unknown rather than inventing a plausible-looking one.
            return new TouchPoint(this, point, new Rect(point, new Size(0, 0)), TouchActionOf(IsDown));
        }

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo)
        {
            // No coalescing: the seam delivers every sample the platform reported, one call each, so
            // there is never a batch of intermediate points to unpack. A backend that coalesces
            // (Android's MotionEvent history, a Wayland frame with several motions) can start
            // reporting them here rather than dropping them.
            return new TouchPointCollection();
        }

        private static TouchAction TouchActionOf(bool isDown) => isDown ? TouchAction.Move : TouchAction.Up;

        /// <summary>Pressure for the stylus path; negative when the device reports none.</summary>
        internal double Pressure => _pressure;

        internal new void Activate() => base.Activate();
        internal new void Deactivate() => base.Deactivate();
        internal new bool ReportMove() => base.ReportMove();

        internal new bool ReportDown()
        {
            IsDown = true;
            return base.ReportDown();
        }

        internal new bool ReportUp()
        {
            bool handled = base.ReportUp();
            IsDown = false;
            return handled;
        }
    }

    /// <summary>
    ///  Routes the windowing backends' contacts onto <see cref="PlatformTouchDevice"/>s.
    /// </summary>
    /// <remarks>
    ///  Installed once, on the same thread WPF's input runs on. Contacts are keyed by the platform's
    ///  own id, which need only be unique among the contacts alive at one time -- a device is created
    ///  on down and retired on up or cancel, so an id the platform recycles afterwards is fine.
    /// </remarks>
    internal sealed class PlatformTouchSink : IPlatformTouchSink
    {
        private static PlatformTouchSink s_instance;

        private readonly Dictionary<int, PlatformTouchDevice> _contacts = new();

        /// <summary>
        ///  Makes touch available to the backends. Called as a window is created, like the drop
        ///  target's own Install; until it runs, PlatformTouch.IsAvailable is false and a backend
        ///  keeps synthesizing mouse input instead.
        /// </summary>
        internal static void Install()
        {
            if (s_instance is not null) return;
            s_instance = new PlatformTouchSink();
            PlatformTouch.Sink = s_instance;
        }

        public bool TouchDown(IntPtr windowHandle, int contactId, int screenX, int screenY, double pressure, uint timestampMs)
        {
            if (!TryResolve(windowHandle, screenX, screenY, out PresentationSource source, out Point position))
            {
                return false;
            }

            // A down for a contact already alive is the platform repeating itself (or an id reused
            // without an intervening up). Retire the old one rather than stacking two devices on one
            // id, which would leave the first permanently active and the manipulation never ending.
            if (_contacts.TryGetValue(contactId, out PlatformTouchDevice existing))
            {
                Retire(contactId, existing, cancel: true);
            }

            var device = new PlatformTouchDevice(contactId);
            _contacts[contactId] = device;

            device.SetSource(source);
            device.SetPosition(position, pressure);
            device.Activate();
            return device.ReportDown();
        }

        public bool TouchMove(IntPtr windowHandle, int contactId, int screenX, int screenY, double pressure, uint timestampMs)
        {
            if (!_contacts.TryGetValue(contactId, out PlatformTouchDevice device)) return false;
            if (!TryResolve(windowHandle, screenX, screenY, out PresentationSource source, out Point position)) return false;

            device.SetSource(source);
            device.SetPosition(position, pressure);
            return device.ReportMove();
        }

        public bool TouchUp(IntPtr windowHandle, int contactId, int screenX, int screenY, uint timestampMs)
        {
            if (!_contacts.TryGetValue(contactId, out PlatformTouchDevice device)) return false;

            if (TryResolve(windowHandle, screenX, screenY, out PresentationSource source, out Point position))
            {
                device.SetSource(source);
                device.SetPosition(position, device.Pressure);
            }

            bool handled = device.ReportUp();
            Retire(contactId, device, cancel: false);
            return handled;
        }

        public void TouchCancel(IntPtr windowHandle, int contactId)
        {
            if (!_contacts.TryGetValue(contactId, out PlatformTouchDevice device)) return;
            Retire(contactId, device, cancel: true);
        }

        /// <summary>
        ///  Takes a contact out of service. A cancel deactivates WITHOUT reporting an up, which is
        ///  the difference that matters downstream: an up completes a tap and finishes a
        ///  manipulation, a cancel must do neither.
        /// </summary>
        private void Retire(int contactId, PlatformTouchDevice device, bool cancel)
        {
            _contacts.Remove(contactId);
            try
            {
                device.Deactivate();
            }
            catch (InvalidOperationException)
            {
                // Deactivating a device that never activated (a cancel arriving before the down was
                // accepted) is not worth propagating into the platform's event loop.
            }
        }

        /// <summary>
        ///  Finds the window the contact landed in and converts the screen point into that source's
        ///  root-visual coordinates -- device pixels to DIPs included, so a contact lands where the
        ///  user touched on a scaled display.
        /// </summary>
        private static bool TryResolve(IntPtr windowHandle, int screenX, int screenY,
                                       out PresentationSource source, out Point position)
        {
            source = null;
            position = default;

            HwndSource hwnd = HwndSource.FromHwnd(windowHandle);
            if (hwnd is null || hwnd.RootVisual is null) return false;

            // Screen -> client. PointUtil.ScreenToClient is a user32 P/Invoke and exists on Windows
            // alone, so off Windows the window's own backend supplies the client origin instead --
            // the same origin the backend added to its surface-local coordinates on the way in, so
            // the round trip is exact.
            Point clientPoint;
            if (OperatingSystem.IsWindows())
            {
                clientPoint = PointUtil.ScreenToClient(new Point(screenX, screenY), hwnd);
            }
            else
            {
                IPlatformWindow platformWindow = PlatformWindow.FromHandle(windowHandle);
                if (platformWindow is null) return false;

                platformWindow.GetClientScreenOriginPixels(out int originX, out int originY);
                clientPoint = new Point(screenX - originX, screenY - originY);
            }

            // Client device pixels -> the root visual's DIPs.
            CompositionTarget target = hwnd.CompositionTarget;
            if (target is null) return false;

            position = target.TransformFromDevice.Transform(clientPoint);
            source = hwnd;
            return true;
        }
    }
}
