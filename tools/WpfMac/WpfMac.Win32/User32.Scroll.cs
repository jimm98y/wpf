// user32: window frames as Windows computes them, scroll bars, mouse tracking.
//
//   AdjustWindowRect(Ex)(ForDpi)  a window's frame for a client rectangle, from its styles, with
//                                 Windows 10's metrics (the same SystemMetric answers): DotNetBar's
//                                 ribbon and Metro forms size their caption and borders from it.
//   Get/SetScrollInfo, Pos, Range, a window's scroll bars. WinForms' scroll bars are controls of their
//   ShowScrollBar, EnableScrollBar own here (Mono's implicit ones), not part of the window's frame, so
//   GetScrollBarInfo              the window's scroll bar is its implicit one; for SB_CTL the window is
//                                 the scroll bar. A window with none has its bar invisible: DotNetBar,
//                                 told nothing (zeros), took both bars for visible, at 0,0.
//   TrackMouseEvent               the driver says WM_MOUSELEAVE whenever the pointer leaves a window of
//                                 its, so TME_LEAVE needs nothing more; TME_HOVER is timed here.
//
// Only the stack's WinForms windows have scroll bars to answer for; a handle that is not one answers
// as Windows does for a window without the bar asked for.

using System.Reflection;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{

    // ---- frames ------------------------------------------------------------------------------------

    private const uint WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    private const uint WS_EX_DLGMODALFRAME = 0x1, WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000;   // WS_EX_TOOLWINDOW: User32.Windows.cs

    /// <summary>Windows' own rule (as Wine has it): the outer frame, the sizing border, the inner
    /// border, the caption, the menu bar and the client edge, from the metrics at 96 dpi -- 8,31,8,8
    /// for an overlapped window.</summary>
    private static void AdjustFrame(RECT* r, uint style, bool menu, uint exStyle, double scale)
    {
        int Scaled(int v) => (int)Math.Round(v * scale);
        int adjust = 0;
        if ((exStyle & (WS_EX_STATICEDGE | WS_EX_DLGMODALFRAME)) == WS_EX_STATICEDGE) adjust = 1;
        else if ((exStyle & WS_EX_DLGMODALFRAME) != 0 || (style & (WS_THICKFRAME | WS_DLGFRAME)) != 0) adjust = 2;
        if ((style & WS_THICKFRAME) != 0) adjust += SystemMetric(5) + SystemMetric(92);   // border width + padded border
        if ((style & (WS_BORDER | WS_DLGFRAME)) != 0 || (exStyle & WS_EX_DLGMODALFRAME) != 0) adjust++;
        adjust = Scaled(adjust);
        r->Left -= adjust; r->Top -= adjust; r->Right += adjust; r->Bottom += adjust;
        if ((style & WS_CAPTION) == WS_CAPTION)
            r->Top -= Scaled((exStyle & WS_EX_TOOLWINDOW) != 0 ? SystemMetric(51) : SystemMetric(4));   // SM_CYSMCAPTION, SM_CYCAPTION
        if (menu) r->Top -= Scaled(SystemMetric(15));                                                    // SM_CYMENU
        if ((exStyle & WS_EX_CLIENTEDGE) != 0)
        {
            int edge = Scaled(SystemMetric(45));
            r->Left -= edge; r->Top -= edge; r->Right += edge; r->Bottom += edge;
        }
    }

    [Win32Export("AdjustWindowRect")]
    public static int AdjustWindowRect(RECT* r, uint style, int menu)
    {
        if (r == null) return FALSE;
        AdjustFrame(r, style, menu != 0, 0, 1);
        return TRUE;
    }

    [Win32Export("AdjustWindowRectEx")]
    public static int AdjustWindowRectEx(RECT* r, uint style, int menu, uint exStyle)
    {
        if (r == null) return FALSE;
        AdjustFrame(r, style, menu != 0, exStyle, 1);
        return TRUE;
    }

    [Win32Export("AdjustWindowRectExForDpi")]
    public static int AdjustWindowRectExForDpi(RECT* r, uint style, int menu, uint exStyle, uint dpi)
    {
        if (r == null) return FALSE;
        AdjustFrame(r, style, menu != 0, exStyle, dpi == 0 ? 1 : dpi / 96.0);
        return TRUE;
    }

    // ---- scroll bars -------------------------------------------------------------------------------

    private const int SB_HORZ = 0, SB_VERT = 1, SB_CTL = 2, SB_BOTH = 3;
    private const uint SIF_RANGE = 1, SIF_PAGE = 2, SIF_POS = 4, SIF_DISABLENOSCROLL = 8, SIF_TRACKPOS = 0x10;

    private static MethodInfo? s_allControls;

    /// <summary>The scroll bar <paramref name="bar"/> of a window: the window itself for SB_CTL, else
    /// its own (implicit) scroll bar of that orientation, visible or not; null for none.</summary>
    private static Forms.ScrollBar? ScrollBarOf(Forms.Control c, int bar)
    {
        if (bar == SB_CTL) return c as Forms.ScrollBar;
        s_allControls ??= c.Controls.GetType().GetMethod("GetAllControls", BindingFlags.NonPublic | BindingFlags.Instance);
        if (s_allControls?.Invoke(c.Controls, null) is not Forms.Control[] all) return null;
        foreach (Forms.Control child in all)
        {
            // Implicit: the control's own, not one the application put in it.
            if (child is Forms.ScrollBar sb && child.GetType().Name.StartsWith("Implicit", StringComparison.Ordinal)
                && (bar == SB_VERT ? sb is Forms.VScrollBar : sb is Forms.HScrollBar))
                return sb;
        }
        return null;
    }

    private static T OnScrollBar<T>(nint hwnd, int bar, T none, Func<Forms.ScrollBar, T> f)
        => Resolve(hwnd) is Forms.Control c ? Ui(c, () => ScrollBarOf(c, bar) is { } sb ? f(sb) : none) : none;

    // SCROLLINFO: cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos. WinForms' Maximum is nMax and its
    // LargeChange nPage, as the native bar it stands for has them.
    [Win32Export("GetScrollInfo")]
    public static int GetScrollInfo(nint hwnd, int bar, uint* si)
    {
        if (si == null) return FALSE;
        uint mask = si[1];
        return OnScrollBar(hwnd, bar, FALSE, sb =>
        {
            if ((mask & SIF_RANGE) != 0) { si[2] = (uint)sb.Minimum; si[3] = (uint)sb.Maximum; }
            if ((mask & SIF_PAGE) != 0) si[4] = (uint)sb.LargeChange;
            if ((mask & SIF_POS) != 0) si[5] = (uint)sb.Value;
            if ((mask & SIF_TRACKPOS) != 0) si[6] = (uint)sb.Value;
            return TRUE;
        });
    }

    [Win32Export("SetScrollInfo")]
    public static int SetScrollInfo(nint hwnd, int bar, uint* si, int redraw)
    {
        if (si == null) return 0;
        uint mask = si[1];
        int min = (int)si[2], max = (int)si[3], page = (int)si[4], pos = (int)si[5];
        return OnScrollBar(hwnd, bar, 0, sb =>
        {
            if ((mask & SIF_RANGE) != 0) { sb.Minimum = min; sb.Maximum = Math.Max(min, max); }
            if ((mask & SIF_PAGE) != 0) sb.LargeChange = Math.Max(1, page);
            if ((mask & SIF_POS) != 0) sb.Value = ClampScroll(sb, pos);
            return sb.Value;
        });
    }

    /// <summary>A position within what the bar can scroll to: nMax - nPage + 1 at most, as Windows clamps.</summary>
    private static int ClampScroll(Forms.ScrollBar sb, int pos)
        => Math.Clamp(pos, sb.Minimum, Math.Max(sb.Minimum, sb.Maximum - Math.Max(sb.LargeChange, 1) + 1));

    [Win32Export("GetScrollPos")] public static int GetScrollPos(nint hwnd, int bar) => OnScrollBar(hwnd, bar, 0, sb => sb.Value);

    [Win32Export("SetScrollPos")]
    public static int SetScrollPos(nint hwnd, int bar, int pos, int redraw)
        => OnScrollBar(hwnd, bar, 0, sb => { int old = sb.Value; sb.Value = ClampScroll(sb, pos); return old; });

    [Win32Export("GetScrollRange")]
    public static int GetScrollRange(nint hwnd, int bar, int* min, int* max)
    {
        (int Min, int Max) range = OnScrollBar(hwnd, bar, (0, 0), sb => (sb.Minimum, sb.Maximum));
        if (min != null) *min = range.Min;
        if (max != null) *max = range.Max;
        return TRUE;
    }

    [Win32Export("SetScrollRange")]
    public static int SetScrollRange(nint hwnd, int bar, int min, int max, int redraw)
        => OnScrollBar(hwnd, bar, FALSE, sb => { sb.Minimum = min; sb.Maximum = Math.Max(min, max); return TRUE; });

    [Win32Export("ShowScrollBar")]
    public static int ShowScrollBar(nint hwnd, int bar, int show)
    {
        if (bar == SB_BOTH)
        {
            OnScrollBar(hwnd, SB_HORZ, 0, sb => { sb.Visible = show != 0; return 0; });
            OnScrollBar(hwnd, SB_VERT, 0, sb => { sb.Visible = show != 0; return 0; });
            return TRUE;
        }
        return OnScrollBar(hwnd, bar, FALSE, sb => { sb.Visible = show != 0; return TRUE; });
    }

    // ESB_ENABLE_BOTH 0, ESB_DISABLE_BOTH 3 (and the one-arrow variants, which a WinForms bar cannot be).
    [Win32Export("EnableScrollBar")]
    public static int EnableScrollBar(nint hwnd, uint bar, uint arrows)
    {
        bool enable = arrows == 0;
        if (bar == SB_BOTH)
        {
            OnScrollBar(hwnd, SB_HORZ, 0, sb => { sb.Enabled = enable; return 0; });
            OnScrollBar(hwnd, SB_VERT, 0, sb => { sb.Enabled = enable; return 0; });
            return TRUE;
        }
        return OnScrollBar(hwnd, (int)bar, FALSE, sb => { sb.Enabled = enable; return TRUE; });
    }

    // SCROLLBARINFO: cbSize, RECT rcScrollBar, dxyLineButton, xyThumbTop, xyThumbBottom, reserved,
    // DWORD rgstate[6]. OBJID_HSCROLL -6, OBJID_VSCROLL -5, OBJID_CLIENT -4 (the window is the bar).
    private const uint STATE_SYSTEM_UNAVAILABLE = 0x1, STATE_SYSTEM_INVISIBLE = 0x8000;

    [Win32Export("GetScrollBarInfo")]
    public static int GetScrollBarInfo(nint hwnd, int idObject, byte* info)
    {
        if (info == null || Resolve(hwnd) is not Forms.Control c) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        int bar = idObject switch { -6 => SB_HORZ, -5 => SB_VERT, -4 => SB_CTL, _ => -1 };
        if (bar < 0) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
        RECT* rect = (RECT*)(info + 4);
        int* metrics = (int*)(info + 20);
        uint* state = (uint*)(info + 36);
        for (int i = 0; i < 6; i++) state[i] = 0;
        Ui(c, () =>
        {
            Forms.ScrollBar? sb = ScrollBarOf(c, bar);
            if (sb == null || !sb.Visible)
            {
                *rect = default;
                metrics[0] = metrics[1] = metrics[2] = metrics[3] = 0;
                // Exactly STATE_SYSTEM_INVISIBLE, as Windows answers for a hidden bar: DotNetBar compares
                // rgstate[0] with it.
                state[0] = STATE_SYSTEM_INVISIBLE;
                return 0;
            }
            *rect = ControlScreenRect(sb);
            bool vertical = sb is Forms.VScrollBar;
            int length = vertical ? sb.Height : sb.Width, button = vertical ? sb.Width : sb.Height;
            // The thumb, as the native bar would place it between its two arrow buttons.
            int track = Math.Max(0, length - 2 * button), range = Math.Max(1, sb.Maximum - sb.Minimum + 1);
            int thumb = Math.Max(8, track * Math.Min(sb.LargeChange, range) / range);
            int travel = Math.Max(1, range - sb.LargeChange);
            int top = button + (track - thumb) * Math.Clamp(sb.Value - sb.Minimum, 0, travel) / travel;
            metrics[0] = button; metrics[1] = top; metrics[2] = top + thumb; metrics[3] = 0;
            if (!sb.Enabled) state[0] = STATE_SYSTEM_UNAVAILABLE;
            return 0;
        });
        return TRUE;
    }

    // ---- mouse tracking ----------------------------------------------------------------------------

    // TRACKMOUSEEVENT: cbSize, dwFlags, HWND hwndTrack, dwHoverTime (with the handle 8-aligned).
    private const uint TME_HOVER = 1, TME_LEAVE = 2, TME_NONCLIENT = 0x10, TME_QUERY = 0x40000000, TME_CANCEL = 0x80000000;
    private const uint HOVER_DEFAULT = 0xFFFFFFFF, WM_MOUSEHOVER = 0x02A1;

    private sealed class Tracking
    {
        public uint Flags, HoverTime;
        public Timer? Hover;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Tracking> s_tracking = new();

    [Win32Export("TrackMouseEvent", "_TrackMouseEvent")]
    public static int TrackMouseEvent(uint* tme)
    {
        if (tme == null) return FALSE;
        uint flags = tme[1];
        nint* hwndField = (nint*)((byte*)tme + 8);
        uint* hoverField = (uint*)((byte*)tme + 8 + sizeof(nint));
        if ((flags & TME_QUERY) != 0)
        {
            // The tracking in force for the window under the pointer, if any.
            foreach (var (hwnd, t) in s_tracking)
            {
                tme[1] = t.Flags; *hwndField = hwnd; *hoverField = t.HoverTime;
                return TRUE;
            }
            tme[1] = 0; *hwndField = 0; *hoverField = 0;
            return TRUE;
        }
        nint target = *hwndField;
        if (Resolve(target) is not Forms.Control control) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        if ((flags & TME_CANCEL) != 0)
        {
            if (s_tracking.TryGetValue(target, out var t))
            {
                t.Flags &= ~(flags & (TME_HOVER | TME_LEAVE | TME_NONCLIENT));
                if ((flags & TME_HOVER) != 0) t.Hover?.Dispose();
                if (t.Flags == 0) s_tracking.TryRemove(target, out _);
            }
            return TRUE;
        }
        var tracking = s_tracking.GetOrAdd(target, _ => new Tracking());
        tracking.Flags |= flags & (TME_HOVER | TME_LEAVE | TME_NONCLIENT);
        if ((flags & TME_HOVER) != 0)
        {
            uint time = *hoverField == HOVER_DEFAULT || *hoverField == 0 ? 400u : *hoverField;   // SPI_GETMOUSEHOVERTIME
            tracking.HoverTime = time;
            tracking.Hover?.Dispose();
            tracking.Hover = new Timer(_ => Hovered(target, control, tracking), null, (int)time, Timeout.Infinite);
        }
        return TRUE;
    }

    /// <summary>The hover time passed: WM_MOUSEHOVER if the pointer is still over the window, once,
    /// as Windows posts it (tracking hover again takes another TrackMouseEvent).</summary>
    private static void Hovered(nint hwnd, Forms.Control control, Tracking tracking)
    {
        try
        {
            if (control.IsDisposed || !control.IsHandleCreated) return;
            control.BeginInvoke(() =>
            {
                tracking.Flags &= ~TME_HOVER;
                if (tracking.Flags == 0) s_tracking.TryRemove(hwnd, out _);
                System.Drawing.Point p = control.PointToClient(Forms.Control.MousePosition);
                if (!control.ClientRectangle.Contains(p)) return;
                nint keys = (Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0 ? 1 : 0;
                FormsSend(control, WM_MOUSEHOVER, keys, (p.Y << 16) | (p.X & 0xFFFF));
            });
        }
        catch (InvalidOperationException)
        {
        }
    }
}
