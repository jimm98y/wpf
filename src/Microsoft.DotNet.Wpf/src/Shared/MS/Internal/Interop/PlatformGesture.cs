// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;

namespace MS.Internal.Interop
{
    /// <summary>
    ///  The WPF side of a platform GESTURE -- a pinch or rotate the operating system recognised for
    ///  us, rather than the raw contacts <see cref="PlatformTouch"/> carries.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   This exists for macOS, and the distinction is not a stylistic one. A Mac has no touchscreen.
    ///   Its trackpad reports positions on the TRACKPAD, normalised 0..1, with no relationship to any
    ///   window -- so there is no honest way to turn them into contacts over elements, and a
    ///   TouchDevice built from them would report positions the user never touched. What AppKit does
    ///   give is the gesture it already recognised: magnifyWithEvent: and rotateWithEvent: carry a
    ///   magnification factor and a rotation, which is exactly the information a pinch conveys.
    ///  </para>
    ///  <para>
    ///   So macOS reports gestures and not touch, and PresentationCore turns them into an
    ///   IManipulator pair rather than into a TouchDevice. Manipulation.AddManipulator takes any
    ///   IManipulator, so this needs no fiction about fingers to work.
    ///  </para>
    ///  <para>
    ///   Points are in SCREEN device pixels, as everywhere else on these seams.
    ///  </para>
    /// </remarks>
    internal interface IPlatformGestureSink
    {
        /// <summary>
        ///  A gesture began at a point. The element under it, if any of them wants manipulation,
        ///  becomes the target for everything until the matching end.
        /// </summary>
        void GestureBegin(IntPtr windowHandle, int screenX, int screenY);

        /// <summary>
        ///  The gesture moved on.
        /// </summary>
        /// <param name="magnification">
        ///  Cumulative scale since the gesture began, where 1 is unchanged. AppKit reports each
        ///  magnifyWithEvent: as a DELTA to accumulate, so the backend does the accumulating and this
        ///  takes the total -- which is what keeps a pinch from drifting when an event is dropped.
        /// </param>
        /// <param name="rotationDegrees">Cumulative rotation since the gesture began.</param>
        void GestureUpdate(IntPtr windowHandle, int screenX, int screenY,
                           double magnification, double rotationDegrees);

        void GestureEnd(IntPtr windowHandle, bool cancel);
    }

    internal static class PlatformGesture
    {
        /// <summary>Installed by PresentationCore; null until a WPF window exists.</summary>
        internal static IPlatformGestureSink? Sink { get; set; }

        internal static bool IsAvailable => Sink is not null;
    }
}
