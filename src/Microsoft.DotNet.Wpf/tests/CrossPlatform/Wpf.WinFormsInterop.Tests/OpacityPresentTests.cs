// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A change of a form's Opacity reaches the screen by itself, within a frame -- no input needed. The
// driver ignored SetWindowTransparency, so nothing asked for a frame: DotNetBar's gallery popup fades in
// from a timer and stayed half transparent until the pointer moved over it.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class OpacityPresentTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        [Fact]
        public void AnOpacityStepFromATimer_IsPresentedWithoutInput()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            int presentsAfterLastStep = -1;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new QuietForm { ClientSize = new Size(120, 80), StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None, Opacity = 0.2 };
                    object? host = null;
                    int atLast = 0, step = 0, waited = 0;
                    PropertyInfo? count = null;
                    var timer = new SWF.Timer { Interval = 20 };
                    timer.Tick += (s, e) =>
                    {
                        host ??= typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                            .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, new object[] { form });
                        count ??= host?.GetType().GetProperty("PresentCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (step < 8)
                        {
                            // The fade: 0.3 .. 1.0, nothing else changing.
                            step++;
                            form.Opacity = Math.Min(1.0, 0.2 + step * 0.1);
                            if (step == 8) { form.Opacity = 1.0; atLast = (int)count!.GetValue(host)!; }
                            return;
                        }
                        if (++waited < 4) return;   // ~80 ms, no input at all
                        presentsAfterLastStep = (int)count!.GetValue(host)! - atLast;
                        timer.Stop();
                        form.Close();
                    };
                    form.Shown += (s, e) => timer.Start();
                    SWF.Application.Run(form);
                    timer.Dispose();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(30)));
            if (failure != null) throw failure;
            Assert.True(presentsAfterLastStep > 0, "the last opacity step was never presented");
        }
    }
}
