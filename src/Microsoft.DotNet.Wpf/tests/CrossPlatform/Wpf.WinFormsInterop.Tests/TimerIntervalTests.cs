// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A System.Windows.Forms.Timer runs as Windows runs it: never sooner than 10 ms (USER_TIMER_MINIMUM),
// and on the system tick -- WM_TIMER is generated at a tick, so a 1-ms timer runs every 15.6 ms on
// Windows, 64 a second. Mono's floor was 15 ms on a sub-millisecond clock, which came out at 19-21 ms
// (about 50 a second); and a timer must not run FASTER than stock either, or an application's
// animation does not look as it does there.
//
// Measuring the cadence itself is at the mercy of the machine's scheduling (a virtual machine's
// above all), so this pins down the two things it is made of: the floor, and the clock.
//

using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TimerIntervalTests
    {
        [Fact]
        public void TheShortestInterval_IsWindows_TenMilliseconds()
        {
            int minimum = (int)typeof(SWF.Timer).GetField("Minimum", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.Equal(10, minimum);
        }

        [Fact]
        public void TimersKeepTime_ByTheSystemTick()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' 15.6-ms system tick");
            PropertyInfo now = typeof(SWF.Timer).GetProperty("StopWatchNowMilliseconds", BindingFlags.NonPublic | BindingFlags.Static)!;
            var steps = new List<long>();
            long last = (long)now.GetValue(null)!;
            var end = DateTime.UtcNow.AddSeconds(3);
            while (steps.Count < 20 && DateTime.UtcNow < end)
            {
                long t = (long)now.GetValue(null)!;
                if (t != last) { steps.Add(t - last); last = t; }
            }
            Assert.True(steps.Count >= 10, $"the clock moved {steps.Count} times in 3 s");
            steps.Sort();
            // The tick: 15 or 16 ms a step (whatever timeBeginPeriod says). A millisecond clock steps by 1.
            Assert.True(steps[steps.Count / 2] >= 10, $"the timers' clock steps by {steps[steps.Count / 2]} ms; Windows' timers move by the 15.6-ms tick");
        }
    }
}
