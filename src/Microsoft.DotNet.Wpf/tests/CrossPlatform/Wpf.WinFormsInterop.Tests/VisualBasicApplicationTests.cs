// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Visual Basic's application framework (src/WinFormsInterop/vbforms: .NET's Microsoft.VisualBasic.Forms, built
// against this WinForms). A VB WinForms app with My.Application derives from WindowsFormsApplicationBase and
// starts through its Run; before, the assembly was not there and such an app did not start.
//

using System;
using System.Drawing;
using System.Threading;
using Microsoft.VisualBasic.ApplicationServices;
using Microsoft.VisualBasic.Devices;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class VisualBasicApplicationTests
    {
        private sealed class App : WindowsFormsApplicationBase
        {
            public bool Started, ShutDown, MainShown;

            // ApplicationDefined: Windows authentication asks WindowsIdentity, which this test process has as .NET's
            // Windows-only build (an app run by wpfrun gets the stack's)
            public App() : base(AuthenticationMode.ApplicationDefined)
            {
                IsSingleInstance = false;
                ShutdownStyle = ShutdownMode.AfterMainFormCloses;
                Startup += (s, e) => Started = true;
                Shutdown += (s, e) => ShutDown = true;
            }

            protected override void OnCreateMainForm()
            {
                var form = new SWF.Form
                {
                    Text = "VisualBasicApplicationTest", StartPosition = SWF.FormStartPosition.Manual,
                    Location = new Point(8000, 80), Size = new Size(200, 120), ShowInTaskbar = false,
                };
                form.Shown += (s, e) =>
                {
                    MainShown = true;
                    form.BeginInvoke(new Action(form.Close));
                };
                MainForm = form;
            }
        }

        [Fact]
        public void AnApplication_RunsItsMainForm_AndRaisesStartupAndShutdown()
        {
            App? app = null;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    app = new App();
                    app.Run(new[] { "VisualBasicApplicationTest" });
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the application did not end with its main form");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(app!.Started);
            Assert.True(app.MainShown);
            Assert.True(app.ShutDown);
        }

        // (its kernel32 and user32 calls -- memory, the keyboard -- reach the stack's Win32 layer through the launcher's
        // rewrite, which this test process does not have)
        [Fact]
        public void MyComputer_AnswersFromThisMachine()
        {
            var computer = new Computer();
            Assert.False(string.IsNullOrEmpty(computer.Name));
            Assert.True(computer.Screen.Bounds.Width > 0);
            Assert.False(string.IsNullOrEmpty(computer.Info.OSFullName));
            computer.Clipboard.SetText("from VB");
            Assert.Equal("from VB", computer.Clipboard.GetText());
            computer.Clipboard.Clear();
        }
    }
}
