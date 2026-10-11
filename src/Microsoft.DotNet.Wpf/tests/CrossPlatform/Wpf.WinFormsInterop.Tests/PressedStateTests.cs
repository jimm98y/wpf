// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// States a control is in only while the user holds it, found by driving stock and ported windows
// with real input and subtracting them.
//
// A held TrackBar thumb VANISHED: the Win11 layout took thumb_mouseclick -- the press's pixel x --
// for a value while the thumb was pressed, and put the thumb far off the control. comctl32 draws a
// held thumb where its value is; a drag moves the value.
//
// A freshly focused TextBox could show no caret for half a second: the host blinked the caret on a
// clock of its own, where user32 restarts the blink whenever the caret is created, moved or shown.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class PressedStateTests
    {
        private static Rectangle ThumbPos(TrackBar tb)
            => (Rectangle)typeof(TrackBar)
                .GetProperty("ThumbPos", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .GetValue(tb)!;

        private static void SetPressed(TrackBar tb, bool pressed, int mouseX)
        {
            typeof(TrackBar).GetField("thumb_pressed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tb, pressed);
            typeof(TrackBar).GetField("thumb_mouseclick", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tb, mouseX);
        }

        private static Rectangle ThumbAfterPaint(TrackBar tb)
        {
            using var bmp = new Bitmap(tb.Width, tb.Height);
            tb.DrawToBitmap(bmp, new Rectangle(0, 0, tb.Width, tb.Height));
            return ThumbPos(tb);
        }

        [Fact]
        public void AHeldThumb_StaysWhereItsValueIs()
        {
            using var form = new Form { ClientSize = new Size(300, 100) };
            var tb = new TrackBar { Left = 10, Top = 10, Width = 200, Minimum = 0, Maximum = 10, Value = 3 };
            form.Controls.Add(tb);
            form.CreateControl();
            tb.CreateControl();

            Rectangle rest = ThumbAfterPaint(tb);
            Assert.True(tb.ClientRectangle.Contains(rest), $"resting thumb {rest} outside {tb.ClientRectangle}");

            // Pressed where it rests, as a click on the thumb leaves it: the press's pixel is NOT a value.
            SetPressed(tb, true, rest.X + rest.Width / 2);
            Rectangle held = ThumbAfterPaint(tb);
            SetPressed(tb, false, 0);

            Assert.Equal(rest, held);
        }

        [Fact]
        public void ShowingOrMovingTheCaret_RestartsItsBlink()
        {
            XplatUIWebGpu driver = XplatUIWebGpu.GetInstance();
            long Elapsed() => driver.GetCaretBlinkElapsed();

            using var form = new Form { ClientSize = new Size(200, 60) };
            var box = new TextBox { Left = 5, Top = 5, Width = 150, Text = "abc" };
            form.Controls.Add(box);
            form.CreateControl();
            IntPtr h = box.Handle;

            driver.CreateCaret(h, 1, 15);
            driver.CaretVisible(h, false);
            System.Threading.Thread.Sleep(700);
            Assert.True(Elapsed() >= 600, "the blink clock runs while nothing happens");

            driver.CaretVisible(h, true);
            Assert.True(Elapsed() < 530, "shown: on at once, for a full blink time");

            System.Threading.Thread.Sleep(700);
            driver.SetCaretPos(h, 7, 2);
            Assert.True(Elapsed() < 530, "moved: on at once again");
        }
    }
}
