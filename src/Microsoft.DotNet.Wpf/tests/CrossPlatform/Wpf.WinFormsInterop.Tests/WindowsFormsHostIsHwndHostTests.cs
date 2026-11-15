// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WindowsFormsHost is an HwndHost, as .NET's is. App code compiled against .NET reaches it through
// HwndHost's members -- Handle, MessageHook, UpdateWindowPos, DpiChanged -- and subclasses override
// BuildWindowCore, DestroyWindowCore and WndProc; with a FrameworkElement base none of that bound to
// anything. The hosted window is the container form, run in HwndHost's foreign mode: its handle is
// the host's Handle, and its messages pass through WndProc and the MessageHook handlers first.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class WindowsFormsHostIsHwndHostTests
    {
        private sealed class RecordingHost : WindowsFormsHost
        {
            public readonly List<int> Messages = new List<int>();
            public int SwallowMessage;

            protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                lock (Messages) Messages.Add(msg);
                if (msg == SwallowMessage)
                {
                    handled = true;
                    return (IntPtr)42;
                }
                return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
            }
        }

        [Fact]
        public void TheHostIsAnHwndHostAndAKeyboardSink()
        {
            RunOnSta(() =>
            {
                var host = new WindowsFormsHost();
                Assert.IsAssignableFrom<HwndHost>(host);
                Assert.IsAssignableFrom<IKeyboardInputSink>(host);
                host.Dispose();
            });
        }

        [Fact]
        public void BeforeItIsInAWindowItHasNoHandleAndRoutesNoMessages()
        {
            RunOnSta(() =>
            {
                var host = new RecordingHost { Child = new SWF.Button { Text = "x" } };
                Assert.Equal(IntPtr.Zero, host.Handle);

                // Nothing is hosted yet, so nothing is the host's to see.
                bool handled = false;
                IntPtr result = HwndHostForeignContent.DispatchMessage(host, (IntPtr)0x1234, 0x000F, IntPtr.Zero, IntPtr.Zero, ref handled);
                Assert.False(handled);
                Assert.Equal(IntPtr.Zero, result);
                Assert.Empty(host.Messages);
                host.Dispose();
            });
        }

        [Fact]
        public void DisposingAHostThatNeverEnteredAWindowDisposesItsChild()
        {
            RunOnSta(() =>
            {
                var button = new SWF.Button { Text = "x" };
                var host = new WindowsFormsHost { Child = button };
                host.Dispose();
                Assert.True(button.IsDisposed);
                host.Dispose();     // twice is fine, as HwndHost's is
            });
        }

        [Fact]
        public void InAWindowTheHandleIsTheContainerAndItsMessagesReachWndProcAndTheHooks()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "only the Windows head has a window per test");
            RunOnSta(() =>
            {
                const int WM_USER = 0x0400;
                var host = new RecordingHost { Child = new SWF.Button { Text = "hosted" }, SwallowMessage = WM_USER + 7 };
                var hooked = new List<int>();
                host.MessageHook += (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    hooked.Add(msg);
                    return IntPtr.Zero;
                };
                var window = new System.Windows.Window { Width = 300, Height = 200, ShowActivated = false, Content = host };
                window.Show();
                try
                {
                    Assert.NotEqual(IntPtr.Zero, host.Handle);
                    SWF.Control container = SWF.Control.FromHandle(host.Handle);
                    Assert.IsAssignableFrom<SWF.Form>(container);
                    Assert.Same(host.Child.Parent, container);

                    // A message to the hosted window goes to WndProc, then to the hooks; one WndProc
                    // handles goes no further.
                    var m = SWF.Message.Create(host.Handle, WM_USER + 1, IntPtr.Zero, IntPtr.Zero);
                    InvokeWndProc(container, ref m);
                    Assert.Contains(WM_USER + 1, host.Messages);
                    Assert.Contains(WM_USER + 1, hooked);

                    m = SWF.Message.Create(host.Handle, WM_USER + 7, IntPtr.Zero, IntPtr.Zero);
                    InvokeWndProc(container, ref m);
                    Assert.Equal((IntPtr)42, m.Result);
                    Assert.DoesNotContain(WM_USER + 7, hooked);
                }
                finally
                {
                    window.Close();
                    host.Dispose();
                }
            });
        }

        private static void InvokeWndProc(SWF.Control control, ref SWF.Message m)
        {
            var method = typeof(SWF.Control).GetMethod("WndProc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            object[] args = { m };
            method.Invoke(control, args);
            m = (SWF.Message)args[0];
        }

        private static void RunOnSta(Action body)
        {
            Exception? thrown = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { thrown = e; }
            });
            if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (thrown != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown).Throw();
        }
    }
}
