// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Blocking on the browser head, where it can be had: the multithreaded runtime (WasmEnableThreads), on
// which the app's code runs on a worker (the runtime's "deputy" thread) that may block, as an
// application's UI thread does on Windows in a modal loop. A modal loop -- WinForms' ShowDialog, WPF's
// nested DispatcherFrame -- then runs to its end there instead of being refused, and the code after it
// gets its result.
//
// What it must keep doing while it blocks is what the thread's JavaScript event loop did for it: the
// page's frames (BrowserWindow.NextFrameOnPage), async continuations, a WebSocket's data -- everything
// posted to the thread's JSSynchronizationContext. A modal loop drains that queue between its rounds
// (DrainJSQueue), as Windows' modal loop dispatches whatever is sent to the thread, and waits between
// them with a wait that takes the calls other threads proxy to this one (Wait), which is how what is
// posted gets there. On the single-threaded runtime none of this exists and CanBlock is false.

using System;
using System.Reflection;
using System.Threading;

namespace MS.Internal.Interop
{
    public static class BrowserBlocking
    {
        // The multithreaded runtime's: a field of its Thread alone (the JS interop threads, which refuse a
        // blocking wait unless asked otherwise), and its way of asking otherwise.
        private static readonly FieldInfo s_throwOnBlockingWait = typeof(Thread).GetField("ThrowOnBlockingWaitOnJSInteropThread", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo s_forceBlockingWait = typeof(Thread).GetMethod("ForceBlockingWait", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly ManualResetEventSlim s_never = new ManualResetEventSlim(false);
        private static SynchronizationContext s_jsContext;
        private static MethodInfo s_jsPump;
        // The context's queue (a Channel's reader) and its work items, run here as Pump runs them -- Pump
        // itself ends by re-arming the context's wait for new data, cancelling the last one with an
        // exception, which on a queue that always has a frame in it was hundreds a second. The context's
        // own wait stays armed from before, and its pump finds the queue drained.
        private static object s_jsReader;
        private static MethodInfo s_jsTryRead;
        private static FieldInfo s_itemCallback, s_itemData, s_itemSignal;

        /// <summary>Whether a modal loop may block: the browser's multithreaded runtime.</summary>
        public static bool CanBlock => OperatingSystem.IsBrowser() && s_throwOnBlockingWait != null && s_forceBlockingWait != null;

        /// <summary>The thread's JS context, from whoever runs on it first (a pump begun on it, before any
        /// framework installs its own context in its place).</summary>
        public static void NoteContext()
        {
            if (s_jsContext != null) return;
            SynchronizationContext current = SynchronizationContext.Current;
            if (current?.GetType().FullName != "System.Runtime.InteropServices.JavaScript.JSSynchronizationContext") return;
            s_jsContext = current;
            s_jsPump = current.GetType().GetMethod("Pump", BindingFlags.NonPublic | BindingFlags.Instance);
            object queue = current.GetType().GetField("Queue", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(current);
            s_jsReader = queue?.GetType().GetProperty("Reader")?.GetValue(queue);
            s_jsTryRead = s_jsReader?.GetType().GetMethod("TryRead");
            Type item = current.GetType().GetNestedType("WorkItem", BindingFlags.NonPublic | BindingFlags.Public);
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            s_itemCallback = item?.GetField("Callback", f);
            s_itemData = item?.GetField("Data", f);
            s_itemSignal = item?.GetField("Signal", f);
            if (s_itemCallback == null || s_itemData == null) s_jsTryRead = null;
        }

        /// <summary>What has been posted to the thread's JS context -- a frame of the page, a continuation,
        /// a socket's data -- run now, as the thread would run it had it returned to its event loop. An
        /// exception from it is the caller's to report.</summary>
        public static void DrainJSQueue()
        {
            if (s_jsContext == null || s_jsPump == null) return;
            if (s_jsTryRead != null)
            {
                var args = new object[1];
                while (s_jsTryRead.Invoke(s_jsReader, args) is true)
                {
                    object work = args[0];
                    var callback = (SendOrPostCallback)s_itemCallback.GetValue(work);
                    try
                    {
                        callback?.Invoke(s_itemData.GetValue(work));
                    }
                    finally
                    {
                        (s_itemSignal?.GetValue(work) as ManualResetEventSlim)?.Set();
                    }
                    args[0] = null;
                }
                return;
            }
            try
            {
                s_jsPump.Invoke(s_jsContext, null);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerException);
            }
        }

        /// <summary>A timed wait the runtime allows on a JS interop thread when asked (ForceBlockingWait); it
        /// also lets the thread take the calls other threads proxy to it while it waits.</summary>
        public static void Wait(int ms)
        {
            if (s_throwOnBlockingWait.GetValue(null) is true)
                s_forceBlockingWait.Invoke(null, new object[] { (Action<object>)(_ => s_never.Wait(ms)), null });
            else
                s_never.Wait(ms);
        }
    }
}
