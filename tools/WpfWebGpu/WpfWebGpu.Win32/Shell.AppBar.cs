// shell32!SHAppBarMessage: the taskbar, as an app sees it to keep its windows clear of it. The OS's own bar --
// the Dock and menu bar on macOS, a desktop's panel on Linux -- stands in: it is what the monitor's work area
// is short of, on the side where most is taken. Where nothing is taken the bar is hidden until pointed at,
// which is what ABS_AUTOHIDE says on Windows.
//
// An app that registers a bar of its own (ABM_NEW, ABM_SETPOS) is told yes; the work area does not shrink for
// it, the OS's windows do not know of it.

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const uint AbmNew = 0, AbmRemove = 1, AbmQueryPos = 2, AbmSetPos = 3, AbmGetState = 4, AbmGetTaskbarPos = 5,
        AbmActivate = 6, AbmGetAutohideBar = 7, AbmSetAutohideBar = 8, AbmWindowPosChanged = 9, AbmSetState = 10, AbmGetAutohideBarEx = 11;
    private const uint AbsAutohide = 1, AbsAlwaysOnTop = 2;
    private const uint AbeLeft = 0, AbeTop = 1, AbeRight = 2, AbeBottom = 3;

    [Win32Export("SHAppBarMessage")]
    public static nuint SHAppBarMessage(uint message, byte* data)
    {
        // APPBARDATA: cbSize, hWnd, uCallbackMessage, uEdge, rc, lParam -- the handle pointer-sized and aligned
        int edgeAt = IntPtr.Size == 8 ? 20 : 12, rectAt = IntPtr.Size == 8 ? 24 : 16;
        switch (message)
        {
            case AbmGetState:
                return TaskbarEdge(out _, out _) ? AbsAlwaysOnTop : AbsAutohide | AbsAlwaysOnTop;
            case AbmGetTaskbarPos:
                if (data == null) return 0;
                TaskbarEdge(out uint edge, out RECT rect);
                *(uint*)(data + edgeAt) = edge;
                *(RECT*)(data + rectAt) = rect;
                return 1;
            case AbmGetAutohideBar:
            case AbmGetAutohideBarEx:
                return 0;                       // no app's bar hides on any edge
            case AbmNew:
            case AbmRemove:
            case AbmSetPos:
            case AbmActivate:
            case AbmWindowPosChanged:
            case AbmSetState:
            case AbmSetAutohideBar:
                return 1;
            case AbmQueryPos:
                return 1;                       // the proposed rectangle stands: nothing else claims the edge
            default:
                return 0;
        }
    }

    /// <summary>The OS bar's edge and rectangle on the primary monitor; false where it takes nothing (hidden).</summary>
    private static bool TaskbarEdge(out uint edge, out RECT rect)
    {
        var (monitor, work) = Screens.All()[0];
        int left = work.Left - monitor.Left, top = work.Top - monitor.Top, right = monitor.Right - work.Right, bottom = monitor.Bottom - work.Bottom;
        int most = Math.Max(Math.Max(left, top), Math.Max(right, bottom));
        rect = monitor;
        if (most <= 0)
        {
            // hidden: Windows still reports where it would show, along the bottom
            edge = AbeBottom;
            rect.Top = rect.Bottom;
            return false;
        }
        if (most == bottom) { edge = AbeBottom; rect.Top = work.Bottom; }
        else if (most == top) { edge = AbeTop; rect.Bottom = work.Top; }
        else if (most == left) { edge = AbeLeft; rect.Right = work.Left; }
        else { edge = AbeRight; rect.Left = work.Right; }
        return true;
    }
}
