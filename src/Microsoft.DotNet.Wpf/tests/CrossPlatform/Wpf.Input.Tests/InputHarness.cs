// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A real window, on a real screen, driven by real operating-system input.
//
// Touch and pen cannot be tested the way the rest of this repo tests things. There is no seam to
// call: WPF's touch events are the far end of a chain that starts in the digitizer driver, becomes
// WM_POINTER messages in a window's queue, and only then enters WPF. Constructing the input objects
// directly would assert that the last link works while leaving the first three -- the ones this port
// actually changed -- uncovered.
//
// So these tests inject contacts into the system's own pointer pipeline (InjectTouchInput /
// InjectSyntheticPointerInput) aimed at a window that is genuinely on screen, and read back what
// WPF raised. Three consequences shape this file:
//
//   * The window must be VISIBLE and at a known screen position. Injection is by screen coordinate
//     and hit-tests like the mouse does, so an off-screen window (the trick the accessibility suite
//     uses) receives nothing at all.
//   * Somebody has to pump. Injected messages land in the queue asynchronously and arrive after the
//     injecting call returns, so every step is followed by a pumped wait rather than an assertion
//     on the next line.
//   * ONE dispatcher for the whole assembly. A test that spins up its own STA thread, shows a
//     window and lets the thread exit leaves a shut-down Dispatcher behind, and the next window
//     built in that process comes up on a half-initialised compositor. A single long-lived UI
//     thread that every test marshals onto avoids the whole class of problem.
//

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wpf.Input.Tests
{
    /// <summary>
    /// The single STA UI thread every test in this assembly runs its WPF work on.
    /// </summary>
    internal static class UiThread
    {
        private static Dispatcher? s_dispatcher;
        private static readonly object s_gate = new object();

        /// <summary>
        /// Adopts a dispatcher owned by another thread instead of creating one.
        /// </summary>
        /// <remarks>
        /// Called by the entry point on macOS, where AppKit aborts the process if a window is
        /// created anywhere but the PROCESS MAIN THREAD -- and the test runner does not run tests
        /// there. Program.Main keeps the main thread pumping and hands its dispatcher here, so every
        /// UiThread.Invoke lands on the one thread Cocoa will accept a window from.
        /// </remarks>
        public static void UseDispatcher(Dispatcher dispatcher)
        {
            lock (s_gate)
            {
                s_dispatcher = dispatcher;
            }
        }

        private static Dispatcher Dispatcher
        {
            get
            {
                lock (s_gate)
                {
                    if (s_dispatcher is not null) return s_dispatcher;

                    using var ready = new ManualResetEventSlim();
                    var thread = new Thread(() =>
                    {
                        s_dispatcher = Dispatcher.CurrentDispatcher;
                        ready.Set();
                        Dispatcher.Run();
                    })
                    {
                        IsBackground = true,
                        Name = "WPF input tests UI thread",
                    };

                    // STA is a COM concept and COM exists only on Windows; SetApartmentState throws
                    // PlatformNotSupportedException elsewhere. WPF's own thread affinity is the
                    // Dispatcher's, not the apartment's, so off Windows the default apartment is fine.
                    if (OperatingSystem.IsWindows())
                    {
                        thread.SetApartmentState(ApartmentState.STA);
                    }

                    thread.Start();
                    ready.Wait();
                    return s_dispatcher!;
                }
            }
        }

        /// <summary>Runs <paramref name="body"/> on the UI thread and rethrows whatever it threw.</summary>
        public static T Invoke<T>(Func<T> body)
        {
            T result = default!;
            Exception? failure = null;

            Dispatcher.Invoke(() =>
            {
                try { result = body(); }
                catch (Exception e) { failure = e; }
            });

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        public static void Invoke(Action body) => Invoke<object?>(() => { body(); return null; });
    }

    /// <summary>
    /// A shown window whose content is one hit-testable surface, plus the screen geometry needed to
    /// aim injected contacts at it. Built and used on the UI thread.
    /// </summary>
    internal sealed class InputWindow : IDisposable
    {
        public Window Window { get; }

        /// <summary>The element under test: fills the window and is hit-testable.</summary>
        public Border Surface { get; }

        private InputWindow(Window window, Border surface)
        {
            Window = window;
            Surface = surface;
        }

        /// <summary>
        /// Shows a window at a fixed screen position and waits until it is actually up. Topmost and
        /// activated deliberately: injected contacts hit-test against whatever window is frontmost at
        /// the point, so a window behind someone else's would silently deliver the input elsewhere.
        /// </summary>
        public static InputWindow Show(double left = 80, double top = 80, double width = 460, double height = 340)
            => UiThread.Invoke(() =>
            {
                // A Background is what makes a panel hit-testable at all: a null brush is not "invisible
                // but present", it is absent from the hit-test, and every touch would fall through to
                // the window with no element under it.
                var surface = new Border { Background = Brushes.White };

                var window = new Window
                {
                    Title = "input probe",
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = left,
                    Top = top,
                    Width = width,
                    Height = height,
                    Content = surface,
                    Topmost = true,
                    ShowInTaskbar = false,
                };
                window.Show();

                // Activate is SetForegroundWindow, which is user32 and therefore Windows only. It
                // matters for the suites that inject into the OS pointer queue -- those contacts
                // hit-test against whatever window is frontmost -- and not at all for the ones that
                // call the platform touch seam directly, which is the only kind that can run
                // anywhere else.
                if (OperatingSystem.IsWindows())
                {
                    window.Activate();
                }

                var probe = new InputWindow(window, surface);
                probe.Pump(TimeSpan.FromMilliseconds(200));
                return probe;
            });

        public IntPtr Handle => UiThread.Invoke(() => new WindowInteropHelper(Window).Handle);

        /// <summary>
        /// A point on the surface, in SCREEN PIXELS, ready to inject. PointToScreen is what makes this
        /// DPI-correct: the injection API speaks physical pixels while everything in WPF above the
        /// HWND speaks device-independent units, and on a scaled display the two differ by the scale
        /// factor -- a mismatch that lands the contact in the wrong place rather than failing loudly.
        /// </summary>
        public (int X, int Y) SurfacePointToScreen(double relativeX, double relativeY)
            => UiThread.Invoke(() =>
            {
                Point screen = Surface.PointToScreen(new Point(relativeX, relativeY));
                return ((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            });

        /// <summary>The centre of the surface, in screen pixels.</summary>
        public (int X, int Y) Centre
            => UiThread.Invoke(() => SurfacePointToScreen(Surface.ActualWidth / 2, Surface.ActualHeight / 2));

        /// <summary>
        /// Pumps the message loop for a fixed span. Used after injecting, because injected input
        /// arrives asynchronously: the inject call returns before the message reaches the queue.
        /// </summary>
        public void Pump(TimeSpan duration) => UiThread.Invoke(() => PumpCore(duration, null));

        /// <summary>
        /// Pumps until <paramref name="done"/> holds or <paramref name="timeout"/> elapses. Returns
        /// whether it held -- callers assert on the events they collected, not on this, so that a
        /// failure reports what did arrive rather than just "timed out".
        /// </summary>
        public bool PumpUntil(Func<bool> done, TimeSpan timeout) => UiThread.Invoke(() => PumpCore(timeout, done));

        private static bool PumpCore(TimeSpan timeout, Func<bool>? done)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                // PushFrame runs the real Win32 message loop, which is the only thing that will
                // dequeue the injected WM_POINTER messages. The frame exits as soon as a
                // background-priority item runs, i.e. once the queue has been drained to idle.
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                if (done is not null && done()) return true;
                if (done is null && clock.Elapsed >= timeout) return true;

                Thread.Sleep(5);
            }
            while (clock.Elapsed < timeout);

            return done is null || done();
        }

        public void Dispose() => UiThread.Invoke(() => Window.Close());
    }
}
