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
// window it moves the mouse, the trigger is the left button, A the right, the thumbstick the wheel.
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
        private bool _cursorVisible;
        private Xr.Vector3 _cursorAt;

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
            public IntPtr NativeWindow;
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
        }

        public void Stop()
        {
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
            // a SurfaceView's layout reports a new size with surfaceChanged; the swapchain's surface takes
            // whatever buffers the renderer queues, so it is only told
            if (resized && p.NativeWindow != IntPtr.Zero)
                AndroidWindow.NotifySurfaceChanged(p.Handle, p.NativeWindow, p.Width, p.Height);
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
            panel.NativeWindow = window;
            Log($"window 0x{panel.Handle:x}: {panel.Width}x{panel.Height} surface");
            AndroidWindow.NotifySurfaceChanged(panel.Handle, window, panel.Width, panel.Height);
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
            var waitInfo = new Xr.FrameWaitInfo { Type = Xr.TypeFrameWaitInfo };
            var state = new Xr.FrameState { Type = Xr.TypeFrameState };
            Check(Xr.xrWaitFrame(_session, &waitInfo, &state), "xrWaitFrame");
            var beginInfo = new Xr.FrameBeginInfo { Type = Xr.TypeFrameBeginInfo };
            Check(Xr.xrBeginFrame(_session, &beginInfo), "xrBeginFrame");

            if (_sessionState == Xr.StateFocused) Pointer(state.PredictedDisplayTime, state.PredictedDisplayPeriod);

            List<Panel> shown;
            lock (_lock) shown = _panels.FindAll(p => p.Visible && p.HasSurface && p.Swapchain != 0);
            int quadsForPanels = 0;
            foreach (Panel p in shown) quadsForPanels += p.Stereo ? 2 : 1;
            int count = state.ShouldRender != 0 ? quadsForPanels + (_cursorVisible && _cursorSwapchain != 0 ? 1 : 0) : 0;
            Xr.CompositionLayerQuad* quads = stackalloc Xr.CompositionLayerQuad[Math.Max(1, count)];
            var flip = new Xr.CompositionLayerImageLayout { Type = Xr.TypeCompositionLayerImageLayout, Flags = Xr.ImageLayoutVerticalFlip };
            _flip = _flipLayers ? &flip : null;
            void** layers = stackalloc void*[Math.Max(1, count)];
            int n = 0;
            if (count > 0)
            {
                for (int i = 0; i < shown.Count; i++)
                {
                    Panel p = shown[i];
                    Xr.Vector3 centre = PanelCentre(p, i);
                    float wide = p.Width * MetresPerPixel, high = p.Height * MetresPerPixel;
                    if (p.Stereo)
                    {
                        // the left half to the left eye, the right half to the right, in the same place
                        quads[n] = Quad(p.Swapchain, 0, p.Width, p.Height, centre, wide, high, p.Translucent, Xr.EyeLeft);
                        layers[n] = &quads[n];
                        n++;
                        quads[n] = Quad(p.Swapchain, p.Width, p.Width, p.Height, centre, wide, high, p.Translucent, Xr.EyeRight);
                    }
                    else
                    {
                        quads[n] = Quad(p.Swapchain, 0, p.Width, p.Height, centre, wide, high, p.Translucent, Xr.EyeBoth);
                    }
                    layers[n] = &quads[n];
                    n++;
                }
                if (_cursorVisible && _cursorSwapchain != 0)
                {
                    Xr.Vector3 at = _cursorAt;
                    at.Z = -Distance + (shown.Count + 2) * LayerStep;
                    quads[n] = Quad(_cursorSwapchain, 0, CursorPixels, CursorPixels, at, CursorMetres, CursorMetres, translucent: true, Xr.EyeBoth);
                    layers[n] = &quads[n];
                    n++;
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

        // the vertical flip chained to this frame's quads (on the stack of Frame), or null
        private Xr.CompositionLayerImageLayout* _flip;

        private Xr.CompositionLayerQuad Quad(ulong swapchain, int x, int width, int height, Xr.Vector3 centre, float metresWide, float metresHigh, bool translucent, int eye) => new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad,
            Next = _flip,
            LayerFlags = translucent ? Xr.LayerBlendTextureSourceAlpha : 0,
            Space = _local,
            EyeVisibility = eye,
            SubImage = new Xr.SwapchainSubImage { Swapchain = swapchain, ImageRect = new Xr.Rect2Di { X = x, Width = width, Height = height } },
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

            Suggest("/interaction_profiles/oculus/touch_controller", new[]
            {
                (_aimAction, "/user/hand/left/input/aim/pose"), (_aimAction, "/user/hand/right/input/aim/pose"),
                (_selectAction, "/user/hand/left/input/trigger/value"), (_selectAction, "/user/hand/right/input/trigger/value"),
                (_scrollAction, "/user/hand/left/input/thumbstick"), (_scrollAction, "/user/hand/right/input/thumbstick"),
                (_secondaryAction, "/user/hand/right/input/a/click"), (_secondaryAction, "/user/hand/left/input/x/click"),
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

            var location = new Xr.SpaceLocation { Type = Xr.TypeSpaceLocation };
            if (Xr.xrLocateSpace(_aimSpaces[_activeHand], _local, time, &location) < 0
                || (location.LocationFlags & (Xr.LocationOrientationValid | Xr.LocationPositionValid)) != (Xr.LocationOrientationValid | Xr.LocationPositionValid))
            {
                _cursorVisible = false;
                return;
            }

            Xr.Vector3 origin = location.Pose.Position;
            Xr.Vector3 dir = Xr.Rotate(location.Pose.Orientation, new Xr.Vector3 { Z = -1 });
            if (dir.Z > -1e-4f) { _cursorVisible = false; return; }    // pointing away from the desktop
            float t = (-Distance - origin.Z) / dir.Z;
            var hit = new Xr.Vector3 { X = origin.X + dir.X * t, Y = origin.Y + dir.Y * t, Z = -Distance };
            int px = (int)MathF.Round(hit.X / MetresPerPixel + DesktopWidth / 2f);
            int py = (int)MathF.Round((CentreHeight - hit.Y) / MetresPerPixel + DesktopHeight / 2f);
            _cursorAt = hit;
            _cursorVisible = px >= -50 && py >= -50 && px < DesktopWidth + 50 && py < DesktopHeight + 50;

            float trigger = select[_activeHand];
            bool down = _pressed ? trigger > 0.4f : trigger > 0.6f;   // a little hysteresis, as a button's travel has
            bool secondary = BoolState(_secondaryAction, hand);

            if (_pressed)
            {
                // the window pressed has the pointer until the release, as a mouse's capture does
                if (!down)
                {
                    AndroidWindow.NotifyPointer(_pressTarget, 2, px, py);
                    _pressed = false;
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
                    if (px != _lastX || py != _lastY || target != _hoverTarget) AndroidWindow.NotifyPointer(target, 0, px, py);
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

                    // the thumbstick is the wheel: a notch's worth of travel for every so much pushed and held
                    Xr.Vector2 stick = Vector2State(_scrollAction, hand);
                    if (MathF.Abs(stick.Y) > 0.2f)
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
                    if (p.Visible && x >= p.X && y >= p.Y && x < p.X + p.Width && y < p.Y + p.Height) return p;
                }
            }
            return null;
        }

        [DllImport("android")] private static extern IntPtr ANativeWindow_fromSurface(IntPtr env, IntPtr surface);
        [DllImport("android")] private static extern void ANativeWindow_release(IntPtr window);
    }
}
