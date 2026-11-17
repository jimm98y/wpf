// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Application.SetUnhandledExceptionMode decides what becomes of an exception a window procedure throws:
// ThrowException lets it go on up (out of the call that sent the message, out of Application.Run),
// Automatic and CatchException hand it to OnThreadException. It was an empty stub: every exception was
// caught, and an application that asked for its exceptions to reach AppDomain.UnhandledException (to log
// and exit) never saw one. The mode is fixed once the thread has a window, as in .NET.
//

using System;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class UnhandledExceptionModeTests
    {
        private sealed class Boom : Exception { }

        private const int WM_USER = 0x0400;

        // throws from its window procedure on WM_USER
        private sealed class Throwing : SWF.Control
        {
            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == WM_USER) throw new Boom();
                base.WndProc(ref m);
            }
        }

        // the driver's SendMessage: the window procedure called through NativeWindow, as every message is
        private static void Send(IntPtr hwnd, int msg)
        {
            Type xplat = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!;
            Type msgType = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.Msg")!;
            MethodInfo send = xplat.GetMethod("SendMessage", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(IntPtr), msgType, typeof(IntPtr), typeof(IntPtr) }, null)!;
            try { send.Invoke(null, new object[] { hwnd, Enum.ToObject(msgType, msg), IntPtr.Zero, IntPtr.Zero }); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        }

        private static Exception? OnThread(Action body)
        {
            Exception? failure = null;
            var t = new Thread(() => { try { body(); } catch (Exception e) { failure = e; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return failure;
        }

        [Fact]
        public void ThrowException_LetsTheExceptionLeaveTheWindowProcedure()
        {
            Exception? escaped = null;
            Exception? failure = OnThread(() =>
            {
                SWF.Application.SetUnhandledExceptionMode(SWF.UnhandledExceptionMode.ThrowException, threadScope: true);
                using var c = new Throwing { Bounds = new Rectangle(0, 0, 40, 40) };
                c.CreateControl();
                try { Send(c.Handle, WM_USER); } catch (Boom e) { escaped = e; }
            });
            Assert.Null(failure);
            Assert.NotNull(escaped);
        }

        [Fact]
        public void TheThreadsMode_CannotChangeOnceItHasAWindow()
        {
            Exception? thrown = null;
            Exception? failure = OnThread(() =>
            {
                using var c = new SWF.Control();
                c.CreateControl();
                try { SWF.Application.SetUnhandledExceptionMode(SWF.UnhandledExceptionMode.CatchException, threadScope: true); }
                catch (Exception e) { thrown = e; }
            });
            Assert.Null(failure);
            Assert.IsType<InvalidOperationException>(thrown);
        }

        [Fact]
        public void AnUndefinedMode_IsRejected()
        {
            Exception? failure = OnThread(() =>
                Assert.Throws<InvalidEnumArgumentException>(() => SWF.Application.SetUnhandledExceptionMode((SWF.UnhandledExceptionMode)7, threadScope: true)));
            Assert.Null(failure);
        }
    }
}
