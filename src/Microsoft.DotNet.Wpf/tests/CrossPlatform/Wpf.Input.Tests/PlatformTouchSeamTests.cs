// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Touch delivered through the PLATFORM SEAM, which is how every head except Windows gets it.
//
// The sibling suites in this project inject into the operating system's own pointer pipeline, which
// is the right test for the Windows path and impossible everywhere else -- there is no digitizer on
// a CI box and no InjectTouchInput outside Windows. These tests take the other half of the chain:
// they call MS.Internal.Interop.PlatformTouch.Sink exactly as a windowing backend does, and assert
// on what WPF raised. That covers everything above the backend -- contact tracking, hit-testing,
// the Touch events, promotion to Manipulation -- on any machine, which is what makes it possible to
// develop the Linux, Android, iPadOS and WebAssembly sources at all.
//
// What it deliberately does NOT cover is a backend's own decoding: whether wl_touch's frame
// semantics or a MotionEvent's pointer indices are read correctly is a question only a real device
// can answer. Those get their own tests per head.
//
// The seam is internal to WindowsBase, so it is reached by reflection rather than by making it
// public for the tests' benefit.
//

using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace Wpf.Input.Tests
{
    public class PlatformTouchSeamTests
    {
        [Fact]
        public void AContactRaisesTouchDownMoveAndUp()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                var seen = new System.Collections.Generic.List<string>();
                scope.Target.TouchDown += (s, e) => seen.Add("down");
                scope.Target.TouchMove += (s, e) => seen.Add("move");
                scope.Target.TouchUp += (s, e) => seen.Add("up");

                scope.Down(1, scope.CentreOfTarget);
                scope.Move(1, scope.CentreOfTarget + new Vector(0, 10));
                scope.Up(1, scope.CentreOfTarget + new Vector(0, 10));

                Assert.Equal(new[] { "down", "move", "up" }, seen);
            });
        }

        /// <summary>
        ///  The contact lands on the element under it, which is the whole point of routing through
        ///  the source's coordinate space rather than reporting raw screen pixels.
        /// </summary>
        [Fact]
        public void AContactHitTestsToTheElementUnderIt()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                object over = null;
                scope.Target.TouchDown += (s, e) => over = e.OriginalSource;

                scope.Down(1, scope.CentreOfTarget);
                scope.Up(1, scope.CentreOfTarget);

                Assert.Same(scope.Target, over);
            });
        }

        /// <summary>
        ///  Two contacts at once, each tracked separately. This is the line between real touch and
        ///  the single-contact mouse emulation the mobile heads shipped before: the second finger
        ///  used to be dropped entirely.
        /// </summary>
        [Fact]
        public void TwoContactsAreTrackedIndependently()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                var down = new System.Collections.Generic.List<int>();
                scope.Target.TouchDown += (s, e) => down.Add(e.TouchDevice.Id);

                scope.Down(7, scope.CentreOfTarget + new Vector(-20, 0));
                scope.Down(9, scope.CentreOfTarget + new Vector(20, 0));
                scope.Up(7, scope.CentreOfTarget + new Vector(-20, 0));
                scope.Up(9, scope.CentreOfTarget + new Vector(20, 0));

                Assert.Equal(2, down.Count);
                Assert.NotEqual(down[0], down[1]);
            });
        }

        /// <summary>
        ///  A cancelled contact must not complete: no up, so no tap and no finished manipulation.
        /// </summary>
        [Fact]
        public void ACancelledContactRaisesNoUp()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                bool up = false;
                scope.Target.TouchUp += (s, e) => up = true;

                scope.Down(1, scope.CentreOfTarget);
                scope.Cancel(1);

                Assert.False(up, "a cancelled contact reported an up");
            });
        }

        /// <summary>
        ///  An up that carries no position lifts the contact where it was last seen.
        /// </summary>
        /// <remarks>
        ///  wl_touch.up names only the contact id, so Linux has nothing to pass. Sending a
        ///  placeholder instead would move the contact to the screen corner on the way up, and a tap
        ///  whose down and up land on different elements is not a click -- so this is the difference
        ///  between "buttons work on Linux" and "buttons never fire".
        /// </remarks>
        [Fact]
        public void AnUpWithNoPositionKeepsTheContactWhereItWas()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                object downOver = null, upOver = null;
                scope.Target.TouchDown += (s, e) => downOver = e.OriginalSource;
                scope.Target.TouchUp += (s, e) => upOver = e.OriginalSource;

                scope.Down(1, scope.CentreOfTarget);
                scope.UpWithoutPosition(1);

                Assert.Same(scope.Target, downOver);
                Assert.Same(scope.Target, upOver);
            });
        }

        /// <summary>
        ///  Manipulation, which is the reason the seam matters beyond touch itself: TouchDevice is an
        ///  IManipulator and promotes its own contacts, so a head that reports contacts accurately
        ///  gets translation, scale and inertia with no further platform work.
        /// </summary>
        [Fact]
        public void ADragProducesManipulationTranslation()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope(manipulation: true);
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                double translated = 0;
                scope.Target.ManipulationDelta += (s, e) => translated += e.DeltaManipulation.Translation.X;

                Point start = scope.CentreOfTarget;
                scope.Down(1, start);
                for (int i = 1; i <= 8; i++) scope.Move(1, start + new Vector(i * 6, 0));
                scope.Up(1, start + new Vector(48, 0));

                Assert.True(translated > 10,
                    $"a 48px drag produced {translated:F1}px of manipulation translation");
            });
        }

        /// <summary>
        ///  Two contacts moving apart scale. Impossible to express at all without multi-touch, so
        ///  this is the end-to-end proof that the seam carries more than one finger.
        /// </summary>
        [Fact]
        public void TwoContactsMovingApartProduceScale()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope(manipulation: true);
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                double scale = 1;
                scope.Target.ManipulationDelta += (s, e) => scale *= e.DeltaManipulation.Scale.X;

                Point centre = scope.CentreOfTarget;
                scope.Down(1, centre + new Vector(-10, 0));
                scope.Down(2, centre + new Vector(10, 0));

                for (int i = 1; i <= 8; i++)
                {
                    scope.Move(1, centre + new Vector(-10 - i * 5, 0));
                    scope.Move(2, centre + new Vector(10 + i * 5, 0));
                }

                scope.Up(1, centre + new Vector(-50, 0));
                scope.Up(2, centre + new Vector(50, 0));

                Assert.True(scale > 1.2, $"fingers moving apart produced a scale of {scale:F2}");
            });
        }

        /// <summary>
        ///  A platform-recognised gesture drives manipulation on the element under it.
        /// </summary>
        /// <remarks>
        ///  This is the macOS path. A Mac has no touchscreen and its trackpad reports positions on
        ///  the TRACKPAD, so there is no honest contact to report and no TouchDevice to build; what
        ///  AppKit gives is the pinch it already recognised. The seam turns that into a PAIR of
        ///  IManipulators, because one point cannot express scale -- scale is a change in the
        ///  distance between two of them.
        /// </remarks>
        [Fact]
        public void APlatformGestureProducesManipulationScale()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope(manipulation: true);
                Assert.SkipWhen(scope.GestureSink is null, "the platform gesture seam is not installed on this head");

                double scale = 1;
                scope.Target.ManipulationDelta += (s, e) => scale *= e.DeltaManipulation.Scale.X;

                Point centre = scope.CentreOfTarget;
                scope.GestureBegin(centre);
                for (int i = 1; i <= 8; i++) scope.GestureUpdate(centre, 1 + i * 0.1, 0);
                scope.GestureEnd();

                Assert.True(scale > 1.2, $"a pinch out to 1.8x produced a scale of {scale:F2}");
            });
        }

        /// <summary>
        ///  A gesture over an element that did not ask for manipulation is left alone, so a pinch
        ///  over an ordinary ScrollViewer keeps scrolling instead of silently becoming something
        ///  nobody opted into.
        /// </summary>
        [Fact]
        public void AGestureOverANonManipulatingElementDoesNothing()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new TouchScope(manipulation: false);
                Assert.SkipWhen(scope.GestureSink is null, "the platform gesture seam is not installed on this head");

                bool any = false;
                scope.Target.ManipulationDelta += (s, e) => any = true;

                Point centre = scope.CentreOfTarget;
                scope.GestureBegin(centre);
                scope.GestureUpdate(centre, 1.5, 0);
                scope.GestureEnd();

                Assert.False(any, "a gesture manipulated an element that never enabled manipulation");
            });
        }

        #region Harness

        /// <summary>
        ///  A real window from the shared harness, plus the reflection needed to call the seam the
        ///  way a windowing backend does.
        /// </summary>
        /// <remarks>
        ///  The window comes from InputWindow.Show rather than being constructed here. That matters: a
        ///  bare `new Window().Show()` throws in this environment, and the harness is also what
        ///  supplies a hit-testable Background (a null brush is absent from the hit test, so every
        ///  contact would fall through to the window with no element under it) and the single
        ///  long-lived UI thread the whole assembly shares.
        /// </remarks>
        private sealed class TouchScope : IDisposable
        {
            private readonly InputWindow _probe;
            private readonly HwndSource _source;
            private readonly MethodInfo _down, _move, _up, _cancel;
            private readonly MethodInfo _gestureBegin, _gestureUpdate, _gestureEnd;

            public Border Target => _probe.Surface;
            public object Sink { get; }

            public TouchScope(bool manipulation = false)
            {
                _probe = InputWindow.Show();
                _probe.Surface.IsManipulationEnabled = manipulation;
                _probe.Pump(TimeSpan.FromMilliseconds(100));

                _source = (HwndSource)PresentationSource.FromVisual(_probe.Window);

                Assembly windowsBase = typeof(DependencyObject).Assembly;
                Type platformTouch = windowsBase.GetType("MS.Internal.Interop.PlatformTouch");
                Sink = platformTouch?.GetProperty("Sink", BindingFlags.NonPublic | BindingFlags.Static)
                                    ?.GetValue(null);

                if (Sink is not null)
                {
                    Type sinkType = windowsBase.GetType("MS.Internal.Interop.IPlatformTouchSink");
                    _down = sinkType.GetMethod("TouchDown");
                    _move = sinkType.GetMethod("TouchMove");
                    _up = sinkType.GetMethod("TouchUp");
                    _cancel = sinkType.GetMethod("TouchCancel");
                }

                Type platformGesture = windowsBase.GetType("MS.Internal.Interop.PlatformGesture");
                GestureSink = platformGesture?.GetProperty("Sink", BindingFlags.NonPublic | BindingFlags.Static)
                                             ?.GetValue(null);
                if (GestureSink is not null)
                {
                    Type gestureType = windowsBase.GetType("MS.Internal.Interop.IPlatformGestureSink");
                    _gestureBegin = gestureType.GetMethod("GestureBegin");
                    _gestureUpdate = gestureType.GetMethod("GestureUpdate");
                    _gestureEnd = gestureType.GetMethod("GestureEnd");
                }
            }

            /// <summary>
            /// The centre of the target element in SCREEN device pixels, which is the seam's unit.
            /// PointToScreen does the whole conversion -- element to root, DIPs to device pixels,
            /// client to screen -- so the test and the seam cannot drift apart on a scaled display.
            /// </summary>
            public Point CentreOfTarget
                => Target.PointToScreen(new Point(Target.ActualWidth / 2, Target.ActualHeight / 2));

            public object GestureSink { get; }

            public void GestureBegin(Point screen)
            {
                _gestureBegin.Invoke(GestureSink, new object[] { _source.Handle, (int)screen.X, (int)screen.Y });
                Pump();
            }

            public void GestureUpdate(Point screen, double magnification, double rotationDegrees)
            {
                _gestureUpdate.Invoke(GestureSink, new object[]
                {
                    _source.Handle, (int)screen.X, (int)screen.Y, magnification, rotationDegrees,
                });
                Pump();
            }

            public void GestureEnd()
            {
                _gestureEnd.Invoke(GestureSink, new object[] { _source.Handle, false });
                Pump();
            }

            public void Down(int id, Point screen) => Invoke(_down, id, screen, -1.0);
            public void Move(int id, Point screen) => Invoke(_move, id, screen, -1.0);

            public void Up(int id, Point screen)
            {
                _up.Invoke(Sink, new object[]
                {
                    _source.Handle, id, (int)screen.X, (int)screen.Y, (uint)Environment.TickCount,
                });
                Pump();
            }

            /// <summary>An up the way a platform that reports no position on release sends it.</summary>
            public void UpWithoutPosition(int id)
            {
                _up.Invoke(Sink, new object[]
                {
                    _source.Handle, id, int.MinValue, int.MinValue, (uint)Environment.TickCount,
                });
                Pump();
            }

            public void Cancel(int id)
            {
                _cancel.Invoke(Sink, new object[] { _source.Handle, id });
                Pump();
            }

            private void Invoke(MethodInfo method, int id, Point screen, double pressure)
            {
                method.Invoke(Sink, new object[]
                {
                    _source.Handle, id, (int)screen.X, (int)screen.Y, Pen(pressure), (uint)Environment.TickCount,
                });
                Pump();
            }

            /// <summary>
            /// A PenState, which the seam takes instead of a bare pressure so that tilt (and, later,
            /// twist) do not each widen the signature again. Built reflectively for the same reason
            /// the sink is reached reflectively: it is internal to WindowsBase.
            /// </summary>
            private object Pen(double pressure)
            {
                Type penType = typeof(DependencyObject).Assembly.GetType("MS.Internal.Interop.PenState");
                return Activator.CreateInstance(
                    penType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new object[] { pressure, double.NaN, double.NaN }, null);
            }

            /// <summary>
            /// Drains the dispatcher. The seam raises the touch events synchronously, but promotion
            /// to Manipulation is queued, so without this the deltas arrive after the assertion.
            /// </summary>
            private void Pump() => _probe.Pump(TimeSpan.FromMilliseconds(16));

            public void Dispose() => _probe.Dispose();
        }

        #endregion
    }
}
