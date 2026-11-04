// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The window layer for an immersive activity on an OpenXR headset (Meta Quest; any Android OpenXR runtime):
// IAndroidHost, as AndroidHost is for a flat activity, with each window a panel in space instead of a view.
//
// A window that has a surface of its own (the app's main window, a dialog, a borderless form) gets an
// OpenXR swapchain backed by an Android Surface (XR_KHR_android_surface_swapchain). That Surface is handed
// to AndroidWindow as the window's native window, exactly as a SurfaceView's would be, so the renderer
// draws into it unchanged; the compositor shows it as a quad layer. A popup has no surface on Android (it
// is drawn into its owner's, PopupsShareOwnerSurface) and none here: it is only a place the pointer can
// be over.
//
// The windows lie on one plane in front of the user, a desktop of GetScreenPixels' size, each at its
// position on it -- so what the app knows of where its windows are holds -- and a little in front of the
// windows under it. The controller's aim ray is met with that plane, and the point is the pointer: over a
// window it moves the mouse, the trigger is the left button, A the right, the right thumbstick the wheel,
// the left one an orbit (Alt and a left drag, what turns a 3D view), B or Y a 3D view alone, big (the
// fullscreen below). Behind the windows is the room, where
// the runtime can show it (XR_FB_passthrough; WPF_XR_PASSTHROUGH=0 for black).
//
// A window is a stereo pair (WPF_XR_STEREO, on unless "0"): its surface twice its width, the compositor
// drawing the window into each half, the left half seen with the left eye and the right with the right
// (AndroidWindow.StereoPair); it is shown as two quads in the same place, each to one eye. The 2D in the
// two halves is the same; a Viewport3D in it is seen from each eye's place, so it has depth.
//
// What is not a window -- the soft keyboard, the clipboard, dialogs the system draws -- is the flat host's
// (the activity still has its view hierarchy, which an immersive session does not show).
//

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Android.App;
using Android.Graphics;
using Android.Runtime;
using Android.Views;

namespace MS.Internal.Interop
{
    internal sealed unsafe class AndroidXrHost : IAndroidHost
    {
        // the desktop the windows lie on: its size in pixels, how far away, how big a pixel is
        private const int DesktopWidth = 1920, DesktopHeight = 1200;
        private const float Distance = 1.5f;            // metres in front of where the head started
        private const float MetresPerPixel = 0.001f;    // 1920 px is 1.92 m: about 65 degrees across at 1.5 m
        private const float CentreHeight = -0.1f;       // a little below eye level
        private const float LayerStep = 0.002f;         // each window this much in front of the one under it
        private const int CursorPixels = 64;
        private static readonly bool s_stereo = Environment.GetEnvironmentVariable("WPF_XR_STEREO") != "0";
        private const float CursorMetres = 0.012f;

        private readonly Activity _activity;
        private readonly AndroidHost _flat;
        private readonly object _lock = new();
        private readonly List<Panel> _panels = new();   // z-order: last is on top
        private readonly Queue<Action> _work = new();
        private Thread? _thread;
        private volatile bool _quit;

        private ulong _instance, _system, _session, _local;
        private bool _running;
        private int _sessionState;
        private IntPtr _createSurfaceSwapchain;

        private ulong _actionSet, _aimAction, _selectAction, _scrollAction, _secondaryAction;
        private ulong _leftHand, _rightHand;
        private readonly ulong[] _aimSpaces = new ulong[2];
        private int _activeHand = 1;                      // the right hand until the left one presses

        private ulong _cursorSwapchain;
        // each hand's ray where it meets the desktop: a cursor for each (the mouse is the active hand's)
        private readonly bool[] _handAims = new bool[2], _handCursors = new bool[2];
        private readonly Xr.Vector3[] _handHits = new Xr.Vector3[2];
        private readonly int[] _handX = new int[2], _handY = new int[2];

        // the pointer
        private bool _pressed, _secondaryPressed;
        private IntPtr _pressTarget;
        private int _lastX = int.MinValue, _lastY = int.MinValue;
        private IntPtr _hoverTarget;
        private float _scrollCarry;

        private sealed class Panel
        {
            public IntPtr Handle;
            public int X, Y, Width, Height;
            public bool Visible = true, HasSurface, FillsScreen, Translucent, Stereo;
            public ulong Swapchain;
            public int SwapchainWidth, SwapchainHeight;     // the window's size the swapchain was made for
            public long ResizedAt;                           // when its size last changed (SetViewFrame)
            public IntPtr NativeWindow;
            // Windows' frame round a window that has one (AndroidWindow.HasFrame): a swapchain of the outer size,
            // painted with a Canvas through its Surface (a global reference), shown behind the window's own
            public ulong FrameSwapchain;
            public IntPtr FrameSurface;
            public int FrameWidth, FrameHeight;
        }

        /// <summary>The headset's screen is a desktop: the app's own window is moved, sized and maximized as any.</summary>
        public bool DesktopWindows => true;

        /// <summary>Windows' frame round a window with a caption is this host's to draw, as the DWM's is on Windows.</summary>
        public bool DrawsFrames => true;

        public void FrameChanged(IntPtr handle)
        {
            Panel? p;
            lock (_lock) p = Find(handle);
            if (p != null) Post(() => PaintFrame(p));
        }

        public AndroidXrHost(Activity activity, AndroidHost flat)
        {
            _activity = activity;
            _flat = flat;
        }

        /// <summary>Starts the session and its frame loop, on a thread of their own (xrWaitFrame blocks).</summary>
        public void Start()
        {
            _thread = new Thread(Run) { Name = "OpenXR", IsBackground = true };
            _thread.Start();
            // the app's own 3D (DXEngine behind a D3DImage) in its stereo mode, for the windows' stereo pairs
            OfferHeadset();
            if (s_stereo) XrStereo3D.Enable();
            if (int.TryParse(Environment.GetEnvironmentVariable("WPF_XR_COMMANDS"), out int port))
                new Thread(() => Commands(port)) { Name = "XR commands", IsBackground = true }.Start();
        }

        // ---- a test driver's pointer (WPF_XR_COMMANDS=<port>, over adb forward) --------------------
        //
        // One command a line, on loopback: "move|down|up|click x y" (desktop pixels, as the ray's hit),
        // "capture <png path>" (the next window presented, drawn offscreen: WgpuPresenter.Capture). The
        // pointer goes where the controller's goes -- the window under it, the pressed one until the
        // release -- so a scripted session takes the path a person in the headset does.

        private IntPtr _commandPressTarget;

        private void Commands(int port)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            try { listener.Start(); }
            catch (Exception e) { Log($"commands: cannot listen on {port}: {e.Message}"); return; }
            Log($"commands on 127.0.0.1:{port}");
            while (!_quit)
            {
                try
                {
                    using var client = listener.AcceptTcpClient();
                    using var stream = client.GetStream();
                    using var reader = new System.IO.StreamReader(stream);
                    using var writer = new System.IO.StreamWriter(stream) { AutoFlush = true };
                    for (string? line; (line = reader.ReadLine()) != null;)
                        writer.WriteLine(Command(line.Trim()));
                }
                catch (Exception e) { Log("commands: " + e.Message); }
            }
        }

