// The few questions the Win32 functions ask the platform itself rather than the stack's windows: where
// the pointer is, which keys and buttons are down, what displays there are, how much memory there is.
//
// macOS answers them for the whole session (CoreGraphics, sysctl), as Windows does: the pointer over
// another app's window, a key held while another app has the keyboard. The other heads -- the browser,
// and Linux, iOS and Android as they come -- have no such session-wide query, so the answer is what the
// stack itself knows: WPF's own mouse and keyboard, and its screen metrics.

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfWebGpu.Win32;

internal static class Platform
{
    /// <summary>The pointer in screen coordinates; false when nothing knows where it is.</summary>
    public static bool Cursor(out int x, out int y)
    {
        if (OperatingSystem.IsMacOS())
        {
            CGPoint c = MacNative.CursorPosition();
            x = (int)Math.Round(c.X);
            y = (int)Math.Round(c.Y);
            return true;
        }
        // where the mouse is in one of the stack's WPF windows, in that window's screen coordinates
        foreach (object o in Exports.TopLevels())
        {
            if (o is not HwndSource { RootVisual: Visual root } s) continue;
            Point? p = Exports.Ui(s, () =>
            {
                if (PresentationSource.FromVisual(root) == null) return (Point?)null;
                Point local = Mouse.GetPosition(root as IInputElement);
                return root is UIElement { IsMouseOver: true } || Mouse.Captured != null ? root.PointToScreen(local) : null;
            });
            if (p is Point screen)
            {
                x = (int)Math.Round(screen.X);
                y = (int)Math.Round(screen.Y);
                return true;
            }
        }
        x = y = 0;
        return false;
    }

    /// <summary>Moves the pointer; false where a program may not (anywhere but macOS here).</summary>
    public static bool SetCursor(int x, int y)
        => OperatingSystem.IsMacOS() && MacNative.CGWarpMouseCursorPosition(new CGPoint { X = x, Y = y }) == 0;

    // Windows virtual-key codes to macOS key codes (kVK_*), for the keys apps actually poll.
    private static readonly Dictionary<int, ushort> s_vkToMac = new()
    {
        [0x08] = 0x33, [0x09] = 0x30, [0x0D] = 0x24, [0x1B] = 0x35, [0x20] = 0x31,
        [0x21] = 0x74, [0x22] = 0x79, [0x23] = 0x77, [0x24] = 0x73, [0x25] = 0x7B, [0x26] = 0x7E, [0x27] = 0x7C, [0x28] = 0x7D,
        [0x2E] = 0x75, [0x70] = 0x7A, [0x71] = 0x78, [0x72] = 0x63, [0x73] = 0x76, [0x74] = 0x60, [0x75] = 0x61,
        [0x76] = 0x62, [0x77] = 0x64, [0x78] = 0x65, [0x79] = 0x6D, [0x7A] = 0x67, [0x7B] = 0x6F,
        ['A'] = 0x00, ['S'] = 0x01, ['D'] = 0x02, ['F'] = 0x03, ['H'] = 0x04, ['G'] = 0x05, ['Z'] = 0x06, ['X'] = 0x07,
        ['C'] = 0x08, ['V'] = 0x09, ['B'] = 0x0B, ['Q'] = 0x0C, ['W'] = 0x0D, ['E'] = 0x0E, ['R'] = 0x0F, ['Y'] = 0x10,
        ['T'] = 0x11, ['1'] = 0x12, ['2'] = 0x13, ['3'] = 0x14, ['4'] = 0x15, ['6'] = 0x16, ['5'] = 0x17, ['9'] = 0x19,
        ['7'] = 0x1A, ['8'] = 0x1C, ['0'] = 0x1D, ['O'] = 0x1F, ['U'] = 0x20, ['I'] = 0x22, ['P'] = 0x23, ['L'] = 0x25,
        ['J'] = 0x26, ['K'] = 0x28, ['N'] = 0x2D, ['M'] = 0x2E,
    };

