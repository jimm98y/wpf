// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Synthesizing digitizer input at the operating-system level.
//
// Windows exposes two injection APIs and this file uses both, because they cover different halves
// of what has to be proved:
//
//   * InjectTouchInput -- finger contacts, up to the count declared at initialisation. This is the
//     one that produces genuine multi-touch: several contacts in a single call become one pointer
//     frame, which is what WPF needs to see before it will raise a second TouchDevice.
//   * InjectSyntheticPointerInput over a CreateSyntheticPointerDevice of type PT_PEN -- a pen,
//     with pressure, tilt, barrel button and eraser. InjectTouchInput cannot express any of those;
//     it only knows contacts.
//
// Both are ordinary user32 entry points available to a normal desktop process. Neither needs
// hardware, a driver, or elevation -- but both need an interactive session, so every entry point
// here reports failure rather than throwing, and the tests skip when injection is unavailable.
//
// The protocol that is easy to get wrong: a contact must be RE-INJECTED to stay down. Windows ages
// out an injected pointer that stops being updated, so a "press, wait, release" sequence written as
// three calls with a sleep in the middle delivers a down and then a cancel. Hold() exists for that.
//

using System;
using System.Runtime.InteropServices;

namespace Wpf.Input.Tests
{
    /// <summary>One finger, as the test describes it: where it is and whether it is touching.</summary>
    internal readonly record struct Contact(uint Id, int X, int Y);

    /// <summary>
    /// Finger injection. Contacts are addressed by id; the injector remembers which are currently
    /// down, because every InjectTouchInput call must carry the complete set.
    /// </summary>
    internal sealed class TouchInjector : IDisposable
    {
        private readonly System.Collections.Generic.Dictionary<uint, Contact> _down = new();
        private bool _initialized;

        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>
        /// Prepares the injection device. False means this machine or session will not accept
        /// injected touch, which is a reason to skip a test rather than fail it.
        /// </summary>
        public bool TryInitialize(uint maxContacts = 10)
        {
            if (!IsSupported) return false;
            if (_initialized) return true;

            // Initialising twice in a process fails with ERROR_ALREADY_INITIALIZED; a suite that
            // creates an injector per test would trip over that, so treat it as success.
            if (!InitializeTouchInjection(maxContacts, TOUCH_FEEDBACK_NONE))
            {
                const int ERROR_ALREADY_INITIALIZED = 1247;
                if (Marshal.GetLastWin32Error() != ERROR_ALREADY_INITIALIZED) return false;
            }

            return _initialized = true;
        }

        /// <summary>Puts contacts down. They stay down until <see cref="Up"/> or <see cref="Dispose"/>.</summary>
        public bool Down(params Contact[] contacts)
        {
            foreach (Contact c in contacts) _down[c.Id] = c;
            return Send(newlyDown: contacts);
        }

        /// <summary>Moves contacts that are already down.</summary>
        public bool Move(params Contact[] contacts)
        {
            foreach (Contact c in contacts) _down[c.Id] = c;
            return Send();
        }

        /// <summary>
        /// Re-sends the current contacts unchanged. Needed while nothing is moving: an injected
        /// pointer that is not refreshed is aged out by the system and arrives at the window as a
        /// cancel, which reads exactly like a bug in WPF.
        /// </summary>
        public bool Hold() => _down.Count > 0 && Send();

        /// <summary>Lifts contacts.</summary>
        public bool Up(params uint[] ids)
        {
            var lifting = new System.Collections.Generic.List<POINTER_TOUCH_INFO>();
            foreach (uint id in ids)
            {
                if (!_down.TryGetValue(id, out Contact c)) continue;
                lifting.Add(MakeContact(c, POINTER_FLAG_UP));
                _down.Remove(id);
            }

            // The lift and whatever remains down go in ONE frame: a call that omitted the still-down
            // contacts would tell Windows they had vanished.
            foreach (Contact c in _down.Values)
            {
                lifting.Add(MakeContact(c, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT));
            }

            return lifting.Count > 0 && InjectTouchInput((uint)lifting.Count, lifting.ToArray());
        }

        /// <summary>A complete one-finger tap, with the down and up in separate frames.</summary>
        public bool Tap(int x, int y)
            => Down(new Contact(0, x, y)) && Up(0);