        private string Command(string line)
        {
            string[] a = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) return "ok";
            if (a[0] == "windows")
            {
                lock (_lock) return "ok " + string.Join("; ", _panels.Select(p => $"0x{p.Handle:x} {p.X},{p.Y} {p.Width}x{p.Height}{(p.Visible ? "" : " hidden")}"));
            }
            if (a[0] == "enter") { Key(66, 0, '\r'); return "ok"; }
            if (a[0] == "text" && a.Length == 2)
            {
                foreach (char ch in a[1])
                {
                    if (!KeyOf(ch, out int code, out bool shift)) return "error: no key for " + ch;
                    Key(code, shift ? 1 : 0, ch);
                }
                return "ok";
            }
            if (a[0] == "capture" && a.Length == 2)
            {
                AppContext.SetData("WpfWebGpu.CaptureRequest", a[1]);
                return "ok";
            }
            string[] xy = a.Length == 2 ? a[1].Split(' ') : Array.Empty<string>();
            if (xy.Length != 2 || !int.TryParse(xy[0], out int x) || !int.TryParse(xy[1], out int y))
                return "error: move|down|up|click|fullscreen x y, capture <path>, windows, text <s>, enter";
            switch (a[0])
            {
                case "move":
                    AndroidWindow.NotifyPointer(_commandPressTarget != IntPtr.Zero ? _commandPressTarget : Under(x, y), 0, x, y);
                    return "ok";
                case "down":
                    _commandPressTarget = Under(x, y);
                    AndroidWindow.NotifyPointer(_commandPressTarget, 0, x, y);
                    AndroidWindow.NotifyPointer(_commandPressTarget, 1, x, y);
                    return $"ok 0x{_commandPressTarget:x}";
                case "up":
                    AndroidWindow.NotifyPointer(_commandPressTarget != IntPtr.Zero ? _commandPressTarget : Under(x, y), 2, x, y);
                    _commandPressTarget = IntPtr.Zero;
                    return "ok";
                case "click":
                    Command($"down {x} {y}");
                    return Command($"up {x} {y}");
                case "fullscreen":      // as B or Y with the pointer there
                    ToggleFullscreen(x, y);
                    return s_full != null ? "ok on" : "ok off";
            }
            return "error: unknown " + a[0];
        }

        // ---- the left thumbstick: an orbit, Alt + a left drag -----------------------------------------
        //
        // What turns a 3D view with a mouse where Alt and a left drag orbit it (DXEngine's Orbit3D navigation,
        // as apps set it up). The stick pushed is Alt down and the left button pressed where the ray is; held,
        // a pointer of its own moves from there as fast as the stick is pushed; let go, the button and Alt
        // are released. The ray's own pointer is left where it was.

        private const int KeyAltLeft = 57;                  // android.view.KeyEvent.KEYCODE_ALT_LEFT
        private const int MetaAltLeftOn = 0x2 | 0x10;      // META_ALT_ON | META_ALT_LEFT_ON
        private const float OrbitPixelsPerSecond = 500f;    // at full tilt
        private bool _orbiting;
        private System.Windows.Rect _orbitView;     // the 3D view the stick orbits, desktop pixels
        private long _stickLogged, _sticksRawLogged;

        private string RawStick(ulong hand)
        {
            var get = new Xr.ActionStateGetInfo { Type = Xr.TypeActionStateGetInfo, Action = _scrollAction, SubactionPath = hand };
            var state = new Xr.ActionStateVector2f { Type = Xr.TypeActionStateVector2f };
            int result = Xr.xrGetActionStateVector2f(_session, &get, &state);
            return $"r{result} active {state.IsActive} ({state.CurrentState.X:0.00},{state.CurrentState.Y:0.00})";
        }
        private static readonly bool s_traceSticks = Environment.GetEnvironmentVariable("WPF_XR_TRACE_STICKS") == "1";
        private IntPtr _orbitTarget;
        private float _orbitX, _orbitY;
        private long _orbitIdleSince;

        private void Orbit(Xr.Vector2 stick, long period, int px, int py)
        {
            bool tilted = MathF.Abs(stick.X) > 0.25f || MathF.Abs(stick.Y) > 0.25f;
            if (_orbiting)
            {
                // held a little below where it started (0.1, not 0.25), and let go of for a quarter of a second
                // before the drag ends: a stick turned the other way passes the middle, a thumb eases off
                bool held = MathF.Abs(stick.X) > 0.1f || MathF.Abs(stick.Y) > 0.1f;
                if (held) _orbitIdleSince = 0;
                else if (_orbitIdleSince == 0) _orbitIdleSince = Environment.TickCount64;
                if (_pressed || (!held && Environment.TickCount64 - _orbitIdleSince > 250))
                {
                    _orbitIdleSince = 0;
                    AndroidWindow.NotifyPointer(_orbitTarget, 2, (int)_orbitX, (int)_orbitY);
                    AndroidWindow.NotifyKey(_orbitTarget, false, KeyAltLeft, 0, 0);
                    _orbiting = false;
                    Log($"orbit ends at ({(int)_orbitX},{(int)_orbitY})");
                    return;
                }
                float seconds = period / 1e9f;
                _orbitX += stick.X * OrbitPixelsPerSecond * seconds;
                _orbitY -= stick.Y * OrbitPixelsPerSecond * seconds;    // pushed up, dragged up
                // within the view it turns: a stick held on never drags past it (into what is round it)
                _orbitX = Math.Clamp(_orbitX, (float)_orbitView.Left + 1, (float)_orbitView.Right - 2);
                _orbitY = Math.Clamp(_orbitY, (float)_orbitView.Top + 1, (float)_orbitView.Bottom - 2);
                AndroidWindow.NotifyPointer(_orbitTarget, 0, (int)_orbitX, (int)_orbitY);
                return;
            }
            if (!tilted || _pressed) return;
            // over a 3D view only: Alt and a drag on a window's caption or edge moved or sized the window, and a
            // stick held on took it off into the floor
            if (!XrStereo3D.TryGetViewRect(px, py, out _orbitView) || _orbitView.Width < 8 || _orbitView.Height < 8) return;
            IntPtr target = Under(px, py);
            if (target == IntPtr.Zero) return;
            _orbitTarget = target;
            _orbitX = px;
            _orbitY = py;
            AndroidWindow.NotifyKey(target, true, KeyAltLeft, 0, MetaAltLeftOn);
            AndroidWindow.NotifyPointer(target, 0, px, py);
            AndroidWindow.NotifyPointer(target, 1, px, py);
            _orbiting = true;
            Log($"orbit from ({px},{py}) in 0x{target:x}");
        }

        /// <summary>Where hand <paramref name="i"/>'s ray meets the desktop (_handHits, _handX/Y) and whether its
        /// cursor shows; false when the hand is not tracked or points away.</summary>
        private bool Aim(int i, long time)
        {
            _handCursors[i] = false;
            var location = new Xr.SpaceLocation { Type = Xr.TypeSpaceLocation };
            if (Xr.xrLocateSpace(_aimSpaces[i], _local, time, &location) < 0
                || (location.LocationFlags & (Xr.LocationOrientationValid | Xr.LocationPositionValid)) != (Xr.LocationOrientationValid | Xr.LocationPositionValid))
                return false;
            Xr.Vector3 origin = location.Pose.Position;
            _handPositions[i] = origin;
            Xr.Vector3 dir = Xr.Rotate(location.Pose.Orientation, new Xr.Vector3 { Z = -1 });
            if (dir.Z > -1e-4f) return false;    // pointing away from the desktop
            Fullscreen? full = s_full;
            float plane = full != null ? -FullDistance : -Distance;
            float t = (plane - origin.Z) / dir.Z;
            var hit = new Xr.Vector3 { X = origin.X + dir.X * t, Y = origin.Y + dir.Y * t, Z = plane };
            int px = full != null
                ? (int)MathF.Round(hit.X / full.MetresPerPixel + full.X + full.W / 2f)
                : (int)MathF.Round(hit.X / MetresPerPixel + DesktopWidth / 2f);
            int py = full != null
                ? (int)MathF.Round((FullCentreHeight - hit.Y) / full.MetresPerPixel + full.Y + full.H / 2f)
                : (int)MathF.Round((CentreHeight - hit.Y) / MetresPerPixel + DesktopHeight / 2f);
            _handHits[i] = hit;
            _handX[i] = px;
            _handY[i] = py;
            // on the desktop, or on a window wherever it went (moved or sized past the desktop's edge), or holding one
            _handCursors[i] = (px >= -50 && py >= -50 && px < DesktopWidth + 50 && py < DesktopHeight + 50)
                || (full == null && (At(px, py) != null || (_pressed && i == _activeHand)));
            return true;
        }

        // a key typed for the window that has the keys, as a hardware keyboard's (Android key codes)
        private static void Key(int code, int meta, char ch)
        {
            AndroidWindow.NotifyKey(true, code, ch, meta);
            AndroidWindow.NotifyKey(false, code, 0, meta);
        }

        private static bool KeyOf(char ch, out int code, out bool shift)
        {
            shift = char.IsUpper(ch);
            char c = char.ToLowerInvariant(ch);
            code = c switch
            {
                >= 'a' and <= 'z' => 29 + (c - 'a'),
                >= '0' and <= '9' => 7 + (c - '0'),
                '/' => 76, '.' => 56, '-' => 69, ',' => 55, ' ' => 62, '\\' => 73, ';' => 74, '=' => 70,
                '_' => 69, ':' => 74,
                _ => 0,
            };
            if (ch is '_' or ':') shift = true;
            return code != 0;
        }

        /// <summary>The window the pointer is over at a desktop point, as the controller's ray finds it.</summary>
        private IntPtr Under(int x, int y)
        {
            IntPtr target = AndroidWindow.PointerTarget(x, y, out _, out _);
            if (target != IntPtr.Zero) return target;
            lock (_lock) return At(x, y)?.Handle ?? IntPtr.Zero;
        }

        public void Stop()
        {
            // the app's 3D drawn for a screen again (the panel it goes back to), the eyes no longer known
            if (s_full != null) { s_full = null; s_immersive = false; AppContext.SetData("WpfWebGpu.Xr.WindowsHidden", false); }
            XrStereo3D.Disable();
            lock (s_eyesLock) s_eyesKnown = false;
            _quit = true;
            if (_session != 0) Xr.xrRequestExitSession(_session);
        }

        private static void Log(string message) => Console.WriteLine("[xr] " + message);

        // ---- IAndroidHost: the windows -------------------------------------------------------

        public bool CreateView(IntPtr handle, int x, int y, int width, int height, bool borderless, bool fillsScreen, bool ownSurface)
        {
            var panel = new Panel
            {
                Handle = handle, X = fillsScreen ? 0 : x, Y = fillsScreen ? 0 : y,
                Width = fillsScreen ? DesktopWidth : Math.Max(1, width), Height = fillsScreen ? DesktopHeight : Math.Max(1, height),
                HasSurface = !borderless || ownSurface, FillsScreen = fillsScreen,
            };
            lock (_lock) _panels.Add(panel);
            if (panel.HasSurface) Post(() => CreateSurface(panel));
            return true;
        }

        public void BringToFront(IntPtr handle)
        {
            lock (_lock)
            {
                Panel? p = Find(handle);
                if (p == null) return;
                _panels.Remove(p);
                _panels.Add(p);
            }
        }

        public void SetViewTranslucent(IntPtr handle)
        {
            lock (_lock) if (Find(handle) is { } p) p.Translucent = true;
        }

        public void SetViewFrame(IntPtr handle, int x, int y, int width, int height)
        {
            Panel? p;
            bool resized;
            lock (_lock)
            {
                p = Find(handle);
                if (p == null || p.FillsScreen) return;
                resized = p.Width != width || p.Height != height;
                p.X = x; p.Y = y;
                p.Width = Math.Max(1, width); p.Height = Math.Max(1, height);
            }
            // A swapchain's images are the size it was made at, and a quad shows a rectangle of them (a stereo
            // pair's eyes its halves): a window of another size is given a swapchain of its own. Queued into
            // the old one, a dialog made at the default 1024x768 and sized to its content showed in one eye
            // only -- the other eye's half lay in the part of the images nothing was drawn to.
            // a new swapchain is black until the window draws into it: made once the size has been still a moment
            // (Frame), the old one meanwhile -- a window being sized would otherwise be black until it was let go
            if (resized) p.ResizedAt = Environment.TickCount64;
        }

        private void ResizeSettled()
        {
            List<Panel> due;
            long now = Environment.TickCount64;
            lock (_lock) due = _panels.FindAll(p => p.Swapchain != 0 && (p.SwapchainWidth != p.Width || p.SwapchainHeight != p.Height) && now - p.ResizedAt > 250);
            foreach (Panel p in due) ResizeSurface(p);
        }

        private void ResizeSurface(Panel panel)
        {
            if (panel.Swapchain == 0 || (panel.SwapchainWidth == panel.Width && panel.SwapchainHeight == panel.Height)) return;
            lock (_lock) if (Find(panel.Handle) != panel) return;
            DestroyFrame(panel);
            AndroidWindow.NotifySurfaceDestroyed(panel.Handle);
            ulong old = panel.Swapchain;
            IntPtr oldWindow = panel.NativeWindow;
            panel.Swapchain = 0;
            panel.NativeWindow = IntPtr.Zero;
            Xr.xrDestroySwapchain(old);
            if (oldWindow != IntPtr.Zero) ANativeWindow_release(oldWindow);
            CreateSurface(panel);
        }

        public void SetViewVisible(IntPtr handle, bool visible)
        {
            lock (_lock) if (Find(handle) is { } p) p.Visible = visible;
        }

        public void DestroyView(IntPtr handle)
        {
            Panel? p;
            lock (_lock)
            {
                p = Find(handle);
                if (p == null) return;
                _panels.Remove(p);
            }
            if (p.NativeWindow != IntPtr.Zero) AndroidWindow.NotifySurfaceDestroyed(p.Handle);
            Post(() =>
            {
                if (p.Swapchain != 0) Xr.xrDestroySwapchain(p.Swapchain);
                if (p.NativeWindow != IntPtr.Zero) ANativeWindow_release(p.NativeWindow);
                p.Swapchain = 0;
                p.NativeWindow = IntPtr.Zero;
                DestroyFrame(p);
            });
        }

        private Panel? Find(IntPtr handle)
        {
            foreach (Panel p in _panels) if (p.Handle == handle) return p;
            return null;
        }

        // ---- IAndroidHost: the rest is the flat host's ------------------------------------------

        public void ShowSoftKeyboard(IntPtr handle, bool multiline, bool password) => _flat.ShowSoftKeyboard(handle, multiline, password);
        public void HideSoftKeyboard(IntPtr handle) => _flat.HideSoftKeyboard(handle);
        public void SetImeCursorRect(IntPtr handle, int x, int y, int width, int height) => _flat.SetImeCursorRect(handle, x, y, width, height);
        public double Density => _flat.Density;
        public double RefreshRateHz => 72;

        public void GetScreenPixels(out int width, out int height)
        {
            width = DesktopWidth;
            height = DesktopHeight;
        }

        // ---- the session ---------------------------------------------------------------------

        private void Post(Action work)
        {
            lock (_work) _work.Enqueue(work);
        }

        private void RunWork()
        {
            while (true)
            {
                Action? work;
                lock (_work) { if (_work.Count == 0) return; work = _work.Dequeue(); }
                try { work(); } catch (Exception e) { Log("work failed: " + e); }
            }
        }

        private void Run()
        {
            try
            {
                if (!Initialize()) return;
                while (!_quit)
                {
                    PollEvents();
                    RunWork();
                    if (!_running) { Thread.Sleep(10); continue; }
                    Frame();
                }
            }
            catch (Exception e) { Log("the session ended with an exception: " + e); }
            finally
            {
                if (_session != 0) Xr.xrDestroySession(_session);
                if (_instance != 0) Xr.xrDestroyInstance(_instance);
                _session = _instance = 0;
            }
        }

        private static void Check(int result, string what)
        {
            if (result < 0) throw new InvalidOperationException($"{what} failed: XrResult {result}");
        }

        private const string FlipExtension = "XR_FB_composition_layer_image_layout";
        private bool _flipLayers;

        private static HashSet<string> InstanceExtensions()
        {
            var names = new HashSet<string>();
            uint count = 0;
            if (Xr.xrEnumerateInstanceExtensionProperties(null, 0, &count, null) < 0 || count == 0) return names;
            var properties = new Xr.ExtensionProperties[count];
            for (int i = 0; i < properties.Length; i++) properties[i].Type = Xr.TypeExtensionProperties;
            fixed (Xr.ExtensionProperties* p = properties)
            {
                if (Xr.xrEnumerateInstanceExtensionProperties(null, count, &count, p) < 0) return names;
                for (int i = 0; i < count; i++) names.Add(Marshal.PtrToStringAnsi((IntPtr)p[i].ExtensionName) ?? "");
            }
            return names;
        }

        private bool Initialize()
        {
            IntPtr vm = Java.Interop.JniEnvironment.Runtime.InvocationPointer;
            IntPtr activity = _activity.Handle;

            // the loader on Android must be told the VM and a context before anything else
            IntPtr init = Xr.Proc(0, "xrInitializeLoaderKHR");
            if (init != IntPtr.Zero)
            {
                var loaderInfo = new Xr.LoaderInitInfoAndroid { Type = Xr.TypeLoaderInitInfoAndroid, ApplicationVM = vm, ApplicationContext = activity };
                Check(((delegate* unmanaged<Xr.LoaderInitInfoAndroid*, int>)init)(&loaderInfo), "xrInitializeLoaderKHR");
            }

            var extensions = new List<string> { "XR_KHR_android_create_instance", "XR_KHR_opengl_es_enable", "XR_KHR_android_surface_swapchain" };
            // a Surface's buffers are top row first, the compositor samples a swapchain bottom row first:
            // where the runtime can, the quads say so (otherwise the windows show upside down)
            _flipLayers = InstanceExtensions().Contains(FlipExtension);
            if (_flipLayers) extensions.Add(FlipExtension);
            Log(_flipLayers ? "quads flipped vertically" : "the runtime has no " + FlipExtension);
            // the room behind the windows (WPF_XR_PASSTHROUGH, on unless "0"), where the runtime can show it
            _passthroughExtension = s_passthrough && InstanceExtensions().Contains(PassthroughExtension);
            if (_passthroughExtension) extensions.Add(PassthroughExtension);
            var names = new IntPtr[extensions.Count];
            try
            {
                for (int i = 0; i < extensions.Count; i++) names[i] = Marshal.StringToHGlobalAnsi(extensions[i]);
                var android = new Xr.InstanceCreateInfoAndroid { Type = Xr.TypeInstanceCreateInfoAndroid, ApplicationVM = vm, ApplicationActivity = activity };
                var info = new Xr.InstanceCreateInfo { Type = Xr.TypeInstanceCreateInfo, Next = &android, EnabledExtensionCount = (uint)names.Length };
                Xr.Copy(_activity.PackageName ?? "wpfrun", info.ApplicationInfo.ApplicationName, 128);
                Xr.Copy("WpfWebGpu", info.ApplicationInfo.EngineName, 128);
                info.ApplicationInfo.ApplicationVersion = 1;
                info.ApplicationInfo.EngineVersion = 1;
                info.ApplicationInfo.ApiVersion = Xr.MakeVersion(1, 0, 34);
                fixed (IntPtr* n = names)
                {
                    info.EnabledExtensionNames = (byte**)n;
                    ulong instance;
                    Check(Xr.xrCreateInstance(&info, &instance), "xrCreateInstance");
                    _instance = instance;
                }
            }
            finally
            {
                foreach (IntPtr n in names) if (n != IntPtr.Zero) Marshal.FreeHGlobal(n);
            }

            var systemInfo = new Xr.SystemGetInfo { Type = Xr.TypeSystemGetInfo, FormFactor = Xr.FormFactorHeadMountedDisplay };
            ulong system;
            Check(Xr.xrGetSystem(_instance, &systemInfo, &system), "xrGetSystem");
            _system = system;

            // a session needs a graphics binding; this host draws nothing with it (its layers are Surfaces)
            IntPtr requirements = Xr.Proc(_instance, "xrGetOpenGLESGraphicsRequirementsKHR");
            var req = new Xr.GraphicsRequirementsOpenGles { Type = Xr.TypeGraphicsRequirementsOpenGles };
            Check(((delegate* unmanaged<ulong, ulong, Xr.GraphicsRequirementsOpenGles*, int>)requirements)(_instance, _system, &req), "xrGetOpenGLESGraphicsRequirementsKHR");
            var binding = CreateEglBinding();

            var sessionInfo = new Xr.SessionCreateInfo { Type = Xr.TypeSessionCreateInfo, Next = &binding, SystemId = _system };
            ulong session;
            Check(Xr.xrCreateSession(_instance, &sessionInfo, &session), "xrCreateSession");
            _session = session;

            var spaceInfo = new Xr.ReferenceSpaceCreateInfo { Type = Xr.TypeReferenceSpaceCreateInfo, ReferenceSpaceType = Xr.ReferenceSpaceLocal, PoseInReferenceSpace = Xr.Identity };
            ulong local;
            Check(Xr.xrCreateReferenceSpace(_session, &spaceInfo, &local), "xrCreateReferenceSpace");
            _local = local;

            _createSurfaceSwapchain = Xr.Proc(_instance, "xrCreateSwapchainAndroidSurfaceKHR");
            if (_createSurfaceSwapchain == IntPtr.Zero) throw new InvalidOperationException("the runtime has no XR_KHR_android_surface_swapchain");

            CreateActions();
            CreateCursor();
            if (_passthroughExtension) CreatePassthrough();
            Log("session created");
            return true;
        }

        private static Xr.GraphicsBindingOpenGlesAndroid CreateEglBinding()
        {
            IntPtr display = Egl.eglGetDisplay(IntPtr.Zero);
            int major, minor;
            Egl.eglInitialize(display, &major, &minor);
            int* attribs = stackalloc int[] { Egl.RedSize, 8, Egl.GreenSize, 8, Egl.BlueSize, 8, Egl.AlphaSize, 8,
                Egl.RenderableType, Egl.OpenGlEs3Bit, Egl.SurfaceType, Egl.PbufferBit, Egl.None };
            IntPtr config;
            int count;
            Egl.eglChooseConfig(display, attribs, &config, 1, &count);
            int* contextAttribs = stackalloc int[] { Egl.ContextClientVersion, 3, Egl.None };
            IntPtr context = Egl.eglCreateContext(display, config, IntPtr.Zero, contextAttribs);
            int* surfaceAttribs = stackalloc int[] { Egl.Width, 16, Egl.Height, 16, Egl.None };
            IntPtr surface = Egl.eglCreatePbufferSurface(display, config, surfaceAttribs);
            Egl.eglMakeCurrent(display, surface, surface, context);
            return new Xr.GraphicsBindingOpenGlesAndroid { Type = Xr.TypeGraphicsBindingOpenGlesAndroid, Display = display, Config = config, Context = context };
        }

        private void PollEvents()
        {
            var buffer = new Xr.EventDataBuffer();
            while (true)
            {
                buffer.Type = Xr.TypeEventDataBuffer;
                buffer.Next = null;
                int result = Xr.xrPollEvent(_instance, &buffer);
                if (result != Xr.Success) return;
                if (buffer.Type == Xr.TypeEventDataSessionStateChanged)
                {
                    var changed = (Xr.EventDataSessionStateChanged*)&buffer;
                    _sessionState = changed->State;
                    Log($"session state {_sessionState}");
                    if (_sessionState == 5) _focusedSince = Environment.TickCount64;     // XR_SESSION_STATE_FOCUSED
                    switch (_sessionState)
                    {
                        case Xr.StateReady:
                            var begin = new Xr.SessionBeginInfo { Type = Xr.TypeSessionBeginInfo, PrimaryViewConfigurationType = Xr.ViewConfigurationPrimaryStereo };
                            Check(Xr.xrBeginSession(_session, &begin), "xrBeginSession");
                            _running = true;
                            break;
                        case Xr.StateStopping:
                            Xr.xrEndSession(_session);
                            _running = false;
                            break;
                        case Xr.StateExiting:
                        case Xr.StateLossPending:
                            _running = false;
                            _quit = true;
                            _activity.RunOnUiThread(() => _activity.Finish());
                            break;
                    }
                }
                else if (buffer.Type == Xr.TypeEventDataInstanceLossPending)
                {
                    _running = false;
                    _quit = true;
                }
            }
        }

        // ---- the windows' surfaces -------------------------------------------------------------

        // the formats a swapchain may be made in, the runtime's own order (asked once); 0 first, the format an
        // Android Surface's producer chooses for itself
        private long[]? _formats;

        private long[] SwapchainFormats()
        {
            if (_formats != null) return _formats;
            uint count = 0;
            Xr.xrEnumerateSwapchainFormats(_session, 0, &count, null);
            var formats = new long[count];
            if (count > 0) fixed (long* f = formats) Xr.xrEnumerateSwapchainFormats(_session, count, &count, f);
            Log("swapchain formats: " + string.Join(", ", Array.ConvertAll(formats, f => "0x" + f.ToString("x"))));
            var list = new List<long> { 0 };
            list.AddRange(formats);
            return _formats = list.ToArray();
        }

        /// <summary>A swapchain backed by an Android Surface, in the first format the runtime takes.</summary>
        private int CreateSurfaceSwapchain(Xr.SwapchainCreateInfo info, out ulong swapchain, out IntPtr surface)
        {
            int result = -1;
            swapchain = 0;
            surface = IntPtr.Zero;
            // what a runtime takes for a Surface's swapchain varies (the extension leaves the producer the
            // format and the usage): the last that worked first, then every usage with every format
            // the Surface's producer owns the format, the samples, the faces, the layers and the mips: the
            // runtime wants them unset (as its compositor samples create them)
            info.SampleCount = info.FaceCount = info.ArraySize = info.MipCount = 0;
            if (_surfaceUsage is { } known)
            {
                info.UsageFlags = known.Usage;
                info.Format = known.Format;
                if (TryCreate(ref info, out swapchain, out surface, out result)) return result;
            }
            foreach (ulong usage in new ulong[] { 0, Xr.SwapchainUsageSampled, Xr.SwapchainUsageColorAttachment, Xr.SwapchainUsageColorAttachment | Xr.SwapchainUsageSampled })
            {
                foreach (long format in SwapchainFormats())
                {
                    info.UsageFlags = usage;
                    info.Format = format;
                    if (TryCreate(ref info, out swapchain, out surface, out result))
                    {
                        if (_surfaceUsage == null) Log($"surface swapchains: usage 0x{usage:x}, format 0x{format:x}");
                        _surfaceUsage = (usage, format);
                        return result;
                    }
                }
            }
            return result;
        }

        private (ulong Usage, long Format)? _surfaceUsage;

        private bool TryCreate(ref Xr.SwapchainCreateInfo info, out ulong swapchain, out IntPtr surface, out int result)
        {
            ulong sc;
            IntPtr sf;
            fixed (Xr.SwapchainCreateInfo* p = &info)
                result = ((delegate* unmanaged<ulong, Xr.SwapchainCreateInfo*, ulong*, IntPtr*, int>)_createSurfaceSwapchain)(_session, p, &sc, &sf);
            swapchain = sc;
            surface = sf;
            return result >= 0 && sf != IntPtr.Zero;
        }

        private void CreateSurface(Panel panel)
        {
            if (panel.Swapchain != 0) return;
            // a stereo pair: the compositor makes the surface twice the window's width (AndroidWindow.StereoPair)
            if (s_stereo && AndroidWindow.FromHandle(panel.Handle) is { } w)
            {
                w.StereoPair = true;
                panel.Stereo = true;
            }
            var info = new Xr.SwapchainCreateInfo
            {
                Type = Xr.TypeSwapchainCreateInfo, UsageFlags = Xr.SwapchainUsageColorAttachment | Xr.SwapchainUsageSampled,
                SampleCount = 1, Width = (uint)(panel.Stereo ? 2 * panel.Width : panel.Width), Height = (uint)panel.Height, FaceCount = 1, ArraySize = 1, MipCount = 1,
            };
            int result = CreateSurfaceSwapchain(info, out ulong swapchain, out IntPtr surface);
            if (result < 0 || surface == IntPtr.Zero) { Log($"no surface for window 0x{panel.Handle:x}: XrResult {result}"); return; }
            IntPtr window = ANativeWindow_fromSurface(JNIEnv.Handle, surface);
            panel.Swapchain = swapchain;
            panel.SwapchainWidth = panel.Width;
            panel.SwapchainHeight = panel.Height;
            panel.NativeWindow = window;
            Log($"window 0x{panel.Handle:x}: {panel.Width}x{panel.Height} surface");
            CreateFrame(panel);
            AndroidWindow.NotifySurfaceChanged(panel.Handle, window, panel.Width, panel.Height);
        }

        // ---- a window's frame: Windows 11's, as the DWM draws it (WindowChrome draws a form's the same) ------------

        private void CreateFrame(Panel panel)
        {
            if (panel.FrameSwapchain != 0 || AndroidWindow.FromHandle(panel.Handle) is not { HasFrame: true } w) return;
            w.GetFrameRect(out _, out _, out int width, out int height);
            var info = new Xr.SwapchainCreateInfo
            {
                Type = Xr.TypeSwapchainCreateInfo, UsageFlags = Xr.SwapchainUsageColorAttachment | Xr.SwapchainUsageSampled,
                SampleCount = 1, Width = (uint)width, Height = (uint)height, FaceCount = 1, ArraySize = 1, MipCount = 1,
            };
            if (CreateSurfaceSwapchain(info, out ulong swapchain, out IntPtr surface) < 0 || surface == IntPtr.Zero)
            {
                Log($"no frame for window 0x{panel.Handle:x}");
                return;
            }
            panel.FrameSwapchain = swapchain;
            panel.FrameSurface = JNIEnv.NewGlobalRef(surface);
            panel.FrameWidth = width;
            panel.FrameHeight = height;
            PaintFrame(panel);
        }

        private static void DestroyFrame(Panel panel)
        {
            if (panel.FrameSwapchain != 0) Xr.xrDestroySwapchain(panel.FrameSwapchain);
            if (panel.FrameSurface != IntPtr.Zero) JNIEnv.DeleteGlobalRef(panel.FrameSurface);
            panel.FrameSwapchain = 0;
            panel.FrameSurface = IntPtr.Zero;
        }

        /// <summary>The frame drawn (the XR thread): the shadow in the resize margin outside the visible edge, the
        /// edge, the caption with the title and the close button -- white, or grey for a window that is not active,
        /// the close button red under the pointer.</summary>
        private static void PaintFrame(Panel panel)
        {
            if (panel.FrameSurface == IntPtr.Zero || AndroidWindow.FromHandle(panel.Handle) is not { HasFrame: true } w) return;
            int width = panel.FrameWidth, height = panel.FrameHeight;
            w.GetFrameInsets(out int left, out int top, out _, out _);
            float scale = (float)w.GetBackingScale();
            bool active = w.IsActiveWindow;
            using var javaSurface = Java.Lang.Object.GetObject<Surface>(panel.FrameSurface, JniHandleOwnership.DoNotTransfer)!;
            Canvas canvas = javaSurface.LockCanvas(null);
            try
            {
                canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear!);
                using var paint = new Paint();
                int margin = Math.Max(0, left - 1);
                int vl = margin, vt = 0, vr = width - margin, vb = height - margin;      // the visible window
                // the shadow: a ring a pixel wide per step out, each pixel in one ring only
                for (int i = margin; i > 0; i--)
                {
                    float k = (float)(margin - i + 1) / Math.Max(1, margin);
                    paint.Color = Color.Argb((int)(28 * k * k), 0, 0, 0);
                    int st = vt + Math.Min(i, 2), sb = vb + i - 1;
                    canvas.DrawRect(vl - i, st, vl - i + 1, sb + 1, paint);
                    canvas.DrawRect(vr + i - 1, st, vr + i, sb + 1, paint);
                    canvas.DrawRect(vl - i + 1, sb, vr + i - 1, sb + 1, paint);
                }
                paint.Color = active ? Color.White : Color.Rgb(243, 243, 243);
                canvas.DrawRect(vl, vt, vr, vb, paint);
                float line = Math.Max(1, MathF.Round(scale));
                paint.SetStyle(Paint.Style.Stroke!);
                paint.StrokeWidth = line;
                paint.Color = active ? Color.Rgb(120, 120, 120) : Color.Rgb(170, 170, 170);
                canvas.DrawRect(vl + line / 2, vt + line / 2, vr - line / 2, vb - line / 2, paint);
                paint.SetStyle(Paint.Style.Fill!);

                // the close button, 46 pixels at 96 dpi across the caption's right end
                int closeRight = vr - 1, closeLeft = w.FrameHasCloseBox ? closeRight - (int)MathF.Round(46 * scale) : closeRight;
                if (w.FrameHasCloseBox)
                {
                    int state = w.FrameState;
                    if (state != 0)
                    {
                        paint.Color = state == AndroidWindow.FrameClosePressed ? Color.Rgb(200, 66, 58) : Color.Rgb(196, 43, 28);
                        canvas.DrawRect(closeLeft, vt + 1, closeRight, top, paint);
                    }
                    float cx = (closeLeft + closeRight) / 2f, cy = (vt + 1 + top) / 2f, arm = 5 * scale;
                    paint.AntiAlias = true;
                    paint.SetStyle(Paint.Style.Stroke!);
                    paint.StrokeWidth = scale;
                    paint.Color = state != 0 ? Color.White : active ? Color.Black : Color.Rgb(150, 150, 150);
                    canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, paint);
                    canvas.DrawLine(cx + arm, cy - arm, cx - arm, cy + arm, paint);
                    paint.SetStyle(Paint.Style.Fill!);
                }

                // the title: the caption font (9 points, 12 pixels at 96 dpi) from the left edge to the buttons
                paint.AntiAlias = true;
                paint.TextSize = 12 * scale;
                paint.Color = active ? Color.Black : Color.Rgb(150, 150, 150);
                float textLeft = vl + 1 + 11 * scale, room = closeLeft - 4 * scale - textLeft;
                string title = w.Title;
                if (room > 0 && paint.MeasureText(title) > room)
                {
                    while (title.Length > 0 && paint.MeasureText(title + "\u2026") > room) title = title[..^1];
                    title += "\u2026";
                }
                Paint.FontMetrics metrics = paint.GetFontMetrics()!;
                float baseline = (vt + 1 + top) / 2f - (metrics.Ascent + metrics.Descent) / 2f;
                if (room > 0) canvas.DrawText(title, textLeft, baseline, paint);
            }
            finally
            {
                javaSurface.UnlockCanvasAndPost(canvas);
            }
        }

        private void CreateCursor()
        {
            var info = new Xr.SwapchainCreateInfo
            {
                Type = Xr.TypeSwapchainCreateInfo, UsageFlags = Xr.SwapchainUsageColorAttachment | Xr.SwapchainUsageSampled,
                SampleCount = 1, Width = CursorPixels, Height = CursorPixels, FaceCount = 1, ArraySize = 1, MipCount = 1,
            };
            if (CreateSurfaceSwapchain(info, out ulong swapchain, out IntPtr surface) < 0 || surface == IntPtr.Zero)
            {
                Log("no surface for the cursor");
                return;
            }
            _cursorSwapchain = swapchain;
            // a white dot ringed in dark grey, drawn once
            using var javaSurface = Java.Lang.Object.GetObject<Surface>(surface, JniHandleOwnership.DoNotTransfer)!;
            Canvas canvas = javaSurface.LockCanvas(null);
            canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear!);
            using var paint = new Paint { AntiAlias = true };
            paint.Color = Color.Argb(220, 40, 40, 40);
            canvas.DrawCircle(CursorPixels / 2f, CursorPixels / 2f, CursorPixels / 2f - 2, paint);
            paint.Color = Color.White;
            canvas.DrawCircle(CursorPixels / 2f, CursorPixels / 2f, CursorPixels / 2f - 10, paint);
            javaSurface.UnlockCanvasAndPost(canvas);
        }

        // ---- a frame -----------------------------------------------------------------------------

        private void Frame()
        {
            TryPendingFullscreen();
            ResizeSettled();
            var waitInfo = new Xr.FrameWaitInfo { Type = Xr.TypeFrameWaitInfo };
            var state = new Xr.FrameState { Type = Xr.TypeFrameState };
            Check(Xr.xrWaitFrame(_session, &waitInfo, &state), "xrWaitFrame");
            var beginInfo = new Xr.FrameBeginInfo { Type = Xr.TypeFrameBeginInfo };
            Check(Xr.xrBeginFrame(_session, &beginInfo), "xrBeginFrame");


            if (_sessionState == Xr.StateFocused) Pointer(state.PredictedDisplayTime, state.PredictedDisplayPeriod);
            if (s_traceSticks && Environment.TickCount64 - _sticksRawLogged > 2000)
            {
                _sticksRawLogged = Environment.TickCount64;
                Log($"sticks raw: session {_sessionState}, left {RawStick(_leftHand)}, right {RawStick(_rightHand)}");
            }
            LocateEyes(state.PredictedDisplayTime);
            NoteEyesTime(state.PredictedDisplayTime);

            List<Panel> shown;
            lock (_lock) shown = _panels.FindAll(p => p.Visible && p.HasSurface && p.Swapchain != 0);
            int quadsForPanels = 0;
            foreach (Panel p in shown) quadsForPanels += (p.Stereo ? 2 : 1) + (p.FrameSwapchain != 0 ? 1 : 0);
            int cursors = _cursorSwapchain == 0 ? 0 : (_handCursors[0] ? 1 : 0) + (_handCursors[1] ? 1 : 0);
            int count = state.ShouldRender != 0 ? quadsForPanels + cursors : 0;
            Xr.CompositionLayerQuad* quads = stackalloc Xr.CompositionLayerQuad[Math.Max(1, count)];
            var flip = new Xr.CompositionLayerImageLayout { Type = Xr.TypeCompositionLayerImageLayout, Flags = Xr.ImageLayoutVerticalFlip };
            _flip = _flipLayers ? &flip : null;
            void** layers = stackalloc void*[count + 2];
            Xr.CompositionLayerProjectionView* projectionViews = stackalloc Xr.CompositionLayerProjectionView[2];
            var projection = new Xr.CompositionLayerProjection();
            int n = 0;
            // the room first, under everything
            var room = new Xr.CompositionLayerPassthrough { Type = Xr.TypeCompositionLayerPassthrough, LayerHandle = _passthroughLayer };
            if (_passthroughLayer != 0 && state.ShouldRender != 0) layers[n++] = &room;
            if (count > 0)
            {
                int q = 0;      // the quads' own index: the layers' may start past the room's
                Fullscreen? full = s_full;
                for (int i = 0; i < shown.Count; i++)
                {
                    Panel p = shown[i];
                    if (full != null)
                    {
                        // the 3D view alone, big, in front of the user: from the window it is in (its centre's --
                        // a view may hang over the window's edge, the tower's did by 41 pixels, and showed nothing)
                        int cx = full.X + full.W / 2, cy = full.Y + full.H / 2;
                        if (cx < p.X || cy < p.Y || cx >= p.X + p.Width || cy >= p.Y + p.Height) continue;
                        var at = new Xr.Vector3 { X = 0, Y = FullCentreHeight, Z = -FullDistance };
                        // a panel of its own once its eyes' pictures are side by side: an engine's D3DImage in its stereo
                        // mode, or a WPF Viewport3D the compositor draws for the eyes
                        bool panel = FullPanel(out int iw, out int ih)
                            && (AppContext.GetData("WpfWebGpu.Xr.SideBySideD3DImages") is true || XrStereo3D.DrawsWpfFullscreen);
                        // the latest image drawn into the panel now, by this frame (XrExternalPanel), and its views
                        // the eyes as they are for this frame: the latest image is moved there by its depth
                        // (XrExternalPanel.Reproject.cs) and shown with these views
                        (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) nowLeft, nowRight;
                        lock (s_eyesLock) { nowLeft = Eye(s_views[0]); nowRight = Eye(s_views[1]); }
                        long number = panel ? (_externalDrawLatest ??= AppContext.GetData("WpfWebGpu.Xr.ExternalDrawLatest") as Func<Func<long, float[]?>, long>)
                            ?.Invoke(k => XrStereo3D.MovedViews(k, nowLeft, nowRight)) ?? -1 : -1;
                        // the view draws as the eyes see once the panel can show it (until then, through the rectangle)
                        s_immersive = panel;
                        if (number >= 0)
                        {
                            MeasureLead(state.PredictedDisplayTime, XrStereo3D.DrawnTime(number));
                            NoteLeap(number, nowLeft.Position, nowLeft.Orientation);
                            // its own image, each eye's half filling that eye, transparent where empty
                            projectionViews[0] = ProjectionView(nowLeft, 0, iw / 2, ih);
                            projectionViews[1] = ProjectionView(nowRight, iw / 2, iw / 2, ih);
                            projection = new Xr.CompositionLayerProjection
                            {
                                // the flip is the layer's (XR_FB_composition_layer_image_layout chains to a layer, not a view)
                                Type = Xr.TypeCompositionLayerProjection, Next = _flip, LayerFlags = Xr.LayerBlendTextureSourceAlpha, Space = _local,
                                ViewCount = 2, Views = projectionViews,
                            };
                            layers[n++] = &projection;
                            break;
                        }
                        // until then its rectangle of the window's image, as far as the window has it
                        int fx = Math.Max(0, full.X - p.X), fy = Math.Max(0, full.Y - p.Y);
                        int cw = Math.Min(full.X + full.W, p.X + p.Width) - p.X - fx, ch = Math.Min(full.Y + full.H, p.Y + p.Height) - p.Y - fy;
                        if (cw <= 0 || ch <= 0) break;
                        float fw = cw * full.MetresPerPixel, fh = ch * full.MetresPerPixel;
                        if (p.Stereo)
                        {
                            quads[q] = Quad(p.Swapchain, fx, cw, ch, at, fw, fh, false, Xr.EyeLeft, fy);
                            layers[n++] = &quads[q++];
                            quads[q] = Quad(p.Swapchain, p.Width + fx, cw, ch, at, fw, fh, false, Xr.EyeRight, fy);
                        }
                        else quads[q] = Quad(p.Swapchain, fx, cw, ch, at, fw, fh, false, Xr.EyeBoth, fy);
                        layers[n++] = &quads[q++];
                        break;
                    }
                    Xr.Vector3 centre = PanelCentre(p, i);
                    float wide = p.Width * MetresPerPixel, high = p.Height * MetresPerPixel;
                    if (p.FrameSwapchain != 0 && AndroidWindow.FromHandle(p.Handle) is { HasFrame: true } framed)
                    {
                        // the frame, behind the window in the same place: its outer rectangle's centre
                        framed.GetFrameInsets(out int fl, out int ft, out int fr, out int fb);
                        Xr.Vector3 outer = centre;
                        outer.X += (fr - fl) / 2f * MetresPerPixel;
                        outer.Y -= (fb - ft) / 2f * MetresPerPixel;
                        // its image of the size it was made at, as wide as the window is now (being sized)
                        quads[q] = Quad(p.FrameSwapchain, 0, p.FrameWidth, p.FrameHeight, outer,
                            (p.Width + fl + fr) * MetresPerPixel, (p.Height + ft + fb) * MetresPerPixel, translucent: true, Xr.EyeBoth);
                        layers[n++] = &quads[q++];
                    }
                    if (p.Stereo)
                    {
                        // the left half to the left eye, the right half to the right, in the same place
                        quads[q] = Quad(p.Swapchain, 0, p.SwapchainWidth, p.SwapchainHeight, centre, wide, high, p.Translucent, Xr.EyeLeft);
                        layers[n++] = &quads[q++];
                        quads[q] = Quad(p.Swapchain, p.SwapchainWidth, p.SwapchainWidth, p.SwapchainHeight, centre, wide, high, p.Translucent, Xr.EyeRight);
                    }
                    else
                    {
                        quads[q] = Quad(p.Swapchain, 0, p.SwapchainWidth, p.SwapchainHeight, centre, wide, high, p.Translucent, Xr.EyeBoth);
                    }
                    layers[n++] = &quads[q++];
                }
                for (int h = 0; h < 2 && cursors > 0; h++)
                {
                    if (!_handCursors[h]) continue;
                    Xr.Vector3 at = _handHits[h];
                    at.Z = (s_full != null ? -FullDistance : -Distance) + (shown.Count + 2) * LayerStep;
                    quads[q] = Quad(_cursorSwapchain, 0, CursorPixels, CursorPixels, at, CursorMetres, CursorMetres, translucent: true, Xr.EyeBoth);
                    layers[n++] = &quads[q++];
                }
            }
            var endInfo = new Xr.FrameEndInfo
            {
                Type = Xr.TypeFrameEndInfo, DisplayTime = state.PredictedDisplayTime, EnvironmentBlendMode = Xr.BlendModeOpaque,
                LayerCount = (uint)n, Layers = n > 0 ? layers : null,
            };
            Check(Xr.xrEndFrame(_session, &endInfo), "xrEndFrame");
            _flip = null;
        }

        // ---- where the eyes are (for 3D drawn from them: XrStereo3D) --------------------------

        private static readonly object s_eyesLock = new object();
        private static System.Numerics.Vector3 s_leftEye, s_rightEye;
        private static bool s_eyesKnown;
        private static readonly Xr.View[] s_views = new Xr.View[2];

        // A fullscreen view is drawn for the eyes as located, and shown a frame or two later: the runtime moves the
        // image to where the eyes are then by their turn only, as if all of it were far off, so a model near by is off
        // by the head's move in between. How much later is measured (the display time of the frame that shows a
        // drawing less that of the frame whose eyes it was drawn for) and logged. Not located that much ahead: some
        // 35 ms of the runtime's prediction overshot as the head swayed, and the model moved further.
        private static long s_eyesTime;         // the display time of the frame the eyes were located in
        private static double s_leadNs;         // how much later a drawing is shown (nanoseconds, averaged)
        private const double MaxLeadNs = 100e6;

        internal static long EyesTime { get { lock (s_eyesLock) return s_eyesTime; } }

        private void NoteEyesTime(long frameTime)
        {
            lock (s_eyesLock) s_eyesTime = frameTime;
        }

        private static void MeasureLead(long shownAt, long drawnFor)
        {
            if (drawnFor <= 0) return;
            double lag = Math.Clamp(shownAt - drawnFor, 0, MaxLeadNs);
            s_leadNs = s_leadNs <= 0 ? lag : s_leadNs * 0.9 + lag * 0.1;
            if (Environment.TickCount64 - s_leadLogged > 5000)
            {
                s_leadLogged = Environment.TickCount64;
                Log($"fullscreen shows a drawing {s_leadNs / 1e6:0.0} ms after its eyes' frame");
            }
        }
        private static long s_leadLogged;

        // a picture shown with eyes far from those it was drawn for (a head moves millimetres a frame): which, how far
        private static long s_leapLogged, s_leaps;
        private static void NoteLeap(long number, System.Numerics.Vector3 now, System.Numerics.Quaternion nowTurn)
        {
            if (!XrStereo3D.TryGetDrawnViews(number, out var drawn, out _)) return;
            float metres = System.Numerics.Vector3.Distance(drawn.Position, now);
            float degrees = 2 * MathF.Acos(Math.Clamp(MathF.Abs(System.Numerics.Quaternion.Dot(drawn.Orientation, nowTurn)), 0f, 1f)) * 180 / MathF.PI;
            if (metres < 0.1f && degrees < 15f) return;
            s_leaps++;
            if (Environment.TickCount64 - s_leapLogged < 1000) return;
            s_leapLogged = Environment.TickCount64;
            Log($"fullscreen leap ({s_leaps} so far): picture {number} drawn at {drawn.Position.X:0.000},{drawn.Position.Y:0.000},{drawn.Position.Z:0.000}, the eye now at {now.X:0.000},{now.Y:0.000},{now.Z:0.000} ({metres:0.000} m, {degrees:0} degrees), drawn {s_leadNs / 1e6:0} ms before");
        }

        private void LocateEyes(long time)
        {
            var info = new Xr.ViewLocateInfo { Type = Xr.TypeViewLocateInfo, ViewConfigurationType = Xr.ViewConfigurationPrimaryStereo, DisplayTime = time, Space = _local };
            var viewState = new Xr.ViewState { Type = Xr.TypeViewState };
            Xr.View* views = stackalloc Xr.View[2];
            views[0] = new Xr.View { Type = Xr.TypeView };
            views[1] = new Xr.View { Type = Xr.TypeView };
            uint count;
            bool known = Xr.xrLocateViews(_session, &info, &viewState, 2, &count, views) >= 0 && count == 2
                && (viewState.ViewStateFlags & Xr.LocationPositionValid) != 0;
            lock (s_eyesLock)
            {
                s_eyesKnown = known;
                if (!known) return;
                s_leftEye = new System.Numerics.Vector3(views[0].Pose.Position.X, views[0].Pose.Position.Y, views[0].Pose.Position.Z);
                s_rightEye = new System.Numerics.Vector3(views[1].Pose.Position.X, views[1].Pose.Position.Y, views[1].Pose.Position.Z);
                s_views[0] = views[0];
                s_views[1] = views[1];
            }
        }

        /// <summary>Where the eyes are now (metres, the session's local space, the desktop in front at -z), false
        /// when the headset does not know.</summary>
        internal static bool TryGetEyes(out System.Numerics.Vector3 left, out System.Numerics.Vector3 right)
        {
            lock (s_eyesLock)
            {
                left = s_leftEye;
                right = s_rightEye;
                return s_eyesKnown;
            }
        }

        /// <summary>An eye as the headset has it now: where (metres, the local space), which way (a rotation), and
        /// its field of view (the tangents of its left, right, up and down angles).</summary>
        internal static bool TryGetEyeView(bool left, out System.Numerics.Vector3 position, out System.Numerics.Quaternion orientation, out System.Numerics.Vector4 tangents)
        {
            lock (s_eyesLock)
            {
                Xr.View v = s_views[left ? 0 : 1];
                position = new System.Numerics.Vector3(v.Pose.Position.X, v.Pose.Position.Y, v.Pose.Position.Z);
                orientation = new System.Numerics.Quaternion(v.Pose.Orientation.X, v.Pose.Orientation.Y, v.Pose.Orientation.Z, v.Pose.Orientation.W);
                tangents = new System.Numerics.Vector4(MathF.Tan(v.Fov.AngleLeft), MathF.Tan(v.Fov.AngleRight), MathF.Tan(v.Fov.AngleUp), MathF.Tan(v.Fov.AngleDown));
                return s_eyesKnown;
            }
        }

        /// <summary>Whether the headset shows a 3D view alone, filling the eyes (XrStereo3D draws it so).</summary>
        internal static bool IsFullscreen => s_full != null && s_immersive;
        private static volatile bool s_immersive;

        /// <summary>A desktop point (device pixels) where the windows lie in the local space (metres).</summary>
        internal static System.Numerics.Vector3 DesktopToSpace(double x, double y) => s_full is { } f
            ? new System.Numerics.Vector3((float)((x - (f.X + f.W / 2.0)) * f.MetresPerPixel), (float)(FullCentreHeight - (y - (f.Y + f.H / 2.0)) * f.MetresPerPixel), -FullDistance)
            : new System.Numerics.Vector3((float)((x - DesktopWidth / 2.0) * MetresPerPixel), (float)(CentreHeight - (y - DesktopHeight / 2.0) * MetresPerPixel), -Distance);

        // ---- the 3D view alone, big (B or Y) ------------------------------------------------------
        //
        // B on the right controller or Y on the left: the 3D view under the pointer (XrStereo3D's views)
        // shown alone, as large as about 80 degrees of the view 1.3 m away -- its rectangle of the window's
        // image on a quad of its own, the rest hidden -- and back. The rays meet that quad and are its pixels of
        // the desktop, so the view is pointed at, clicked and orbited as before; the eyes' cameras look
        // through it (DesktopToSpace).

        private sealed class Fullscreen
        {
            public int X, Y, W, H;
            public float MetresPerPixel;
        }

        private static volatile Fullscreen? s_full;
        private const float FullDistance = 1.3f, FullCentreHeight = -0.05f;
        private ulong _fullscreenAction;

        // in fullscreen the model is handled here, not by the app's navigation (its window is not shown, and its
        // Alt-drag and wheel did not reach its camera there): the left stick turns it about its upright, the right
        // one scales it, a grip held moves it with the hand (XrStereo3D.Manipulate)
        private ulong _gripAction;
        private readonly Xr.Vector3[] _handPositions = new Xr.Vector3[2], _gripFrom = new Xr.Vector3[2];
        private readonly bool[] _gripHeld = new bool[2];
        private const float TurnRadiansPerSecond = MathF.PI / 2, ScalePerSecond = 2f;

        private void Manipulate(Xr.Vector2 leftStick, long period)
        {
            float seconds = period / 1e9f;
            Xr.Vector2 rightStick = Vector2State(_scrollAction, _rightHand);
            float turn = MathF.Abs(leftStick.X) > 0.2f ? leftStick.X * TurnRadiansPerSecond * seconds : 0f;
            float scale = MathF.Abs(rightStick.Y) > 0.2f ? MathF.Pow(ScalePerSecond, rightStick.Y * seconds) : 1f;
            var move = new System.Numerics.Vector3();
            ulong[] hands = { _leftHand, _rightHand };
            for (int i = 0; i < 2; i++)
            {
                float grip = FloatState(_gripAction, hands[i]);
                bool held = _gripHeld[i] ? grip > 0.4f : grip > 0.6f;
                if (held && _gripHeld[i])
                    move += new System.Numerics.Vector3(_handPositions[i].X - _gripFrom[i].X, _handPositions[i].Y - _gripFrom[i].Y, _handPositions[i].Z - _gripFrom[i].Z);
                _gripHeld[i] = held;
                _gripFrom[i] = _handPositions[i];
            }
            if (turn != 0f || scale != 1f || move != default) XrStereo3D.Manipulate(turn, scale, move);
        }
        // held until seen released: the B that brought the app here (the panel's) is not a press of this session's
        private bool _fullscreenHeld = true;
        private long _focusedSince;
        private bool _fullscreenClick;      // B went down in this session, so its release is a click

        // the trigger's clicks steadied (Pointer): the press's point, held while the ray stays near it, and the last click's
        private const int ClickSlop = 20, DoubleClickMilliseconds = 500;
        private int _pressX, _pressY, _clickX, _clickY;
        private bool _pressHeld;
        private long _clickedAt;

        /// <summary>B or Y over the app away from a 3D view: the activity goes back to the 2D panel (raised on the
        /// session's thread).</summary>
        public event Action? LeaveRequested;

        // a 3D view to fill the eyes once the windows are here (the panel's B over it): the point, on the desktop
        private (int X, int Y, long Until)? _pendingFullscreen;

        /// <summary>The 3D view at (<paramref name="x"/>, <paramref name="y"/>) on the desktop to fill the eyes as soon
        /// as it is laid out here -- the panel's B over it, which brought the app here.</summary>
        public void FullscreenWhenShown(int x, int y) => _pendingFullscreen = (x, y, Environment.TickCount64 + 8000);

        private void TryPendingFullscreen()
        {
            if (_pendingFullscreen is not { } p || s_full != null) return;
            if (Environment.TickCount64 > p.Until) { _pendingFullscreen = null; Log($"no 3D view came to ({p.X},{p.Y})"); return; }
            if (!XrStereo3D.TryGetViewRect(p.X, p.Y, out System.Windows.Rect r) || r.Width < 8 || r.Height < 8) return;
            _pendingFullscreen = null;
            ToggleFullscreen(p.X, p.Y);
        }

        private void ToggleFullscreen(int px, int py)
        {
            if (s_full != null)
            {
                s_full = null;
                s_immersive = false;
                AppContext.SetData("WpfWebGpu.Xr.WindowsHidden", false);
                XrStereo3D.EndFullscreen();
                (_externalTarget ?? AppContext.GetData("WpfWebGpu.Xr.ExternalTarget") as Func<IntPtr, int, int, bool>)?.Invoke(IntPtr.Zero, 0, 0);
                Log("fullscreen off");
                return;
            }
            if (!XrStereo3D.TryGetViewRect(px, py, out System.Windows.Rect r) || r.Width < 8 || r.Height < 8)
            {
                // the rest of the app: back to the 2D panel, where the app came from (a hybrid app's way out) -- not
                // in the moment the session has come up, when a second press of the B that brought it here lands
                if (Environment.TickCount64 - _focusedSince < 1500) { Log("B as the session comes up: stays"); return; }
                Log($"no 3D view at ({px},{py}): {XrStereo3D.ViewsNote()}{(LeaveRequested != null ? "; back to the panel" : "")}");
                LeaveRequested?.Invoke();
                return;
            }
            int w = (int)Math.Round(r.Width), h = (int)Math.Round(r.Height);
            float maxWide = 2 * FullDistance * MathF.Tan(40 * MathF.PI / 180), maxHigh = 2 * FullDistance * MathF.Tan(32 * MathF.PI / 180);
            AppContext.SetData("WpfWebGpu.Xr.WindowsHidden", true);
            s_full = new Fullscreen { X = (int)Math.Round(r.X), Y = (int)Math.Round(r.Y), W = w, H = h, MetresPerPixel = MathF.Min(maxWide / w, maxHigh / h) };
            Log($"fullscreen: the 3D view at ({s_full.X},{s_full.Y}) {w}x{h}, {w * s_full.MetresPerPixel:0.00} m wide");
            XrStereo3D.BeginFullscreen(r);
        }

        // the fullscreen view's own image (DXEngine's D3DImage, rendered sharper): a swapchain of its size, drawn
        // by the compositor (XrExternalPanel, through AppContext) each frame; until it has drawn, the crop
        private ulong _fullSwapchain;
        private IntPtr _fullWindow;
        private int _fullWidth, _fullHeight;
        private bool _fullDrawn;
        private Func<IntPtr, int, int, bool>? _externalTarget;
        private Func<Func<long, float[]?>, long>? _externalDrawLatest;

        private bool FullPanel(out int width, out int height)
        {
            width = height = 0;
            // the size of the image the view is published at now (the largest of the D3DImages: rendered so for this)
            if (!XrStereo3D.TryGetFullscreenImage(out _, out _)) return false;
            long largest = (AppContext.GetData("WpfWebGpu.Xr.LargestExternal") as Func<long>)?.Invoke() ?? 0;
            int w = (int)(largest >> 32), h = (int)(largest & 0xFFFFFFFF);
            // a still head draws nothing new: the panel keeps the size it has (the largest is only the last second's)
            if ((w <= 0 || h <= 0) && _fullSwapchain != 0) { w = _fullWidth; h = _fullHeight; }
            if (w <= 0 || h <= 0) return false;
            if (_fullSwapchain == 0 || w != _fullWidth || h != _fullHeight)
            {
                if (_fullSwapchain != 0) Xr.xrDestroySwapchain(_fullSwapchain);
                _fullSwapchain = 0;
                _fullDrawn = false;
                var info = new Xr.SwapchainCreateInfo { Type = Xr.TypeSwapchainCreateInfo, Width = (uint)w, Height = (uint)h, SampleCount = 1, FaceCount = 1, ArraySize = 1, MipCount = 1 };
                if (CreateSurfaceSwapchain(info, out ulong swapchain, out IntPtr surface) < 0 || surface == IntPtr.Zero) return false;
                _fullWindow = ANativeWindow_fromSurface(JNIEnv.Handle, surface);
                _fullSwapchain = swapchain;
                _fullWidth = w;
                _fullHeight = h;
                Log($"fullscreen panel {w}x{h}");
            }
            _externalTarget ??= AppContext.GetData("WpfWebGpu.Xr.ExternalTarget") as Func<IntPtr, int, int, bool>;
            if (_externalTarget != null && _externalTarget(_fullWindow, w, h)) _fullDrawn = true;
            width = w;
            height = h;
            return _fullDrawn;
        }

        // ---- passthrough: the room around the windows (XR_FB_passthrough) ---------------------------

        private const string PassthroughExtension = "XR_FB_passthrough";
        private static readonly bool s_passthrough = Environment.GetEnvironmentVariable("WPF_XR_PASSTHROUGH") != "0";
        private bool _passthroughExtension;
        private ulong _passthrough, _passthroughLayer;

        private void CreatePassthrough()
        {
            IntPtr create = Xr.Proc(_instance, "xrCreatePassthroughFB"), createLayer = Xr.Proc(_instance, "xrCreatePassthroughLayerFB");
            if (create == IntPtr.Zero || createLayer == IntPtr.Zero) { Log("no passthrough functions"); return; }
            var info = new Xr.PassthroughCreateInfo { Type = Xr.TypePassthroughCreateInfo, Flags = Xr.PassthroughIsRunningAtCreation };
            ulong passthrough;
            int result = ((delegate* unmanaged<ulong, Xr.PassthroughCreateInfo*, ulong*, int>)create)(_session, &info, &passthrough);
            if (result < 0) { Log($"no passthrough: XrResult {result}"); return; }
            var layerInfo = new Xr.PassthroughLayerCreateInfo
            {
                Type = Xr.TypePassthroughLayerCreateInfo, Passthrough = passthrough, Flags = Xr.PassthroughIsRunningAtCreation,
                Purpose = Xr.PassthroughLayerPurposeReconstruction,
            };
            ulong layer;
            result = ((delegate* unmanaged<ulong, Xr.PassthroughLayerCreateInfo*, ulong*, int>)createLayer)(_session, &layerInfo, &layer);
            if (result < 0) { Log($"no passthrough layer: XrResult {result}"); return; }
            _passthrough = passthrough;
            _passthroughLayer = layer;
            Log("passthrough: the room behind the windows");
        }

        private Xr.CompositionLayerProjectionView ProjectionView((System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) view, int x, int width, int height) => new Xr.CompositionLayerProjectionView
        {
            Type = Xr.TypeCompositionLayerProjectionView,
            Pose = new Xr.Pose
            {
                Orientation = new Xr.Quaternion { X = view.Orientation.X, Y = view.Orientation.Y, Z = view.Orientation.Z, W = view.Orientation.W },
                Position = new Xr.Vector3 { X = view.Position.X, Y = view.Position.Y, Z = view.Position.Z },
            },
            Fov = new Xr.Fov { AngleLeft = view.Fov.X, AngleRight = view.Fov.Y, AngleUp = view.Fov.Z, AngleDown = view.Fov.W },
            SubImage = new Xr.SwapchainSubImage { Swapchain = _fullSwapchain, ImageRect = new Xr.Rect2Di { X = x, Width = width, Height = height } },
        };

        private static (System.Numerics.Vector3, System.Numerics.Quaternion, System.Numerics.Vector4) Eye(Xr.View v) =>
            (new System.Numerics.Vector3(v.Pose.Position.X, v.Pose.Position.Y, v.Pose.Position.Z),
             new System.Numerics.Quaternion(v.Pose.Orientation.X, v.Pose.Orientation.Y, v.Pose.Orientation.Z, v.Pose.Orientation.W),
             new System.Numerics.Vector4(v.Fov.AngleLeft, v.Fov.AngleRight, v.Fov.AngleUp, v.Fov.AngleDown));

        /// <summary>This host as the 3D views ask the headset (XrHeadset): the eyes, their frame's time, whether a view
        /// fills them, where the desktop lies.</summary>
        private static void OfferHeadset()
        {
            AppContext.SetData("WpfWebGpu.Xr.EyeView", (Func<int, float[]?>)(eye =>
            {
                lock (s_eyesLock)
                {
                    if (!s_eyesKnown) return null;
                    Xr.View v = s_views[eye == 0 ? 0 : 1];
                    return new[] { v.Pose.Position.X, v.Pose.Position.Y, v.Pose.Position.Z,
                        v.Pose.Orientation.X, v.Pose.Orientation.Y, v.Pose.Orientation.Z, v.Pose.Orientation.W,
                        v.Fov.AngleLeft, v.Fov.AngleRight, v.Fov.AngleUp, v.Fov.AngleDown };
                }
            }));
            AppContext.SetData("WpfWebGpu.Xr.EyesTime", (Func<long>)(() => EyesTime));
            AppContext.SetData("WpfWebGpu.Xr.IsFullscreen", (Func<bool>)(() => IsFullscreen));
            AppContext.SetData("WpfWebGpu.Xr.DesktopToSpace", (Func<double, double, float[]>)((x, y) =>
            {
                var p = DesktopToSpace(x, y);
                return new[] { p.X, p.Y, p.Z };
            }));
        }

        // the vertical flip chained to this frame's quads (on the stack of Frame), or null
        private Xr.CompositionLayerImageLayout* _flip;

        private Xr.CompositionLayerQuad Quad(ulong swapchain, int x, int width, int height, Xr.Vector3 centre, float metresWide, float metresHigh, bool translucent, int eye, int y = 0) => new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad,
            Next = _flip,
            LayerFlags = translucent ? Xr.LayerBlendTextureSourceAlpha : 0,
            Space = _local,
            EyeVisibility = eye,
            SubImage = new Xr.SwapchainSubImage { Swapchain = swapchain, ImageRect = new Xr.Rect2Di { X = x, Y = y, Width = width, Height = height } },
            Pose = new Xr.Pose { Orientation = new Xr.Quaternion { W = 1 }, Position = centre },
            Size = new Xr.Extent2Df { Width = metresWide, Height = metresHigh },
        };

        /// <summary>Where on the desktop plane a window's centre is, the <paramref name="layer"/>th from the back.</summary>
        private static Xr.Vector3 PanelCentre(Panel p, int layer) => new Xr.Vector3
        {
            X = (p.X + p.Width / 2f - DesktopWidth / 2f) * MetresPerPixel,
            Y = CentreHeight - (p.Y + p.Height / 2f - DesktopHeight / 2f) * MetresPerPixel,
            Z = -Distance + layer * LayerStep,
        };

        // ---- the pointer -----------------------------------------------------------------------

        private void CreateActions()
        {
            var setInfo = new Xr.ActionSetCreateInfo { Type = Xr.TypeActionSetCreateInfo };
            Xr.Copy("wpf", setInfo.ActionSetName, 64);
            Xr.Copy("Windows", setInfo.LocalizedActionSetName, 128);
            ulong set;
            Check(Xr.xrCreateActionSet(_instance, &setInfo, &set), "xrCreateActionSet");
            _actionSet = set;

            _leftHand = Xr.Path(_instance, "/user/hand/left");
            _rightHand = Xr.Path(_instance, "/user/hand/right");
            _aimAction = CreateAction("aim", "Point", Xr.ActionPose);
            _selectAction = CreateAction("select", "Click", Xr.ActionFloat);
            _scrollAction = CreateAction("scroll", "Scroll", Xr.ActionVector2f);
            _secondaryAction = CreateAction("secondary", "Right click", Xr.ActionBoolean);
            _fullscreenAction = CreateAction("fullscreen", "Fullscreen 3D", Xr.ActionBoolean);
            _gripAction = CreateAction("grip", "Move 3D", Xr.ActionFloat);

            Suggest("/interaction_profiles/oculus/touch_controller", new[]
            {
                (_aimAction, "/user/hand/left/input/aim/pose"), (_aimAction, "/user/hand/right/input/aim/pose"),
                (_selectAction, "/user/hand/left/input/trigger/value"), (_selectAction, "/user/hand/right/input/trigger/value"),
                (_scrollAction, "/user/hand/left/input/thumbstick"), (_scrollAction, "/user/hand/right/input/thumbstick"),
                (_secondaryAction, "/user/hand/right/input/a/click"), (_secondaryAction, "/user/hand/left/input/x/click"),
                (_fullscreenAction, "/user/hand/right/input/b/click"), (_fullscreenAction, "/user/hand/left/input/y/click"),
                (_gripAction, "/user/hand/left/input/squeeze/value"), (_gripAction, "/user/hand/right/input/squeeze/value"),
            });
            // hands, and any runtime without the Touch profile: point and pinch
            Suggest("/interaction_profiles/khr/simple_controller", new[]
            {
                (_aimAction, "/user/hand/left/input/aim/pose"), (_aimAction, "/user/hand/right/input/aim/pose"),
                (_selectAction, "/user/hand/left/input/select/click"), (_selectAction, "/user/hand/right/input/select/click"),
            });

            ulong* sets = stackalloc ulong[] { _actionSet };
            var attach = new Xr.SessionActionSetsAttachInfo { Type = Xr.TypeSessionActionSetsAttachInfo, CountActionSets = 1, ActionSets = sets };
            Check(Xr.xrAttachSessionActionSets(_session, &attach), "xrAttachSessionActionSets");

            ulong[] hands = { _leftHand, _rightHand };
            for (int i = 0; i < 2; i++)
            {
                var spaceInfo = new Xr.ActionSpaceCreateInfo { Type = Xr.TypeActionSpaceCreateInfo, Action = _aimAction, SubactionPath = hands[i], PoseInActionSpace = Xr.Identity };
                ulong space;
                Check(Xr.xrCreateActionSpace(_session, &spaceInfo, &space), "xrCreateActionSpace");
                _aimSpaces[i] = space;
            }
        }

        private ulong CreateAction(string name, string localized, int type)
        {
            ulong* subactions = stackalloc ulong[] { _leftHand, _rightHand };
            var info = new Xr.ActionCreateInfo { Type = Xr.TypeActionCreateInfo, ActionType = type, CountSubactionPaths = 2, SubactionPaths = subactions };
            Xr.Copy(name, info.ActionName, 64);
            Xr.Copy(localized, info.LocalizedActionName, 128);
            ulong action;
            Check(Xr.xrCreateAction(_actionSet, &info, &action), "xrCreateAction " + name);
            return action;
        }

        private void Suggest(string profile, (ulong Action, string Path)[] bindings)
        {
            var list = new Xr.ActionSuggestedBinding[bindings.Length];
            for (int i = 0; i < bindings.Length; i++) list[i] = new Xr.ActionSuggestedBinding { Action = bindings[i].Action, Binding = Xr.Path(_instance, bindings[i].Path) };
            fixed (Xr.ActionSuggestedBinding* b = list)
            {
                var suggested = new Xr.InteractionProfileSuggestedBinding
                {
                    Type = Xr.TypeInteractionProfileSuggestedBinding, InteractionProfile = Xr.Path(_instance, profile),
                    CountSuggestedBindings = (uint)list.Length, SuggestedBindings = b,
                };
                int result = Xr.xrSuggestInteractionProfileBindings(_instance, &suggested);
                if (result < 0) Log($"bindings for {profile} refused: XrResult {result}");
            }
        }

        private float FloatState(ulong action, ulong hand)
        {
            var get = new Xr.ActionStateGetInfo { Type = Xr.TypeActionStateGetInfo, Action = action, SubactionPath = hand };
            var state = new Xr.ActionStateFloat { Type = Xr.TypeActionStateFloat };
            return Xr.xrGetActionStateFloat(_session, &get, &state) >= 0 && state.IsActive != 0 ? state.CurrentState : 0f;
        }

        private bool BoolState(ulong action, ulong hand)
        {
            var get = new Xr.ActionStateGetInfo { Type = Xr.TypeActionStateGetInfo, Action = action, SubactionPath = hand };
            var state = new Xr.ActionStateBoolean { Type = Xr.TypeActionStateBoolean };
            return Xr.xrGetActionStateBoolean(_session, &get, &state) >= 0 && state.IsActive != 0 && state.CurrentState != 0;
        }

        private Xr.Vector2 Vector2State(ulong action, ulong hand)
        {
            var get = new Xr.ActionStateGetInfo { Type = Xr.TypeActionStateGetInfo, Action = action, SubactionPath = hand };
            var state = new Xr.ActionStateVector2f { Type = Xr.TypeActionStateVector2f };
            return Xr.xrGetActionStateVector2f(_session, &get, &state) >= 0 && state.IsActive != 0 ? state.CurrentState : default;
        }

        /// <summary>The controller as the mouse: where its aim ray meets the desktop, and its buttons.</summary>
        private void Pointer(long time, long period)
        {
            var active = new Xr.ActiveActionSet { ActionSet = _actionSet };
            var sync = new Xr.ActionsSyncInfo { Type = Xr.TypeActionsSyncInfo, CountActiveActionSets = 1, ActiveActionSets = &active };
            if (Xr.xrSyncActions(_session, &sync) < 0) return;

            ulong[] hands = { _leftHand, _rightHand };
            float[] select = { FloatState(_selectAction, _leftHand), FloatState(_selectAction, _rightHand) };
            // the hand that presses is the pointer from then on
            for (int i = 0; i < 2; i++) if (select[i] > 0.6f && !_pressed) _activeHand = i;
            ulong hand = hands[_activeHand];

            for (int i = 0; i < 2; i++) _handAims[i] = Aim(i, time);

            // B or Y on its release, as a button clicks: the system makes the release a Back, which then lands on this
            // activity (which ignores it), not on the panel the press would already have gone back to
            bool fullscreen = BoolState(_fullscreenAction, _leftHand) || BoolState(_fullscreenAction, _rightHand);
            if (fullscreen && !_fullscreenHeld) _fullscreenClick = !_pressed && !_orbiting;
            if (!fullscreen && _fullscreenHeld && _fullscreenClick)
            {
                _fullscreenClick = false;
                int h = _handAims[_activeHand] ? _activeHand : 1 - _activeHand;
                ToggleFullscreen(_handX[h], _handY[h]);
                for (int i = 0; i < 2; i++) _handAims[i] = Aim(i, time);     // the rays meet the other plane now
            }
            _fullscreenHeld = fullscreen;

            // the left thumbstick orbits where the left hand points (the right one may hold the mouse meanwhile)
            Xr.Vector2 leftStick = Vector2State(_scrollAction, _leftHand);
            int orbitHand = _handAims[0] ? 0 : _activeHand;
            if (s_traceSticks && (MathF.Abs(leftStick.X) > 0.25f || MathF.Abs(leftStick.Y) > 0.25f) && Environment.TickCount64 - _stickLogged > 1000)
            {
                _stickLogged = Environment.TickCount64;
                Log($"sticks: left ({leftStick.X:0.00},{leftStick.Y:0.00}), pressed {_pressed}, orbiting {_orbiting}, under 0x{Under(_handX[orbitHand], _handY[orbitHand]):x}");
            }
            if (s_full != null) Manipulate(leftStick, period);
            else if (_handAims[orbitHand] || _orbiting) Orbit(leftStick, period, _handX[orbitHand], _handY[orbitHand]);

            if (!_handAims[_activeHand]) return;
            int px = _handX[_activeHand], py = _handY[_activeHand];

            float trigger = select[_activeHand];
            bool down = _pressed ? trigger > 0.4f : trigger > 0.6f;   // a little hysteresis, as a button's travel has
            bool secondary = BoolState(_secondaryAction, hand);

            // A ray is never still, and a mouse's double-click must land within SM_CXDOUBLECLK (4 pixels) of the
            // first: as Windows widens it for a pen or a finger, a press here holds the point it went down at
            // until the ray leaves it by ClickSlop (a click is no drag of a few pixels), and a second press that
            // soon and that near is at the first's point (a double-click, wherever the hand shook to).
            if (_pressed && _pressHeld)
            {
                if (Math.Abs(px - _pressX) > ClickSlop || Math.Abs(py - _pressY) > ClickSlop) _pressHeld = false;
                else { px = _pressX; py = _pressY; }
            }
            else if (!_pressed && down)
            {
                if (Environment.TickCount64 - _clickedAt <= DoubleClickMilliseconds
                    && Math.Abs(px - _clickX) <= ClickSlop && Math.Abs(py - _clickY) <= ClickSlop)
                {
                    px = _clickX;
                    py = _clickY;
                }
                _pressX = px;
                _pressY = py;
                _pressHeld = true;
            }

            if (_pressed)
            {
                // the window pressed has the pointer until the release, as a mouse's capture does
                if (!down)
                {
                    AndroidWindow.NotifyPointer(_pressTarget, 2, px, py);
                    _pressed = false;
                    _clickedAt = Environment.TickCount64;
                    _clickX = _pressX;
                    _clickY = _pressY;
                }
                else if (px != _lastX || py != _lastY)
                {
                    AndroidWindow.NotifyPointer(_pressTarget, 0, px, py);
                }
            }
            else
            {
                Panel? under = At(px, py);
                if (under != null)
                {
                    // the window under the pointer, a child (an ElementHost's WPF tree) over its parent's panel
                    IntPtr target = AndroidWindow.PointerTarget(px, py, out int left, out int top);
                    if (target == IntPtr.Zero) { target = under.Handle; left = under.X; top = under.Y; }
                    int lx = px - left, ly = py - top;
                    // (not while the left stick orbits: its own pointer is the mouse until it is let go)
                    if (!_orbiting && (px != _lastX || py != _lastY || target != _hoverTarget)) AndroidWindow.NotifyPointer(target, 0, px, py);
                    _hoverTarget = target;
                    if (down)
                    {
                        _pressed = true;
                        _pressTarget = target;
                        AndroidWindow.NotifyPointer(target, 1, px, py);
                    }
                    else if (secondary != _secondaryPressed)
                    {
                        AndroidWindow.NotifyPointer(target, secondary ? AndroidWindow.RightPress : AndroidWindow.RightRelease, px, py);
                    }

                    // the right thumbstick is the wheel: a notch's worth of travel for every so much pushed and held
                    Xr.Vector2 stick = Vector2State(_scrollAction, _rightHand);
                    if (s_full == null && MathF.Abs(stick.Y) > 0.2f)
                    {
                        float seconds = period / 1e9f;
                        _scrollCarry += stick.Y * 600f * seconds;      // logical pixels a second, at full tilt
                        if (MathF.Abs(_scrollCarry) >= 16f)
                        {
                            AndroidWindow.NotifyScroll(target, _scrollCarry, lx, ly);
                            _scrollCarry = 0f;
                        }
                    }
                    else _scrollCarry = 0f;
                }
            }
            _secondaryPressed = secondary;
            _lastX = px;
            _lastY = py;
        }

        /// <summary>The window the point is over: the topmost one that holds it, a popup's place included.</summary>
        private Panel? At(int x, int y)
        {
            lock (_lock)
            {
                for (int i = _panels.Count - 1; i >= 0; i--)
                {
                    Panel p = _panels[i];
                    int l = 0, t = 0, r = 0, b = 0;
                    if (p.FrameSwapchain != 0 && AndroidWindow.FromHandle(p.Handle) is { HasFrame: true } w) w.GetFrameInsets(out l, out t, out r, out b);
                    if (p.Visible && x >= p.X - l && y >= p.Y - t && x < p.X + p.Width + r && y < p.Y + p.Height + b) return p;
                }
            }
            return null;
        }

        [DllImport("android")] private static extern IntPtr ANativeWindow_fromSurface(IntPtr env, IntPtr surface);
        [DllImport("android")] private static extern void ANativeWindow_release(IntPtr window);
    }
}
