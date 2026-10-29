// user32, menus: the window menu (GetSystemMenu) and popup menus an app tracks itself.
//   GetSystemMenu            a window's own copy of the system menu, made on first use: Restore, Move,
//                            Size, Minimize, Maximize, a separator, Close (the default item)
//   EnableMenuItem           MF_GRAYED/MF_DISABLED by command or by position; answers the old state
//   TrackPopupMenu(Ex)       shows the menu at a screen point and returns once it is dismissed, as the
//                            Windows menu loop does; TPM_RETURNCMD answers the chosen command, otherwise
//                            it is sent to the owner (WM_SYSCOMMAND for the system menu, WM_COMMAND else)
//   GetMenu                  a window's menu bar: none here
// Windows sets the system menu's Restore/Move/Size/Minimize/Maximize from the window's state and styles
// when it tracks it (SetSysMenu), whatever the app enabled before; so does TrackPopupMenu here. The menu
// is drawn by WPF's ContextMenu in a dispatcher frame, on every platform alike.

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const uint MF_BYPOSITION = 0x400, MF_GRAYED = 0x1, MF_DISABLED = 0x2, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    private const uint SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;
    private const uint WM_COMMAND = 0x111;

    private sealed class MenuItemData
    {
        public uint Id;
        public string Text = "";
        public uint Flags;
    }

    private sealed class MenuData
    {
        public readonly nint Handle;
        public readonly nint SystemOf;   // the window whose system menu this is, or 0
        public readonly List<MenuItemData> Items = new();
        public uint DefaultId = uint.MaxValue;
        public MenuData(nint handle, nint systemOf) { Handle = handle; SystemOf = systemOf; }
    }

    private static readonly ConcurrentDictionary<nint, MenuData> s_menus = new();
    private static readonly ConcurrentDictionary<nint, MenuData> s_systemMenus = new();
    private static int s_nextMenu = 0x00A0_0000;

    private static MenuData NewMenu(nint systemOf)
    {
        var m = new MenuData(Interlocked.Increment(ref s_nextMenu) * 4, systemOf);
        s_menus[m.Handle] = m;
        return m;
    }

    private static MenuData NewSystemMenu(nint hwnd)
    {
        MenuData m = NewMenu(hwnd);
        void Add(uint id, string text) => m.Items.Add(new MenuItemData { Id = id, Text = text });
        Add(SC_RESTORE, "&Restore");
        Add(SC_MOVE, "&Move");
        Add(SC_SIZE, "&Size");
        Add(SC_MINIMIZE, "Mi&nimize");
        Add(SC_MAXIMIZE, "Ma&ximize");
        m.Items.Add(new MenuItemData { Flags = MF_SEPARATOR });
        Add(SC_CLOSE, "&Close\tAlt+F4");
        m.DefaultId = SC_CLOSE;
        return m;
    }

    // Menus tracked so far: the window layer asks whether its procedure showed one of its own while
    // handling a right-click on the caption, before DefWindowProc's system menu would show another.
    private static int s_tracked;

    private static void RegisterMenus()
    {
        AppContext.SetData("WpfWebGpu.Win32.MenusTracked", (Func<int>)(() => Volatile.Read(ref s_tracked)));
        AppContext.SetData("WpfWebGpu.Win32.ShowSystemMenu", (Func<IntPtr, int, int, bool>)ShowSystemMenu);
    }

    /// <summary>DefWindowProc's WM_CONTEXTMENU on a caption: the window menu at the point, its choice sent
    /// to the window as WM_SYSCOMMAND.</summary>
    private static bool ShowSystemMenu(IntPtr hwnd, int x, int y)
        => Track(GetSystemMenu(hwnd, 0), 0, x, y, hwnd) != 0;

    [Win32Export("GetSystemMenu")]
    public static nint GetSystemMenu(nint hwnd, int revert)
    {
        if (Resolve(hwnd) == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return 0; }
        if (revert != 0)
        {
            // Back to the default menu; Windows answers NULL here.
            if (s_systemMenus.TryRemove(hwnd, out var old)) s_menus.TryRemove(old.Handle, out _);
            return 0;
        }
        return s_systemMenus.GetOrAdd(hwnd, NewSystemMenu).Handle;
    }

    [Win32Export("GetMenu")] public static nint GetMenu(nint hwnd) => 0;

    private static MenuItemData? FindItem(MenuData m, uint item, uint flags)
    {
        if ((flags & MF_BYPOSITION) != 0) return item < m.Items.Count ? m.Items[(int)item] : null;
        return m.Items.FirstOrDefault(i => (i.Flags & MF_SEPARATOR) == 0 && i.Id == item);
    }

    [Win32Export("EnableMenuItem")]
    public static int EnableMenuItem(nint menu, uint item, uint enable)
    {
        if (!s_menus.TryGetValue(menu, out var m)) { SetLastError(1401); return -1; }   // ERROR_INVALID_MENU_HANDLE
        lock (m)
        {
            MenuItemData? it = FindItem(m, item, enable);
            if (it == null) return -1;
            int old = (int)(it.Flags & (MF_GRAYED | MF_DISABLED));
            it.Flags = (it.Flags & ~(MF_GRAYED | MF_DISABLED)) | (enable & (MF_GRAYED | MF_DISABLED));
            return old;
        }
    }

    [Win32Export("TrackPopupMenu")]
    public static int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, void* rect)
        => Track(menu, flags, x, y, hwnd);

    [Win32Export("TrackPopupMenuEx")]
    public static int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, void* tpm)
        => Track(menu, flags, x, y, hwnd);

    private static int Track(nint menu, uint flags, int x, int y, nint hwnd)
    {
        if (!s_menus.TryGetValue(menu, out var m)) { SetLastError(1401); return 0; }
        object? owner = Resolve(hwnd);
        if (owner == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return 0; }
        if (m.SystemOf != 0) UpdateSystemMenu(m, owner);
        Interlocked.Increment(ref s_tracked);
        uint chosen = Ui(owner, () => ShowMenu(m, x, y, owner));
        if ((flags & TPM_RETURNCMD) != 0) return (int)chosen;
        if (chosen != 0 && (flags & TPM_NONOTIFY) == 0)
            Post(hwnd, m.SystemOf != 0 ? WM_SYSCOMMAND : WM_COMMAND, (nint)chosen, 0);
        return TRUE;
    }

    // SetSysMenu: the items that follow the window's state and frame, as Windows computes them.
    private static void UpdateSystemMenu(MenuData m, object owner)
    {
        WindowState state = StateOf(owner) ?? WindowState.Normal;
        (bool canMin, bool canMax, bool canSize) = Ui(owner, () => owner switch
        {
            HwndSource s when WindowOf(s) is Window w => (w.ResizeMode != ResizeMode.NoResize,
                                                          w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip,
                                                          w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip),
            Forms.Form f => (f.MinimizeBox, f.MaximizeBox,
                             f.FormBorderStyle is Forms.FormBorderStyle.Sizable or Forms.FormBorderStyle.SizableToolWindow),
            _ => (false, false, false),
        });
        void Set(uint id, bool enabled)
        {
            if (FindItem(m, id, 0) is { } it) it.Flags = enabled ? it.Flags & ~(MF_GRAYED | MF_DISABLED) : it.Flags | MF_GRAYED;
        }
        lock (m)
        {
            bool normal = state == WindowState.Normal;
            Set(SC_RESTORE, !normal);
            Set(SC_MOVE, normal);
            Set(SC_SIZE, normal && canSize);
            Set(SC_MINIMIZE, canMin && state != WindowState.Minimized);
            Set(SC_MAXIMIZE, canMax && state != WindowState.Maximized);
        }
    }

    // The menu loop: shown at the point (screen coordinates, the stack's units), the frame runs until
    // the menu closes -- by a choice, Escape, or a click elsewhere -- and answers the command or 0.
    private static uint ShowMenu(MenuData m, int x, int y, object owner)
    {
        uint chosen = 0;
        var cm = new ContextMenu
        {
            Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = x,
            VerticalOffset = y,
        };
        if (owner is HwndSource { RootVisual: UIElement root }) cm.PlacementTarget = root;
        MenuItemData[] items;
        lock (m) items = m.Items.ToArray();
        foreach (MenuItemData it in items)
        {
            if ((it.Flags & MF_SEPARATOR) != 0) { cm.Items.Add(new Separator()); continue; }
            string[] parts = it.Text.Split('\t', 2);
            var mi = new MenuItem
            {
                Header = AccessText(parts[0]),
                InputGestureText = parts.Length > 1 ? parts[1] : "",
                IsEnabled = (it.Flags & (MF_GRAYED | MF_DISABLED)) == 0,
                IsChecked = (it.Flags & MF_CHECKED) != 0,
                FontWeight = it.Id == m.DefaultId ? FontWeights.Bold : FontWeights.Normal,
                Icon = m.SystemOf != 0 ? SystemGlyph(it.Id, (it.Flags & (MF_GRAYED | MF_DISABLED)) == 0) : null,
            };
            uint id = it.Id;
            mi.Click += (_, _) => chosen = id;
            cm.Items.Add(mi);
        }
        var frame = new DispatcherFrame();
        cm.Closed += (_, _) => frame.Continue = false;
        cm.IsOpen = true;
        if (cm.IsOpen) Dispatcher.PushFrame(frame);
        return chosen;
    }

    // "&Restore" -> "_Restore"; "&&" is a literal ampersand, and a literal '_' doubles.
    private static string AccessText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '&' && i + 1 < text.Length && text[i + 1] == '&') { sb.Append('&'); i++; }
            else if (c == '&') sb.Append('_');
            else if (c == '_') sb.Append("__");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // The glyphs Windows draws beside the system menu's Restore, Minimize, Maximize and Close
    // (HBMMENU_POPUP_RESTORE/MINIMIZE/MAXIMIZE/CLOSE): 10x10, one-pixel strokes.
    private static UIElement? SystemGlyph(uint id, bool enabled)
    {
        string? data = id switch
        {
            SC_RESTORE => "M2.5,0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z",
            SC_MINIMIZE => "M0,5.5 H10",
            SC_MAXIMIZE => "M0.5,0.5 H9.5 V9.5 H0.5 Z",
            SC_CLOSE => "M0,0 L10,10 M10,0 L0,10",
            _ => null,
        };
        if (data == null) return null;
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stroke = enabled ? Brushes.Black : Brushes.Gray,
            StrokeThickness = 1,
            Width = 10,
            Height = 10,
            SnapsToDevicePixels = true,
        };
    }
}
