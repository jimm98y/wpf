// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// More than one WPF UI thread.
//
// Stock WPF lets any thread run a Dispatcher of its own and own windows there: a splash screen
// animating while the main thread loads, a busy indicator, a tool window isolated from a hung main
// window. On the WebGPU stack this ABORTED THE PROCESS -- two windows on two STA threads, each in its
// own Dispatcher.Run, and wgpu panicked inside wgpuSurfaceConfigure ("Invalid surface",
// 0xC0000409) -- because every thread's MediaContext funnelled into ONE composition sink: one handle
// table, one surface dictionary, one GPU device, driven from two threads at once.
//
// The sink is now per UI thread (DUCE.ManagedComposition), so what these tests pin is:
//   * two UI threads both render and present at the same time, and close in either order;
//   * a splash window keeps presenting while the main UI thread is blocked;
//   * a window can come up on a new thread after another thread's window has gone;
//   * a UI thread that exits gives its GPU device back (and one that dies without shutting its
//     Dispatcher down is reaped);
//   * RenderTargetBitmap on worker threads still produces the right pixels.
//
// Every scenario repeats (WPF_MT_REPEAT, default 3) because what broke was a race, and a race that
// is lost one time in ten passes a single run nine times in ten.
//
// Windows only for the positive cases: macOS and the Wayland head serve windows from one thread by
// design, and are covered by the refusal tests at the bottom (a catchable exception, never an abort).
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Wpf.Window.Tests
{
    public class MultipleUiThreadTests
    {
        private static readonly int s_repeat =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_MT_REPEAT"), out int r) && r > 0 ? r : 3;

        // ---- the scenarios ---------------------------------------------------------------------

        [Fact]
        public void TwoWindowsOnTwoUiThreadsBothPresentAndCloseInEitherOrder()
        {
            SkipUnlessMultiThreadHead();

            for (int i = 0; i < s_repeat; i++)
            {
                bool firstClosesFirst = (i % 2) == 0;
                using var a = new UiThreadHost("MT A");
                using var b = new UiThreadHost("MT B");

                System.Windows.Window wa = a.Invoke(() => ShowWindow("MT A", Colors.SteelBlue, 40));
                System.Windows.Window wb = b.Invoke(() => ShowWindow("MT B", Colors.IndianRed, 460));

                Assert.True(WaitPresented(a, 3), $"run {i}: thread A's window never presented");
                Assert.True(WaitPresented(b, 3), $"run {i}: thread B's window never presented");

                object sinkA = a.Invoke(Sink.Current)!;
                object sinkB = b.Invoke(Sink.Current)!;

                // Both threads animate at once: every frame of each is composed, rendered and
                // presented concurrently with the other's. This is the load that aborted the process
                // (verified against the single-sink build: 0xC0000409 from wgpuSurfaceConfigure).
                int beforeA = Sink.PresentedFrames(sinkA), beforeB = Sink.PresentedFrames(sinkB);
                a.Invoke(() => Animate(wa));
                b.Invoke(() => Animate(wb));
                Assert.True(SpinUntil(() => Sink.PresentedFrames(sinkA) >= beforeA + 10
                                         && Sink.PresentedFrames(sinkB) >= beforeB + 10, 8000),
                    $"run {i}: concurrent frames A {Sink.PresentedFrames(sinkA) - beforeA}, " +
                    $"B {Sink.PresentedFrames(sinkB) - beforeB} (want 10 each)");

                Assert.NotSame(sinkA, sinkB);   // one compositor per UI thread, not one for the process

                UiThreadHost first = firstClosesFirst ? a : b, second = firstClosesFirst ? b : a;
                System.Windows.Window w1 = firstClosesFirst ? wa : wb, w2 = firstClosesFirst ? wb : wa;
                object sink2 = firstClosesFirst ? sinkB : sinkA;

                first.Invoke(w1.Close);
                first.Shutdown();

                // The survivor is unaffected by its neighbour's teardown.
                int before2 = Sink.PresentedFrames(sink2);
                Assert.True(SpinUntil(() => Sink.PresentedFrames(sink2) >= before2 + 5, 5000),
                    $"run {i}: the remaining window stopped presenting when the other thread exited");

                second.Invoke(w2.Close);
                second.Shutdown();
            }
        }

        [Fact]
        public void ASplashWindowOnASecondThreadPresentsWhileTheMainThreadIsBlocked()
        {
            SkipUnlessMultiThreadHead();

            for (int i = 0; i < s_repeat; i++)
            {
                // The suite's own UI thread stands in for the application's main thread.
                System.Windows.Window main = UiThread.Invoke(() => ShowWindow("MT main", Colors.DarkSeaGreen, 40));
                Assert.True(SpinUntil(() => UiThread.Invoke(() => Sink.PresentedFrames(Sink.Current()!)) > 0, 5000),
                    "the main thread's window never presented");

                using var splash = new UiThreadHost("MT splash");
                System.Windows.Window sw = splash.Invoke(() =>
                {
                    System.Windows.Window w = ShowWindow("MT splash", Colors.Goldenrod, 460);
                    Animate(w);
                    return w;
                });
                Assert.True(WaitPresented(splash, 3), $"run {i}: the splash never presented");
                object splashSink = splash.Invoke(Sink.Current)!;

                // Block the main UI thread for a while, the way an application loading on it does.
                using var blocked = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                Dispatcher mainDispatcher = UiThread.Invoke(() => Dispatcher.CurrentDispatcher);
                _ = mainDispatcher.InvokeAsync(() => { blocked.Set(); release.Wait(10000); });
                Assert.True(blocked.Wait(5000));

                int before = Sink.PresentedFrames(splashSink);
                bool progressed = SpinUntil(() => Sink.PresentedFrames(splashSink) >= before + 10, 5000);
                int during = Sink.PresentedFrames(splashSink) - before;
                release.Set();
                Assert.True(progressed, $"run {i}: the splash presented {during} frame(s) while the main thread was blocked");

                splash.Invoke(sw.Close);
                splash.Shutdown();
                UiThread.Invoke(main.Close);
            }
        }

        [Fact]
        public void AWindowComesUpOnANewThreadAfterAnotherThreadsWindowClosed()
        {
            SkipUnlessMultiThreadHead();

            for (int i = 0; i < s_repeat; i++)
            {
                using (var a = new UiThreadHost("MT before"))
                {
                    System.Windows.Window wa = a.Invoke(() => ShowWindow("MT before", Colors.SlateBlue, 40));
                    Assert.True(WaitPresented(a, 3), $"run {i}: the first window never presented");
                    a.Invoke(wa.Close);
                    // Thread A stays alive with its window gone for one variant, and has exited for
                    // the other: the next thread must not care which.
                    if ((i % 2) == 0) a.Shutdown();

                    using var b = new UiThreadHost("MT after");
                    System.Windows.Window wb = b.Invoke(() => ShowWindow("MT after", Colors.Tomato, 460));
                    Assert.True(WaitPresented(b, 3), $"run {i}: a window on a new thread never presented");
                    b.Invoke(wb.Close);
                    b.Shutdown();
                }
            }
        }

        [Fact]
        public void AUiThreadThatExitsReleasesItsGpuResources()
        {
            SkipUnlessMultiThreadHead();

            for (int i = 0; i < s_repeat; i++)
            {
                int liveBefore = Sink.LiveCount();
                object sink;
                using (var t = new UiThreadHost("MT exits"))
                {
                    System.Windows.Window w = t.Invoke(() => ShowWindow("MT exits", Colors.Orchid, 40));
                    Assert.True(WaitPresented(t, 3), $"run {i}: the window never presented");
                    sink = t.Invoke(Sink.Current)!;
                    Assert.True(Sink.HasGpu(sink), "a presenting sink has a device");
                    Assert.Equal(liveBefore + 1, Sink.LiveCount());

                    t.Invoke(w.Close);
                    t.Shutdown();   // Dispatcher shutdown -> MediaContext disposed -> last channel closed
                }

                Assert.False(Sink.HasGpu(sink), $"run {i}: the exited thread's sink still holds its GPU device");
                Assert.Equal(liveBefore, Sink.LiveCount());
                Assert.Equal(liveBefore, Sink.LiveThreadSinks());
            }
        }

        [Fact]
        public void AThreadThatDiesWithoutShuttingDownIsReaped()
        {
            SkipUnlessMultiThreadHead();

            int liveBefore = Sink.LiveCount();
            object? sink = null;
            bool rendered = false;
            Exception? failure = null;

            // A worker that renders once and returns: it never runs (or shuts down) a Dispatcher, so
            // its MediaContext and channels are never closed.
            var worker = new Thread(() =>
            {
                try
                {
                    rendered = RenderedColourIs(Colors.Crimson, RenderOnce(Colors.Crimson));
                    sink = Sink.Current();
                }
                catch (Exception e) { failure = e; }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join();

            Assert.Null(failure);
            Assert.True(rendered, "the worker's RenderTargetBitmap came back wrong");
            Assert.NotNull(sink);
            Assert.True(SpinUntil(() => !Sink.HasGpu(sink!), 10000),
                "a dead thread's sink was never reaped; its GPU device lives until the process exits");
            Assert.True(SpinUntil(() => Sink.LiveCount() == liveBefore, 5000),
                $"live sinks {Sink.LiveCount()}, expected {liveBefore}");
        }

        [Fact]
        public void RenderTargetBitmapOnWorkerThreadsStillWorks()
        {
            SkipUnlessManagedComposition();

            // Alongside a UI thread that is presenting, so the workers' sinks run next to a live one.
            System.Windows.Window? main = null;
            if (OperatingSystem.IsWindows())
            {
                main = UiThread.Invoke(() => ShowWindow("MT rtb", Colors.CadetBlue, 40));
                UiThread.Invoke(() => Animate(main));
            }

            try
            {
                Color[] colours = { Colors.Red, Colors.Lime, Colors.Blue, Colors.Gold };
                for (int i = 0; i < s_repeat; i++)
                {
                    var failures = new List<string>();
                    var threads = colours.Select(c => new Thread(() =>
                    {
                        try
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                byte[] px = RenderOnce(c);
                                if (!RenderedColourIs(c, px))
                                    lock (failures) failures.Add($"{c}: centre pixel " +
                                        $"B{px[Centre]} G{px[Centre + 1]} R{px[Centre + 2]} A{px[Centre + 3]}");
                            }
                        }
                        catch (Exception e)
                        {
                            lock (failures) failures.Add($"{c}: {e.GetType().Name}: {e.Message}");
                        }
                        finally
                        {
                            Dispatcher.CurrentDispatcher.InvokeShutdown();
                        }
                    })).ToList();

                    foreach (Thread t in threads)
                    {
                        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
                        t.Start();
                    }
                    foreach (Thread t in threads) Assert.True(t.Join(60000), "a RenderTargetBitmap worker hung");
                    Assert.True(failures.Count == 0, $"run {i}: " + string.Join("; ", failures));
                }
            }
            finally
            {
                if (main != null) UiThread.Invoke(main.Close);
            }
        }

        // ---- the heads that have one UI thread: refused, catchably ------------------------------

        [Fact]
        public void OnMacOSASecondThreadsWindowIsRefusedNotAborted()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit's main-thread rule");

            using var t = new UiThreadHost("MT mac");
            Exception? e = t.Invoke<Exception?>(() =>
            {
                try { ShowWindow("MT mac", Colors.Gray, 40); return null; }
                catch (Exception ex) { return ex; }
            });
            Assert.NotNull(e);
            Assert.IsType<InvalidOperationException>(e is TargetInvocationException tie ? tie.InnerException : e);
            t.Shutdown();
        }

        [Fact]
        public void OnWaylandASecondThreadsWindowIsRefusedNotAborted()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid(), "the Wayland head");

            // The suite's UI thread owns the connection once it has had a window.
            System.Windows.Window first = UiThread.Invoke(() => ShowWindow("MT wl main", Colors.Gray, 40));
            try
            {
                using var t = new UiThreadHost("MT wl second");
                Exception? e = t.Invoke<Exception?>(() =>
                {
                    try { ShowWindow("MT wl second", Colors.Gray, 460); return null; }
                    catch (Exception ex) { return ex; }
                });
                Assert.NotNull(e);
                t.Shutdown();
            }
            finally
            {
                UiThread.Invoke(first.Close);
            }
        }

        // ---- helpers ----------------------------------------------------------------------------

        private const int RtbSize = 32;
        private const int Centre = ((RtbSize / 2) * RtbSize + RtbSize / 2) * 4;

        private static System.Windows.Window ShowWindow(string title, Color colour, int left)
        {
            var w = new System.Windows.Window
            {
                Title = title,
                Width = 400,
                Height = 300,
                Left = left,
                Top = 60,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                Background = new SolidColorBrush(colour),
                Content = new System.Windows.Controls.TextBlock { Text = title, FontSize = 20, Margin = new Thickness(12) },
            };
            w.Show();
            return w;
        }

        /// <summary>Changes the window's background every few milliseconds, so each frame is new.</summary>
        private static void Animate(System.Windows.Window w)
        {
            int n = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Normal, w.Dispatcher) { Interval = TimeSpan.FromMilliseconds(8) };
            timer.Tick += (s, e) =>
            {
                n++;
                w.Background = new SolidColorBrush(Color.FromRgb((byte)(n * 7), (byte)(n * 13), (byte)(255 - n * 5)));
            };
            w.Closed += (s, e) => timer.Stop();
            timer.Start();
        }

        /// <summary>RenderTargetBitmap of a filled square on the CALLING thread; BGRA32 pixels.</summary>
        private static byte[] RenderOnce(Color colour)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(colour), null, new Rect(0, 0, RtbSize, RtbSize));
            var rtb = new RenderTargetBitmap(RtbSize, RtbSize, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new byte[RtbSize * RtbSize * 4];
            rtb.CopyPixels(px, RtbSize * 4, 0);
            return px;
        }

        private static bool RenderedColourIs(Color c, byte[] px)
            => Math.Abs(px[Centre] - c.B) <= 2 && Math.Abs(px[Centre + 1] - c.G) <= 2
               && Math.Abs(px[Centre + 2] - c.R) <= 2 && px[Centre + 3] == 255;

        private static bool WaitPresented(UiThreadHost t, int frames)
            => SpinUntil(() => t.Invoke(() => Sink.Current() is { } s && Sink.PresentedFrames(s) >= frames), 8000);

        private static bool SpinUntil(Func<bool> condition, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        private static void SkipUnlessManagedComposition()
            => Assert.SkipUnless(Sink.ManagedCompositionEnabled(), "managed (WebGPU) composition is not running in this process");

        private static void SkipUnlessMultiThreadHead()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(),
                "only the Windows head has a window per thread; macOS and Wayland have one UI thread by design");
            SkipUnlessManagedComposition();
        }

        /// <summary>
        /// The composition internals these tests assert on, reached by reflection: the sink type is
        /// internal to Microsoft.Wpf.Interop.WebGpu and ManagedComposition to PresentationCore.
        /// </summary>
        private static class Sink
        {
            private static readonly Type s_sink =
                typeof(Microsoft.Wpf.Interop.WebGpu.Composition.EmbeddedContent).Assembly
                    .GetType("Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.WpfCompositionSink", throwOnError: true)!;

            private static readonly Type s_managed =
                typeof(System.Windows.Media.Brush).Assembly
                    .GetType("System.Windows.Media.Composition.DUCE+ManagedComposition", throwOnError: true)!;

            private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            /// <summary>The calling thread's sink (WpfCompositionSink.Current is per thread).</summary>
            public static object? Current() => s_sink.GetProperty("Current", Static)!.GetValue(null);

            public static int PresentedFrames(object sink) => (int)s_sink.GetProperty("PresentedFrames", Instance)!.GetValue(sink)!;

            public static bool HasGpu(object sink) => (bool)s_sink.GetProperty("HasGpu", Instance)!.GetValue(sink)!;

            public static int LiveCount() => (int)s_sink.GetProperty("LiveCount", Static)!.GetValue(null)!;

            public static int LiveThreadSinks() => (int)s_managed.GetProperty("LiveThreadSinks", Static)!.GetValue(null)!;

            public static bool ManagedCompositionEnabled()
            {
                s_managed.GetMethod("EnsureAutoRegistered", Static)!.Invoke(null, null);
                return (bool)s_managed.GetProperty("IsEnabled", Static)!.GetValue(null)!;
            }
        }

        /// <summary>A UI thread of the test's own: STA, its own Dispatcher, Run until Shutdown.</summary>
        private sealed class UiThreadHost : IDisposable
        {
            private readonly Thread _thread;
            private readonly Dispatcher _dispatcher;

            public UiThreadHost(string name)
            {
                Dispatcher? d = null;
                using var ready = new ManualResetEventSlim();
                _thread = new Thread(() =>
                {
                    d = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = name,
                };
                if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait();
                _dispatcher = d!;
            }

            public T Invoke<T>(Func<T> body)
            {
                T result = default!;
                Exception? failure = null;
                _dispatcher.Invoke(() =>
                {
                    try { result = body(); }
                    catch (Exception e) { failure = e; }
                });
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                return result;
            }

            public void Invoke(Action body) => Invoke<object?>(() => { body(); return null; });

            /// <summary>Shuts the Dispatcher down and waits for the thread to end.</summary>
            public void Shutdown()
            {
                if (!_thread.IsAlive) return;
                _dispatcher.InvokeShutdown();
                Assert.True(_thread.Join(30000), $"UI thread '{_thread.Name}' did not exit after Dispatcher shutdown");
            }

            public void Dispose()
            {
                try
                {
                    if (_thread.IsAlive && !_dispatcher.HasShutdownStarted)
                    {
                        // Close whatever the test left open, then stop.
                        _dispatcher.Invoke(() =>
                        {
                            foreach (System.Windows.Window w in Application.Current?.Windows.Cast<System.Windows.Window>().ToList()
                                                                 ?? new List<System.Windows.Window>())
                                if (w.Dispatcher == _dispatcher) w.Close();
                        });
                    }
                }
                catch { /* best effort */ }
                Shutdown();
            }
        }
    }
}
