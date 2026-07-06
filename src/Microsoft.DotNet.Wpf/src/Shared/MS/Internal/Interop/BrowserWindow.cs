// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The WebAssembly/browser windowing backend — the browser sibling of CocoaWindow.
// A WPF "window" is an HTMLCanvasElement created and positioned by browser-window.js;
// the canvas handle (an integer this class allocates) is what flows through WPF as
// the HWND, and the WebGPU compositor resolves it to the canvas via the shared
// globalThis.__wpfCanvases registry.
//
// Input: DOM listeners in browser-window.js queue events; the dispatcher's browser
// pump calls PumpEvents() each tick, which drains the queue over JS interop and
// raises the static MouseInput/KeyInput events consumed by the WPF input providers
// (mirroring CocoaWindow's event shape). No JSExport is needed, and events are
// delivered on the dispatcher thread by construction.
//
// The host page must register the JS module before the app runs:
//   import * as wpfWindow from './browser-window.js';
//   setModuleImports('wpfBrowserWindow', wpfWindow);
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace MS.Internal.Interop
{
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    public sealed partial class BrowserWindow : IPlatformWindow
    {
        // Handles are only compared/registry-keyed, never dereferenced. Range chosen
        // to avoid HwndWrapper's synthetic handles (0x7F00_0000+) and JS-side ids.
        private static long s_nextHandle = 0x0B00_0000;
        private static readonly Dictionary<IntPtr, BrowserWindow> s_byHandle = new Dictionary<IntPtr, BrowserWindow>();

        public IntPtr Handle { get; private set; }

        public bool IsBorderless { get; private set; }

        private int _lastResizeW = -1, _lastResizeH = -1;   // resize-report dedupe

        public static IntPtr MouseCaptureHandle { get; set; }

        /// <summary>Raised when the window's content size changed (args in device pixels).</summary>
        public event Action<int, int> Resized;

        public static BrowserWindow FromHandle(IntPtr handle)
            => s_byHandle.TryGetValue(handle, out BrowserWindow w) ? w : null;

        /// <param name="x">Creation origin in top-left device pixels (borderless windows only;
        /// the main window's canvas is page-positioned).</param>
        public void Create(string title, int x, int y, int width, int height, bool borderless)
        {
            Handle = (IntPtr)System.Threading.Interlocked.Add(ref s_nextHandle, 0x10);
            IsBorderless = borderless;
            Js.CreateWindow((int)Handle, title ?? "", x, y, width, height, borderless);
            s_byHandle[Handle] = this;
        }

        public void Destroy()
        {
            if (Handle == IntPtr.Zero)
                return;
            s_byHandle.Remove(Handle);
            Js.DestroyWindow((int)Handle);
            Handle = IntPtr.Zero;
        }

        public void SetContentSize(int width, int height) => Js.SetContentSize((int)Handle, width, height);

        public void SetFrameOrigin(int xPixels, int yPixels) => Js.SetFrameOrigin((int)Handle, xPixels, yPixels);

        public void GetContentSize(out int width, out int height)
        {
            width = Js.GetWidth((int)Handle, false);
            height = Js.GetHeight((int)Handle, false);
        }

        public void GetPixelSize(out int width, out int height)
        {
            width = Js.GetWidth((int)Handle, true);
            height = Js.GetHeight((int)Handle, true);
        }

        public void GetClientScreenOriginPixels(out int sx, out int sy)
        {
            sx = Js.GetScreenOriginX((int)Handle);
            sy = Js.GetScreenOriginY((int)Handle);
        }

        public double GetBackingScale() => Js.GetDevicePixelRatio();

        public static IntPtr HitTest(int x, int y) => (IntPtr)Js.HitTest(x, y);

        public static void SetCursor(string cssCursor) => Js.SetCursor(cssCursor);

        public static bool GetPrimaryScreenPixels(
            out int monLeft, out int monTop, out int monRight, out int monBottom,
            out int workLeft, out int workTop, out int workRight, out int workBottom)
        {
            monLeft = monTop = workLeft = workTop = 0;
            monRight = workRight = Js.GetViewportWidthPixels();
            monBottom = workBottom = Js.GetViewportHeightPixels();
            return true;
        }

        // ---- Input / window events (drained by the dispatcher's browser pump) ------

        public readonly struct BrowserMouseMessage
        {
            /// <summary>0=move 1=down 2=up 3=wheel.</summary>
            public int Kind { get; }
            public IntPtr Window { get; }
            /// <summary>DOM button: 0 left, 1 middle, 2 right.</summary>
            public int Button { get; }
            /// <summary>Client coordinates in device pixels, relative to <see cref="Window"/>.</summary>
            public int X { get; }
            public int Y { get; }
            /// <summary>Wheel delta in Win32 units (+120 per notch away from user).</summary>
            public int Wheel { get; }
            public int TimestampMs { get; }

            public BrowserMouseMessage(int kind, IntPtr window, int button, int x, int y, int wheel, int timestampMs)
            {
                Kind = kind; Window = window; Button = button; X = x; Y = y; Wheel = wheel; TimestampMs = timestampMs;
            }
        }

        public readonly struct BrowserKeyMessage
        {
            public IntPtr Window { get; }
            public bool IsDown { get; }
            /// <summary>DOM KeyboardEvent.code (physical key, e.g. "KeyA", "Enter").</summary>
            public string Code { get; }
            /// <summary>DOM KeyboardEvent.key (logical, e.g. "a", "Shift", "é").</summary>
            public string Key { get; }
            public bool IsRepeat { get; }
            public bool Ctrl { get; }
            public bool Shift { get; }
            public bool Alt { get; }
            public bool Meta { get; }
            public int TimestampMs { get; }

            public BrowserKeyMessage(IntPtr window, bool isDown, string code, string key, bool isRepeat,
                bool ctrl, bool shift, bool alt, bool meta, int timestampMs)
            {
                Window = window; IsDown = isDown; Code = code; Key = key; IsRepeat = isRepeat;
                Ctrl = ctrl; Shift = shift; Alt = alt; Meta = meta; TimestampMs = timestampMs;
            }
        }

        public static event Action<BrowserMouseMessage> MouseInput;
        public static event Action<BrowserKeyMessage> KeyInput;

        /// <summary>Completes on the next animation frame (display-aligned pacing for the
        /// dispatcher pump; falls back to a 250ms timeout when the tab is hidden).</summary>
        public static System.Threading.Tasks.Task NextFrameAsync() => Js.NextFrame();

        /// <summary>Drains DOM events queued by browser-window.js and raises the corresponding
        /// managed events. Called once per dispatcher pump tick on the browser.</summary>
        public static void PumpEvents()
        {
            // No windows -> the JS module may not even be registered yet; do nothing.
            if (s_byHandle.Count == 0)
                return;

            string json = Js.DrainEvents();
            if (string.IsNullOrEmpty(json))
                return;

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement[] events = System.Linq.Enumerable.ToArray(doc.RootElement.EnumerateArray());
            for (int idx = 0; idx < events.Length; idx++)
            {
                JsonElement e = events[idx];
                string type = e.GetProperty("t").GetString();
                switch (type)
                {
                    case "m":
                    {
                        int kind = e.GetProperty("k").GetInt32();
                        int handle = e.GetProperty("h").GetInt32();
                        int wheel = e.GetProperty("w").GetInt32();
                        // Coalesce trackpad floods within one tick: consecutive moves for the
                        // same window keep only the last; consecutive wheels sum their deltas
                        // (exactly what a Win32 message queue does with WM_MOUSEMOVE/WM_MOUSEWHEEL).
                        // One layout per frame instead of one per DOM event.
                        while (idx + 1 < events.Length)
                        {
                            JsonElement n = events[idx + 1];
                            if (n.GetProperty("t").GetString() != "m" ||
                                n.GetProperty("k").GetInt32() != kind ||
                                n.GetProperty("h").GetInt32() != handle ||
                                (kind != 0 && kind != 3))
                                break;
                            if (kind == 3) wheel += n.GetProperty("w").GetInt32();
                            idx++;
                            e = n;
                        }
                        MouseInput?.Invoke(new BrowserMouseMessage(
                            kind,
                            (IntPtr)handle,
                            e.GetProperty("b").GetInt32(),
                            e.GetProperty("x").GetInt32(),
                            e.GetProperty("y").GetInt32(),
                            kind == 3 ? wheel : e.GetProperty("w").GetInt32(),
                            e.GetProperty("ts").GetInt32()));
                        break;
                    }

                    case "k":
                        KeyInput?.Invoke(new BrowserKeyMessage(
                            (IntPtr)e.GetProperty("h").GetInt32(),
                            e.GetProperty("d").GetBoolean(),
                            e.GetProperty("c").GetString(),
                            e.GetProperty("key").GetString(),
                            e.GetProperty("r").GetBoolean(),
                            e.GetProperty("ctl").GetBoolean(),
                            e.GetProperty("sh").GetBoolean(),
                            e.GetProperty("alt").GetBoolean(),
                            e.GetProperty("meta").GetBoolean(),
                            e.GetProperty("ts").GetInt32()));
                        break;

                    case "r":
                    {
                        BrowserWindow win = FromHandle((IntPtr)e.GetProperty("h").GetInt32());
                        if (win != null)
                        {
                            int rw = e.GetProperty("x").GetInt32();
                            int rh = e.GetProperty("y").GetInt32();
                            // Unchanged size never reaches WPF: a WM_SIZE re-layouts the tree
                            // and invalidates the renderer's layer cache.
                            if (rw != win._lastResizeW || rh != win._lastResizeH)
                            {
                                win._lastResizeW = rw;
                                win._lastResizeH = rh;
                                win.Resized?.Invoke(rw, rh);
                            }
                        }
                        break;
                    }
                }
            }
        }

        internal static partial class Js
        {
            private const string Module = "wpfBrowserWindow";

            [JSImport("createWindow", Module)]
            internal static partial void CreateWindow(int handle, string title, int x, int y, int width, int height, bool borderless);

            [JSImport("destroyWindow", Module)]
            internal static partial void DestroyWindow(int handle);

            [JSImport("setContentSize", Module)]
            internal static partial void SetContentSize(int handle, int width, int height);

            [JSImport("setFrameOrigin", Module)]
            internal static partial void SetFrameOrigin(int handle, int xPixels, int yPixels);

            [JSImport("getWidth", Module)]
            internal static partial int GetWidth(int handle, bool devicePixels);

            [JSImport("getHeight", Module)]
            internal static partial int GetHeight(int handle, bool devicePixels);

            [JSImport("getScreenOriginX", Module)]
            internal static partial int GetScreenOriginX(int handle);

            [JSImport("getScreenOriginY", Module)]
            internal static partial int GetScreenOriginY(int handle);

            [JSImport("getDevicePixelRatio", Module)]
            internal static partial double GetDevicePixelRatio();

            [JSImport("getViewportWidthPixels", Module)]
            internal static partial int GetViewportWidthPixels();

            [JSImport("getViewportHeightPixels", Module)]
            internal static partial int GetViewportHeightPixels();

            [JSImport("hitTest", Module)]
            internal static partial int HitTest(int x, int y);

            [JSImport("setCursor", Module)]
            internal static partial void SetCursor(string cssCursor);

            [JSImport("drainEvents", Module)]
            internal static partial string DrainEvents();

            [JSImport("nextFrame", Module)]
            internal static partial System.Threading.Tasks.Task<int> NextFrame();
        }
    }
}
