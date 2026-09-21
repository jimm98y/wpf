// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An IPlatformWindow whose every call happens on the process main thread.
//
// The tests drive the windowing head directly and read geometry back, and on macOS all of that is
// AppKit: -backingScaleFactor, -frame, -contentView, -close. AppKit is main-thread-only for the
// whole surface, not merely for -initWithContentRect:, so marshalling just the creation would swap
// a loud failure (a process abort at Create) for a quiet one (values read from a window while the
// window server is mutating it, on a thread AppKit never expected).
//
// Wrapping is what keeps that out of the tests. They hold an IPlatformWindow and call it exactly as
// they did when this suite skipped on macOS, so the assertions stay statements about the head rather
// than about threading -- and the same tests still run unwrapped on Linux, where none of this
// applies.
//

using System;
using MS.Internal.Interop;

namespace Wpf.Platform.Tests
{
    internal sealed class MainThreadWindow : IPlatformWindow
    {
        private readonly IPlatformWindow _inner;

        private MainThreadWindow(IPlatformWindow inner) => _inner = inner;

        /// <summary>
        /// Wraps <paramref name="window"/> if this platform needs the main thread, otherwise hands it
        /// back untouched -- Linux has no such rule and gains nothing but indirection from a wrapper.
        /// </summary>
        public static IPlatformWindow Wrap(IPlatformWindow window)
            => OperatingSystem.IsMacOS() ? new MainThreadWindow(window) : window;

        public bool IsBorderless => PlatformThread.InvokeOnMain(() => _inner.IsBorderless);

        public void SetContentSize(int width, int height)
            => PlatformThread.InvokeOnMain(() => _inner.SetContentSize(width, height));

        public void SetContentSizePixels(int cx, int cy)
            => PlatformThread.InvokeOnMain(() => _inner.SetContentSizePixels(cx, cy));

        public void SetFrameOrigin(int xPixels, int yPixels)
            => PlatformThread.InvokeOnMain(() => _inner.SetFrameOrigin(xPixels, yPixels));

        // The out parameters cannot cross the lambda, so each of these returns a tuple and unpacks it
        // on this side.
        public void GetContentSize(out int width, out int height)
        {
            (width, height) = PlatformThread.InvokeOnMain(() =>
            {
                _inner.GetContentSize(out int w, out int h);
                return (w, h);
            });
        }

        public void GetPixelSize(out int width, out int height)
        {
            (width, height) = PlatformThread.InvokeOnMain(() =>
            {
                _inner.GetPixelSize(out int w, out int h);
                return (w, h);
            });
        }

        public void GetWindowPixelSize(out int width, out int height)
        {
            (width, height) = PlatformThread.InvokeOnMain(() =>
            {
                _inner.GetWindowPixelSize(out int w, out int h);
                return (w, h);
            });
        }

        public void GetClientScreenOriginPixels(out int sx, out int sy)
        {
            (sx, sy) = PlatformThread.InvokeOnMain(() =>
            {
                _inner.GetClientScreenOriginPixels(out int x, out int y);
                return (x, y);
            });
        }

        public double GetBackingScale() => PlatformThread.InvokeOnMain(() => _inner.GetBackingScale());

        public double GetRefreshRateHz() => PlatformThread.InvokeOnMain(() => _inner.GetRefreshRateHz());

        public void SetVisible(bool visible)
            => PlatformThread.InvokeOnMain(() => _inner.SetVisible(visible));

        public void SetVisible(bool visible, bool activate)
            => PlatformThread.InvokeOnMain(() => _inner.SetVisible(visible, activate));

        public void Activate() => PlatformThread.InvokeOnMain(() => _inner.Activate());

        public void SetWindowState(int state)
            => PlatformThread.InvokeOnMain(() => _inner.SetWindowState(state));

        // Forwarded rather than inherited. IPlatformWindow gives GetWindowState a default body, so a
        // wrapper that does not override it answers SW_NORMAL on its own behalf and never asks the
        // window it wraps -- which would make the state test pass for the wrong reason on a head that
        // reports nothing, and fail on one that does.
        public int GetWindowState() => PlatformThread.InvokeOnMain(() => _inner.GetWindowState());

        public void BeginMoveDrag() => PlatformThread.InvokeOnMain(() => _inner.BeginMoveDrag());

        public void Destroy() => PlatformThread.InvokeOnMain(() => _inner.Destroy());

        // Raised by the head on the pump thread; nothing to marshal, only to forward.
        public event Action<double> ScaleChanged
        {
            add => _inner.ScaleChanged += value;
            remove => _inner.ScaleChanged -= value;
        }
    }
}
