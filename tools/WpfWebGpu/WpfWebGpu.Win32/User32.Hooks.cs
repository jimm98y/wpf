// Windows hooks (SetWindowsHookEx). The thread hooks an application installs to see its own input
// before its windows do are called: WH_KEYBOARD as each key message comes off the queue, WH_MOUSE as
// each mouse message does, and a hook that answers non-zero discards the message -- no message
// filter, no window, sees it. DotNetBar runs its menus on exactly these two: the keyboard hook gives
// an open popup its arrow keys, Enter and Escape, and the mouse hook closes the popup on a press
// anywhere else. Accepted and never called, an open app context menu took no keys and never
// closed, and the next right click opened another beside it.
//
// The stack's input reaches here through two entry points it finds in AppContext (on Windows the
// real user32 calls the hooks and neither is published): "WpfWebGpu.Win32.KeyboardHook" and
// "WpfWebGpu.Win32.MouseHook". The other hook types are accepted and not called: there is no
// system-wide message stream to hook, and an app that installs one to watch for activity must not
// fail to start because of it.

using System.Runtime.InteropServices;
using WpfWebGpu.ComInterop;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int WH_KEYBOARD = 2, WH_MOUSE = 7, HC_ACTION = 0;

    private sealed class WindowsHook
    {
        public int Id;
        public nint Proc;
        public uint Thread;   // 0: every thread
        public nint Handle;
    }

    // Newest first, as Windows calls them.
    private static readonly List<WindowsHook> s_hooks = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEHOOKSTRUCT
    {
        public int X, Y;
        public nint Hwnd;
        public uint HitTestCode;
        public nint ExtraInfo;
    }

    private static void RegisterHooks()
    {
        AppContext.SetData("WpfWebGpu.Win32.KeyboardHook", (Func<int, long, bool>)KeyboardHook);
        AppContext.SetData("WpfWebGpu.Win32.MouseHook", (Func<int, int, int, IntPtr, int, bool>)MouseHook);
    }

    [Win32Export("SetWindowsHookExW", "SetWindowsHookEx", "SetWindowsHookExA")]
    public static nint SetWindowsHookEx(int id, void* proc, nint module, uint thread)
    {
        var hook = new WindowsHook { Id = id, Proc = (nint)proc, Thread = thread };
        hook.Handle = NewHandle(hook);
        lock (s_hooks) s_hooks.Insert(0, hook);
        return hook.Handle;
    }

    [Win32Export("UnhookWindowsHookEx")]
    public static int UnhookWindowsHookEx(nint hook)
    {
        if (s_handles.TryRemove(hook, out object? o) && o is WindowsHook h)
            lock (s_hooks) s_hooks.Remove(h);
        return TRUE;
    }

    /// <summary>The next hook of the same kind on this thread after <paramref name="hook"/>, or 0 when
    /// it is the last.</summary>
    [Win32Export("CallNextHookEx")]
    public static nint CallNextHookEx(nint hook, int code, nint w, nint l)
    {
        WindowsHook? next = null;
        lock (s_hooks)
        {
            int at = s_hooks.FindIndex(h => h.Handle == hook);
            if (at >= 0) next = NextHook(s_hooks[at].Id, at + 1);
        }
        return next == null ? 0 : CallHook(next, code, w, l);
    }

    private static WindowsHook? NextHook(int id, int from)
    {
        uint thread = GetCurrentThreadId();
        for (int i = from; i < s_hooks.Count; i++)
        {
            WindowsHook h = s_hooks[i];
            if (h.Id == id && (h.Thread == 0 || h.Thread == thread)) return h;
        }
        return null;
    }

    // In the browser the procedure is a token of the app's delegate (WpfWebGpu.ComInterop.Callbacks),
    // which is what is called; natively, the runtime's thunk.
    private static nint CallHook(WindowsHook hook, int code, nint w, nint l)
    {
        if (Callbacks.Of(hook.Proc) is { } d) return Callbacks.Invoke(d, code, w, l);
        return ((delegate* unmanaged<int, nint, nint, nint>)hook.Proc)(code, w, l);
    }

    /// <summary>Runs the chain from its first hook; true when a hook discarded the message.</summary>
    private static bool RunHooks(int id, nint w, nint l)
    {
        WindowsHook? first;
        lock (s_hooks) first = NextHook(id, 0);
        if (first == null) return false;
        try { return CallHook(first, HC_ACTION, w, l) != 0; }
        catch (Exception e) { LogOnce("hook-fail", "win32 shim: a windows hook threw: " + e.Message); return false; }
    }

    /// <summary>A key message coming off the queue: WH_KEYBOARD's wParam is the virtual key, lParam
    /// the message's keystroke flags (repeat count, context code, previous state, transition).</summary>
    private static bool KeyboardHook(int virtualKey, long keystroke)
        => RunHooks(WH_KEYBOARD, virtualKey, (nint)keystroke);

    /// <summary>A mouse message coming off the queue, the point in screen pixels: WH_MOUSE's wParam
    /// is the message, lParam a MOUSEHOOKSTRUCT with the window it is for.</summary>
    private static bool MouseHook(int message, int screenX, int screenY, IntPtr hwnd, int hitTest)
    {
        var info = new MOUSEHOOKSTRUCT { X = screenX, Y = screenY, Hwnd = hwnd, HitTestCode = (uint)hitTest };
        return RunHooks(WH_MOUSE, message, (nint)(&info));
    }
}
