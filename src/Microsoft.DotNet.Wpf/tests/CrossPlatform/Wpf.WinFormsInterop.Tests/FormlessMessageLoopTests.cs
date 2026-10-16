// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A message loop with no form on screen runs until ExitThread or Application.Exit, as in .NET: a
// tray app, an ApplicationContext-based app and Application.Run() all depend on it. The driver ended
// any loop the moment no window was on screen -- which a modal dialog closed with a timer still
// running needed (SharpDevelop's About box hung the application without it) -- so Application.Run()
// returned at once. Now only a loop whose form is gone ends that way.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class FormlessMessageLoopTests
    {
        private static void OnSta(Action body, int seconds = 60)
        {
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(seconds)), "the message loop did not end");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>Ticks, a BeginInvoke from another thread, then ExitThread -- all with no form.</summary>
        private static void PumpsUntilExitThread(Action run)
        {
            int ticks = 0, invoked = 0;
            bool returned = false;
            OnSta(() =>
            {
                // A control with a window but no form on screen, to BeginInvoke through.
                using var marshal = new SWF.Control();
                marshal.CreateControl();
                _ = marshal.Handle;
                var timer = new SWF.Timer { Interval = 20 };
                timer.Tick += (s, e) =>
                {
                    ticks++;
                    if (ticks == 3)
                        new Thread(() => marshal.BeginInvoke(new Action(() => invoked++))).Start();
                    if (ticks >= 10 && invoked > 0)
                    {
                        timer.Stop();
                        SWF.Application.ExitThread();
                    }
                };
                timer.Start();
                run();
                returned = true;
                timer.Dispose();
            });
            Assert.True(returned);
            Assert.True(ticks >= 10, $"only {ticks} ticks before the loop ended");
            Assert.Equal(1, invoked);
        }

        [Fact]
        public void ApplicationRun_WithNoForm_PumpsUntilExitThread() => PumpsUntilExitThread(() => SWF.Application.Run());

        [Fact]
        public void ApplicationRun_WithAnEmptyContext_PumpsUntilExitThread() =>
            PumpsUntilExitThread(() => SWF.Application.Run(new SWF.ApplicationContext()));

        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        [Fact]
        public void AModalDialogClosedWithItsTimerRunning_Returns_AndTheFormlessLoopGoesOn()
        {
            // SharpDevelop's About box: a modal dialog whose timer runs until the form is disposed --
            // which only happens after ShowDialog returns. Closing it must end the dialog's loop, and
            // the loop underneath, which has no form, must keep running.
            int dialogTicks = 0, ticksAfter = 0;
            SWF.DialogResult result = SWF.DialogResult.None;
            bool returned = false;
            OnSta(() =>
            {
                var outer = new SWF.Timer { Interval = 20 };
                bool shown = false;
                outer.Tick += (s, e) =>
                {
                    if (!shown)
                    {
                        shown = true;
                        outer.Stop();   // its ticks would otherwise run inside the dialog's loop
                        using var dialog = new QuietForm
                        {
                            Text = "FormlessLoopModalTest", StartPosition = SWF.FormStartPosition.Manual,
                            Location = new Point(8000, 80), Size = new Size(200, 120), ShowInTaskbar = false,
                            FormBorderStyle = SWF.FormBorderStyle.None,
                        };
                        var spin = new SWF.Timer { Interval = 15 };   // still running when the dialog closes
                        spin.Tick += (s2, e2) =>
                        {
                            if (++dialogTicks == 5) dialog.DialogResult = SWF.DialogResult.OK;
                        };
                        dialog.Shown += (s2, e2) => spin.Start();
                        result = dialog.ShowDialog();
                        spin.Dispose();
                        outer.Start();
                        return;
                    }
                    if (++ticksAfter == 5)
                    {
                        outer.Stop();
                        SWF.Application.ExitThread();
                    }
                };
                outer.Start();
                SWF.Application.Run();
                returned = true;
                outer.Dispose();
            });
            Assert.True(returned);
            Assert.True(result == SWF.DialogResult.OK, $"{result} after {dialogTicks} dialog ticks");
            Assert.True(dialogTicks >= 5);
            Assert.Equal(5, ticksAfter);
        }
    }
}