        private bool Send(Contact[]? newlyDown = null)
        {
            var frame = new POINTER_TOUCH_INFO[_down.Count];
            int i = 0;
            foreach (Contact c in _down.Values)
            {
                bool isNew = false;
                if (newlyDown is not null)
                {
                    foreach (Contact n in newlyDown) if (n.Id == c.Id) { isNew = true; break; }
                }

                frame[i++] = MakeContact(
                    c,
                    (isNew ? POINTER_FLAG_DOWN : POINTER_FLAG_UPDATE) | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
            }

            return frame.Length > 0 && InjectTouchInput((uint)frame.Length, frame);
        }

        private static POINTER_TOUCH_INFO MakeContact(Contact c, uint flags) => new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = PT_TOUCH,
                pointerId = c.Id,
                ptPixelLocation = new POINT { x = c.X, y = c.Y },
                pointerFlags = flags,
            },
            touchFlags = 0,
            touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE,
            // A contact rectangle rather than a point: WPF reports it as the touch bounds, and a
            // zero-area rect is how "the digitizer told us nothing" looks.
            rcContact = new RECT { left = c.X - 2, top = c.Y - 2, right = c.X + 2, bottom = c.Y + 2 },
            orientation = 90,
            pressure = 32000,
        };

        public void Dispose()
        {
            if (_down.Count == 0) return;

            var ids = new uint[_down.Count];
            _down.Keys.CopyTo(ids, 0);
            Up(ids);
        }

