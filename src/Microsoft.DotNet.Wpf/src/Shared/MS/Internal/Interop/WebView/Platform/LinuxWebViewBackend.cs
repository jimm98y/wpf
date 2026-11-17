// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// LinuxWebViewBackend -- the Linux IWebViewBackend, backed by WPE WebKit on a wl_subsurface.
//
// WPE, not WebKitGTK, and the distinction is the whole reason this head works at all. This fork's
// Linux head is Wayland-native and runs its own loop: WebKitGTK would bring GTK and a GTK window of its
// own, which Wayland cannot place inside another client's; WPE is the WebKit port built for embedding --
// no toolkit, no window of its own, frames handed to the embedder as buffers.
//
// The engine is WPE WebKit's 2.0 API series with WPEBackend-fdo in shared-memory mode: each frame the web
// process renders arrives as a wl_shm buffer of WPE's nested compositor (export_shm_buffer), whose pixels
// are copied into a buffer of ours on the overlay -- a wl_subsurface of the window's surface, which the
// Wayland layer owns (WaylandWebViewSupport) -- after which the buffer is released and WebKit told the
// frame is done, so it draws the next. Shared memory works on every machine (no GPU needed in the web
// process's export path); a dmabuf path is an optimisation for later.
//
// WebKit runs on GLib's main loop, which nothing else here iterates: the Wayland layer runs a hook of ours
// on each pass of its loop (the UI thread), and that iterates GLib's default context.
//
// Input reaches the engine from the overlay -- pointer events on the subsurface and, after a press on it,
// the keyboard -- as libwpe events (wpe_view_backend_dispatch_*).
//
// PREREQUISITE: WPE WebKit 2.0 (libwpewebkit-2.0-1) and WPEBackend-fdo (libwpebackend-fdo-1.0-1). Ubuntu
// does not package them: the Linux installer adds Debian's archive as an apt source pinned to exactly
// WPE's packages, and the system's apt installs and updates them. A machine without them gets a clear
// PlatformNotSupportedException naming the packages.
//
// GObject, in three rules, because everything below follows them:
//   * Signals are connected with g_signal_connect_data and a static [UnmanagedCallersOnly] callback whose
//     first argument is the emitting object and whose last is the user data -- here a GCHandle to this
//     instance.
//   * Asynchronous calls take a GAsyncReadyCallback and are finished from inside it.
//   * Strings returned by WebKit are owned by WebKit unless the API says otherwise; the ones that are ours
//     (g_strdup'd results, GError messages) are freed here and marked as such.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace MS.Internal.Interop.WebView
{
    [SupportedOSPlatform("linux")]
    internal sealed unsafe class LinuxWebViewBackend : IWebViewBackend
    {
        private const string Wpe = "libWPEWebKit-2.0.so.1";
        private const string LibWpe = "libwpe-1.0.so.1";
        private const string Fdo = "libWPEBackend-fdo-1.0.so.1";
        private const string WaylandServer = "libwayland-server.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        private const string GLib = "libglib-2.0.so.0";

        /// <summary>The name the shim posts through: window.webkit.messageHandlers.wpf, as on the Apple heads.</summary>
        private const string MessageHandlerName = "wpf";

        /// <summary>window.chrome.webview, built on the message handler: the same vocabulary every head speaks.</summary>
        private const string ChromeWebViewShim = @"
(function () {
    if (window.chrome && window.chrome.webview) { return; }
    var listeners = [];
    window.chrome = window.chrome || {};
    window.chrome.webview = {
        postMessage: function (m) {
            window.webkit.messageHandlers." + MessageHandlerName + @".postMessage(
                typeof m === 'string' ? m : JSON.stringify(m));
        },
        addEventListener: function (t, f) { if (t === 'message') { listeners.push(f); } },
        removeEventListener: function (t, f) {
            if (t !== 'message') { return; }
            var i = listeners.indexOf(f); if (i >= 0) { listeners.splice(i, 1); }
        },
        __deliver: function (data) {
            var e = { data: data };
            for (var i = 0; i < listeners.length; i++) { listeners[i](e); }
        }
    };
})();";

        // wpe_view_backend_exportable_fdo_client: export_buffer_resource, export_dmabuf_resource, export_shm_buffer,
        // and two reserved slots
        [StructLayout(LayoutKind.Sequential)]
        private struct ExportableClient { public IntPtr BufferResource, DmabufResource, ShmBuffer, Reserved0, Reserved1; }

        // struct wpe_input_pointer_event
        [StructLayout(LayoutKind.Sequential)]
        private struct PointerEvent { public uint Type, Time; public int X, Y; public uint Button, State, Modifiers; }

        // struct wpe_input_axis_event
        [StructLayout(LayoutKind.Sequential)]
        private struct AxisEvent { public uint Type, Time; public int X, Y; public uint Axis; public int Value; public uint Modifiers; }

        // struct wpe_input_keyboard_event
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardEvent { public uint Time, KeyCode, HardwareKeyCode; public byte Pressed; public uint Modifiers; }

        private static bool s_initialized;
        private static ExportableClient* s_client;

        private IntPtr _view;              // WebKitWebView*
        private IntPtr _exportable;        // wpe_view_backend_exportable_fdo*
        private IntPtr _wpeBackend;        // wpe_view_backend*
        private int _overlay;              // the Wayland layer's id for our subsurface
        private GCHandle _self;
        private bool _visible = true;
        private double _zoom = 1.0;
        private double _scale = 1.0;
        private int _width = 1, _height = 1;
        private uint _buttons;             // wpe_input_pointer_modifier_button1.. held
        private string _lastRequestedUri;
        private ulong _navigationId;
        private WebViewSettings _settings = new WebViewSettings();
        private TaskCompletionSource<object> _attach;
        private readonly List<ulong> _signals = new List<ulong>();
        private readonly List<(ulong Handler, IntPtr Instance)> _managerSignals = new();
        private Action _pump;
        private byte[] _lastFrame;
        private int _lastFrameWidth, _lastFrameHeight;

        public WebViewPresentation Presentation => WebViewPresentation.Overlay;

        public bool IsAttached => _view != IntPtr.Zero;

        public Task AttachAsync(IntPtr ownerWindow)
        {
            if (_attach is not null)
            {
                return _attach.Task;
            }

            _attach = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!LinuxWebViewRegistration.IsAvailable)
            {
                _attach.TrySetException(new PlatformNotSupportedException(
                    "The Wayland windowing layer has not registered its web-view support."));
                return _attach.Task;
            }

            // The parent is the window's wl_surface*, which stands behind its handle (WaylandWindow). Asked through
            // AppContext: this file is built into assemblies that do not see the Wayland layer.
            IntPtr parent = AppContext.GetData("WpfWebGpu.Wayland.SurfaceOf") is Func<IntPtr, IntPtr> surfaceOf ? surfaceOf(ownerWindow) : ownerWindow;
            _overlay = LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.CreateSubsurface, parent, 0, 0, 1, 1) is int id ? id : 0;
            if (_overlay == 0)
            {
                _attach.TrySetException(new InvalidOperationException("The Wayland layer could not create a subsurface for the web view."));
                return _attach.Task;
            }

            try
            {
                _self = GCHandle.Alloc(this);
                _view = CreateWebView();
            }
            catch (DllNotFoundException ex)
            {
                LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.DestroySubsurface, _overlay);
                _overlay = 0;
                // said plainly: a bare DllNotFoundException names a library the application never referenced
                _attach.TrySetException(new PlatformNotSupportedException(
                    "WPE WebKit is not installed, so web content cannot be hosted on this machine. " +
                    "It needs the packages libwpewebkit-2.0-1 and libwpebackend-fdo-1.0-1.", ex));
                return _attach.Task;
            }

            LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.SetInput, _overlay,
                new Action<int, int, int, int, int, int>(OnMouse), new Action<uint, uint, bool, uint, string>(OnKey));
            _pump = PumpGLib;
            LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.AddPump, _pump);

            ConnectSignals();
            ApplySettings(_settings);
            SetVisible(_visible);

            // usable as soon as it is constructed; no asynchronous start-up, unlike WebView2's environment
            _attach.TrySetResult(null);
            return _attach.Task;
        }

        /// <summary>
        /// The view: libwpe loads WPEBackend-fdo, which runs in shared-memory mode; the exportable view backend
        /// hands each rendered frame to <see cref="OnShmBuffer"/>.
        /// </summary>
        private IntPtr CreateWebView()
        {
            if (!s_initialized)
            {
                using (var fdo = new Utf8(Fdo))
                    wpe_loader_init(fdo.Pointer);
                wpe_fdo_initialize_shm();
                s_client = (ExportableClient*)NativeMemory.AllocZeroed((nuint)sizeof(ExportableClient));
                s_client->ShmBuffer = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnShmBuffer;
                s_initialized = true;
            }

            _exportable = wpe_view_backend_exportable_fdo_create(s_client, GCHandle.ToIntPtr(_self), (uint)_width, (uint)_height);
            _wpeBackend = wpe_view_backend_exportable_fdo_get_view_backend(_exportable);
            IntPtr viewBackend = webkit_web_view_backend_new(_wpeBackend, IntPtr.Zero, IntPtr.Zero);
            IntPtr view = webkit_web_view_new(viewBackend);
            // visible, in a window (focused once a press gives it the keyboard)
            wpe_view_backend_add_activity_state(_wpeBackend, 1 /* visible */ | 4 /* in_window */);

            // the page side of window.chrome.webview, and its channel back
            IntPtr manager = webkit_web_view_get_user_content_manager(view);
            if (manager != IntPtr.Zero)
            {
                using (var name = new Utf8(MessageHandlerName))
                    webkit_user_content_manager_register_script_message_handler(manager, name.Pointer, IntPtr.Zero);
                using (var signal = new Utf8("script-message-received::" + MessageHandlerName))
                {
                    ulong handler = g_signal_connect_data(manager, signal.Pointer,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnScriptMessage,
                        GCHandle.ToIntPtr(_self), IntPtr.Zero, 0);
                    if (handler != 0) _managerSignals.Add((handler, manager));
                }
                AddUserScript(manager, ChromeWebViewShim);
            }
            return view;
        }

        /// <summary>A frame from the web process: its pixels onto the overlay, the buffer back, and WebKit told the
        /// frame is done.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnShmBuffer(IntPtr data, IntPtr exported)
        {
            LinuxWebViewBackend b = FromUserData(data);
            try
            {
                IntPtr shm = wpe_fdo_shm_exported_buffer_get_shm_buffer(exported);
                if (b is not null && shm != IntPtr.Zero)
                {
                    wl_shm_buffer_begin_access(shm);
                    try
                    {
                        IntPtr pixels = wl_shm_buffer_get_data(shm);
                        int width = wl_shm_buffer_get_width(shm), height = wl_shm_buffer_get_height(shm), stride = wl_shm_buffer_get_stride(shm);
                        if (pixels != IntPtr.Zero && width > 0 && height > 0)
                        {
                            LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.AttachPixels, b._overlay, pixels, width, height, stride);
                            b.KeepFrame(pixels, width, height, stride);
                        }
                    }
                    finally { wl_shm_buffer_end_access(shm); }
                }
            }
            catch (Exception) { }
            finally
            {
                if (b is not null && b._exportable != IntPtr.Zero)
                {
                    wpe_view_backend_exportable_fdo_dispatch_release_shm_exported_buffer(b._exportable, exported);
                    wpe_view_backend_exportable_fdo_dispatch_frame_complete(b._exportable);
                }
            }
        }

        /// <summary>The last frame, kept for CapturePreviewAsync.</summary>
        private void KeepFrame(IntPtr pixels, int width, int height, int stride)
        {
            int row = width * 4;
            if (_lastFrame == null || _lastFrame.Length != row * height) _lastFrame = new byte[row * height];
            fixed (byte* dst = _lastFrame)
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy((byte*)pixels + (long)y * stride, dst + (long)y * row, row, row);
            _lastFrameWidth = width;
            _lastFrameHeight = height;
        }

        /// <summary>GLib's default context, on the UI thread: WebKit's IPC, timers and callbacks.</summary>
        private static void PumpGLib()
        {
            for (int i = 0; i < 64 && g_main_context_iteration(IntPtr.Zero, 0) != 0; i++) { }
        }

        public void Detach()
        {
            if (_pump != null)
            {
                try { LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.RemovePump, _pump); } catch (PlatformNotSupportedException) { }
                _pump = null;
            }

            foreach ((ulong handler, IntPtr instance) in _managerSignals) g_signal_handler_disconnect(instance, handler);
            _managerSignals.Clear();

            if (_view != IntPtr.Zero)
            {
                foreach (ulong handler in _signals) g_signal_handler_disconnect(_view, handler);
                _signals.Clear();
                // a WebKitWebView is a GObject we own one reference to; its view backend goes with it
                g_object_unref(_view);
                _view = IntPtr.Zero;
            }

            if (_exportable != IntPtr.Zero)
            {
                wpe_view_backend_exportable_fdo_destroy(_exportable);
                _exportable = IntPtr.Zero;
                _wpeBackend = IntPtr.Zero;
            }

            if (_overlay != 0)
            {
                try { LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.DestroySubsurface, _overlay); }
                catch (PlatformNotSupportedException) { }
                _overlay = 0;
            }

            if (_self.IsAllocated) _self.Free();
            _attach = null;
        }

        public void Dispose() => Detach();

        // ---- placement -------------------------------------------------------------------------

        /// <summary>Device pixels within the window; the subsurface and the engine work in logical ones.</summary>
        public void SetBounds(int x, int y, int width, int height, double scale)
        {
            if (_overlay == 0) return;
            _scale = scale <= 0 ? 1.0 : scale;
            int lx = (int)Math.Round(x / _scale), ly = (int)Math.Round(y / _scale);
            int lw = Math.Max(1, (int)Math.Round(width / _scale)), lh = Math.Max(1, (int)Math.Round(height / _scale));
            LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.MoveSubsurface, _overlay, lx, ly, lw, lh);
            if (_wpeBackend != IntPtr.Zero && (lw != _width || lh != _height))
            {
                _width = lw;
                _height = lh;
                wpe_view_backend_dispatch_set_device_scale_factor(_wpeBackend, (float)_scale);
                wpe_view_backend_dispatch_set_size(_wpeBackend, (uint)lw, (uint)lh);
            }
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_overlay != 0) LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.SetSubsurfaceVisible, _overlay, visible);
            if (_wpeBackend != IntPtr.Zero)
            {
                if (visible) wpe_view_backend_add_activity_state(_wpeBackend, 1);
                else wpe_view_backend_remove_activity_state(_wpeBackend, 1);
            }
        }

        // ---- input -------------------------------------------------------------------------------

        private static uint Time() => (uint)Environment.TickCount;

        private static int Ticks(int delta) => delta / 120 != 0 ? delta / 120 : Math.Sign(delta);

        /// <summary>From the overlay: WaylandMouseKind (0 move, 1 down, 2 up, 3 wheel, 4 leave), the evdev button, the
        /// point on the overlay, and the wheels in WPF's units (120 a notch, positive up and right).</summary>
        private void OnMouse(int kind, int button, int x, int y, int wheel, int wheelH)
        {
            if (_wpeBackend == IntPtr.Zero) return;
            // the Wayland layer reports device pixels; the engine's view is in logical ones
            x = (int)Math.Round(x / _scale);
            y = (int)Math.Round(y / _scale);
            switch (kind)
            {
                case 0:
                {
                    var e = new PointerEvent { Type = 1 /* motion */, Time = Time(), X = x, Y = y, Modifiers = _buttons };
                    wpe_view_backend_dispatch_pointer_event(_wpeBackend, &e);
                    break;
                }
                case 1:
                case 2:
                {
                    // BTN_LEFT/RIGHT/MIDDLE (0x110..0x112) are WPE's 1/2/3 (WebEventFactory's buttonForEvent)
                    uint b = button >= 0x110 && button <= 0x112 ? (uint)(button - 0x110 + 1) : 1;
                    uint mask = 1u << (int)(19 + b);   // wpe_input_pointer_modifier_button1 = 1 << 20
                    if (kind == 1)
                    {
                        _buttons |= mask;
                        wpe_view_backend_add_activity_state(_wpeBackend, 2 /* focused */);
                    }
                    else _buttons &= ~mask;
                    var e = new PointerEvent { Type = 2 /* button */, Time = Time(), X = x, Y = y, Button = b, State = kind == 1 ? 1u : 0u, Modifiers = _buttons };
                    wpe_view_backend_dispatch_pointer_event(_wpeBackend, &e);
                    break;
                }
                case 3:
                {
                    // WPE takes wheel ticks, signed as WPF's are (positive up, as its reference shell sends them);
                    // WPF's 120 a notch, a smooth fraction counting as one
                    // one event a tick: WPE scrolls a line step for each, whatever its value's size
                    for (int t = Ticks(wheel), n = Math.Abs(t); n > 0; n--)
                    {
                        var e = new AxisEvent { Type = 1 /* motion */, Time = Time(), X = x, Y = y, Axis = 0, Value = Math.Sign(t) };
                        wpe_view_backend_dispatch_axis_event(_wpeBackend, &e);
                    }
                    // WM_MOUSEHWHEEL's positive is to the right, WPE's to the left
                    for (int t = Ticks(wheelH), n = Math.Abs(t); n > 0; n--)
                    {
                        var e = new AxisEvent { Type = 1, Time = Time(), X = x, Y = y, Axis = 1, Value = -Math.Sign(t) };
                        wpe_view_backend_dispatch_axis_event(_wpeBackend, &e);
                    }
                    break;
                }
            }
        }

        /// <summary>A key, while the overlay has the keyboard: the XKB keysym and the evdev scancode, as libwpe takes
        /// them; WaylandModifiers (shift 1, control 2, alt 4) to WPE's (control 1, shift 2, alt 4).</summary>
        private void OnKey(uint keysym, uint scancode, bool down, uint modifiers, string text)
        {
            if (_wpeBackend == IntPtr.Zero) return;
            uint mods = ((modifiers & 2) != 0 ? 1u : 0) | ((modifiers & 1) != 0 ? 2u : 0) | ((modifiers & 4) != 0 ? 4u : 0) | ((modifiers & 8) != 0 ? 8u : 0);
            var e = new KeyboardEvent { Time = Time(), KeyCode = keysym, HardwareKeyCode = scancode + 8, Pressed = down ? (byte)1 : (byte)0, Modifiers = mods };
            wpe_view_backend_dispatch_keyboard_event(_wpeBackend, &e);
        }

        // ---- navigation ---------------------------------------------------------------------------

        public void Navigate(string uri)
        {
            RequireAttached();
            _lastRequestedUri = uri;
            RaiseNavigationStarting(uri);
            using var utf8 = new Utf8(uri);
            webkit_web_view_load_uri(_view, utf8.Pointer);
        }

        public void NavigateToString(string htmlContent)
        {
            RequireAttached();
            _lastRequestedUri = null;
            RaiseNavigationStarting(null);
            using var html = new Utf8(htmlContent);
            webkit_web_view_load_html(_view, html.Pointer, IntPtr.Zero);
        }

        /// <summary>
        /// WebKit can navigate with a body only through a WebKitURIRequest carrying one, and the API here exposes no
        /// setter for it. Refused rather than downgraded to a GET, which would turn a form post into a page load.
        /// </summary>
        public void NavigateWithPost(string uri, byte[] postData, string additionalHeaders)
        {
            RequireAttached();
            if ((postData is null || postData.Length == 0) && string.IsNullOrEmpty(additionalHeaders))
            {
                Navigate(uri);
                return;
            }
            throw new NotSupportedException(
                "Navigating with POST data or additional headers is not available through this engine.");
        }

        private void RaiseNavigationStarting(string uri)
        {
            NavigationStarting?.Invoke(this, new WebViewNavigationStartingEventArgs
            {
                Uri = uri,
                IsUserInitiated = false,
                NavigationId = ++_navigationId,
            });
        }

        public void Reload(bool noCache)
        {
            RequireAttached();
            if (noCache) webkit_web_view_reload_bypass_cache(_view);
            else webkit_web_view_reload(_view);
        }

        public void Stop()
        {
            RequireAttached();
            webkit_web_view_stop_loading(_view);
        }

        public void GoBack()
        {
            RequireAttached();
            webkit_web_view_go_back(_view);
        }

        public void GoForward()
        {
            RequireAttached();
            webkit_web_view_go_forward(_view);
        }

        public bool CanGoBack => _view != IntPtr.Zero && webkit_web_view_can_go_back(_view);

        public bool CanGoForward => _view != IntPtr.Zero && webkit_web_view_can_go_forward(_view);

        /// <summary>The current URI. WebKit owns the string, so it is copied and not freed.</summary>
        public string Source =>
            _view == IntPtr.Zero
                ? _lastRequestedUri
                : Marshal.PtrToStringUTF8(webkit_web_view_get_uri(_view)) ?? _lastRequestedUri;

        public string DocumentTitle =>
            _view == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(webkit_web_view_get_title(_view));

        // ---- scripting ------------------------------------------------------------------------------

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            RequireAttached();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = GCHandle.Alloc(tcs);
            using var script = new Utf8(javaScript);
            // length -1: NUL-terminated; no world (the page's own), no source URI
            webkit_web_view_evaluate_javascript(
                _view, script.Pointer, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ScriptCompleted,
                GCHandle.ToIntPtr(pending));
            return tcs.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ScriptCompleted(IntPtr source, IntPtr result, IntPtr userData)
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            var tcs = (TaskCompletionSource<string>)handle.Target;
            IntPtr error = IntPtr.Zero;
            try
            {
                IntPtr value = webkit_web_view_evaluate_javascript_finish(source, result, &error);
                if (value == IntPtr.Zero)
                {
                    tcs.TrySetException(new InvalidOperationException(TakeGError(ref error)));
                    return;
                }
                try
                {
                    // JSC serialises a value to JSON directly, which is exactly the seam's contract
                    IntPtr json = jsc_value_to_json(value, 0);
                    if (json == IntPtr.Zero)
                    {
                        tcs.TrySetResult("null");
                        return;
                    }
                    try { tcs.TrySetResult(Marshal.PtrToStringUTF8(json) ?? "null"); }
                    finally { g_free(json); }   // newly allocated: ours
                }
                finally { g_object_unref(value); }
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { handle.Free(); }
        }

        /// <summary>Read a GError's message and free the GError. Returns a fallback if there is none.</summary>
        private static string TakeGError(ref IntPtr error)
        {
            if (error == IntPtr.Zero) return "The script failed.";
            // GError is { GQuark domain; gint code; gchar *message; }: the message after two 32-bit fields
            IntPtr message = *(IntPtr*)((byte*)error + 8);
            string text = Marshal.PtrToStringUTF8(message) ?? "The script failed.";
            g_error_free(error);
            error = IntPtr.Zero;
            return text;
        }

        /// <summary>Script in every document before its own, through the user content manager.</summary>
        public void AddScriptToExecuteOnDocumentCreated(string javaScript)
        {
            RequireAttached();
            IntPtr manager = webkit_web_view_get_user_content_manager(_view);
            if (manager != IntPtr.Zero) AddUserScript(manager, javaScript);
        }

        private static void AddUserScript(IntPtr manager, string javaScript)
        {
            using var source = new Utf8(javaScript);
            // injected-frames 0 == all frames, injection-time 0 == at document start, no allow/block lists
            IntPtr script = webkit_user_script_new(source.Pointer, 0, 0, IntPtr.Zero, IntPtr.Zero);
            if (script == IntPtr.Zero) return;
            try { webkit_user_content_manager_add_script(manager, script); }
            finally { webkit_user_script_unref(script); }
        }

        /// <summary>window.chrome.webview.postMessage: the shim always posts a string.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnScriptMessage(IntPtr manager, IntPtr value, IntPtr userData)
        {
            LinuxWebViewBackend b = FromUserData(userData);
            if (b is null || value == IntPtr.Zero) return;
            IntPtr text = jsc_value_to_string(value);
            string body;
            try { body = Marshal.PtrToStringUTF8(text) ?? ""; }
            finally { if (text != IntPtr.Zero) g_free(text); }
            b.WebMessageReceived?.Invoke(b, new WebViewMessageReceivedEventArgs
            {
                Source = b.Source,
                WebMessageAsJson = WebViewScript.WriteJsonString(body),
                WebMessageAsString = body,
            });
        }

        public void PostWebMessageAsJson(string webMessageAsJson) => DeliverToPage(webMessageAsJson);

        public void PostWebMessageAsString(string webMessageAsString) =>
            DeliverToPage(WebViewScript.WriteJsonString(webMessageAsString));

        /// <summary>WebKit's script-message channel runs page-to-host only: host-to-page goes through the shim.</summary>
        private void DeliverToPage(string jsonLiteral)
        {
            RequireAttached();
            _ = ExecuteScriptAsync("window.chrome.webview.__deliver(" + jsonLiteral + ");");
        }

        // ---- configuration -----------------------------------------------------------------------------

        public void ApplySettings(WebViewSettings settings)
        {
            _settings = settings ?? new WebViewSettings();
            if (_view == IntPtr.Zero) return;
            IntPtr s = webkit_web_view_get_settings(_view);
            if (s == IntPtr.Zero) return;
            webkit_settings_set_enable_javascript(s, _settings.ScriptEnabled);
            webkit_settings_set_enable_developer_extras(s, _settings.AreDevToolsEnabled);
            if (!string.IsNullOrEmpty(_settings.UserAgent))
            {
                using var ua = new Utf8(_settings.UserAgent);
                webkit_settings_set_user_agent(s, ua.Pointer);
            }
        }

        public double ZoomFactor
        {
            get => _view == IntPtr.Zero ? _zoom : webkit_web_view_get_zoom_level(_view);
            set
            {
                _zoom = value <= 0 ? 1.0 : value;
                if (_view != IntPtr.Zero) webkit_web_view_set_zoom_level(_view, _zoom);
            }
        }

        /// <summary>The last frame the engine drew, as a PNG (the only encoding kept here, whatever was asked).</summary>
        public Task<byte[]> CapturePreviewAsync(bool png)
        {
            RequireAttached();
            if (_lastFrame == null)
                return Task.FromException<byte[]>(new InvalidOperationException(
                    "The web view has not produced a frame yet, so there is nothing to capture."));
            return Task.FromResult(EncodePng(_lastFrame, _lastFrameWidth, _lastFrameHeight));
        }

        /// <summary>BGRA rows (premultiplied, opaque in practice) to an RGBA PNG.</summary>
        private static byte[] EncodePng(byte[] bgra, int width, int height)
        {
            var raw = new byte[(width * 4 + 1) * height];
            for (int y = 0, o = 0; y < height; y++)
            {
                raw[o++] = 0;   // filter: none
                for (int x = 0; x < width; x++, o += 4)
                {
                    int i = (y * width + x) * 4;
                    raw[o] = bgra[i + 2]; raw[o + 1] = bgra[i + 1]; raw[o + 2] = bgra[i]; raw[o + 3] = bgra[i + 3];
                }
            }
            using var png = new System.IO.MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            void Chunk(string type, byte[] data)
            {
                byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
                byte[] len = BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data.Length));
                png.Write(len);
                png.Write(t);
                png.Write(data);
                uint crc = Crc(t, data);
                png.Write(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(crc)));
            }
            var ihdr = new byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8; ihdr[9] = 6;   // 8-bit RGBA
            Chunk("IHDR", ihdr);
            using (var z = new System.IO.MemoryStream())
            {
                using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true))
                    zs.Write(raw);
                Chunk("IDAT", z.ToArray());
            }
            Chunk("IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static uint Crc(byte[] type, byte[] data)
        {
            uint c = 0xFFFFFFFF;
            void Add(byte[] bytes)
            {
                foreach (byte v in bytes)
                {
                    c ^= v;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
            }
            Add(type);
            Add(data);
            return c ^ 0xFFFFFFFF;
        }

        public Task ClearBrowsingDataAsync() =>
            throw new NotSupportedException("Clearing browsing data is not implemented on this head yet.");

        // ---- signals ---------------------------------------------------------------------------------------

        private void ConnectSignals()
        {
            Connect("load-changed", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnLoadChanged);
            Connect("load-failed", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr, IntPtr, byte>)&OnLoadFailed);
            Connect("notify::title", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnTitleChanged);
        }

        private void Connect(string signal, IntPtr callback)
        {
            using var name = new Utf8(signal);
            ulong handler = g_signal_connect_data(_view, name.Pointer, callback, GCHandle.ToIntPtr(_self), IntPtr.Zero, 0);
            if (handler != 0) _signals.Add(handler);
        }

        private static LinuxWebViewBackend FromUserData(IntPtr userData) =>
            userData == IntPtr.Zero ? null : GCHandle.FromIntPtr(userData).Target as LinuxWebViewBackend;

        /// <summary>WebKitLoadEvent: Started 0, Redirected 1, Committed 2, Finished 3.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnLoadChanged(IntPtr view, int loadEvent, IntPtr userData)
        {
            LinuxWebViewBackend b = FromUserData(userData);
            if (b is null) return;
            switch (loadEvent)
            {
                case 0:
                    b.ContentLoading?.Invoke(b, EventArgs.Empty);
                    break;
                case 2:
                    b.SourceChanged?.Invoke(b, new WebViewSourceChangedEventArgs { IsNewDocument = true });
                    break;
                case 3:
                    b.NavigationCompleted?.Invoke(b, new WebViewNavigationCompletedEventArgs
                    {
                        IsSuccess = true,
                        WebErrorStatus = WebViewErrorStatus.Unknown,
                        NavigationId = b._navigationId,
                    });
                    break;
            }
        }

        /// <summary>Returns FALSE to let WebKit show its own error page, as the seam's other heads do.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte OnLoadFailed(IntPtr view, int loadEvent, IntPtr uri, IntPtr error, IntPtr userData)
        {
            LinuxWebViewBackend b = FromUserData(userData);
            b?.NavigationCompleted?.Invoke(b, new WebViewNavigationCompletedEventArgs
            {
                IsSuccess = false,
                WebErrorStatus = TranslateNetworkError(error),
                NavigationId = b._navigationId,
            });
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTitleChanged(IntPtr view, IntPtr pspec, IntPtr userData)
        {
            LinuxWebViewBackend b = FromUserData(userData);
            b?.DocumentTitleChanged?.Invoke(b, EventArgs.Empty);
        }

        /// <summary>WebKitNetworkError codes, mapped only where unambiguous; the code follows the 32-bit domain.</summary>
        private static WebViewErrorStatus TranslateNetworkError(IntPtr error)
        {
            if (error == IntPtr.Zero) return WebViewErrorStatus.Unknown;
            int code = *(int*)((byte*)error + 4);
            return code switch
            {
                302 => WebViewErrorStatus.OperationCanceled,   // CANCELLED
                399 => WebViewErrorStatus.HostNameNotResolved, // HOST_NOT_FOUND (transport)
                _ => WebViewErrorStatus.Unknown,
            };
        }

        private void RequireAttached()
        {
            if (_view == IntPtr.Zero)
                throw new InvalidOperationException("The web view is not ready yet; await AttachAsync before using it.");
        }

        // ---- events -------------------------------------------------------------------------------------------

        public event EventHandler<WebViewNavigationStartingEventArgs> NavigationStarting;
        public event EventHandler<WebViewSourceChangedEventArgs> SourceChanged;
        public event EventHandler ContentLoading;
        public event EventHandler<WebViewNavigationCompletedEventArgs> NavigationCompleted;
        public event EventHandler<WebViewMessageReceivedEventArgs> WebMessageReceived;
#pragma warning disable CS0067 // not raised by this engine yet: window.open and downloads are refused, a crash not reported
        public event EventHandler<WebViewNewWindowRequestedEventArgs> NewWindowRequested;
        public event EventHandler<WebViewDownloadStartingEventArgs> DownloadStarting;
        public event EventHandler<WebViewProcessFailedEventArgs> ProcessFailed;
#pragma warning restore CS0067
        public event EventHandler DocumentTitleChanged;

        // ---- interop -----------------------------------------------------------------------------------------------

        /// <summary>A NUL-terminated UTF-8 copy, freed when the call that used it returns.</summary>
        private readonly struct Utf8 : IDisposable
        {
            internal IntPtr Pointer { get; }

            internal Utf8(string value)
            {
                if (value is null)
                {
                    Pointer = IntPtr.Zero;
                    return;
                }
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
                Pointer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, Pointer, bytes.Length);
            }

            public void Dispose()
            {
                if (Pointer != IntPtr.Zero) Marshal.FreeHGlobal(Pointer);
            }
        }

        [DllImport(LibWpe)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool wpe_loader_init(IntPtr implLibraryName);
        [DllImport(LibWpe)] private static extern void wpe_view_backend_dispatch_set_size(IntPtr backend, uint width, uint height);
        [DllImport(LibWpe)] private static extern void wpe_view_backend_dispatch_set_device_scale_factor(IntPtr backend, float scale);
        [DllImport(LibWpe)] private static extern void wpe_view_backend_add_activity_state(IntPtr backend, uint state);
        [DllImport(LibWpe)] private static extern void wpe_view_backend_remove_activity_state(IntPtr backend, uint state);
        [DllImport(LibWpe)] private static extern void wpe_view_backend_dispatch_pointer_event(IntPtr backend, PointerEvent* e);
        [DllImport(LibWpe)] private static extern void wpe_view_backend_dispatch_axis_event(IntPtr backend, AxisEvent* e);
        [DllImport(LibWpe)] private static extern void wpe_view_backend_dispatch_keyboard_event(IntPtr backend, KeyboardEvent* e);

        [DllImport(Fdo)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool wpe_fdo_initialize_shm();
        [DllImport(Fdo)] private static extern IntPtr wpe_view_backend_exportable_fdo_create(ExportableClient* client, IntPtr data, uint width, uint height);
        [DllImport(Fdo)] private static extern IntPtr wpe_view_backend_exportable_fdo_get_view_backend(IntPtr exportable);
        [DllImport(Fdo)] private static extern void wpe_view_backend_exportable_fdo_destroy(IntPtr exportable);
        [DllImport(Fdo)] private static extern void wpe_view_backend_exportable_fdo_dispatch_frame_complete(IntPtr exportable);
        [DllImport(Fdo)] private static extern void wpe_view_backend_exportable_fdo_dispatch_release_shm_exported_buffer(IntPtr exportable, IntPtr buffer);
        [DllImport(Fdo)] private static extern IntPtr wpe_fdo_shm_exported_buffer_get_shm_buffer(IntPtr buffer);

        [DllImport(WaylandServer)] private static extern IntPtr wl_shm_buffer_get_data(IntPtr buffer);
        [DllImport(WaylandServer)] private static extern int wl_shm_buffer_get_stride(IntPtr buffer);
        [DllImport(WaylandServer)] private static extern int wl_shm_buffer_get_width(IntPtr buffer);
        [DllImport(WaylandServer)] private static extern int wl_shm_buffer_get_height(IntPtr buffer);
        [DllImport(WaylandServer)] private static extern void wl_shm_buffer_begin_access(IntPtr buffer);
        [DllImport(WaylandServer)] private static extern void wl_shm_buffer_end_access(IntPtr buffer);

        [DllImport(Wpe)] private static extern IntPtr webkit_web_view_backend_new(IntPtr backend, IntPtr notify, IntPtr data);
        [DllImport(Wpe)] private static extern IntPtr webkit_web_view_new(IntPtr backend);
        [DllImport(Wpe)] private static extern void webkit_web_view_load_uri(IntPtr view, IntPtr uri);
        [DllImport(Wpe)] private static extern void webkit_web_view_load_html(IntPtr view, IntPtr content, IntPtr baseUri);
        [DllImport(Wpe)] private static extern void webkit_web_view_reload(IntPtr view);
        [DllImport(Wpe)] private static extern void webkit_web_view_reload_bypass_cache(IntPtr view);
        [DllImport(Wpe)] private static extern void webkit_web_view_stop_loading(IntPtr view);
        [DllImport(Wpe)] private static extern void webkit_web_view_go_back(IntPtr view);
        [DllImport(Wpe)] private static extern void webkit_web_view_go_forward(IntPtr view);
        [DllImport(Wpe)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool webkit_web_view_can_go_back(IntPtr view);
        [DllImport(Wpe)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool webkit_web_view_can_go_forward(IntPtr view);
        [DllImport(Wpe)] private static extern IntPtr webkit_web_view_get_uri(IntPtr view);
        [DllImport(Wpe)] private static extern IntPtr webkit_web_view_get_title(IntPtr view);
        [DllImport(Wpe)] private static extern IntPtr webkit_web_view_get_settings(IntPtr view);
        [DllImport(Wpe)] private static extern IntPtr webkit_web_view_get_user_content_manager(IntPtr view);
        [DllImport(Wpe)] private static extern double webkit_web_view_get_zoom_level(IntPtr view);
        [DllImport(Wpe)] private static extern void webkit_web_view_set_zoom_level(IntPtr view, double level);
        [DllImport(Wpe)] private static extern void webkit_web_view_evaluate_javascript(
            IntPtr view, IntPtr script, nint length, IntPtr worldName, IntPtr sourceUri, IntPtr cancellable, IntPtr callback, IntPtr userData);
        [DllImport(Wpe)] private static extern IntPtr webkit_web_view_evaluate_javascript_finish(IntPtr view, IntPtr result, IntPtr* error);
        [DllImport(Wpe)] private static extern IntPtr jsc_value_to_json(IntPtr value, uint indent);
        [DllImport(Wpe)] private static extern IntPtr jsc_value_to_string(IntPtr value);
        [DllImport(Wpe)] private static extern IntPtr webkit_user_script_new(IntPtr source, int injectedFrames, int injectionTime, IntPtr allowList, IntPtr blockList);
        [DllImport(Wpe)] private static extern void webkit_user_script_unref(IntPtr script);
        [DllImport(Wpe)] private static extern void webkit_user_content_manager_add_script(IntPtr manager, IntPtr script);
        [DllImport(Wpe)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool webkit_user_content_manager_register_script_message_handler(IntPtr manager, IntPtr name, IntPtr worldName);
        [DllImport(Wpe)] private static extern void webkit_settings_set_enable_javascript(IntPtr settings, [MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Wpe)] private static extern void webkit_settings_set_enable_developer_extras(IntPtr settings, [MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Wpe)] private static extern void webkit_settings_set_user_agent(IntPtr settings, IntPtr userAgent);

        [DllImport(GObject)] private static extern void g_object_unref(IntPtr obj);
        [DllImport(GObject)] private static extern ulong g_signal_connect_data(IntPtr instance, IntPtr signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
        [DllImport(GObject)] private static extern void g_signal_handler_disconnect(IntPtr instance, ulong handler);

        [DllImport(GLib)] private static extern void g_free(IntPtr mem);
        [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
        [DllImport(GLib)] private static extern int g_main_context_iteration(IntPtr context, int mayBlock);
    }
}
