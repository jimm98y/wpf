// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WPF window owned by a WinForms form, as a third-party dialog over a WinForms main window is
// (Telerik's RadOpenFileDialog over GraphWorX). The form's Handle is the driver's managed handle, not
// an OS window; WPF handed it to CreateWindowEx as the owner and the window was never created
// ("Invalid window handle"). It must be owned by the form's native host window instead.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class WpfWindowOwnedByFormTests
    {
        [Fact]
        public void AWindowOwnedByAFormHandle_IsOwnedByTheFormsHostWindow() => OnSta(() => Run(modal: false));

        [Fact]
        public void ADialogOwnedByAFormHandle_Shows_AndReturns() => OnSta(() => Run(modal: true));

        private static void OnSta(Action body)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the owner is a Win32 window");
            Exception? failure = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run(bool modal)
        {
            using var form = new SWF.Form { Text = "OwnerForm", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 60), ClientSize = new Size(320, 200), ShowInTaskbar = false };
            form.Show();
            Pump(800);
            Assert.True(SWF.EmbeddedScenes.TryGetHostWindow(form, out IntPtr host, out _), "the form has no host window");
            Assert.False(IsWindow(form.Handle) && form.Handle == host, "the form's Handle is its host window here: nothing to resolve");

            var w = new System.Windows.Window { Title = "OwnedWpf", Width = 200, Height = 120, Left = 8030, Top = 90, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            new WindowInteropHelper(w).Owner = form.Handle;
            IntPtr owner = IntPtr.Zero;
            if (modal)
            {
                // closed from inside its own modal loop, once it exists
                var close = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                close.Tick += (_, _) => { close.Stop(); owner = GetWindow(new WindowInteropHelper(w).Handle, GW_OWNER); w.DialogResult = true; };
                close.Start();
                bool? result = w.ShowDialog();
                Assert.True(result == true, "the dialog did not run its loop");
            }
            else
            {
                w.Show();
                owner = GetWindow(new WindowInteropHelper(w).Handle, GW_OWNER);
                w.Close();
            }
            form.Close();
            Assert.Equal(host, owner);
        }

        private static void Pump(int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                Thread.Sleep(15);
            }
        }

        private static readonly System.Reflection.MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        private const uint GW_OWNER = 4;
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    }
}
