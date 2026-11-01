// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Press and hold: a finger's right button, on every head whose touches drive the mouse (iOS, Android).
//
// A finger has one button, and a context menu wants the other. Windows turns a press held still for a
// moment into a right click, and does not give the press to the left button until it is known not to
// be one -- else the control under it would take a click first. So a touch's press waits: moved past
// the slop, or lifted before the hold time, it goes as the left press it was (where it began); held
// still, it becomes the right button's press and release as soon as the hold time has passed, and the
// rest of that touch is dropped. The pointer itself moves at once (hover, the cursor), only the press
// waits.
//

using System;

namespace MS.Internal.Interop
{
    internal sealed class TouchHold
    {
        /// <summary>The touch kinds of the right button (the mouse kinds 0 move, 1 press, 2 release, 3 wheel
        /// beside them).</summary>
        public const int RightPress = 5, RightRelease = 6;

        private const int HoldMilliseconds = 600;
        private const double SlopPoints = 10;

        private readonly Action<IntPtr, int, double, double> _emit;   // window, kind, x, y (device pixels)
        private readonly Action<Action> _post;                         // runs work on the UI thread, later
        private readonly Func<IntPtr, bool> _alive;

        private bool _pending, _fired;
        private IntPtr _window;
        private double _x, _y, _slop;
        private int _serial;
        private System.Threading.Timer? _timer;

        public TouchHold(Action<IntPtr, int, double, double> emit, Action<Action> post, Func<IntPtr, bool> alive)
        {
            _emit = emit;
            _post = post;
            _alive = alive;
        }

        /// <summary>A touch of the mouse's finger: <paramref name="kind"/> 0 move, 1 press, 2 release (a
        /// cancel as a release), at device pixels of <paramref name="window"/>; <paramref name="scale"/>
        /// pixels to a point. UI thread.</summary>
        public void Touch(IntPtr window, int kind, double x, double y, double scale)
        {
            switch (kind)
            {
                case 1:
                    _pending = true;
                    _fired = false;
                    _window = window;
                    _x = x; _y = y;
                    _slop = SlopPoints * Math.Max(scale, 0.01);
                    int serial = ++_serial;
                    _timer?.Dispose();
                    _timer = new System.Threading.Timer(_ => _post(() => Elapsed(serial)), null, HoldMilliseconds, System.Threading.Timeout.Infinite);
                    _emit(window, 0, x, y);
                    return;
                case 0:
                    if (_fired) return;
                    if (_pending)
                    {
                        if (Math.Abs(x - _x) < _slop && Math.Abs(y - _y) < _slop) return;
                        ReleasePress();
                    }
                    _emit(window, 0, x, y);
                    return;
                case 2:
                    _timer?.Dispose();
                    _timer = null;
                    if (_fired) { _fired = false; return; }
                    if (_pending) ReleasePress();
                    _emit(window, 2, x, y);
                    return;
                default:
                    _emit(window, kind, x, y);
                    return;
            }
        }

        /// <summary>The held press goes as the left button's, where the touch began.</summary>
        private void ReleasePress()
        {
            _pending = false;
            _timer?.Dispose();
            _timer = null;
            if (_alive(_window)) _emit(_window, 1, _x, _y);
        }

        private void Elapsed(int serial)
        {
            if (serial != _serial || !_pending || !_alive(_window)) return;
            _pending = false;
            _fired = true;
            _emit(_window, RightPress, _x, _y);
            _emit(_window, RightRelease, _x, _y);
        }
    }
}
