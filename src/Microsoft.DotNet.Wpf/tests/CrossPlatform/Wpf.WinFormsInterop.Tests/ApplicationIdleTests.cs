// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Application.Idle is raised when the message loop has handled what there was and is about to wait,
// once per batch -- not repeatedly while nothing happens -- and on the thread that subscribed. The
// driver never raised it at all, so work an application left for "when the UI is idle" never ran.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ApplicationIdleTests
    {
        [Fact]
        public void Idle_IsRaisedAfterMessages_AndNotWhileNothingHappens()
        {
            Exception? failure = null;
            int afterShown = -1, afterPost = -1, afterQuiet = -1, otherThread = 0;
            bool wrongThread = false;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "ApplicationIdleTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(200, 120), ShowInTaskbar = false,
                    };
                    int idles = 0;
                    EventHandler onIdle = (s, e) =>
                    {
                        if (form.InvokeRequired) wrongThread = true;   // must run on the thread that subscribed
                        idles++;
                    };
                    SWF.Application.Idle += onIdle;
                    // A handler added on another thread belongs to that thread's loop, not this one.
                    var other = new Thread(() => SWF.Application.Idle += (s, e) => Interlocked.Increment(ref otherThread));
                    other.Start(); other.Join();

                    var steps = new SWF.Timer { Interval = 50 };
                    int phase = 0;
                    steps.Tick += (s, e) =>
                    {
                        phase++;
                        if (phase == 10) { afterShown = idles; form.BeginInvoke(new Action(() => { })); }
                        else if (phase == 14) { afterPost = idles; steps.Interval = 600; }
                        else if (phase == 15)
                        {
                            // Between two ticks 600 ms apart only the tick itself was handled.
                            afterQuiet = idles - afterPost;
                            steps.Stop();
                            form.Close();
                        }
                    };
                    form.Shown += (s, e) => steps.Start();
                    SWF.Application.Run(form);
                    SWF.Application.Idle -= onIdle;
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            Assert.True(afterShown > 0, "Idle was never raised");
            Assert.True(afterPost > afterShown, $"no Idle after a posted message ({afterShown} -> {afterPost})");
            Assert.InRange(afterQuiet, 1, 4);
            Assert.Equal(0, otherThread);
            Assert.False(wrongThread);
        }
    }
}
