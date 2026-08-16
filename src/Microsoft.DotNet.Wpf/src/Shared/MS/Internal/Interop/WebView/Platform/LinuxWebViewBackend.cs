// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// LinuxWebViewBackend -- the Linux IWebViewBackend, backed by WPE WebKit on a wl_subsurface.
//
// WPE, not WebKitGTK, and the distinction is the whole reason this head works at all. This fork's
// Linux head is Wayland-native and runs its own loop: LinuxInterop states that X11 is "NOT a fallback
// for 'Wayland failed'" and that WaylandWindow is the only thing that creates windows here. WebKitGTK
// would bring GTK and a GTK main loop, and its usual embedding trick -- reparenting a GtkWindow with
// XReparentWindow -- is X11-only; Wayland has no client-side reparenting and a client cannot position
// a toplevel. WPE is the WebKit port built for exactly this: no toolkit, no main loop of its own, and
// frames handed to the embedder as buffers.
//
// The overlay is a wl_subsurface, which is the Wayland primitive that means what this seam needs: a
// surface positioned relative to a parent, clipped by it, and ordered above it.
//
// The split with the Wayland layer follows what each side can reach (see LinuxWebViewRegistration):
// this file P/Invokes WPE -- allowed, it is plain P/Invoke -- and the subsurface, its buffers and its
// commits belong to WaylandWindow, which owns wl_compositor and wl_subcompositor.
//
// GObject, in three rules, because everything below follows them:
//   * Signals are connected with g_signal_connect_data and a static [UnmanagedCallersOnly] callback
//     whose first argument is the emitting object and whose last is the user data -- here a GCHandle
//     to this instance, exactly as the Objective-C heads pass one through indexed ivars.
//   * Asynchronous calls take a GAsyncReadyCallback and are finished from inside it. There is no
//     synchronous form of run_javascript, so ExecuteScriptAsync completes from that callback.
//   * Strings returned by WebKit are owned by WebKit unless the API says otherwise; the two that are
//     ours (g_strdup'd results, GError messages) are freed here and marked as such.
//
// PREREQUISITE, and a documented one: libWPEWebKit must be installed (Debian/Ubuntu
// libwpewebkit-1.1-0, Fedora wpewebkit), the same way libgdiplus and GStreamer already are for this
// head. A machine without it gets a clear PlatformNotSupportedException naming the package, not a
// DllNotFoundException from a library the application never referenced.
//
// NOT YET RUN ON LINUX. The symbol names and signal names below were taken from the WPE WebKit
// headers rather than recalled, but nothing here has met a real engine.
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
        // Two sonames, newest first: 1.1 is the current API series, 1.0 the previous one. Probed
        // rather than pinned so a distribution that ships either satisfies the prerequisite.
        private const string Wpe11 = "libWPEWebKit-1.1.so.0";
        private const string Wpe10 = "libWPEWebKit-1.0.so.3";
        private const string GObject = "libgobject-2.0.so.0";
        private const string GLib = "libglib-2.0.so.0";

        private IntPtr _view;              // WebKitWebView*
        private int _subsurface;           // the Wayland layer's id for our overlay
        private GCHandle _self;
        private bool _visible = true;
        private double _zoom = 1.0;
        private string _lastRequestedUri;
        private ulong _navigationId;
        private WebViewSettings _settings = new WebViewSettings();
        private TaskCompletionSource<object> _attach;
        private readonly List<ulong> _signals = new List<ulong>();

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

            // The handle IS the parent wl_surface* on this head, and it is stable for the window's
            // life, so it can be handed straight to the Wayland layer.
            object created = LinuxWebViewRegistration.Invoke(
                LinuxWebViewCommands.CreateSubsurface, ownerWindow, 0, 0, 1, 1);

            _subsurface = created is int id ? id : 0;

            if (_subsurface == 0)
            {
                _attach.TrySetException(new InvalidOperationException(
                    "The Wayland layer could not create a subsurface for the web view."));
                return _attach.Task;
            }

            try
            {
                _self = GCHandle.Alloc(this);
                _view = CreateWebView();
            }
            catch (DllNotFoundException ex)
            {
                LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.DestroySubsurface, _subsurface);
                _subsurface = 0;

                // The overwhelmingly likely cause, said plainly: a bare DllNotFoundException names a
                // library the application never referenced and helps nobody.
                _attach.TrySetException(new PlatformNotSupportedException(
                    "WPE WebKit is not installed, so web content cannot be hosted on this machine. " +
                    "Install libwpewebkit (Debian/Ubuntu: libwpewebkit-1.1-0, Fedora: wpewebkit).",
                    ex));

                return _attach.Task;
            }

            ConnectSignals();
            ApplySettings(_settings);
            SetVisible(_visible);

            // A WebKitWebView is usable as soon as it is constructed; there is no asynchronous
            // start-up to wait for, unlike WebView2's environment and controller.
            _attach.TrySetResult(null);
            return _attach.Task;
        }

        /// <summary>
        /// Build the view. The FDO backend is what turns rendered frames into buffers the embedder
        /// can attach to its own surface -- it is initialised against the process's wl_display, which
        /// only the Wayland layer knows.
        /// </summary>
        private IntPtr CreateWebView()
        {
            IntPtr display = (IntPtr)LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.GetDisplay);

            wpe_fdo_initialize_for_wayland_display(display);

            return webkit_web_view_new();
        }

        public void Detach()
        {
            if (_view != IntPtr.Zero)
            {
                foreach (ulong handler in _signals)
                {
                    g_signal_handler_disconnect(_view, handler);
                }

                _signals.Clear();

                // A WebKitWebView is a GObject we own one reference to.
                g_object_unref(_view);
                _view = IntPtr.Zero;
            }

            if (_subsurface != 0)
            {
                try
                {
                    LinuxWebViewRegistration.Invoke(LinuxWebViewCommands.DestroySubsurface, _subsurface);
                }
                catch (PlatformNotSupportedException)
                {
                    // The windowing layer went away first; nothing left to destroy.
                }

                _subsurface = 0;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _attach = null;
        }

        public void Dispose() => Detach();

        // ---- placement -------------------------------------------------------------------------

        public void SetBounds(int x, int y, int width, int height, double scale)
        {
            if (_subsurface == 0)
            {
                return;
            }

            LinuxWebViewRegistration.Invoke(
                LinuxWebViewCommands.MoveSubsurface, _subsurface, x, y,
                Math.Max(1, width), Math.Max(1, height));
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;

            if (_subsurface != 0)
            {
                LinuxWebViewRegistration.Invoke(
                    LinuxWebViewCommands.SetSubsurfaceVisible, _subsurface, visible);
            }
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
        /// WebKit can navigate with a body only through a WebKitURIRequest carrying one, and the WPE
        /// API series here exposes no setter for it. Refused rather than downgraded to a GET, which
        /// would turn a form post into a page load.
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

            if (noCache)
            {
                webkit_web_view_reload_bypass_cache(_view);
            }
            else
            {
                webkit_web_view_reload(_view);
            }
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

            webkit_web_view_run_javascript(
                _view, script.Pointer, IntPtr.Zero,
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
                IntPtr jsResult = webkit_web_view_run_javascript_finish(source, result, &error);

                if (jsResult == IntPtr.Zero)
                {
                    tcs.TrySetException(new InvalidOperationException(TakeGError(ref error)));
                    return;
                }

                try
                {
                    IntPtr value = webkit_javascript_result_get_js_value(jsResult);

                    // JSC can serialise a value to JSON directly, which is exactly the seam's
                    // contract -- no scalar/aggregate special-casing as the Apple heads need.
                    IntPtr json = jsc_value_to_json(value, 0);

                    if (json == IntPtr.Zero)
                    {
                        tcs.TrySetResult("null");
                        return;
                    }

                    try
                    {
                        tcs.TrySetResult(Marshal.PtrToStringUTF8(json) ?? "null");
                    }
                    finally
                    {
                        // jsc_value_to_json returns a newly allocated string; this one IS ours.
                        g_free(json);
                    }
                }
                finally
                {
                    webkit_javascript_result_unref(jsResult);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>Read a GError's message and free the GError. Returns a fallback if there is none.</summary>
        private static string TakeGError(ref IntPtr error)
        {
            if (error == IntPtr.Zero)
            {
                return "The script failed.";
            }

            // GError is { GQuark domain; gint code; gchar *message; } -- the message is the third
            // field, after two 32-bit ones.
            IntPtr message = *(IntPtr*)((byte*)error + 8);
            string text = Marshal.PtrToStringUTF8(message) ?? "The script failed.";

            g_error_free(error);
            error = IntPtr.Zero;
            return text;
        }

        /// <summary>
        /// Run script in every document before its own scripts run, through the user content
        /// manager -- the same mechanism the WKWebView heads use, under a different name.
        /// </summary>
        public void AddScriptToExecuteOnDocumentCreated(string javaScript)
        {
            RequireAttached();

            IntPtr manager = webkit_web_view_get_user_content_manager(_view);

            if (manager == IntPtr.Zero)
            {
                return;
            }

            using var source = new Utf8(javaScript);

            // injected-frames 0 == all frames, injection-time 0 == at document start, and no
            // allow/block lists: the same shape as the WKUserScript the Apple heads add.
            IntPtr script = webkit_user_script_new(source.Pointer, 0, 0, IntPtr.Zero, IntPtr.Zero);

            if (script == IntPtr.Zero)
            {
                return;
            }

            try
            {
                webkit_user_content_manager_add_script(manager, script);
            }
            finally
            {
                webkit_user_script_unref(script);
            }
        }

        public void PostWebMessageAsJson(string webMessageAsJson) => DeliverToPage(webMessageAsJson);

        public void PostWebMessageAsString(string webMessageAsString) =>
            DeliverToPage(WebViewScript.WriteJsonString(webMessageAsString));

        /// <summary>
        /// Hand a message to the page. WebKit's script-message channel runs page-to-host only, so
        /// host-to-page goes the way it does on the Apple heads: through the shim's __deliver.
        /// </summary>
        private void DeliverToPage(string jsonLiteral)
        {
            RequireAttached();
            _ = ExecuteScriptAsync("window.chrome.webview.__deliver(" + jsonLiteral + ");");
        }

        // ---- configuration -----------------------------------------------------------------------------

        public void ApplySettings(WebViewSettings settings)
        {
            _settings = settings ?? new WebViewSettings();

            if (_view == IntPtr.Zero)
            {
                return;
            }

            IntPtr s = webkit_web_view_get_settings(_view);

            if (s == IntPtr.Zero)
            {
                return;
            }

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

                if (_view != IntPtr.Zero)
                {
                    webkit_web_view_set_zoom_level(_view, _zoom);
                }
            }
        }

        /// <summary>
        /// Clearing storage is a WebKitWebsiteDataManager operation whose asynchronous form needs
        /// more of the GObject surface than this binding carries. Refused rather than reported as
        /// done -- a caller clearing cookies is usually doing it for a reason.
        /// </summary>
        public Task ClearBrowsingDataAsync() =>
            throw new NotSupportedException(
                "Clearing browsing data is not implemented on this head yet.");

        // ---- signals ---------------------------------------------------------------------------------------

        private void ConnectSignals()
        {
            Connect("load-changed",
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnLoadChanged);

            Connect("load-failed",
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr, IntPtr, byte>)&OnLoadFailed);

            Connect("notify::title",
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnTitleChanged);
        }

        private void Connect(string signal, IntPtr callback)
        {
            using var name = new Utf8(signal);

            ulong handler = g_signal_connect_data(
                _view, name.Pointer, callback, GCHandle.ToIntPtr(_self), IntPtr.Zero, 0);

            if (handler != 0)
            {
                _signals.Add(handler);
            }
        }

        private static LinuxWebViewBackend FromUserData(IntPtr userData) =>
            userData == IntPtr.Zero ? null : GCHandle.FromIntPtr(userData).Target as LinuxWebViewBackend;

        /// <summary>WebKitLoadEvent: Started 0, Redirected 1, Committed 2, Finished 3.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnLoadChanged(IntPtr view, int loadEvent, IntPtr userData)
        {
            LinuxWebViewBackend b = FromUserData(userData);

            if (b is null)
            {
                return;
            }

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

        /// <summary>
        /// WebKitNetworkError codes, mapped only where the meaning is unambiguous. The GError's code
        /// is its second field, after the 32-bit domain quark.
        /// </summary>
        private static WebViewErrorStatus TranslateNetworkError(IntPtr error)
        {
            if (error == IntPtr.Zero)
            {
                return WebViewErrorStatus.Unknown;
            }

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
            {
                throw new InvalidOperationException(
                    "The web view is not ready yet; await AttachAsync before using it.");
            }
        }

        // ---- events -------------------------------------------------------------------------------------------

        public event EventHandler<WebViewNavigationStartingEventArgs> NavigationStarting;
        public event EventHandler<WebViewSourceChangedEventArgs> SourceChanged;
        public event EventHandler ContentLoading;
        public event EventHandler<WebViewNavigationCompletedEventArgs> NavigationCompleted;
        public event EventHandler<WebViewMessageReceivedEventArgs> WebMessageReceived;
        public event EventHandler<WebViewNewWindowRequestedEventArgs> NewWindowRequested;
        public event EventHandler<WebViewDownloadStartingEventArgs> DownloadStarting;
        public event EventHandler DocumentTitleChanged;
        public event EventHandler<WebViewProcessFailedEventArgs> ProcessFailed;

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
                if (Pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Pointer);
                }
            }
        }

        [DllImport(Wpe11, EntryPoint = "wpe_fdo_initialize_for_wayland_display")]
        private static extern void wpe_fdo_initialize_for_wayland_display(IntPtr display);

        [DllImport(Wpe11)] private static extern IntPtr webkit_web_view_new();
        [DllImport(Wpe11)] private static extern void webkit_web_view_load_uri(IntPtr view, IntPtr uri);
        [DllImport(Wpe11)] private static extern void webkit_web_view_load_html(IntPtr view, IntPtr content, IntPtr baseUri);
        [DllImport(Wpe11)] private static extern void webkit_web_view_reload(IntPtr view);
        [DllImport(Wpe11)] private static extern void webkit_web_view_reload_bypass_cache(IntPtr view);
        [DllImport(Wpe11)] private static extern void webkit_web_view_stop_loading(IntPtr view);
        [DllImport(Wpe11)] private static extern void webkit_web_view_go_back(IntPtr view);
        [DllImport(Wpe11)] private static extern void webkit_web_view_go_forward(IntPtr view);
        [DllImport(Wpe11)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool webkit_web_view_can_go_back(IntPtr view);
        [DllImport(Wpe11)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool webkit_web_view_can_go_forward(IntPtr view);
        [DllImport(Wpe11)] private static extern IntPtr webkit_web_view_get_uri(IntPtr view);
        [DllImport(Wpe11)] private static extern IntPtr webkit_web_view_get_title(IntPtr view);
        [DllImport(Wpe11)] private static extern IntPtr webkit_web_view_get_settings(IntPtr view);
        [DllImport(Wpe11)] private static extern IntPtr webkit_web_view_get_user_content_manager(IntPtr view);
        [DllImport(Wpe11)] private static extern double webkit_web_view_get_zoom_level(IntPtr view);
        [DllImport(Wpe11)] private static extern void webkit_web_view_set_zoom_level(IntPtr view, double level);

        [DllImport(Wpe11)] private static extern void webkit_web_view_run_javascript(
            IntPtr view, IntPtr script, IntPtr cancellable, IntPtr callback, IntPtr userData);
        [DllImport(Wpe11)] private static extern IntPtr webkit_web_view_run_javascript_finish(
            IntPtr view, IntPtr result, IntPtr* error);
        [DllImport(Wpe11)] private static extern IntPtr webkit_javascript_result_get_js_value(IntPtr result);
        [DllImport(Wpe11)] private static extern void webkit_javascript_result_unref(IntPtr result);
        [DllImport(Wpe11)] private static extern IntPtr jsc_value_to_json(IntPtr value, uint indent);

        [DllImport(Wpe11)] private static extern IntPtr webkit_user_script_new(
            IntPtr source, int injectedFrames, int injectionTime, IntPtr allowList, IntPtr blockList);
        [DllImport(Wpe11)] private static extern void webkit_user_script_unref(IntPtr script);
        [DllImport(Wpe11)] private static extern void webkit_user_content_manager_add_script(
            IntPtr manager, IntPtr script);

        [DllImport(Wpe11)] private static extern void webkit_settings_set_enable_javascript(
            IntPtr settings, [MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Wpe11)] private static extern void webkit_settings_set_enable_developer_extras(
            IntPtr settings, [MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Wpe11)] private static extern void webkit_settings_set_user_agent(
            IntPtr settings, IntPtr userAgent);

        [DllImport(GObject)] private static extern void g_object_unref(IntPtr obj);
        [DllImport(GObject)] private static extern ulong g_signal_connect_data(
            IntPtr instance, IntPtr signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
        [DllImport(GObject)] private static extern void g_signal_handler_disconnect(IntPtr instance, ulong handler);

        [DllImport(GLib)] private static extern void g_free(IntPtr mem);
        [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
    }
}
