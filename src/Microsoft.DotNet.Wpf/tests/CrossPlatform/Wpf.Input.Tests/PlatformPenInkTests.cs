// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A platform pen drawing on a real InkCanvas, end to end above the backends.
//
// PlatformTouchSeamTests stops at PlatformTouchSink.CurrentPen. This goes the rest of the way: the
// contact reaches the seam exactly as a backend reports it, the MOUSE follows it exactly as a
// backend's mouse provider reports it (HwndMouseInputProvider.ReportMacInput, the one entry every
// off-Windows head's mouse goes through), and the assertions are on the Stroke the InkCanvas
// collected -- its StylusPoints, their description and their values. That is the whole chain the
// ink path off Windows depends on, and the only one a user sees.
//
// Everything is reached by reflection: the seam is internal to WindowsBase, the mouse provider and
// RawMouseActions to PresentationCore.
//

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace Wpf.Input.Tests
{
    public class PlatformPenInkTests
    {
        /// <summary>
        ///  The samples a platform coalesced into a move reach the stroke, in order, ahead of the
        ///  move's own point -- and the move is still one move.
        /// </summary>
        [Fact]
        public void CoalescedSamplesLandInTheStrokeInOrder()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new PenInkScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                int touchMoves = 0;
                TouchPointCollection? intermediate = null;
                scope.Canvas.TouchMove += (s, e) => { touchMoves++; intermediate = e.GetIntermediateTouchPoints(scope.Canvas); };

                var pen = PenInkScope.Pen(pressure: 0.2);
                scope.Down(new Point(40, 100), pen);

                scope.Move(new Point(100, 100), PenInkScope.Pen(pressure: 0.5),
                           (new Point(60, 100), PenInkScope.Pen(pressure: 0.3)),
                           (new Point(80, 100), PenInkScope.Pen(pressure: 0.4)));
                scope.Move(new Point(160, 100), PenInkScope.Pen(pressure: 0.8),
                           (new Point(120, 100), PenInkScope.Pen(pressure: 0.6)),
                           (new Point(140, 100), PenInkScope.Pen(pressure: 0.7)));
                scope.Up(new Point(160, 100));

                Stroke stroke = Assert.Single(scope.Canvas.Strokes);
                var xs = new List<double>();
                var pressures = new List<float>();
                foreach (StylusPoint point in stroke.StylusPoints)
                {
                    xs.Add(Math.Round(point.X));
                    pressures.Add(point.PressureFactor);
                }

                Assert.Equal(new double[] { 40, 60, 80, 100, 120, 140, 160 }, xs);
                for (int i = 0; i < pressures.Count; i++)
                {
                    Assert.Equal(0.2f + 0.1f * i, pressures[i], 2);
                }

                // One TouchMove per platform event: the samples ride inside the event, as Windows'
                // packets do, rather than becoming events of their own. (The mouse is the harness's
                // own -- one report per event -- so there is nothing to count there; WPF adds a
                // synthetic move of its own when the canvas captures it.)
                Assert.Equal(2, touchMoves);

                // ...and TouchDevice hands them out as its intermediate points, the current one last.
                Assert.NotNull(intermediate);
                Assert.Equal(3, intermediate!.Count);
                Assert.Equal(120, Math.Round(intermediate[0].Position.X));
                Assert.Equal(140, Math.Round(intermediate[1].Position.X));
                Assert.Equal(160, Math.Round(intermediate[2].Position.X));
            });
        }

        /// <summary>
        ///  Tilt, twist, contact size and the barrel button reach the stroke in Windows' units, when
        ///  the application's description asks for them.
        /// </summary>
        [Fact]
        public void TwistTiltSizeAndBarrelReachTheStrokeInWindowsUnits()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new PenInkScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                scope.Canvas.DefaultStylusPointDescription = new StylusPointDescription(new[]
                {
                    new StylusPointPropertyInfo(StylusPointProperties.X),
                    new StylusPointPropertyInfo(StylusPointProperties.Y),
                    new StylusPointPropertyInfo(StylusPointProperties.NormalPressure),
                    new StylusPointPropertyInfo(StylusPointProperties.XTiltOrientation),
                    new StylusPointPropertyInfo(StylusPointProperties.YTiltOrientation),
                    new StylusPointPropertyInfo(StylusPointProperties.TwistOrientation),
                    new StylusPointPropertyInfo(StylusPointProperties.Width),
                    new StylusPointPropertyInfo(StylusPointProperties.Height),
                    new StylusPointPropertyInfo(StylusPointProperties.BarrelButton),
                });

                object pen = PenInkScope.Pen(pressure: 0.6, tiltX: 30, tiltY: -15, twist: 45,
                                             width: 20, height: 10, barrel: true);
                scope.Down(new Point(40, 100), pen);
                scope.Move(new Point(120, 100), PenInkScope.Pen(pressure: 0.6, tiltX: 30, tiltY: -15, twist: 90,
                                                                width: 20, height: 10, barrel: true),
                           (new Point(80, 100), pen));
                scope.Up(new Point(120, 100));

                Stroke stroke = Assert.Single(scope.Canvas.Strokes);
                StylusPointDescription description = stroke.StylusPoints.Description;
                Assert.True(description.HasProperty(StylusPointProperties.TwistOrientation), "the stroke has no TwistOrientation");
                Assert.True(description.HasProperty(StylusPointProperties.Width), "the stroke has no Width");
                Assert.True(description.HasProperty(StylusPointProperties.BarrelButton), "the stroke has no BarrelButton");

                StylusPoint sample = stroke.StylusPoints[1];
                StylusPoint last = stroke.StylusPoints[stroke.StylusPoints.Count - 1];

                // Tenths of a degree, as Wisp and the pointer stack both count them.
                Assert.Equal(300, last.GetPropertyValue(StylusPointProperties.XTiltOrientation));
                Assert.Equal(-150, last.GetPropertyValue(StylusPointProperties.YTiltOrientation));
                Assert.Equal(450, sample.GetPropertyValue(StylusPointProperties.TwistOrientation));
                Assert.Equal(900, last.GetPropertyValue(StylusPointProperties.TwistOrientation));
                Assert.Equal(1, last.GetPropertyValue(StylusPointProperties.BarrelButton));

                // HIMETRIC: 20 device pixels, through the display scale into DIPs, at 2540/96 a DIP.
                double dipsPerPixel = scope.Source.CompositionTarget.TransformFromDevice.M11;
                Assert.Equal((int)Math.Round(20 * dipsPerPixel * 2540.0 / 96.0), last.GetPropertyValue(StylusPointProperties.Width));
                Assert.Equal((int)Math.Round(10 * dipsPerPixel * 2540.0 / 96.0), last.GetPropertyValue(StylusPointProperties.Height));
            });
        }

        /// <summary>
        ///  An InkCanvas left at its default description keeps X, Y and pressure only -- as on
        ///  Windows, where the stroke is the common description of the canvas's and the device's --
        ///  and a pen that reports tilt no longer abandons the stroke it starts.
        /// </summary>
        [Fact]
        public void TheDefaultDescriptionKeepsPressureAndTheStrokeCompletes()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new PenInkScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                scope.Down(new Point(40, 100), PenInkScope.Pen(pressure: 0.3, tiltX: 20, tiltY: 10));
                scope.Move(new Point(80, 100), PenInkScope.Pen(pressure: 0.6, tiltX: 25, tiltY: 10));
                scope.Move(new Point(120, 100), PenInkScope.Pen(pressure: 0.9, tiltX: 30, tiltY: 10));
                scope.Up(new Point(120, 100));

                Stroke stroke = Assert.Single(scope.Canvas.Strokes);
                Assert.Equal(3, stroke.StylusPoints.Description.PropertyCount);
                Assert.Equal(3, stroke.StylusPoints.Count);
                Assert.Equal(0.3f, stroke.StylusPoints[0].PressureFactor, 2);
                Assert.Equal(0.9f, stroke.StylusPoints[2].PressureFactor, 2);
            });
        }

        /// <summary>
        ///  The inverted end erases: InkCanvas switches to EditingModeInverted at the down, and the
        ///  stroke it crosses is removed instead of a new one drawn.
        /// </summary>
        [Fact]
        public void TheInvertedEndErasesTheStrokeItCrosses()
        {
            UiThread.Invoke(() =>
            {
                using var scope = new PenInkScope();
                Assert.SkipWhen(scope.Sink is null, "the platform touch seam is not installed on this head");

                scope.Down(new Point(40, 100), PenInkScope.Pen(pressure: 0.5));
                scope.Move(new Point(120, 100), PenInkScope.Pen(pressure: 0.5));
                scope.Move(new Point(200, 100), PenInkScope.Pen(pressure: 0.5));
                scope.Up(new Point(200, 100));
                Assert.Single(scope.Canvas.Strokes);

                scope.Down(new Point(120, 40), PenInkScope.Pen(pressure: 0.5, inverted: true));
                scope.Move(new Point(120, 100), PenInkScope.Pen(pressure: 0.5, inverted: true),
                           (new Point(120, 70), PenInkScope.Pen(pressure: 0.5, inverted: true)));
                scope.Move(new Point(120, 160), PenInkScope.Pen(pressure: 0.5, inverted: true));
                scope.Up(new Point(120, 160));

                Assert.Empty(scope.Canvas.Strokes);
            });
        }

        #region Harness

        /// <summary>
        ///  A shown window holding an InkCanvas, and a pen driven through the seam and the mouse the
        ///  way a backend drives them: the contact first, then the mouse, for every platform event.
        /// </summary>
        private sealed class PenInkScope : IDisposable
        {
            private static readonly Assembly s_windowsBase = typeof(DependencyObject).Assembly;
            private static readonly Assembly s_core = typeof(UIElement).Assembly;
            private static readonly Type s_penType = s_windowsBase.GetType("MS.Internal.Interop.PenState")!;
            private static readonly Type s_sampleType = s_windowsBase.GetType("MS.Internal.Interop.TouchSample")!;

            private const int ContactId = 4242;

            private readonly InputWindow _probe;
            private readonly MethodInfo? _down, _moveCoalesced, _up;
            private readonly object? _mouse;
            private readonly MethodInfo? _reportMouse;
            private readonly Type? _actions;

            public InkCanvas Canvas { get; }
            public HwndSource Source { get; }
            public object? Sink { get; }

            public PenInkScope()
            {
                _probe = InputWindow.Show();
                Canvas = new InkCanvas { Background = Brushes.White };
                _probe.Surface.Child = Canvas;
                _probe.Pump(TimeSpan.FromMilliseconds(150));

                Source = (HwndSource)PresentationSource.FromVisual(_probe.Window);

                Sink = s_windowsBase.GetType("MS.Internal.Interop.PlatformTouch")
                                    ?.GetProperty("Sink", BindingFlags.NonPublic | BindingFlags.Static)
                                    ?.GetValue(null);
                Type sinkType = s_windowsBase.GetType("MS.Internal.Interop.IPlatformTouchSink")!;
                _down = sinkType.GetMethod("TouchDown");
                _moveCoalesced = sinkType.GetMethod("TouchMoveCoalesced");
                _up = sinkType.GetMethod("TouchUp");

                _mouse = typeof(HwndSource).GetField("_mouse", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Source);
                _reportMouse = _mouse?.GetType().GetMethod("ReportMacInput", BindingFlags.Instance | BindingFlags.NonPublic);
                _actions = s_core.GetType("System.Windows.Input.RawMouseActions");

                // Without the off-Windows mouse entry there is nothing to drive the ink with.
                if (_reportMouse is null || _actions is null) Sink = null;
            }

            /// <summary>A PenState; every field not given is "not reported".</summary>
            public static object Pen(double pressure = -1, double tiltX = double.NaN, double tiltY = double.NaN,
                                     bool inverted = false, double twist = double.NaN,
                                     double width = double.NaN, double height = double.NaN, bool? barrel = null)
                => Activator.CreateInstance(
                    s_penType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new object?[] { pressure, tiltX, tiltY, inverted, twist, width, height, barrel }, null)!;

            public void Down(Point onCanvas, object pen)
            {
                Point screen = Screen(onCanvas);
                _down!.Invoke(Sink, new object[] { Source.Handle, ContactId, (int)Math.Round(screen.X), (int)Math.Round(screen.Y), pen, Now });
                Mouse("AbsoluteMove", onCanvas);
                Mouse("Button1Press", onCanvas);
                Pump();
            }

            public void Move(Point onCanvas, object pen, params (Point OnCanvas, object Pen)[] coalesced)
            {
                Array samples = Array.CreateInstance(s_sampleType, coalesced.Length);
                for (int i = 0; i < coalesced.Length; i++)
                {
                    Point at = Screen(coalesced[i].OnCanvas);
                    samples.SetValue(Activator.CreateInstance(
                        s_sampleType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                        new object[] { (int)Math.Round(at.X), (int)Math.Round(at.Y), coalesced[i].Pen, Now }, null), i);
                }

                Point screen = Screen(onCanvas);
                _moveCoalesced!.Invoke(Sink, new object[] { Source.Handle, ContactId, (int)Math.Round(screen.X), (int)Math.Round(screen.Y), pen, Now, samples });
                Mouse("AbsoluteMove", onCanvas);
                Pump();
            }

            public void Up(Point onCanvas)
            {
                Point screen = Screen(onCanvas);
                Mouse("Button1Release", onCanvas);
                _up!.Invoke(Sink, new object[] { Source.Handle, ContactId, (int)Math.Round(screen.X), (int)Math.Round(screen.Y), Now });
                Pump();
            }

            /// <summary>A point on the canvas, in screen device pixels, the seam's unit.</summary>
            private Point Screen(Point onCanvas) => Canvas.PointToScreen(onCanvas);

            /// <summary>
            ///  The mouse, the way every off-Windows head's provider reports it: client device pixels.
            /// </summary>
            private void Mouse(string action, Point onCanvas)
            {
                Point root = Canvas.TranslatePoint(onCanvas, (UIElement)Source.RootVisual);
                Point client = Source.CompositionTarget.TransformToDevice.Transform(root);
                object actions = Enum.Parse(_actions!, action);
                _reportMouse!.Invoke(_mouse, new object[]
                {
                    actions, (int)Math.Round(client.X), (int)Math.Round(client.Y), 0, Environment.TickCount,
                });
            }

            private static uint Now => (uint)Environment.TickCount;

            private void Pump() => _probe.Pump(TimeSpan.FromMilliseconds(16));

            public void Dispose() => _probe.Dispose();
        }

        #endregion
    }
}
