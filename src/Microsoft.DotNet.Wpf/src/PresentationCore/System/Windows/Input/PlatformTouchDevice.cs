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

        // The contact's size in the root visual's DIPs, or zero where the digitizer measured none.
        private Size _size;

        // The samples the platform coalesced into the latest move, in the root visual's
        // coordinates, oldest first -- what GetIntermediateTouchPoints hands out.
        private Point[] _intermediate = Array.Empty<Point>();

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

        internal void SetContactSize(Size size) => _size = size;

        internal void SetIntermediate(Point[] points) => _intermediate = points ?? Array.Empty<Point>();

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

            return new TouchPoint(this, point, BoundsAt(point), TouchActionOf(IsDown));
        }

        // A contact has an area, not a point, and WPF reports it as a rect centred on the contact, as
        // Windows' rcContact is. Where the digitizer measured no size the rect is the point itself:
        // honest about the size being unknown rather than inventing a plausible-looking one.
        private Rect BoundsAt(Point point)
            => new Rect(new Point(point.X - _size.Width / 2, point.Y - _size.Height / 2), _size);

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo)
        {
            // The samples the platform coalesced into the latest move -- Windows' own contract,
            // where the intermediate points are the packets that arrived since the last event --
            // followed by the current point, which Windows includes as the last entry.
            var points = new TouchPointCollection();
            PresentationSource source = ActiveSource;
            foreach (Point root in _intermediate)
            {
                Point point = root;
                if (relativeTo is not null && source is not null)
                {
                    point = InputElement.TranslatePoint(root, source.RootVisual, (DependencyObject)relativeTo);
                }
                points.Add(new TouchPoint(this, point, BoundsAt(point), TouchAction.Move));
            }
            points.Add(GetTouchPoint(relativeTo));
            return points;
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

        /// <summary>
        ///  What the digitizer measured about the contact currently driving the emulated mouse, or
        ///  <see cref="PenState.None"/> when nothing is down or it measures nothing (a finger).
        /// </summary>
        /// <remarks>
        ///  Read by the ink path. InkCanvas takes its points from a StylusDevice when one has capture
        ///  and otherwise from the MOUSE, which carries no pressure -- and off Windows there is no
        ///  StylusDevice at all, so a pen's pressure reached the seam and stopped there. This is how
        ///  it gets the last few inches, without a Wisp emulation standing in the way.
        ///
        ///  The FIRST contact is the one reported, because that is the one promoted to the mouse; a
        ///  second finger does not draw.
        ///
        ///  Its contact size is in the root visual's DIPs, already converted, as the position is;
        ///  the other fields are as the digitizer reported them.
        /// </remarks>
        internal static PenState CurrentPen { get; private set; } = PenState.None;

        /// <summary>
        ///  The samples the platform coalesced into the primary contact's latest move, not yet taken
        ///  by the ink path: where the contact was (root-visual DIPs) and what was measured there.
        /// </summary>
        /// <remarks>
        ///  REPLACED by every move rather than accumulated. The backends report the contact and then
        ///  the mouse for the same platform event, so the mouse move that follows a contact move is
        ///  the one these samples belong in front of. A move whose mouse never arrives (a press still
        ///  being held for a possible right-click) takes its samples with it, as it takes its own
        ///  point -- accumulating instead would hand the next stroke a backlog from wherever the
        ///  finger had been.
        /// </remarks>
        private static readonly List<(PresentationSource Source, Point Position, PenState Pen)> s_pending = new();

        /// <summary>
        ///  Hands the ink path the samples pending for the primary contact, translated into
        ///  <paramref name="relativeTo"/>'s coordinates, oldest first -- and forgets them, so each is
        ///  drawn once.
        /// </summary>
        internal static void TakeCoalescedSamples(IInputElement relativeTo, List<(Point Position, PenState Pen)> into)
        {
            foreach ((PresentationSource source, Point root, PenState pen) in s_pending)
            {
                Point point = root;
                if (relativeTo is DependencyObject element && source?.RootVisual is Visual rootVisual)
                {
                    point = InputElement.TranslatePoint(root, rootVisual, element);
                }
                into.Add((point, pen));
            }
            s_pending.Clear();
        }

        private int _primaryContact = -1;

        private void TrackPen(int contactId, in PenState pen, bool down)
        {
            if (down && _primaryContact < 0) _primaryContact = contactId;
            if (contactId != _primaryContact) return;
            CurrentPen = pen;
            if (down) s_pending.Clear();
        }

        private void ForgetPressure(int contactId)
        {
            if (contactId != _primaryContact) return;
            _primaryContact = -1;
            CurrentPen = PenState.None;
            s_pending.Clear();
        }

        public bool TouchDown(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen, uint timestampMs)
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

            PenState inDips = InDips(source, pen);
            device.SetSource(source);
            device.SetPosition(position, pen.Pressure);
            device.SetContactSize(SizeOf(inDips));
            TrackPen(contactId, inDips, down: true);
            device.Activate();
            return device.ReportDown();
        }

        public bool TouchMove(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen, uint timestampMs)
            => TouchMoveCoalesced(windowHandle, contactId, screenX, screenY, pen, timestampMs, null);

        public bool TouchMoveCoalesced(IntPtr windowHandle, int contactId, int screenX, int screenY, in PenState pen,
                                       uint timestampMs, TouchSample[] coalesced)
        {
            if (!_contacts.TryGetValue(contactId, out PlatformTouchDevice device)) return false;
            if (!TryResolve(windowHandle, screenX, screenY, out PresentationSource source, out Point position)) return false;

            Point[] intermediate = Array.Empty<Point>();
            bool primary = contactId == _primaryContact;
            if (primary) s_pending.Clear();

            if (coalesced is { Length: > 0 })
            {
                var resolved = new List<Point>(coalesced.Length);
                foreach (TouchSample sample in coalesced)
                {
                    // Each sample resolves against the window the move is for: the contact belongs
                    // to that window for its whole life, and a sample a pixel past its edge is still
                    // the same stroke.
                    if (!TryResolve(windowHandle, sample.ScreenX, sample.ScreenY, out PresentationSource at, out Point point)) continue;
                    resolved.Add(point);
                    if (primary) s_pending.Add((at, point, InDips(at, sample.Pen)));
                }
                intermediate = resolved.ToArray();
            }

            PenState inDips = InDips(source, pen);
            device.SetSource(source);
            device.SetPosition(position, pen.Pressure);
            device.SetContactSize(SizeOf(inDips));
            device.SetIntermediate(intermediate);
            TrackPen(contactId, inDips, down: false);
            return device.ReportMove();
        }

        /// <summary>The contact size converted from device pixels into the source's DIPs.</summary>
        private static PenState InDips(PresentationSource source, in PenState pen)
        {
            if (!pen.HasContactSize) return pen;
            CompositionTarget target = source?.CompositionTarget;
            if (target is null) return pen;
            Vector size = target.TransformFromDevice.Transform(new Vector(pen.Width, pen.Height));
            return pen.WithContactSize(Math.Abs(size.X), Math.Abs(size.Y));
        }

        private static Size SizeOf(in PenState pen)
            => pen.HasContactSize ? new Size(pen.Width, pen.Height) : new Size(0, 0);

        public bool TouchUp(IntPtr windowHandle, int contactId, int screenX, int screenY, uint timestampMs)
        {
            if (!_contacts.TryGetValue(contactId, out PlatformTouchDevice device)) return false;

            // An up that carries no position lifts the contact where it was last seen. Repositioning
            // it to a placeholder would land the up somewhere the finger never was -- and a tap whose
            // down and up hit different elements is not a click.
            if (screenX != PlatformTouch.NoPosition && screenY != PlatformTouch.NoPosition &&
                TryResolve(windowHandle, screenX, screenY, out PresentationSource source, out Point position))
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

        public void TouchCancelAll(IntPtr windowHandle)
        {
            if (_contacts.Count == 0) return;

            // Copied first: Retire mutates the dictionary.
            var live = new List<KeyValuePair<int, PlatformTouchDevice>>(_contacts);
            foreach (KeyValuePair<int, PlatformTouchDevice> contact in live)
            {
                Retire(contact.Key, contact.Value, cancel: true);
            }
        }

        /// <summary>
        ///  Takes a contact out of service. A cancel deactivates WITHOUT reporting an up, which is
        ///  the difference that matters downstream: an up completes a tap and finishes a
        ///  manipulation, a cancel must do neither.
        /// </summary>
        private void Retire(int contactId, PlatformTouchDevice device, bool cancel)
        {
            _contacts.Remove(contactId);
            ForgetPressure(contactId);
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
        internal static bool TryResolve(IntPtr windowHandle, int screenX, int screenY,
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
