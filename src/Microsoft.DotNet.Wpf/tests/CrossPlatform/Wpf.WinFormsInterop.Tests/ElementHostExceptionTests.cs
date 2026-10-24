// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An exception thrown by the application's own code in a WPF tree hosted in an ElementHost goes where
// it goes on Windows: WPF's Dispatcher.UnhandledException, then WinForms' Application.ThreadException.
// The host carries on. Ours caught it in the pump that runs the tree, called it a hosting failure and
// stopped pumping that ElementHost for good (app's NullReferenceException after its component
// editor closed).
//

using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ElementHostExceptionTests
    {
        [Fact]
        public void AnExceptionInAWpfHandler_ReachesThreadException_AndTheHostCarriesOn()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a real hosted WPF window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { Run(); }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A hosted WinForms control that throws from its window procedure on a message of
        /// the application's own.</summary>
        private sealed class Thrower : SWF.Control
        {
            public const int WM_APPBUG = 0x0400 + 77;
            public int Handled;
            public bool ThrowNext = true;
            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == WM_APPBUG)
                {
                    Handled++;
                    if (ThrowNext) { ThrowNext = false; throw new AppBug(); }
                    return;
                }
                base.WndProc(ref m);
            }
        }

        /// <summary>The other way round: WinForms in WPF. An exception from a hosted control's own code
        /// reaches Application.ThreadException, and the host goes on running the control.</summary>
        [Fact]
        public void AnExceptionInAHostedWinFormsControl_ReachesThreadException_AndTheHostCarriesOn()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a WPF window of the Windows head");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                var seen = new System.Collections.Generic.List<Exception>();
                System.Threading.ThreadExceptionEventHandler onThread = (s, e) => seen.Add(e.Exception);
                SWF.Application.ThreadException += onThread;
                System.Windows.Window? w = null;
                try
                {
                    var ctl = new Thrower { Width = 160, Height = 40 };
                    var host = new WindowsFormsHost { Child = ctl };
                    w = new System.Windows.Window
                    {
                        Title = "WfhExceptionTest", Width = 300, Height = 200, Left = 8000, Top = 80,
                        WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                        ShowActivated = false, ShowInTaskbar = false, Content = host,
                    };
                    w.Show();
                    WpfPump(() => ctl.IsHandleCreated, 10);
                    Assert.True(ctl.IsHandleCreated, "the hosted control never got a handle");

                    MethodInfo post = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                        .GetMethod("PostMessage", BindingFlags.NonPublic | BindingFlags.Static, null,
                            new[] { typeof(IntPtr), typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.Msg")!, typeof(IntPtr), typeof(IntPtr) }, null)!;
                    Type msgType = post.GetParameters()[1].ParameterType;
                    void Post() => post.Invoke(null, new object[] { ctl.Handle, Enum.ToObject(msgType, Thrower.WM_APPBUG), IntPtr.Zero, IntPtr.Zero });

                    Post();
                    WpfPump(() => seen.Count > 0, 5);
                    Assert.Single(seen);
                    Assert.IsType<AppBug>(seen[0]);

                    Post();
                    WpfPump(() => ctl.Handled == 2, 5);
                    Assert.Equal(2, ctl.Handled);
                    Assert.Single(seen);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    SWF.Application.ThreadException -= onThread;
                    try { w?.Close(); } catch { }
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void WpfPump(Func<bool> until, double seconds)
        {
            var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            for (var end = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < end && !until();)
            {
                d.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Thread.Sleep(15);
            }
        }

        private sealed class AppBug : Exception
        {
            public AppBug() : base("thrown by the application's Click handler") { }
        }

        private static void Run()
        {
            // Off screen and never activated: nothing appears on the desktop.
            using var form = new SWF.Form
            {
                Text = "ElementHostExceptionTest", StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ClientSize = new Size(300, 200), ShowInTaskbar = false,
            };
            var button = new System.Windows.Controls.Button { Content = "Go" };
            var root = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.White, Child = button };
            int clicks = 0;
            bool throwNext = true;
            button.Click += (s, e) =>
            {
                clicks++;
                if (throwNext) { throwNext = false; throw new AppBug(); }
            };
            var host = new ElementHost { Dock = SWF.DockStyle.Fill, Child = root };
            form.Controls.Add(host);

            var seen = new System.Collections.Generic.List<Exception>();
            System.Threading.ThreadExceptionEventHandler onThread = (s, e) => seen.Add(e.Exception);
            SWF.Application.ThreadException += onThread;
            try
            {
                form.Show();
                Pump(() => host.IsChildLive, 10);
                Assert.True(host.IsChildLive, "the hosted tree never rendered");

                // The click is dispatched by the WPF dispatcher, run from the host's pump -- as a real
                // click on the hosted window is.
                void Click() => button.Dispatcher.BeginInvoke(new Action(() =>
                    button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent))),
                    System.Windows.Threading.DispatcherPriority.Input);

                Click();
                Pump(() => seen.Count > 0, 5);
                Assert.Single(seen);
                Assert.IsType<AppBug>(seen[0]);

                // The host goes on running the tree: the next click is handled ...
                Click();
                Pump(() => clicks == 2, 5);
                Assert.Equal(2, clicks);
                Assert.Single(seen);

                // ... and what the tree draws still reaches the host.
                int v0 = Version(host);
                root.Background = System.Windows.Media.Brushes.Red;
                Pump(() => Version(host) != v0, 5);
                Assert.NotEqual(v0, Version(host));
                form.Close();
            }
            finally { SWF.Application.ThreadException -= onThread; }
        }

        private static int Version(ElementHost host) =>
            (int)typeof(ElementHost).GetProperty("Version", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;

        private static void Pump(Func<bool> until, double seconds)
        {
            for (var end = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < end && !until();)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                Thread.Sleep(15);
            }
        }

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);
    }
}