    /// <summary>Whether virtual key <paramref name="vk"/> (or mouse button: VK_LBUTTON, ...) is down;
    /// for VK_CAPITAL, whether caps lock is on.</summary>
    public static bool KeyDown(int vk)
    {
        if (OperatingSystem.IsMacOS())
        {
            ulong flags = MacNative.CGEventSourceFlagsState(MacNative.CombinedSessionState);
            switch (vk)
            {
                case 0x10: case 0xA0: case 0xA1: return (flags & 0x20000) != 0;   // shift
                case 0x11: case 0xA2: case 0xA3: return (flags & 0x100000) != 0;  // control <- Command, as the stack maps it
                case 0x12: case 0xA4: case 0xA5: return (flags & 0x80000) != 0;   // alt <- Option
                case 0x14: return (flags & 0x10000) != 0;                          // caps lock
                case 0x01: return MacNative.CGEventSourceButtonState(MacNative.CombinedSessionState, 0);
                case 0x02: return MacNative.CGEventSourceButtonState(MacNative.CombinedSessionState, 1);
                case 0x04: return MacNative.CGEventSourceButtonState(MacNative.CombinedSessionState, 2);
            }
            return s_vkToMac.TryGetValue(vk, out ushort key) && MacNative.CGEventSourceKeyState(MacNative.CombinedSessionState, key);
        }
        // WPF's own devices, on a WPF window's thread (they belong to its dispatcher)
        object? window = Exports.TopLevels().FirstOrDefault(o => o is HwndSource);
        return Exports.Ui(window, () => vk switch
        {
            0x01 => Mouse.LeftButton == MouseButtonState.Pressed,
            0x02 => Mouse.RightButton == MouseButtonState.Pressed,
            0x04 => Mouse.MiddleButton == MouseButtonState.Pressed,
            0x05 => Mouse.XButton1 == MouseButtonState.Pressed,
            0x06 => Mouse.XButton2 == MouseButtonState.Pressed,
            0x14 => Keyboard.IsKeyToggled(Key.CapsLock),
            0x10 => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift),
            0x11 => Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl),
            0x12 => Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt),
            _ => KeyInterop.KeyFromVirtualKey(vk) is Key k && k != Key.None && Keyboard.IsKeyDown(k),
        });
    }

    /// <summary>Every display, main first: its bounds and work area, in screen coordinates.</summary>
    public static List<(RECT Bounds, RECT Work)> Displays()
    {
        var list = new List<(RECT, RECT)>();
        if (OperatingSystem.IsMacOS())
        {
            foreach (var (_, bounds) in MacNative.Displays())
            {
                RECT b = new()
                {
                    Left = (int)bounds.Origin.X, Top = (int)bounds.Origin.Y,
                    Right = (int)(bounds.Origin.X + bounds.Size.Width), Bottom = (int)(bounds.Origin.Y + bounds.Size.Height),
                };
                // The menu bar takes the top of the main display; the Dock's share is not known from here.
                RECT w = b;
                if (list.Count == 0) w.Top += 25;
                list.Add((b, w));
            }
            return list;
        }
        // one display, as the stack's WPF describes it (the page, in the browser)
        Rect work = SystemParameters.WorkArea;
        list.Add((new RECT { Left = 0, Top = 0, Right = (int)SystemParameters.PrimaryScreenWidth, Bottom = (int)SystemParameters.PrimaryScreenHeight },
                  new RECT { Left = (int)work.Left, Top = (int)work.Top, Right = (int)work.Right, Bottom = (int)work.Bottom }));
        return list;
    }

    /// <summary>The machine's physical memory in bytes (0 when unknown).</summary>
    public static ulong PhysicalMemory()
    {
        if (OperatingSystem.IsMacOS()) return MacNative.SysctlU64("hw.memsize");
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? (ulong)available : 0;
    }
}
