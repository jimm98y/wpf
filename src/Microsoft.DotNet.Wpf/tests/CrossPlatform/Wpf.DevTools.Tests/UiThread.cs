// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The thread a Window is allowed to exist on, and how a test reaches it.
//
// On macOS that is the PROCESS MAIN THREAD and nothing else, so Program.Main keeps it pumping a
// Dispatcher and hands it here before the runner starts. Everywhere else a dispatcher thread of our
// own is fine, and one is created on first use.
//

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

namespace Wpf.DevTools.Tests
{
    internal static class UiThread
    {
        private static Dispatcher? s_dispatcher;
        private static readonly object s_gate = new object();

        /// <summary>Adopts a dispatcher owned by another thread instead of creating one.</summary>
        public static void UseDispatcher(Dispatcher dispatcher)
        {
            lock (s_gate) { s_dispatcher = dispatcher; }
        }

        private static Dispatcher Dispatcher
        {
            get
            {
                lock (s_gate)
                {
                    if (s_dispatcher is not null) return s_dispatcher;

                    using var ready = new ManualResetEventSlim();
                    var thread = new Thread(() =>
                    {
                        s_dispatcher = Dispatcher.CurrentDispatcher;
                        ready.Set();
                        Dispatcher.Run();
                    })
                    {
                        IsBackground = true,
                        Name = "WPF devtools tests UI thread",
                    };

                    // STA is a COM concept and COM exists only on Windows; SetApartmentState throws
                    // PlatformNotSupportedException elsewhere. WPF's thread affinity is the
                    // Dispatcher's, not the apartment's, so off Windows the default is fine.
                    if (OperatingSystem.IsWindows())
                    {
                        thread.SetApartmentState(ApartmentState.STA);
                    }

                    thread.Start();
                    ready.Wait();
                    return s_dispatcher!;
                }
            }
        }

        /// <summary>Runs <paramref name="body"/> on the UI thread and rethrows whatever it threw.</summary>
        public static T Invoke<T>(Func<T> body)
        {
            T result = default!;
            Exception? failure = null;

            Dispatcher.Invoke(() =>
            {
                try { result = body(); }
                catch (Exception e) { failure = e; }
            });

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        public static void Invoke(Action body) => Invoke<object?>(() => { body(); return null; });

        /// <summary>
        /// Waits for something the UI thread will do on its own, without blocking it.
        /// </summary>
        /// <remarks>
        /// The state changes under test are ASYNCHRONOUS on every head: a window is asked to
        /// maximize, the window server does it when it does it, and the head notices on its next
        /// pump. So the test thread sleeps -- which is what lets the UI thread pump at all -- and
        /// re-reads the condition through Invoke between naps.
        /// </remarks>
        public static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (Invoke(condition)) return true;
                Thread.Sleep(25);
            }
            return Invoke(condition);
        }
    }
}
