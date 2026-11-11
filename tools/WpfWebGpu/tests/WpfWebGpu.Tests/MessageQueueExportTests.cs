using System.Reflection;
using Xunit;

namespace WpfWebGpu.Tests;

// user32's message queue for a thread that pumps its own (WpfWebGpu.Win32/User32.MessageQueue.cs): thread messages
// in order, WM_QUIT after what was posted before it, GetMessage's 0 for it. (Timers go through the same queue;
// SetTimer resolves its window through WPF, which this process does not load.)
public unsafe class MessageQueueExportTests
{
    private static MethodInfo Export(string name) => Win32Exports.Method(name);

    private const uint WM_USER = 0x400;

    [Fact]
    public void A_thread_pumping_its_own_queue_gets_its_messages_in_order_and_then_WM_QUIT()
    {
        MethodInfo getMessage = Export("GetMessageW"), peek = Export("PeekMessageW"), quit = Export("PostQuitMessage"),
            postThread = Export("PostThreadMessage"), threadId = Export("GetCurrentThreadId");
        var received = new List<(uint Msg, long W)>();
        int exitCode = -1;
        uint id = 0;
        using var ready = new ManualResetEventSlim();
        var pump = new Thread(() =>
        {
            byte* msg = stackalloc byte[48];
            // a thread has a queue once it asks for one
            peek.Invoke(null, new object[] { Pointer.Box(msg, typeof(byte*)), (nint)0, 0u, 0u, 0u });
            id = (uint)threadId.Invoke(null, null)!;
            ready.Set();
            while ((int)getMessage.Invoke(null, new object[] { Pointer.Box(msg, typeof(byte*)), (nint)0, 0u, 0u })! != 0)
            {
                uint m = *(uint*)(msg + sizeof(nint));
                long w = *(nint*)(msg + 2 * sizeof(nint));
                received.Add((m, w));
                if (m == WM_USER + 2) quit.Invoke(null, new object[] { 7 });
            }
            exitCode = (int)*(nint*)(msg + 2 * sizeof(nint));        // WM_QUIT's wParam: the code given
        });
        pump.Start();
        Assert.True(ready.Wait(5000));
        foreach (uint m in new[] { WM_USER, WM_USER + 1, WM_USER + 2 })
            Assert.Equal(1, (int)postThread.Invoke(null, new object[] { id, m, (nint)(m - WM_USER), (nint)0 })!);
        Assert.True(pump.Join(5000), "the loop ends at WM_QUIT");

        Assert.Equal(7, exitCode);
        Assert.Equal(new[] { (WM_USER, 0L), (WM_USER + 1, 1L), (WM_USER + 2, 2L) }, received.ToArray());
        // a thread that never asked for a queue cannot be posted to
        Assert.Equal(0, (int)postThread.Invoke(null, new object[] { 0x7fff_fff0u, WM_USER, (nint)0, (nint)0 })!);
    }
}
