// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF content focused while its form is coming up -- before the form has a window on screen -- keeps
// the keyboard when the form is activated, as with .NET's ElementHost, whose HwndSource exists from
// handle creation. Ours was made only once the form's window existed, so the Focus() did nothing and
// activation gave the keyboard to the first control in tab order (GraphWorX's main view lost it to a
// docked pane).
//

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ElementHostEarlyFocusTests
    {
        private sealed class FocusesOnShow : SWF.UserControl
        {
            public System.Windows.Controls.TextBox? Box;
            protected override void OnVisibleChanged(EventArgs e)
            {
                if (Visible) Box?.Focus();          // as an app focuses its main view while it appears
                base.OnVisibleChanged(e);
            }
        }

        [Fact]
        public void WpfFocusBeforeTheWindowExists_MakesTheElementHostActive()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the hosted tree is a real child window only on Windows");
            Exception? failure = null;
            string result = "";
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(300, 200), ShowInTaskbar = false };
                    form.Controls.Add(new SWF.Button { Text = "First", Bounds = new Rectangle(10, 160, 80, 25), TabIndex = 0 });
                    var box = new System.Windows.Controls.TextBox();
                    var panel = new FocusesOnShow { Bounds = new Rectangle(0, 0, 300, 150), TabIndex = 1, Box = box };
                    var host = new ElementHost { Dock = SWF.DockStyle.Fill, Child = box };
                    panel.Controls.Add(host);
                    form.Controls.Add(panel);
                    form.Show();
                    var end = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < end && !(form.ActiveControl is SWF.UserControl uc && uc.ActiveControl == host))
                    {
                        SWF.Application.DoEvents();
                        s_tick?.Invoke(null, null);
                        Thread.Sleep(15);
                    }
                    result = $"{form.ActiveControl?.GetType().Name}/{(form.ActiveControl as SWF.ContainerControl)?.ActiveControl?.GetType().Name}";
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal("FocusesOnShow/ElementHost", result);
        }

        private static readonly System.Reflection.MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    }
}
