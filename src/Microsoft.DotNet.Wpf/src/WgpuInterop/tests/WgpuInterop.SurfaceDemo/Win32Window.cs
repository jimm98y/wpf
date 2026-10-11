// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A minimal Win32 top-level window: the first concrete platform "surface
// provider" for the cross-platform renderer. In the full design this sits
// behind an ISurfaceProvider abstraction (Phase 2) so the macOS/Linux/WASM
// providers are siblings. For now it exposes just enough (HWND, HINSTANCE, a
// drained message pump) to hand a window to wgpu and present to it.
//

using System;
using System.Runtime.InteropServices;

namespace WgpuInterop.SurfaceDemo
{
    internal sealed unsafe class Win32Window : IDisposable
    {
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_VISIBLE = 0x10000000;
        private const int CW_USEDEFAULT = unchecked((int)0x80000000);
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_QUIT = 0x0012;
        private const uint PM_REMOVE = 0x0001;
        private const int IDC_ARROW = 32512;

        private readonly WndProcDelegate _wndProc; // rooted for the window's lifetime
        private readonly IntPtr _classNamePtr;

        public IntPtr Hwnd { get; }
        public IntPtr HInstance { get; }
        public bool QuitRequested { get; private set; }

        public Win32Window(string title, int width, int height)
        {
            HInstance = GetModuleHandleW(null);
            _wndProc = WindowProc;
            _classNamePtr = Marshal.StringToHGlobalUni("WgpuInteropSurfaceDemo");

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = 0x0003, // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = HInstance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                lpszClassName = _classNamePtr,
            };
            if (RegisterClassExW(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassExW failed (0x{Marshal.GetLastWin32Error():x}).");

            IntPtr titlePtr = Marshal.StringToHGlobalUni(title);
            try
            {
                Hwnd = CreateWindowExW(
                    0, _classNamePtr, titlePtr, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                    CW_USEDEFAULT, CW_USEDEFAULT, width, height,
                    IntPtr.Zero, IntPtr.Zero, HInstance, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(titlePtr);
            }

            if (Hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowExW failed (0x{Marshal.GetLastWin32Error():x}).");
        }

        /// <summary>Drains pending window messages without blocking.</summary>
        public void PumpMessages()
        {
            MSG msg;
            while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                {
                    QuitRequested = true;
                    return;
                }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        public void Dispose()
        {
            if (Hwnd != IntPtr.Zero)
                DestroyWindow(Hwnd);
            if (_classNamePtr != IntPtr.Zero)
                Marshal.FreeHGlobal(_classNamePtr);
            GC.KeepAlive(_wndProc);
        }

        // ---- Win32 interop ----

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            public IntPtr lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32", SetLastError = true)]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

        [DllImport("user32", SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, IntPtr lpClassName, IntPtr lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32")]
        private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    }
}
