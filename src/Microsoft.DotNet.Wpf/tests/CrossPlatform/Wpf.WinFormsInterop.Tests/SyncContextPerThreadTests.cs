// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A UI thread's WindowsFormsSynchronizationContext posts to that thread. Mono's posted every thread's work through
// one static control, made by the first thread that touched the type: once that thread had ended, a later UI thread's
// await never came back (its continuation sat in a dead thread's queue) -- a second UI thread's async event handler
// stopped at its first await.
//

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class SyncContextPerThreadTests
    {
        [Fact]
        public void AnAwaitOnASecondUiThread_ComesBackToIt_AfterTheFirstHasEnded()
        {
            // the first UI thread: a control installs its context, and the thread ends
            var first = new Thread(() =>
            {
                using var control = new SWF.Control();
                control.CreateControl();
                SynchronizationContext.Current?.Post(_ => { }, null);
            });
            first.TrySetApartmentState(ApartmentState.STA);
            first.Start();
            first.Join();

            Exception? failure = null;
            int uiThread = -1, resumedOn = -2, postedOn = -3;
            var second = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "SyncContextTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(200, 120), ShowInTaskbar = false,
                    };
                    uiThread = Environment.CurrentManagedThreadId;
                    form.Shown += async (s, e) =>
                    {
                        try
                        {
                            SynchronizationContext context = SynchronizationContext.Current!;
                            await Task.Run(() => { });
                            resumedOn = Environment.CurrentManagedThreadId;
                            var posted = new TaskCompletionSource();
                            ThreadPool.QueueUserWorkItem(_ => context.Post(__ => { postedOn = Environment.CurrentManagedThreadId; posted.SetResult(); }, null));
                            await posted.Task;
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    SWF.Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            second.TrySetApartmentState(ApartmentState.STA);
            second.Start();
            Assert.True(second.Join(TimeSpan.FromSeconds(30)), "the second thread's await never came back");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal(uiThread, resumedOn);
            Assert.Equal(uiThread, postedOn);
        }

        [Fact]
        public void Send_ToAThreadThatHasEnded_Throws()
        {
            SynchronizationContext? context = null;
            var t = new Thread(() =>
            {
                using var control = new SWF.Control();
                context = SynchronizationContext.Current;
            });
            t.Start();
            t.Join();
            Assert.IsType<SWF.WindowsFormsSynchronizationContext>(context);
            Assert.Throws<System.ComponentModel.InvalidAsynchronousStateException>(() => context!.Send(_ => { }, null));
        }
    }
}
