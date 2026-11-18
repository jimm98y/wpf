// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What each head makes of what its platform says about a pen, short of the platform itself.
//
// The browser's contact message is JSON the page writes (browser-window.js pushContact), Android's
// history is a flat array its host fills from a MotionEvent, and an Apple Pencil's tool switch is a
// state machine over the user's Settings choice. None needs the platform to test, and each is the
// kind of transcription that goes wrong quietly: a field read under the wrong name is "not
// reported", which nothing ever complains about.
//

using System;
using System.Text.Json;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class HeadPenInputTests
    {
        // ---- browser ----

        [Fact]
        public void ABrowserPenMessageCarriesEveryMeasurement()
        {
            using JsonDocument doc = JsonDocument.Parse(
                """{"t":"tc","k":0,"h":1,"id":3,"x":10,"y":20,"p":0.6,"ts":5,"tx":12,"ty":-8,"inv":true,"tw":370,"br":true,"cw":6,"ch":4}""");
            PenState pen = BrowserPenInput.ParsePen(doc.RootElement);

            Assert.Equal(0.6, pen.Pressure, 6);
            Assert.Equal(12, pen.TiltX, 6);
            Assert.Equal(-8, pen.TiltY, 6);
            Assert.True(pen.IsInverted);
            Assert.Equal(10, pen.Twist, 6);   // folded into one turn
            Assert.True(pen.BarrelButton);
            Assert.Equal(6, pen.Width, 6);
            Assert.Equal(4, pen.Height, 6);
        }

        /// <summary>A finger's message is a position and a "no pressure"; nothing else is invented.</summary>
        [Fact]
        public void ABrowserFingerMessageReportsNothingButWhereItIs()
        {
            using JsonDocument doc = JsonDocument.Parse("""{"t":"tc","k":1,"h":1,"id":3,"x":10,"y":20,"p":-1,"ts":5}""");
            PenState pen = BrowserPenInput.ParsePen(doc.RootElement);

            Assert.False(pen.HasPressure);
            Assert.False(pen.HasTilt);
            Assert.False(pen.HasTwist);
            Assert.False(pen.HasContactSize);
            Assert.Null(pen.BarrelButton);
            Assert.False(pen.IsInverted);
            Assert.Null(BrowserPenInput.ParseCoalesced(doc.RootElement, 0, 0));
        }

        [Fact]
        public void BrowserCoalescedSamplesBecomeScreenSamplesInOrder()
        {
            using JsonDocument doc = JsonDocument.Parse(
                """{"t":"tc","k":0,"h":1,"id":3,"x":30,"y":20,"p":0.9,"ts":9,"cs":[{"x":10,"y":20,"p":0.3,"ts":7},{"x":20,"y":21,"p":0.6,"ts":8,"tw":45}]}""");
            TouchSample[] samples = BrowserPenInput.ParseCoalesced(doc.RootElement, 100, 200);

            Assert.Equal(2, samples.Length);
            Assert.Equal(110, samples[0].ScreenX);
            Assert.Equal(220, samples[0].ScreenY);
            Assert.Equal(0.3, samples[0].Pen.Pressure, 6);
            Assert.Equal(120, samples[1].ScreenX);
            Assert.Equal(221, samples[1].ScreenY);
            Assert.Equal(45, samples[1].Pen.Twist, 6);
            Assert.Equal(8u, samples[1].TimestampMs);
        }

        // ---- Android ----

        [Fact]
        public void AndroidHistoryBecomesSamplesInOrder()
        {
            const int s = AndroidWindow.MotionHistoryStride;
            var history = new double[2 * s];
            // x, y, pressure, tilt (rad), orientation (rad), touch major, touch minor, time
            new double[] { 10.7, 20.2, 0.25, Math.PI / 6, Math.PI / 2, 8, 6, 1000 }.CopyTo(history, 0);
            new double[] { 12.0, 22.0, 0.50, 0, 0, 0, 0, 1016 }.CopyTo(history, s);

            TouchSample[] samples = AndroidPenInput.SamplesFromHistory(history, isStylus: true, isEraser: true, barrelButton: true);
            samples = AndroidPenInput.Offset(samples, 100, 200);

            Assert.Equal(2, samples.Length);
            Assert.Equal(110, samples[0].ScreenX);    // truncated, as the event's own point is
            Assert.Equal(220, samples[0].ScreenY);
            Assert.Equal(0.25, samples[0].Pen.Pressure, 6);
            Assert.Equal(30, samples[0].Pen.TiltX, 6);   // leaning right: orientation pi/2
            Assert.Equal(0, samples[0].Pen.TiltY, 6);
            Assert.Equal(8, samples[0].Pen.Width, 6);   // a stylus's ellipse is sized as a circle
            Assert.True(samples[0].Pen.IsInverted, "a historical sample lost the eraser end its event was made with");
            Assert.True(samples[0].Pen.BarrelButton);
            Assert.False(samples[1].Pen.HasContactSize);
            Assert.Equal(1016u, samples[1].TimestampMs);
        }

        [Fact]
        public void AndroidWithoutHistoryHasNoSamples()
        {
            Assert.Null(AndroidPenInput.SamplesFromHistory(null!, true, false, null));
            Assert.Null(AndroidPenInput.SamplesFromHistory(Array.Empty<double>(), true, false, null));
        }

        /// <summary>
        ///  A finger's pressure is a constant that measures nothing, and it has no tilt -- but its
        ///  touch ellipse is real, and becomes the bounding box of the ellipse as it is turned.
        /// </summary>
        [Fact]
        public void AnAndroidFingerReportsItsContactBoxAndNothingElse()
        {
            PenState finger = AndroidPenInput.Measure(1.0, 0.7, Math.PI / 2, 20, 10, isStylus: false, isEraser: false, barrelButton: null);
            Assert.False(finger.HasPressure);
            Assert.False(finger.HasTilt);
            Assert.False(finger.HasTwist);
            Assert.Null(finger.BarrelButton);
            Assert.Equal(20, finger.Width, 6);    // the long axis turned to horizontal
            Assert.Equal(10, finger.Height, 6);
        }

        [Fact]
        public void AnAndroidStylusReportsPressureTiltAndBarrel()
        {
            // Leaning 45 degrees towards the user: orientation pi is "pointing down the screen".
            PenState pen = AndroidPenInput.Measure(0.4, Math.PI / 4, Math.PI, 0, 0, isStylus: true, isEraser: false, barrelButton: false);
            Assert.Equal(0.4, pen.Pressure, 6);
            Assert.Equal(0, pen.TiltX, 6);
            Assert.Equal(45, pen.TiltY, 6);
            Assert.False(pen.HasContactSize);
            Assert.False(pen.HasTwist);
            Assert.False(pen.BarrelButton!.Value);
        }

        // ---- iPadOS ----

        [Fact]
        public void PencilDoubleTapTogglesTheEraserWhenTheUserChoseIt()
        {
            var tool = new PencilToolState();
            Assert.False(tool.EraserActive);

            Assert.True(tool.Apply(PencilToolState.SwitchEraser));
            Assert.True(tool.EraserActive);
            Assert.True(tool.Apply(PencilToolState.SwitchEraser));
            Assert.False(tool.EraserActive);

            // The colour palette, a system shortcut or "off" are not WPF's to act on.
            Assert.False(tool.Apply(PencilToolState.Ignore));
            Assert.False(tool.Apply(3));
            Assert.False(tool.EraserActive);
        }

        /// <summary>
        ///  "Switch to the previous tool" goes back from the eraser, and to it only once it has been
        ///  the tool: a pen that was never anything else has no previous tool.
        /// </summary>
        [Fact]
        public void PencilSwitchPreviousOnlyGoesBackToAToolThatWasUsed()
        {
            var tool = new PencilToolState();
            Assert.False(tool.Apply(PencilToolState.SwitchPrevious));
            Assert.False(tool.EraserActive);

            tool.Apply(PencilToolState.SwitchEraser);
            Assert.True(tool.Apply(PencilToolState.SwitchPrevious));
            Assert.False(tool.EraserActive);
            Assert.True(tool.Apply(PencilToolState.SwitchPrevious));
            Assert.True(tool.EraserActive);
        }

        [Fact]
        public void PencilAnglesBecomeTiltAndTwist()
        {
            // Upright: no lean whatever the azimuth.
            UIKitPencil.Tilt(Math.PI / 2, 1.0, out double x, out double y);
            Assert.Equal(0, x, 6);
            Assert.Equal(0, y, 6);

            // 30 degrees from the screen (60 from upright), leaning along +x.
            UIKitPencil.Tilt(Math.PI / 6, 0, out x, out y);
            Assert.Equal(60, x, 6);
            Assert.Equal(0, y, 6);

            // A Pencil Pro rolled a quarter turn; a Pencil without the sensor reports exactly 0.
            Assert.Equal(90, UIKitPencil.TwistFromRoll(Math.PI / 2), 6);
            Assert.Equal(270, UIKitPencil.TwistFromRoll(-Math.PI / 2), 6);
            Assert.True(double.IsNaN(UIKitPencil.TwistFromRoll(0)));
        }
    }
}
