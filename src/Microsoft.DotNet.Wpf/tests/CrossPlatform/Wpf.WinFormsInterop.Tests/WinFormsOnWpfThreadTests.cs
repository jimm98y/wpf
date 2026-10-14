// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WinForms on a thread whose loop is WPF's Dispatcher (a WPF app that hosts WinForms controls).
// Two things stock does that ours did not, and together they hung Workbench at "Connecting to the
// server..." for good:
//   - creating a WinForms control there does NOT replace the thread's DispatcherSynchronizationContext
//     (.NET installs WindowsFormsSynchronizationContext only where there is no context, or the plain
//     base one) -- ours did, and every await after it posted into WinForms' queue;
//   - a Control.BeginInvoke there runs under WPF's loop (on Windows it is a window message WPF's loop
//     dispatches) -- ours sat in the driver's queue, which only Application.Run reads.
//

using System;
using System.Threading;
using System.Windows.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class WinFormsOnWpfThreadTests
    {
        private static void OnDispatcherThread(Action<Dispatcher> body)
        {
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { body(Dispatcher.CurrentDispatcher); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(20000), "the dispatcher thread did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void ACreatedControlLeavesTheDispatcherContextInPlace()
        {
            OnDispatcherThread(d =>
            {
                string? before = null, after = null;
                d.BeginInvoke(() =>
                {
                    before = SynchronizationContext.Current?.GetType().Name;
                    using var c = new SWF.Panel();
                    c.CreateControl();
                    after = SynchronizationContext.Current?.GetType().Name;
                    d.BeginInvokeShutdown(DispatcherPriority.Background);
                });
                Dispatcher.Run();
                Assert.Equal(nameof(DispatcherSynchronizationContext), before);
                Assert.Equal(nameof(DispatcherSynchronizationContext), after);
            });
        }

        [Fact]
        public void BeginInvokeRunsUnderTheWpfLoop()
        {
            OnDispatcherThread(d =>
            {
                bool ran = false;
                SWF.Panel? c = null;
                d.BeginInvoke(() =>
                {
                    c = new SWF.Panel();
                    c.CreateControl();
                    c.BeginInvoke(new Action(() => { ran = true; d.BeginInvokeShutdown(DispatcherPriority.Background); }));
                });
                var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (s, e) => d.BeginInvokeShutdown(DispatcherPriority.Send), d);
                Dispatcher.Run();
                timeout.Stop();
                c?.Dispose();
                Assert.True(ran, "Control.BeginInvoke never ran under Dispatcher.Run");
            });
        }

        [Fact]
        public void AnAwaitAfterAControlComesBackOnTheDispatcher()
        {
            OnDispatcherThread(d =>
            {
                bool resumed = false;
                d.BeginInvoke(async () =>
                {
                    using var c = new SWF.Panel();
                    c.CreateControl();
                    await System.Threading.Tasks.Task.Delay(50);
                    resumed = Dispatcher.CurrentDispatcher == d;
                    d.BeginInvokeShutdown(DispatcherPriority.Background);
                });
                var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (s, e) => d.BeginInvokeShutdown(DispatcherPriority.Send), d);
                Dispatcher.Run();
                timeout.Stop();
                Assert.True(resumed, "the continuation never ran on the dispatcher");
            });
        }
    }
}