        internal const uint PT_TOUCH = 2, PT_PEN = 3;
        internal const uint POINTER_FLAG_NONE = 0x00000000;
        internal const uint POINTER_FLAG_INRANGE = 0x00000002, POINTER_FLAG_INCONTACT = 0x00000004;
        internal const uint POINTER_FLAG_DOWN = 0x00010000, POINTER_FLAG_UPDATE = 0x00020000, POINTER_FLAG_UP = 0x00040000;
        private const uint TOUCH_FEEDBACK_NONE = 3;
        private const uint TOUCH_MASK_CONTACTAREA = 0x1, TOUCH_MASK_ORIENTATION = 0x2, TOUCH_MASK_PRESSURE = 0x4;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InjectTouchInput(uint count, POINTER_TOUCH_INFO[] contacts);
    }

    /// <summary>
    /// Pen injection, through a synthetic pointer device. Separate from <see cref="TouchInjector"/>
    /// because the API is different in kind: a device handle is created up front and every packet
    /// carries the full pen state (pressure, tilt, buttons) rather than just a contact rectangle.
    /// </summary>
    internal sealed class PenInjector : IDisposable
    {
        private IntPtr _device = IntPtr.Zero;

        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>
        /// Creates the synthetic pen. False means the API is unavailable (it arrived in Windows 10
        /// 1809) or the session refuses injection -- skip, do not fail.
        /// </summary>
        public bool TryInitialize()
        {
            if (!IsSupported) return false;
            if (_device != IntPtr.Zero) return true;

            try
            {
                _device = CreateSyntheticPointerDevice(TouchInjector.PT_PEN, 1, POINTER_FEEDBACK_NONE);
            }
            catch (EntryPointNotFoundException) { return false; }
            catch (DllNotFoundException) { return false; }

            return _device != IntPtr.Zero;
        }

        /// <summary>
        /// Brings the pen into hover range above the surface, not touching it. The eraser end is
        /// declared HERE rather than at the press: a real pen reports which end is toward the glass
        /// from the moment it comes in range, and Windows treats a pen that flips over mid-stream as
        /// a state change it will not honour, so pressing "with the eraser" after hovering with the
        /// nib delivers a plain nib press.
        /// </summary>
        public bool Hover(int x, int y, bool eraser = false)
            => Send(x, y, TouchInjector.POINTER_FLAG_INRANGE, pressure: 0, tiltX: 0, tiltY: 0,
                    eraser ? PEN_FLAG_INVERTED : PEN_FLAG_NONE);

        /// <summary>Presses the tip, with a pressure in the digitizer's 0..1024 range and tilt in degrees.</summary>
        public bool Down(int x, int y, uint pressure = 512, int tiltX = 0, int tiltY = 0, bool barrelButton = false, bool eraser = false)
            => Send(x, y,
                    TouchInjector.POINTER_FLAG_DOWN | TouchInjector.POINTER_FLAG_INRANGE | TouchInjector.POINTER_FLAG_INCONTACT,
                    pressure, tiltX, tiltY, PenFlags(barrelButton, eraser));

        /// <summary>Moves or re-states the pressed pen. Also the way to keep it from ageing out.</summary>
        public bool Move(int x, int y, uint pressure = 512, int tiltX = 0, int tiltY = 0, bool barrelButton = false, bool eraser = false)
            => Send(x, y,
                    TouchInjector.POINTER_FLAG_UPDATE | TouchInjector.POINTER_FLAG_INRANGE | TouchInjector.POINTER_FLAG_INCONTACT,
                    pressure, tiltX, tiltY, PenFlags(barrelButton, eraser));

        /// <summary>Lifts the tip but stays in range, as a real pen does when you raise it slightly.</summary>
        public bool Up(int x, int y, bool eraser = false)
            => Send(x, y, TouchInjector.POINTER_FLAG_UP | TouchInjector.POINTER_FLAG_INRANGE, pressure: 0, tiltX: 0, tiltY: 0, PenFlags(false, eraser));

        /// <summary>Takes the pen out of range entirely.</summary>
        public bool Leave(int x, int y)
            => Send(x, y, TouchInjector.POINTER_FLAG_NONE, pressure: 0, tiltX: 0, tiltY: 0, PEN_FLAG_NONE);

        // INVERTED means "the pen is turned over"; ERASER means "the eraser end is touching". They are
        // not interchangeable: a flipped pen reports INVERTED the whole time it is in range and adds
        // ERASER only once it makes contact.
        private static uint PenFlags(bool barrelButton, bool eraser)
            => (barrelButton ? PEN_FLAG_BARREL : 0u) | (eraser ? PEN_FLAG_INVERTED | PEN_FLAG_ERASER : 0u);

        private bool Send(int x, int y, uint pointerFlags, uint pressure, int tiltX, int tiltY, uint penFlags)
        {
            if (_device == IntPtr.Zero) return false;

            var info = new POINTER_TYPE_INFO
            {
                type = TouchInjector.PT_PEN,
                info = new POINTER_TYPE_UNION
                {
                    penInfo = new POINTER_PEN_INFO
                    {
                        pointerInfo = new POINTER_INFO
                        {
                            pointerType = TouchInjector.PT_PEN,
                            pointerId = 0,
                            ptPixelLocation = new POINT { x = x, y = y },
                            pointerFlags = pointerFlags,
                        },
                        penFlags = penFlags,
                        penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y,
                        pressure = pressure,
                        rotation = 0,
                        tiltX = tiltX,
                        tiltY = tiltY,
                    },
                },
            };

            return InjectSyntheticPointerInput(_device, new[] { info }, 1);
        }

        public void Dispose()
        {
            if (_device == IntPtr.Zero) return;
            DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }

        private const uint POINTER_FEEDBACK_NONE = 3;
        private const uint PEN_FLAG_NONE = 0x0, PEN_FLAG_BARREL = 0x1, PEN_FLAG_INVERTED = 0x2, PEN_FLAG_ERASER = 0x4;
        private const uint PEN_MASK_PRESSURE = 0x1, PEN_MASK_TILT_X = 0x4, PEN_MASK_TILT_Y = 0x8;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InjectSyntheticPointerInput(IntPtr device, POINTER_TYPE_INFO[] pointerInfo, uint count);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void DestroySyntheticPointerDevice(IntPtr device);
    }

    // ---- The shapes user32 expects ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    // A union in C. Laid out explicitly so both arms start where the native header puts them --
    // offset 0 of the union, which Sequential then places at 8 because POINTER_INFO holds pointers.
    [StructLayout(LayoutKind.Explicit)]
    internal struct POINTER_TYPE_UNION
    {
        [FieldOffset(0)] public POINTER_TOUCH_INFO touchInfo;
        [FieldOffset(0)] public POINTER_PEN_INFO penInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_TYPE_INFO
    {
        public uint type;
        public POINTER_TYPE_UNION info;
    }
}
