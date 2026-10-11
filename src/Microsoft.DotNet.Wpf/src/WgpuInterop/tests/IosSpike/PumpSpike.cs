using System.Windows.Threading;

namespace IosSpike;

/// <summary>
/// M3c acceptance test: does the CADisplayLink pump actually drive the WPF dispatcher on iOS?
///
/// Deliberately uses only PUBLIC dispatcher API (BeginInvoke / DispatcherTimer / Dispatcher.Run),
/// so a pass exercises the whole chain end to end - PushFrameImpl's iOS branch -> RunIosPump ->
/// UIKitWindow.StartDisplayLink -> the synthesized WpfDisplayLinkTarget -> PumpOneTick ->
/// ProcessQueue - rather than poking at the pump's internals.
/// </summary>
public static class PumpSpike
{
    private static int s_ops;
    private static int s_timerTicks;
    private static readonly System.Diagnostics.Stopwatch s_clock = new();

    public static void Run()
    {
        Console.WriteLine("PUMPSPIKE: start");

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        Console.WriteLine("PUMPSPIKE: dispatcher acquired");

        // A queued operation that re-posts itself: proves ProcessQueue is being serviced, and the
        // rate tells us the pump is display-paced rather than firing once and stalling.
        s_clock.Start();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RepostingOp));

        // A DispatcherTimer proves the other half of the tick body - PromoteTimers - since timers
        // are promoted by the pump, not by the queue.
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (s, e) =>
        {
            s_timerTicks++;
            double sec = s_clock.Elapsed.TotalSeconds;
            Console.WriteLine($"PUMPSPIKE: timer#{s_timerTicks} at {sec:F1}s, ops={s_ops} ({s_ops / sec:F0}/s)");

            if (s_timerTicks == 6)
            {
                // Exercise the shutdown path too: the frame must unwind and the display link stop.
                Console.WriteLine(s_ops > 100
                    ? "PUMPSPIKE: PASS - queue and timers both serviced"
                    : $"PUMPSPIKE: FAIL - only {s_ops} operations serviced");
                ((DispatcherTimer)s!).Stop();
                dispatcher.InvokeShutdown();
                Console.WriteLine("PUMPSPIKE: shutdown requested");
            }
        };
        timer.Start();

        // On iOS this returns immediately (UIKit owns the run loop); the pump keeps running.
        Dispatcher.Run();  // Application.Run's inner call
        Console.WriteLine("PUMPSPIKE: Dispatcher.Run returned (expected on iOS - pump is detached)");
    }

    private static void RepostingOp()
    {
        s_ops++;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RepostingOp));
    }
}
