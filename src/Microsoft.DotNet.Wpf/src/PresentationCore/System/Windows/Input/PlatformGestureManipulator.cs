// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Manipulation from a platform-recognised gesture, for heads with no touchscreen.
//
// The touch heads need nothing here: a TouchDevice is an IManipulator and promotes its own contacts,
// so pinch and rotate follow from delivering them. macOS cannot do that. A Mac has no touchscreen,
// and its trackpad reports positions on the TRACKPAD rather than on the window, so there is no
// honest contact to report. What AppKit does hand over is the gesture it already recognised -- a
// magnification factor and a rotation -- and that is what this turns into manipulation.
//
// The representation is a PAIR of manipulators. A single one can only ever express translation:
// scale is a change in the distance between two points, and rotation a change in the angle between
// them, so one point cannot carry either no matter what it reports. Two synthetic manipulators
// placed symmetrically about the gesture's centre, with their separation scaled by the magnification
// and their axis turned by the rotation, give WPF's own manipulation processor exactly the geometry
// a real two-finger gesture would -- which is, after all, what the user's two fingers are doing on
// the trackpad.
//

using MS.Internal.Interop;
using System.Windows.Media;

namespace System.Windows.Input
{
    /// <summary>One of the two synthetic contacts a platform gesture is modelled as.</summary>
    internal sealed class PlatformGestureManipulator : IManipulator
    {
        private Point _position;

        internal PlatformGestureManipulator(int id) => Id = id;

        public int Id { get; }

        public event EventHandler Updated;

        /// <summary>Moves the manipulator and tells the manipulation processor to re-read it.</summary>
        internal void MoveTo(Point position)
        {
            _position = position;
            Updated?.Invoke(this, EventArgs.Empty);
        }

        public Point GetPosition(IInputElement relativeTo)
        {
            if (relativeTo is null) return _position;

            var visual = relativeTo as Visual;
            return visual is null
                ? _position
                : s_root is null ? _position : s_root.TransformToDescendant(visual)?.Transform(_position) ?? _position;
        }

        public void ManipulationEnded(bool cancel)
        {
        }

        /// <summary>
        ///  The visual the positions above are expressed in. Set by the sink for the duration of a
        ///  gesture: GetPosition is asked for coordinates relative to arbitrary elements, and without
        ///  the root to transform from there is nothing to convert.
        /// </summary>
        internal static Visual s_root;
    }

    /// <summary>
    ///  Turns platform gestures into manipulation on the element under them.
    /// </summary>
    internal sealed class PlatformGestureSink : IPlatformGestureSink
    {
        private static PlatformGestureSink s_instance;

        // How far apart the two synthetic manipulators sit at magnification 1, in root units. The
        // value is arbitrary in itself -- only the RATIO between the current and the initial spacing
        // reaches ManipulationDelta.Scale -- but it has to be large enough that the processor does
        // not treat the pair as one point and small enough to stay inside a typical element.
        private const double RestingSpan = 100.0;

        private PlatformGestureManipulator _first, _second;
        private UIElement _target;
        private Point _origin;

        internal static void Install()
        {
            if (s_instance is not null) return;
            s_instance = new PlatformGestureSink();
            PlatformGesture.Sink = s_instance;
        }

        public void GestureBegin(IntPtr windowHandle, int screenX, int screenY)
        {
            End(cancel: true);

            if (!PlatformTouchSink.TryResolve(windowHandle, screenX, screenY,
                                              out PresentationSource source, out Point position))
            {
                return;
            }

            _target = FindManipulationTarget(source, position);
            if (_target is null) return;   // nothing here wants a manipulation; leave it to scrolling

            PlatformGestureManipulator.s_root = source.RootVisual as Visual;
            _origin = position;

            _first = new PlatformGestureManipulator(1);
            _second = new PlatformGestureManipulator(2);
            Place(magnification: 1, rotationDegrees: 0, centre: position);

            Manipulation.AddManipulator(_target, _first);
            Manipulation.AddManipulator(_target, _second);
        }

        public void GestureUpdate(IntPtr windowHandle, int screenX, int screenY,
                                  double magnification, double rotationDegrees)
        {
            if (_target is null) return;
            if (!PlatformTouchSink.TryResolve(windowHandle, screenX, screenY, out _, out Point centre))
            {
                centre = _origin;
            }

            Place(magnification, rotationDegrees, centre);
        }

        public void GestureEnd(IntPtr windowHandle, bool cancel) => End(cancel);

        /// <summary>
        ///  Puts the pair on a line through <paramref name="centre"/>, turned by the rotation and
        ///  half the scaled span out on either side.
        /// </summary>
        private void Place(double magnification, double rotationDegrees, Point centre)
        {
            if (_first is null || _second is null) return;

            // A non-positive magnification would collapse the pair onto each other and make the
            // scale undefined; AppKit does not report one, but a backend accumulating deltas could
            // arrive at it.
            if (!(magnification > 0.01)) magnification = 0.01;

            double half = RestingSpan * magnification / 2;
            double radians = rotationDegrees * Math.PI / 180.0;
            double dx = Math.Cos(radians) * half;
            double dy = Math.Sin(radians) * half;

            _first.MoveTo(new Point(centre.X - dx, centre.Y - dy));
            _second.MoveTo(new Point(centre.X + dx, centre.Y + dy));
        }

        private void End(bool cancel)
        {
            if (_target is not null && _first is not null)
            {
                Manipulation.RemoveManipulator(_target, _first);
                Manipulation.RemoveManipulator(_target, _second);
            }

            _first = _second = null;
            _target = null;
            PlatformGestureManipulator.s_root = null;
        }

        /// <summary>
        ///  The nearest element at the point that asked for manipulation. Nothing is invented if none
        ///  did: a pinch over a plain ScrollViewer should keep scrolling rather than silently become
        ///  a manipulation nobody opted into.
        /// </summary>
        private static UIElement FindManipulationTarget(PresentationSource source, Point position)
        {
            if (source.RootVisual is not Visual root) return null;

            HitTestResult hit = VisualTreeHelper.HitTest(root, position);
            DependencyObject node = hit?.VisualHit;

            while (node is not null)
            {
                if (node is UIElement element && element.IsManipulationEnabled) return element;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }
    }
}
