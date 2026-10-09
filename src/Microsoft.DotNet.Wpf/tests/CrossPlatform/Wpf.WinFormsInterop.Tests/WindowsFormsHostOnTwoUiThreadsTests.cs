// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WinForms hosted in WPF on two WPF UI threads at once.
//
// WindowsFormsHost publishes its controls' scenes into EmbeddedContent from a render tick, and the
// WPF compositor overlays them. That tick was ONE for the process -- subscribed to whichever thread's
// CompositionTarget.Rendering came first -- and EmbeddedContent.Set replaced ONE process-wide set.
// With a second UI thread hosting WinForms, the first thread's tick walked the second thread's hosts
// (IsVisible and TransformToAncestor on another thread's elements throw), and each thread's publish
// erased the other's controls. Now each Dispatcher has its own tick and publishes its own set, and
// the caret belongs to the thread (and window) that drew it, as a user32 caret does.
//

using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class WindowsFormsHostOnTwoUiThreadsTests
    {
        [Fact]
        public void EachUiThreadPublishesItsOwnHostedControls()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "only the Windows head has a window per UI thread");

            for (int run = 0; run < 3; run++)
            {
                using var a = new Ui("WFH A");
                using var b = new Ui("WFH B");
                Exception? unhandled = null;
                a.Dispatcher.UnhandledException += (s, e) => { unhandled ??= e.Exception; e.Handled = true; };
                b.Dispatcher.UnhandledException += (s, e) => { unhandled ??= e.Exception; e.Handled = true; };

                (System.Windows.Window wa, IntPtr ha) = a.Invoke(() => Show("WFH A", 40));
                (System.Windows.Window wb, IntPtr hb) = b.Invoke(() => Show("WFH B", 520));

                // Both threads' hosts publish, each tagged with its own window, and neither erases
                // the other's.
                bool both = SpinUntil(() =>
                {
                    IntPtr[] windows = PublishedWindows();
                    return windows.Contains(ha) && windows.Contains(hb);
                }, 10000);
                Assert.Null(unhandled);
                Assert.True(both, $"run {run}: published windows [{string.Join(", ", PublishedWindows())}], " +
                                  $"expected both 0x{ha:x} and 0x{hb:x}");

                // Still both, a few ticks later (a shared set would flip between them).
                Thread.Sleep(300);
                IntPtr[] later = PublishedWindows();
                Assert.Contains(ha, later);
                Assert.Contains(hb, later);

                // Closing one thread's window withdraws its controls and leaves the other's.
                a.Invoke(wa.Close);
                Assert.True(SpinUntil(() => !PublishedWindows().Contains(ha), 5000), "thread A's controls outlived its window");
                Assert.Contains(hb, PublishedWindows());

                b.Invoke(wb.Close);
                Assert.Null(unhandled);
            }
        }

        private static (System.Windows.Window, IntPtr) Show(string title, int left)
        {
            var host = new WindowsFormsHost { Child = new SWF.Button { Text = title, Width = 160, Height = 40 } };
            var w = new System.Windows.Window
            {
                Title = title,
                Width = 420,
                Height = 260,
                Left = left,
                Top = 80,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                ShowActivated = false,
                Content = host,
            };
            w.Show();
            return (w, new WindowInteropHelper(w).Handle);
        }

        /// <summary>The windows the hosted scenes currently published are tagged with, across every
        /// publishing thread (EmbeddedContent's registry is internal).</summary>
        private static IntPtr[] PublishedWindows()
        {
            // By name: this project reaches the engine only through WindowsFormsIntegration.
            Type ec = Assembly.Load("Microsoft.Wpf.Interop.WebGpu")
                .GetType("Microsoft.Wpf.Interop.WebGpu.Composition.EmbeddedContent", throwOnError: true)!;
            var gate = ec.GetField("s_lock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var publishers = (IEnumerable)ec.GetField("s_publishers", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            lock (gate)
            {
                return publishers.Cast<object>()
                    .SelectMany(p => ((IEnumerable)p.GetType().GetField("Items")!.GetValue(p)!).Cast<object>())
                    .Select(i => (IntPtr)i.GetType().GetProperty("Window")!.GetValue(i)!)
                    .Distinct()
                    .ToArray();
            }
        }

        private static bool SpinUntil(Func<bool> condition, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(25);
            }
            return condition();
        }

        private sealed class Ui : IDisposable
        {
            private readonly Thread _thread;
            public Dispatcher Dispatcher { get; }

            public Ui(string name)
            {
                Dispatcher? d = null;
                using var ready = new ManualResetEventSlim();
                _thread = new Thread(() => { d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); })
                {
                    IsBackground = true,
                    Name = name,
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait();
                Dispatcher = d!;
            }

            public T Invoke<T>(Func<T> body)
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

            public void Invoke(Action body) => Invoke<object?>(() => { body(); return null; });

            public void Dispose()
            {
                Dispatcher.InvokeShutdown();
                _thread.Join(30000);
            }
        }
    }
}
