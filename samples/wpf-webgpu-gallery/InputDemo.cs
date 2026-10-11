// Synthesizes real OS input (mouse, keyboard, touch) so we can prove end-to-end that
// input flows OS -> WPF WndProc -> InputManager -> controls, even though composition is
// handled by the managed WebGPU backend. Nothing here is WebGPU-specific: it's the proof
// that we only replaced rendering, not windowing/input.

using System;
using System.Runtime.InteropServices;
using System.Windows;

internal static class InputDemo
{
    // ---- mouse --------------------------------------------------------------------

    public static void Click(Point screenPx)
    {
        SetCursorPos((int)screenPx.X, (int)screenPx.Y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    public static void MoveCursor(Point screenPx) => SetCursorPos((int)screenPx.X, (int)screenPx.Y);

    // ---- keyboard (Unicode -> WM_CHAR, lands in the focused TextBox) ---------------

    public static void Type(string text)
    {
        foreach (char ch in text)
        {
            SendKey(ch, down: true);
            SendKey(ch, down: false);
        }
    }

    private static void SendKey(char ch, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | (down ? 0u : KEYEVENTF_KEYUP),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ---- touch (real WM_POINTER injection) -----------------------------------------

    private static bool s_touchReady;

    public static bool Tap(Point screenPx)
    {
        try
        {
            if (!s_touchReady)
            {
                if (!InitializeTouchInjection(1, TOUCH_FEEDBACK_DEFAULT)) return false;
                s_touchReady = true;
            }
            var contact = MakeContact((int)screenPx.X, (int)screenPx.Y,
                POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
            if (!InjectTouchInput(1, new[] { contact })) return false;
            contact.pointerInfo.pointerFlags = POINTER_FLAG_UP;
            return InjectTouchInput(1, new[] { contact });
        }
        catch { return false; }
    }

    private static POINTER_TOUCH_INFO MakeContact(int x, int y, uint flags) => new POINTER_TOUCH_INFO
    {
        pointerInfo = new POINTER_INFO
        {
            pointerType = PT_TOUCH,
            pointerId = 0,
            ptPixelLocation = new POINT { x = x, y = y },
            pointerFlags = flags,
        },
        touchFlags = 0,
        touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE,
        rcContact = new RECT { left = x - 2, top = y - 2, right = x + 2, bottom = y + 2 },
        orientation = 0,
        pressure = 32000,
    };

    // ---- P/Invoke ------------------------------------------------------------------

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, POINTER_TOUCH_INFO[] contacts);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    private const uint TOUCH_FEEDBACK_DEFAULT = 1;
    private const uint PT_TOUCH = 2;
    private const uint POINTER_FLAG_DOWN = 0x00010000, POINTER_FLAG_UP = 0x00040000;
    private const uint POINTER_FLAG_INRANGE = 0x00000002, POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint TOUCH_MASK_CONTACTAREA = 0x1, TOUCH_MASK_ORIENTATION = 0x2, TOUCH_MASK_PRESSURE = 0x4;

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }
}
