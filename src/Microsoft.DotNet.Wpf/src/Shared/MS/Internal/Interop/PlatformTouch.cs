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
    internal interface IPlatformTouchSink
    {
        /// <summary>A contact touched down. Returns true if WPF handled it.</summary>
        /// <param name="pressure">
        ///  0..1, or a negative value where the device does not report pressure. A finger normally
        ///  does not; a pen does.
        /// </param>
        bool TouchDown(IntPtr windowHandle, int contactId, int screenX, int screenY, double pressure, uint timestampMs);

        bool TouchMove(IntPtr windowHandle, int contactId, int screenX, int screenY, double pressure, uint timestampMs);

        bool TouchUp(IntPtr windowHandle, int contactId, int screenX, int screenY, uint timestampMs);

        /// <summary>
        ///  The platform took the contact away -- the compositor started a gesture, the app lost the
        ///  surface, the touch was rejected as a palm. Distinct from an up: no tap or click should
        ///  follow, and any manipulation in progress is abandoned rather than completed.
        /// </summary>
        void TouchCancel(IntPtr windowHandle, int contactId);
    }

    internal static class PlatformTouch
    {
        /// <summary>Installed by PresentationCore; null until a WPF window exists.</summary>
        internal static IPlatformTouchSink? Sink { get; set; }

        /// <summary>
        ///  True when a touch source has somewhere to deliver to. A backend checks this before doing
        ///  the work of decoding a touch event, and -- more importantly -- to decide whether it must
        ///  still fall back to synthesizing mouse input for a head that has no sink yet.
        /// </summary>
        internal static bool IsAvailable => Sink is not null;
    }
}
