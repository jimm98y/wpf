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

        // The browser backend has no multi-monitor DPI change; the event exists only to satisfy
        // IPlatformWindow and is never raised here.
#pragma warning disable CS0067
        public event Action<double> ScaleChanged;
#pragma warning restore CS0067

        public static BrowserWindow FromHandle(IntPtr handle)
            => s_byHandle.TryGetValue(handle, out BrowserWindow w) ? w : null;

        /// <summary>The window's title: the page's, while it is the frontmost framed window
        /// (browser-window.js updatePageTitle).</summary>
        internal static void TitleOf(IntPtr handle, string title)
        {
            if (s_byHandle.ContainsKey(handle)) Js.SetWindowTitle((int)handle, title ?? "");
        }

        /// <param name="x">Creation origin in top-left device pixels (borderless windows only;
        /// the main window's canvas is page-positioned).</param>
        public void Create(string title, int x, int y, int width, int height, bool borderless)
        {
            Handle = (IntPtr)System.Threading.Interlocked.Add(ref s_nextHandle, 0x10);
            IsBorderless = borderless;
            Js.CreateWindow((int)Handle, title ?? "", x, y, width, height, borderless);
            s_byHandle[Handle] = this;

            // Start mirroring the accessibility tree for the first real window. Unlike every other
            // head this cannot wait to be asked: the web has no way to detect a screen reader, by
            // design. BrowserAccessibility declines if the page opted out.
            if (!borderless) BrowserAccessibility.Attach(Handle);
        }

        /// <summary>Show/hide the canvas (WPF's ShowWindow SW_HIDE/SW_SHOW), keeping it and this
        /// handle alive so the same window can be shown again.</summary>
        public void SetVisible(bool visible)
        {
            if (Handle == IntPtr.Zero) return;
            Js.SetWindowVisible((int)Handle, visible);
        }

        /// <summary>No-op: a browser page cannot move its own window from script.</summary>
        public void BeginMoveDrag() { }

        public void Destroy()
        {
            if (Handle == IntPtr.Zero)
                return;
            s_byHandle.Remove(Handle);
            Js.DestroyWindow((int)Handle);
            Handle = IntPtr.Zero;
        }

        public void SetContentSize(int width, int height) { _originFrame = -1; Js.SetContentSize((int)Handle, width, height); }

        // Device pixels -> content units. Matches the prior in-caller conversion (round(pixels / scale));
        // the browser backend does not have the Retina odd-width issue the macOS path corrects for.
        public void SetContentSizePixels(int cx, int cy)
        {
            double scale = GetBackingScale();
            if (scale <= 0) scale = 1.0;
            SetContentSize((int)System.Math.Round(cx / scale), (int)System.Math.Round(cy / scale));
        }

        public void SetFrameOrigin(int xPixels, int yPixels) { _originFrame = -1; Js.SetFrameOrigin((int)Handle, xPixels, yPixels); }

        /// <summary>Whether this is a window over the page's own (a dialog): the page's own fills the
        /// page and follows it; this one keeps the place and size it is given.</summary>
        public bool IsSecondary => Handle != IntPtr.Zero && Js.IsSecondaryWindow((int)Handle);

        /// <summary>The window draws its own frame (a WinForms dialog's title bar and edges): the page
        /// adds no shadow of its own round it.</summary>
        public void SetOwnFrame() { _originFrame = -1; Js.SetOwnFrame((int)Handle); }

        /// <summary>A child window (WS_CHILD) that takes the keyboard when it is pressed, as SetFocus on a
        /// child window does on Windows: an ElementHost's WPF tree, whose keys otherwise went to the
        /// page's first window -- the form, whose focused control was whatever had it before the click.</summary>
        public bool TakesKeyboardOnPress
        {
            get => _takesKeyboard;
            set { _takesKeyboard = value; if (Handle != IntPtr.Zero) Js.SetTakesKeyboardOnPress((int)Handle, value); }
        }
        private bool _takesKeyboard;

        /// <summary>The keyboard back to the page's own window: another window was activated, which on
        /// Windows moves the focus out of a child window of the one before.</summary>
        public static void ReleaseKeyboard() => Js.ReleaseKeyboard();

        /// <summary>The page's keys to this window (PlatformFocus moving the focus to it).</summary>
        public void TakeKeyboard() { if (Handle != IntPtr.Zero) Js.SetKeyboardWindow((int)Handle); }

        /// <summary>The parts of the window that show (and take the pointer): x, y, width, height in CSS
        /// pixels from its top-left, four to a rectangle; null for all of it. A child window's clip, as on
        /// Windows (see browser-window.js).</summary>
        /// <summary>A child window's parent (WS_CHILD): its canvas is kept just above the parent's in the
        /// page's z-order, as a child window lies on its parent and under the windows above it.</summary>
        internal void SetParentWindow(IntPtr parent) { if (parent != IntPtr.Zero) Js.SetParentWindow((int)Handle, (int)parent); }

        public void SetVisibleRegion(int[] rects) => Js.SetVisibleRegion((int)Handle, rects == null ? "" : string.Join(",", rects));

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

        // The browser head has no window caption; the outer window size equals the content size.
        public void GetWindowPixelSize(out int width, out int height) => GetPixelSize(out width, out height);

        /// <summary>
        ///  A queued drag event, handed to the WPF drop target.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   The browser only lets a drop happen where dragover answered synchronously, and WPF's
        ///   answer arrives here -- a frame later. So each answer is pushed back to JS to be used for
        ///   the NEXT dragover; see the drag section of browser-window.js for why that is both
        ///   necessary and good enough.
        ///  </para>
        ///  <para>
        ///   Data is only readable during the drop event, so it arrives already read, as a map of
        ///   MIME type to string. Before the drop the type list alone is known, which is all
        ///   DragEnter/DragOver need.
        ///  </para>
        /// </remarks>
        private static void DispatchDrag(JsonElement e)
        {
            IPlatformDropTarget target = PlatformDragDrop.Target;
            if (target == null) return;

            IntPtr handle = (IntPtr)e.GetProperty("h").GetInt32();
            int kind = e.GetProperty("k").GetInt32();

            if (kind == 2)
            {
                target.DragLeave(handle);
                Js.SetDragEffect(0);
                return;
            }

            // The seam takes SCREEN device pixels; the event carries canvas-relative ones.
            int x = e.GetProperty("x").GetInt32();
            int y = e.GetProperty("y").GetInt32();
            BrowserWindow window = FromHandle(handle);
            if (window != null)
            {
                window.GetClientScreenOriginPixels(out int ox, out int oy);
                x += ox;
                y += oy;
            }

            int allowed = e.GetProperty("a").GetInt32();
            int effect;

            switch (kind)
            {
                case 0:
                    effect = target.DragEnter(handle, x, y, MimeTypes(e), ReaderFor(e), allowed);
                    break;

                case 1:
                    effect = target.DragOver(handle, x, y, allowed);
                    break;

                case 3:
                    // The types and data arrive together on the drop. Re-entering with them is what
                    // gives the drop handler a data object it can actually read: everything before
                    // this point could only answer which FORMATS were on offer.
                    target.DragEnter(handle, x, y, MimeTypes(e), ReaderFor(e), allowed);
                    effect = target.Drop(handle, x, y, allowed);
                    Js.SetDragEffect(0);
                    return;

                default:
                    return;
            }

            Js.SetDragEffect(effect);
        }

        private static string[] MimeTypes(JsonElement e)
        {
            JsonElement types = e.GetProperty("m");
            var mimes = new string[types.GetArrayLength()];
            int i = 0;
            foreach (JsonElement type in types.EnumerateArray())
            {
                mimes[i++] = type.GetString();
            }
            return mimes;
        }

        private static Func<string, byte[]> ReaderFor(JsonElement e)
        {
            JsonElement values = e.GetProperty("v");
            if (values.ValueKind != JsonValueKind.Object)
            {
                // Before the drop there is nothing readable, by the browser's rules. Formats are
                // still known, so the drop target can still decide whether it wants this drag.
                return _ => null;
            }

            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (JsonProperty value in values.EnumerateObject())
            {
                string text = value.Value.GetString();
                if (text != null) data[value.Name] = System.Text.Encoding.UTF8.GetBytes(text);
            }

            return mime => data.TryGetValue(mime, out byte[] bytes) ? bytes : null;
        }

        // A canvas does not move on the page within a frame: its origin is asked of the page once a
        // frame (PumpEvents counts them). Asked every time, each mouse event and each hosted scene's
        // frame was two blocking round trips from the app's thread to the page's.
        private static int s_frame;
        private int _originFrame = -1, _originX, _originY;

        public void GetClientScreenOriginPixels(out int sx, out int sy)
        {
            int frame = System.Threading.Volatile.Read(ref s_frame);
            if (_originFrame != frame)
            {
                _originX = Js.GetScreenOriginX((int)Handle);
                _originY = Js.GetScreenOriginY((int)Handle);
                _originFrame = frame;
            }
            sx = _originX;
            sy = _originY;
        }

        // the page's pixel ratio, asked once a frame like the origin (it changes only between frames:
        // a zoom, a move to another display)
        private static int s_ratioFrame = -1;
        private static double s_ratio = 1;

        public double GetBackingScale()
        {
            int frame = System.Threading.Volatile.Read(ref s_frame);
            if (s_ratioFrame != frame)
            {
                s_ratio = Js.GetDevicePixelRatio();
                s_ratioFrame = frame;
            }
            return s_ratio;
        }

        /// <summary>
        /// The page's animation-frame cadence, which IS the display refresh a browser will give us.
        /// Measured over the first frames rather than declared, because no web API reports it.
        /// </summary>
        public double GetRefreshRateHz() => Js.GetRefreshRateHz();

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

        /// <summary>
        /// Deliver a mouse event as if the platform had reported it. See CocoaWindow.InjectMouse
        /// for why the visual-tree inspector goes in here rather than raising routed events:
        /// GetPosition and CaptureMouse read the MouseDevice, not the event.
        /// </summary>
        /// <param name="kind">0 move, 1 down, 2 up, 3 wheel.</param>
        /// <param name="button">DOM button index: 0 left, 1 middle, 2 right.</param>
        internal static void InjectMouse(IntPtr window, int kind, int button, int x, int y, int wheel)
        {
            MouseInput?.Invoke(new BrowserMouseMessage(kind, window, button, x, y, wheel, Environment.TickCount));
        }
        public static event Action<BrowserKeyMessage> KeyInput;

        /// <summary>Deliver a key event as if the DOM had reported it. See InjectMouse.</summary>
        internal static void InjectKey(IntPtr window, bool isDown, string code, string key,
                                       bool ctrl, bool shift, bool alt, bool meta)
        {
            KeyInput?.Invoke(new BrowserKeyMessage(window, isDown, code, key, isRepeat: false,
                                                   ctrl, shift, alt, meta, Environment.TickCount));
        }

        /// <summary>Completes on the next animation frame (display-aligned pacing for the
        /// dispatcher pump; falls back to a 250ms timeout when the tab is hidden).</summary>
        public static System.Threading.Tasks.Task NextFrameAsync() => Js.NextFrame();

        // The page's thread's context on the multithreaded runtime (JSSynchronizationContext, which the
        // runtime installs there), seen the first time a pump waits from it.
        private static System.Threading.SynchronizationContext s_pageContext;

        /// <summary>
        /// Awaits the next animation frame and resumes on the page's thread: <c>await BrowserWindow.NextFrameOnPage()</c>.
        /// On the multithreaded runtime the continuation of a frame's Task lands on a pool thread -- a Web
        /// Worker, where the page's windows and JS modules are out of reach -- and completing a Task from the
        /// page's thread does not bring it back; so the waiting method's own continuation is posted to the
        /// page's context. A pump must not capture the context it runs under: that is its own (a WPF
        /// dispatcher's, a WinForms loop's), which only the pump drains. Single-threaded, the continuation runs
        /// where the frame completed, the page's one thread.
        /// </summary>
        public static PageFrameAwaitable NextFrameOnPage() => default;

        public readonly struct PageFrameAwaitable : System.Runtime.CompilerServices.ICriticalNotifyCompletion
        {
            public PageFrameAwaitable GetAwaiter() => this;
            public bool IsCompleted => false;
            public void GetResult() { }
            public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation)
            {
                if (s_pageContext == null && System.Threading.SynchronizationContext.Current is { } current
                    && current.GetType().Name == "JSSynchronizationContext")
                    s_pageContext = current;
                System.Threading.SynchronizationContext page = s_pageContext;
                NextFrameAsync().ContinueWith(_ =>
                {
                    if (page != null) page.Post(static state => ((Action)state)(), continuation);
                    else continuation();
                }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        private static int s_cursorX, s_cursorY;
        private static bool s_cursorKnown;

        // The modifiers as the page last reported them, with any event over any canvas (Ctrl 1, Shift 2,
        // Alt 4, Meta 8), and which side of each a key event last said was down (bit per side, left low):
        // GetKeyState is the thread's keyboard, not the window's, and a page's pointer events say which
        // modifiers are held. Tracked from WPF's own key reports alone, an Alt pressed while the WinForms
        // form had the keys read as up in the WPF content it hosts, and Alt+drag never rotated a 3D view.
        private static int s_modifiers, s_modifierSides;
        private static bool s_modifiersKnown;

        private static void NoteModifiers(int modifiers)
        {
            s_modifiers = modifiers;
            s_modifiersKnown = true;
        }

        private static void NoteModifierKey(string code, bool down)
        {
            int bit = code switch
            {
                "ControlLeft" => 1 << 0, "ControlRight" => 1 << 1, "ShiftLeft" => 1 << 2, "ShiftRight" => 1 << 3,
                "AltLeft" => 1 << 4, "AltRight" => 1 << 5, "MetaLeft" => 1 << 6, "MetaRight" => 1 << 7,
                _ => 0,
            };
            if (bit == 0) return;
            if (down) s_modifierSides |= bit;
            else s_modifierSides &= ~bit;
        }

        /// <summary>Whether a modifier key (by virtual-key code: VK_SHIFT, VK_LCONTROL, VK_MENU, VK_LWIN,
        /// ...) is down, as the page last reported it; null for any other key, or before the page has
        /// reported anything.</summary>
        internal static bool? ModifierDown(int virtualKey)
        {
            if (!s_modifiersKnown) return null;
            (int held, int side) = virtualKey switch
            {
                0x11 => (1, -1), 0xA2 => (1, 0), 0xA3 => (1, 1),     // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
                0x10 => (2, -1), 0xA0 => (2, 2), 0xA1 => (2, 3),     // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
                0x12 => (4, -1), 0xA4 => (4, 4), 0xA5 => (4, 5),     // VK_MENU, VK_LMENU, VK_RMENU
                0x5B => (8, 6), 0x5C => (8, 7),                      // VK_LWIN, VK_RWIN
                _ => (0, 0),
            };
            if (held == 0) return null;
            if ((s_modifiers & held) == 0) return false;
            if (side < 0) return true;
            // which side: as a key event said; held with no key event seen (pressed outside the page), the left
            int pair = s_modifierSides & (3 << (side & ~1));
            return pair == 0 ? (side & 1) == 0 : (s_modifierSides & (1 << side)) != 0;
        }

        // For the WinForms driver, which answers Control.MousePosition: the pointer as the page last
        // reported it over any canvas, the WPF windows' included (x in the low half, y in the high;
        // long.MinValue before the first event). It heard of the pointer only over its own windows,
        // and a menu popped up at the cursor over a hosted WPF scene went where it had last been.
        static BrowserWindow()
        {
            AppContext.SetData("WpfWebGpu.BrowserWindow.Cursor",
                (Func<long>)(() => s_cursorKnown ? ((long)s_cursorY << 32) | (uint)s_cursorX : long.MinValue));
        }

        /// <summary>GetCursorPos: the pointer's last place over one of the app's canvases, in screen
        /// device pixels -- the space ClientToScreen maps into. A context menu opened with no target
        /// to place it by goes where this says; answering nothing put every one in the corner.</summary>
        internal static bool TryGetCursorPixels(out int x, out int y)
        {
            x = s_cursorX;
            y = s_cursorY;
            return s_cursorKnown;
        }

        /// <summary>Drains DOM events queued by browser-window.js and raises the corresponding
        /// managed events. Called once per dispatcher pump tick on the browser.</summary>
        public static void PumpEvents()
        {
            System.Threading.Interlocked.Increment(ref s_frame);
            // No windows -> the JS module may not even be registered yet; do nothing.
            if (s_byHandle.Count == 0)
                return;

            BrowserAccessibility.Pump();

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
                    case "s":
                    {
                        // Display scale changed (window moved to a different-DPI screen, or the user
                        // zoomed). Same contract as the macOS head's ScaleChanged: HwndTarget updates
                        // its DPI scale, re-lays-out and reconfigures the render surface. The size
                        // report that follows keeps the surface and the backing store in step.
                        int handle = e.GetProperty("h").GetInt32();
                        double scale = e.GetProperty("d").GetDouble();
                        if (s_byHandle.TryGetValue((IntPtr)handle, out BrowserWindow scaled))
                        {
                            scaled.ScaleChanged?.Invoke(scale);
                            scaled.Resized?.Invoke(e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32());
                        }
                        break;
                    }

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
                        // A press on a child window that takes the keyboard gave it the page's keys
                        // (browser-window.js): the focus moved to it, and whichever window had it is told.
                        // Where the pointer is on the screen: what GetCursorPos answers (a canvas has no
                        // other way to ask -- the page reports the pointer only with its events).
                        if (e.TryGetProperty("md", out JsonElement md)) NoteModifiers(md.GetInt32());
                        if (FromHandle((IntPtr)handle) is BrowserWindow under)
                        {
                            under.GetClientScreenOriginPixels(out int ox, out int oy);
                            s_cursorX = ox + e.GetProperty("x").GetInt32();
                            s_cursorY = oy + e.GetProperty("y").GetInt32();
                            s_cursorKnown = true;
                        }
                        // The left button only, as browser-window.js and Windows have it (DefWindowProc
                        // sets the focus on WM_LBUTTONDOWN alone).
                        if (kind == 1 && e.GetProperty("b").GetInt32() == 0 && FromHandle((IntPtr)handle) is { TakesKeyboardOnPress: true })
                            PlatformFocus.FocusMovedByPlatform((IntPtr)handle);
                        int mx = e.GetProperty("x").GetInt32(), my = e.GetProperty("y").GetInt32();
                        // A window holding the mouse capture (SetCapture: WPF's Mouse.Capture, a WinForms
                        // control's grab on a press) gets the mouse wherever the pointer is, as Windows
                        // routes it; the page sends each event to the canvas under the pointer. A panel
                        // dragged out of app's dock lost its moves and its release over the display
                        // canvas (a WPF window of its own) and was left mid-drag.
                        IntPtr captured = MouseCaptureHandle;
                        if (kind != 3 && captured != IntPtr.Zero && captured != (IntPtr)handle
                            && FromHandle(captured) is BrowserWindow holder && FromHandle((IntPtr)handle) is BrowserWindow from)
                        {
                            from.GetClientScreenOriginPixels(out int fx, out int fy);
                            holder.GetClientScreenOriginPixels(out int hx, out int hy);
                            mx += fx - hx;
                            my += fy - hy;
                            handle = (int)captured;
                        }
                        MouseInput?.Invoke(new BrowserMouseMessage(
                            kind,
                            (IntPtr)handle,
                            e.GetProperty("b").GetInt32(),
                            mx,
                            my,
                            kind == 3 ? wheel : e.GetProperty("w").GetInt32(),
                            e.GetProperty("ts").GetInt32()));
                        break;
                    }

                    // A touch or pen CONTACT. Unlike "m", these are not coalesced: each carries its
                    // own contact id, so collapsing consecutive events would merge two fingers.
                    case "tc":
                    {
                        IPlatformTouchSink sink = PlatformTouch.Sink;
                        if (sink is null) break;

                        int kind = e.GetProperty("k").GetInt32();
                        var handle = (IntPtr)e.GetProperty("h").GetInt32();
                        int id = e.GetProperty("id").GetInt32();
                        int x = e.GetProperty("x").GetInt32();
                        int y = e.GetProperty("y").GetInt32();
                        var pen = new PenState(
                            e.GetProperty("p").GetDouble(),
                            e.TryGetProperty("tx", out JsonElement tx) ? tx.GetDouble() : double.NaN,
                            e.TryGetProperty("ty", out JsonElement ty) ? ty.GetDouble() : double.NaN,
                            e.TryGetProperty("inv", out JsonElement inv) && inv.GetBoolean());
                        var ts = (uint)e.GetProperty("ts").GetInt32();

                        // Canvas-relative device pixels plus the window's client origin: the seam
                        // takes screen device pixels, and PlatformTouchDevice subtracts the same
                        // origin on the way back out.
                        IPlatformWindow window = PlatformWindow.FromHandle(handle);
                        if (window is not null)
                        {
                            window.GetClientScreenOriginPixels(out int originX, out int originY);
                            x += originX;
                            y += originY;
                        }

                        switch (kind)
                        {
                            case 1: sink.TouchDown(handle, id, x, y, pen, ts); break;
                            case 0: sink.TouchMove(handle, id, x, y, pen, ts); break;
                            case 2: sink.TouchUp(handle, id, x, y, ts); break;
                            case 3: sink.TouchCancel(handle, id); break;
                        }
                        break;
                    }

                    // Input-method composition. Queued by the hidden editable element rather than by
                    // a window, so there is no handle to route on: the browser has one focused
                    // element at a time, and ImmComposition routes to whichever editor holds focus.
                    // A mirror element was activated or focused by the user or their screen reader.
                    case "a":
                        BrowserAccessibility.DispatchAction(
                            e.GetProperty("id").GetInt32(), e.GetProperty("k").GetInt32());
                        break;

                    case "i":
                        BrowserTextInput.DispatchQueuedEvent(
                            e.GetProperty("k").GetInt32(), e.GetProperty("s").GetString());
                        break;

                    case "d":
                        DispatchDrag(e);
                        break;

                    case "k":
                        NoteModifierKey(e.GetProperty("c").GetString(), e.GetProperty("d").GetBoolean());
                        NoteModifiers((e.GetProperty("ctl").GetBoolean() ? 1 : 0) | (e.GetProperty("sh").GetBoolean() ? 2 : 0)
                            | (e.GetProperty("alt").GetBoolean() ? 4 : 0) | (e.GetProperty("meta").GetBoolean() ? 8 : 0));
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

            [JSImport("setWindowVisible", Module)]
            internal static partial void SetWindowVisible(int handle, bool visible);

            [JSImport("destroyWindow", Module)]
            internal static partial void DestroyWindow(int handle);

            [JSImport("setWindowTitle", Module)]
            internal static partial void SetWindowTitle(int handle, string title);

            [JSImport("setContentSize", Module)]
            internal static partial void SetContentSize(int handle, int width, int height);

            [JSImport("setFrameOrigin", Module)]
            internal static partial void SetFrameOrigin(int handle, int xPixels, int yPixels);

            [JSImport("isSecondaryWindow", Module)]
            internal static partial bool IsSecondaryWindow(int handle);

            [JSImport("setOwnFrame", Module)]
            internal static partial void SetOwnFrame(int handle);

            [JSImport("setTakesKeyboardOnPress", Module)]
            internal static partial void SetTakesKeyboardOnPress(int handle, bool takes);

            [JSImport("releaseKeyboard", Module)]
            internal static partial void ReleaseKeyboard();

            [JSImport("setKeyboardWindow", Module)]
            internal static partial void SetKeyboardWindow(int handle);

            [JSImport("setParentWindow", Module)]
            internal static partial void SetParentWindow(int handle, int parent);

            [JSImport("setVisibleRegion", Module)]
            internal static partial void SetVisibleRegion(int handle, string rects);

            [JSImport("getWidth", Module)]
            internal static partial int GetWidth(int handle, bool devicePixels);

            [JSImport("getHeight", Module)]
            internal static partial int GetHeight(int handle, bool devicePixels);

            [JSImport("getScreenOriginX", Module)]
            internal static partial int GetScreenOriginX(int handle);

            [JSImport("getScreenOriginY", Module)]
            internal static partial int GetScreenOriginY(int handle);

            [JSImport("getRefreshRateHz", Module)]
            internal static partial double GetRefreshRateHz();

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

            [JSImport("a11yIsEnabled", Module)]
            internal static partial bool A11yIsEnabled();

            [JSImport("a11ySync", Module)]
            internal static partial void A11ySync(string json);

            [JSImport("a11ySetFocus", Module)]
            internal static partial void A11ySetFocus(int nodeId);

            [JSImport("enableTextInput", Module)]
            internal static partial void EnableTextInput();

            [JSImport("disableTextInput", Module)]
            internal static partial void DisableTextInput();

            [JSImport("setImeCaretRect", Module)]
            internal static partial void SetImeCaretRect(int x, int y, int width, int height);

            [JSImport("drainEvents", Module)]
            internal static partial string DrainEvents();

            [JSImport("setDragEffect", Module)]
            internal static partial void SetDragEffect(int effect);

            [JSImport("nextFrame", Module)]
            internal static partial System.Threading.Tasks.Task<int> NextFrame();

            [JSImport("printDocument", Module)]
            internal static partial bool PrintDocument(string jobName,
                                                       [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] document);
        }
    }
}
