// user32's message queue, for the threads that pump one of their own: a thread with no WPF dispatcher and no
// WinForms loop that creates a hidden window (for device notifications, a COM server's apartment, a hook) and
// runs while (GetMessage(...)) { TranslateMessage(...); DispatchMessage(...); }.
//
// As on Windows, a thread has a queue once it asks for one (any of the functions below); what is posted to its
// windows, what is posted to the thread, its timers and its WM_QUIT wait there until it takes them, and what
// another thread sends to its windows runs while it waits in GetMessage -- on the window's own thread. Threads
// with a dispatcher (WPF, WinForms) keep theirs: their loops are the stack's, and posts reach them through it.

using System.Collections.Concurrent;
using WpfWebGpu.ComInterop;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const uint WM_QUIT = 0x12, PM_REMOVE = 1;

    /// <summary>A thread's queue: posted messages in order, and the quit request (taken after them, as Windows does).</summary>
    internal sealed class ThreadQueue(System.Threading.Thread thread)
    {
        public readonly System.Threading.Thread Thread = thread;
        public readonly LinkedList<Message> Posted = new();
        public bool Quit;
        public nint QuitCode;
    }

    internal readonly record struct Message(nint Hwnd, uint Msg, nint WParam, nint LParam, uint Time);

    private static readonly ConcurrentDictionary<int, ThreadQueue> s_queues = new();

    /// <summary>The queue of a thread that pumps its own; null for one that has never asked.</summary>
    internal static ThreadQueue? QueueOf(int threadId) => s_queues.TryGetValue(threadId, out var q) && q.Thread.IsAlive ? q : null;

    private static ThreadQueue MyQueue() =>
        s_queues.GetOrAdd(Environment.CurrentManagedThreadId, _ => new ThreadQueue(System.Threading.Thread.CurrentThread));

    /// <summary>Puts a message on a thread's queue and wakes it; a WM_TIMER already waiting is not added twice.</summary>
    internal static void Enqueue(ThreadQueue q, nint hwnd, uint msg, nint w, nint l)
    {
        lock (q.Posted)
        {
            if (msg == WM_TIMER && q.Posted.Any(m => m.Msg == WM_TIMER && m.Hwnd == hwnd && m.WParam == w)) return;
            q.Posted.AddLast(new Message(hwnd, msg, w, l, (uint)Environment.TickCount64));
        }
        SentSignal(q.Thread).Set();
    }

    private static bool Matches(Message m, nint hwnd, uint min, uint max) =>
        (hwnd == 0 || (hwnd == -1 ? m.Hwnd == 0 : m.Hwnd == hwnd))
        && (min == 0 && max == 0 || m.Msg >= min && m.Msg <= max);

    // the next message for the filter, taken off the queue or left on it; WM_QUIT once the posted ones are gone
    private static bool Next(ThreadQueue q, nint hwnd, uint min, uint max, bool remove, out Message message)
    {
        lock (q.Posted)
        {
            for (var node = q.Posted.First; node != null; node = node.Next)
            {
                if (!Matches(node.Value, hwnd, min, max)) continue;
                message = node.Value;
                if (remove) q.Posted.Remove(node);
                return true;
            }
            if (q.Quit)
            {
                message = new Message(0, WM_QUIT, q.QuitCode, 0, (uint)Environment.TickCount64);
                if (remove) q.Quit = false;
                return true;
            }
        }
        message = default;
        return false;
    }

    // MSG: hwnd, message, wParam, lParam, time, pt -- pointer-sized fields aligned
    private static void Write(byte* msg, Message m)
    {
        int p = sizeof(nint);
        *(nint*)msg = m.Hwnd;
        *(uint*)(msg + p) = m.Msg;
        *(nint*)(msg + 2 * p) = m.WParam;
        *(nint*)(msg + 3 * p) = m.LParam;
        *(uint*)(msg + 4 * p) = m.Time;
        *(POINT*)(msg + 4 * p + 4) = default;
    }

    [Win32Export("GetMessageW", "GetMessage", "GetMessageA")]
    public static int GetMessageW(byte* msg, nint hwnd, uint min, uint max)
    {
        ThreadQueue q = MyQueue();
        System.Threading.Thread me = System.Threading.Thread.CurrentThread;
        while (true)
        {
            RunSentTo(me);                              // what other threads sent to this one's windows, first
            if (Next(q, hwnd, min, max, remove: true, out Message m))
            {
                if (msg != null) Write(msg, m);
                return m.Msg == WM_QUIT ? 0 : 1;
            }
            SentSignal(me).WaitOne();
        }
    }

    [Win32Export("PeekMessageW", "PeekMessage", "PeekMessageA")]
    public static int PeekMessageW(byte* msg, nint hwnd, uint min, uint max, uint flags)
    {
        ThreadQueue q = MyQueue();
        RunSentTo(System.Threading.Thread.CurrentThread);
        if (!Next(q, hwnd, min, max, remove: (flags & PM_REMOVE) != 0, out Message m)) return FALSE;
        if (msg != null) Write(msg, m);
        return TRUE;
    }

    [Win32Export("WaitMessage")]
    public static int WaitMessage()
    {
        ThreadQueue q = MyQueue();
        System.Threading.Thread me = System.Threading.Thread.CurrentThread;
        while (true)
        {
            RunSentTo(me);
            lock (q.Posted) if (q.Posted.Count > 0 || q.Quit) return TRUE;
            SentSignal(me).WaitOne();
        }
    }

    [Win32Export("PostQuitMessage")]
    public static void PostQuitMessage(int code)
    {
        ThreadQueue q = MyQueue();
        lock (q.Posted) { q.Quit = true; q.QuitCode = code; }
        SentSignal(q.Thread).Set();
    }

    [Win32Export("TranslateMessage")]
    public static int TranslateMessage(byte* msg) => FALSE;     // no keystrokes reach an app's own windows: nothing to translate

    [Win32Export("DispatchMessageW", "DispatchMessage", "DispatchMessageA")]
    public static nint DispatchMessageW(byte* msg)
    {
        if (msg == null) return 0;
        int p = sizeof(nint);
        nint hwnd = *(nint*)msg, w = *(nint*)(msg + 2 * p), l = *(nint*)(msg + 3 * p);
        uint message = *(uint*)(msg + p);
        // a timer set with a TIMERPROC carries it in lParam, and DispatchMessage calls it
        if (message == WM_TIMER && l != 0)
        {
            uint tick = (uint)Environment.TickCount64;
            if (Callbacks.Of(l) is { } d) Callbacks.Invoke(d, hwnd, (nint)WM_TIMER, w, (nint)tick);
            else ((delegate* unmanaged<nint, uint, nint, uint, void>)l)(hwnd, WM_TIMER, w, tick);
            return 0;
        }
        return hwnd == 0 ? 0 : Send(hwnd, message, w, l);   // a thread message has no window to go to
    }

    [Win32Export("MsgWaitForMultipleObjects")]
    public static uint MsgWaitForMultipleObjects(uint count, nint* handles, int waitAll, uint ms, uint wakeMask) =>
        MsgWaitForMultipleObjectsEx(count, handles, ms, wakeMask, waitAll != 0 ? 1u : 0u);

    [Win32Export("MsgWaitForMultipleObjectsEx")]
    public static uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint wakeMask, uint flags)
    {
        ThreadQueue q = MyQueue();
        System.Threading.Thread me = System.Threading.Thread.CurrentThread;
        var waits = new WaitHandle[count + 1];
        for (int i = 0; i < count; i++)
        {
            if (Handle(handles[i]) is not WaitHandle h) { SetLastError(ERROR_INVALID_HANDLE); return WAIT_FAILED; }
            waits[i] = h;
        }
        waits[count] = SentSignal(me);
        long deadline = ms == uint.MaxValue ? long.MaxValue : Environment.TickCount64 + ms;
        while (true)
        {
            RunSentTo(me);
            // MWMO_INPUTAVAILABLE aside, a message already waiting ends the wait (as one arriving does)
            lock (q.Posted) if (wakeMask != 0 && (q.Posted.Count > 0 || q.Quit)) return WAIT_OBJECT_0 + count;
            if ((flags & 1) != 0 && count > 0)          // MWMO_WAITALL: every handle, or a message
            {
                if (WaitHandle.WaitAll(waits[..(int)count], 0)) return WAIT_OBJECT_0;
            }
            else
            {
                for (int i = 0; i < count; i++) if (waits[i].WaitOne(0)) return WAIT_OBJECT_0 + (uint)i;
            }
            long left = deadline - Environment.TickCount64;
            if (left <= 0) return WAIT_TIMEOUT;
            int index = WaitHandle.WaitAny(waits, (int)Math.Min(left, (flags & 1) != 0 ? 50 : int.MaxValue));
            if (index != WaitHandle.WaitTimeout && index < count && (flags & 1) == 0) return WAIT_OBJECT_0 + (uint)index;
        }
    }
}
