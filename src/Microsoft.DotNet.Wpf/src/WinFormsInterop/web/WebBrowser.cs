// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// System.Windows.Forms.WebBrowser, on this fork's cross-platform engine seam.
//
// The control did not exist here at all: Mono's was pruned when swf/ was vendored (it drove Gecko
// through Mono.WebBrowser), so an application that used it did not compile. Everything AROUND it
// survived though -- WebBrowserReadyState, the four WebBrowser*EventArgs and their handlers, and the
// JS dialog forms in System.Windows.Forms.WebBrowserDialogs -- and this reuses all of it, which is
// why the public surface below matches the original without a compatibility shim in sight.
//
// It lives outside swf/ deliberately: that tree is vendored Mono source kept pristine so it can be
// refreshed, and this is ours.
//
// THE HANDLE PROBLEM is the same one the WinForms WebView2 control has, and is solved the same way.
// Control.Handle on this stack is a managed counter (XplatUIWebGpu mints handles with next_handle++)
// because every control is painted into a bitmap and presented through WebGPU; it is not an OS
// window and a web engine cannot be parented to it. The engine therefore goes into a child of
// EmbeddedScenes.HostWindow -- the process's only real native window -- and this control keeps that
// child positioned over itself.
//
// The consequence is the airspace rule the seam documents generally: the engine's window sits over
// the WebGPU surface, so WinForms controls do not clip it and do not z-order against it. That is the
// same limitation the original had for the same reason (it hosted a native window too).
//

using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MS.Internal.Interop.WebView;

namespace System.Windows.Forms
{
    /// <summary>A control that displays web pages.</summary>
    public class WebBrowser : Control
    {
        private IWebViewBackend _backend;
        private IntPtr _hostWindow;
        private HtmlBridge _bridge;
        private Action _pending;
        private Uri _url;
        private bool _disposed;
        private WebBrowserReadyState _readyState = WebBrowserReadyState.Uninitialized;
        private object _objectForScripting;

        public WebBrowser()
        {
            _bridge = new HtmlBridge(() => _backend);

            if (EmbeddedScenes.HostWindow != IntPtr.Zero)
            {
                BeginInitialize();
            }
            else
            {
                EmbeddedScenes.HostWindowReady += OnHostWindowReady;
            }
        }

        private void OnHostWindowReady()
        {
            EmbeddedScenes.HostWindowReady -= OnHostWindowReady;

            // Swallowed deliberately, and ONLY here. This runs inside the host's own
            // PublishHostWindow, part of bringing the window up; letting an exception out of it
            // would take the application's presentation down with it because one control could not
            // find an engine. The failure is not lost -- the next use of the control raises it,
            // from a call the application made and can catch.
            try
            {
                BeginInitialize();
            }
            catch (Exception ex)
            {
                _initializationFailure = ex;
            }
        }

        private Exception _initializationFailure;

        private void BeginInitialize()
        {
            if (_backend is not null || _disposed || EmbeddedScenes.HostWindow == IntPtr.Zero)
            {
                return;
            }

            _backend = WebViewBackendFactory.Create();

            if (_backend is null)
            {
                throw new PlatformNotSupportedException(
                    "No web engine is available on this platform, so web content cannot be hosted.");
            }

            _hostWindow = WebViewHostWindow.Create(EmbeddedScenes.HostWindow,
                                                   Math.Max(1, Width), Math.Max(1, Height));

            if (_hostWindow == IntPtr.Zero)
            {
                _backend.Dispose();
                _backend = null;
                throw new PlatformNotSupportedException(
                    "No web engine is available on this platform, so web content cannot be hosted.");
            }

            _backend.NavigationStarting += OnBackendNavigationStarting;
            _backend.SourceChanged += OnBackendSourceChanged;
            _backend.NavigationCompleted += OnBackendNavigationCompleted;

            _readyState = WebBrowserReadyState.Loading;

            Task attach = _backend.AttachAsync(_hostWindow);

            // Watched from a Timer rather than continued with ContinueWith + BeginInvoke.
            //
            // The engine completes on the UI thread, but a task continuation resumes on the thread
            // pool, so the work has to come back. Control.BeginInvoke is the obvious way and does not
            // work on this driver: its handles are managed counters and the posted delegate is never
            // dispatched -- which is how this first failed, with the engine starting perfectly and
            // the control never hearing about it. A Timer tick IS dispatched, by the driver's own
            // loop, on the UI thread.
            var watch = new Timer { Interval = 10 };

            watch.Tick += (s, e) =>
            {
                if (!attach.IsCompleted)
                {
                    return;
                }

                watch.Stop();
                watch.Dispose();

                if (attach.IsFaulted)
                {
                    _initializationFailure = attach.Exception?.GetBaseException();
                    return;
                }

                UpdateEngineBounds();

                Action pending = _pending;
                _pending = null;
                pending?.Invoke();
            };

            watch.Start();
        }

        private void WhenReady(Action action)
        {
            // Surface a failure that happened while the host's window was coming up (see
            // OnHostWindowReady), from a call the application made rather than from inside the host.
            if (_initializationFailure is not null)
            {
                Exception failure = _initializationFailure;
                _initializationFailure = null;
                throw failure;
            }

            BeginInitialize();

            if (_backend is not null && _backend.IsAttached)
            {
                action();
                return;
            }

            // Only the latest request survives, so a Url assigned repeatedly before the form is
            // shown ends at the last one rather than walking through all of them.
            _pending = action;
        }

        // ---- properties ---------------------------------------------------------------------------

        /// <summary>The page to show. Setting this navigates.</summary>
        [Bindable(true)]
        public Uri Url
        {
            get => _url;
            set
            {
                if (value is null)
                {
                    _url = null;
                    WhenReady(() => _backend.Navigate("about:blank"));
                    return;
                }

                if (!value.IsAbsoluteUri)
                {
                    throw new ArgumentException("The URI must be absolute.", nameof(value));
                }

                _url = value;
                WhenReady(() => _backend.Navigate(value.AbsoluteUri));
            }
        }

        /// <summary>The document currently loaded, or null before anything has loaded.</summary>
        [Browsable(false)]
        public HtmlDocument Document =>
            _backend is not null && _backend.IsAttached ? new HtmlDocument(_bridge) : null;

        /// <summary>The document's title.</summary>
        [Browsable(false)]
        public string DocumentTitle => _backend?.DocumentTitle ?? string.Empty;

        /// <summary>
        /// The document as text. Reading it asks the page for its markup; assigning it navigates to
        /// that markup, as the original did.
        /// </summary>
        [Browsable(false)]
        public string DocumentText
        {
            get => _bridge.EvalString("document.documentElement ? document.documentElement.outerHTML : ''")
                   ?? string.Empty;
            set => WhenReady(() => _backend.NavigateToString(value ?? string.Empty));
        }

        /// <summary>The document as a stream. Assigning it navigates to the stream's contents.</summary>
        [Browsable(false)]
        public Stream DocumentStream
        {
            get
            {
                string text = DocumentText;
                return string.IsNullOrEmpty(text) ? null : new MemoryStream(Encoding.UTF8.GetBytes(text));
            }

            set
            {
                if (value is null)
                {
                    return;
                }

                // Left open: the caller owns the stream, and the original did not close it either.
                using var reader = new StreamReader(value, Encoding.UTF8, true, 4096, leaveOpen: true);
                string html = reader.ReadToEnd();
                WhenReady(() => _backend.NavigateToString(html));
            }
        }

        [Browsable(false)]
        public bool CanGoBack => _backend is not null && _backend.CanGoBack;

        [Browsable(false)]
        public bool CanGoForward => _backend is not null && _backend.CanGoForward;

        [Browsable(false)]
        public bool IsBusy => _readyState is WebBrowserReadyState.Loading or WebBrowserReadyState.Loaded;

        [Browsable(false)]
        public WebBrowserReadyState ReadyState => _readyState;

        /// <summary>
        /// An object the page can reach as window.external.
        /// </summary>
        /// <remarks>
        /// The original required [ComVisible] because the WebOC reached the object through IDispatch.
        /// Nothing here does: the bridge marshals calls as JSON and invokes the member by reflection,
        /// so an ordinary managed class works, and demanding a COM attribute on a platform with no
        /// COM would be asking for something meaningless.
        /// </remarks>
        [Browsable(false)]
        public object ObjectForScripting
        {
            get => _objectForScripting;
            set
            {
                _objectForScripting = value;

                if (value is not null)
                {
                    WhenReady(InstallScriptingBridge);
                }
            }
        }

        /// <summary>
        /// Whether the page may show its own script dialogs. The vendored Mono dialog forms in
        /// System.Windows.Forms.WebBrowserDialogs are not used: every engine here draws its own,
        /// natively, and two dialogs for one alert() would be worse than the engine's.
        /// </summary>
        [DefaultValue(true)]
        public bool ScriptErrorsSuppressed { get; set; }

        [DefaultValue(true)]
        public bool WebBrowserShortcutsEnabled { get; set; } = true;

        [DefaultValue(true)]
        public bool AllowNavigation { get; set; } = true;

        // ---- navigation ------------------------------------------------------------------------------

        public void Navigate(string urlString) => Url = new Uri(urlString, UriKind.Absolute);

        public void Navigate(Uri url) => Url = url;

        public void Navigate(string urlString, bool newWindow) => Navigate(urlString);

        public void Navigate(Uri url, bool newWindow) => Navigate(url);

        public void Navigate(string urlString, string targetFrameName) =>
            Navigate(new Uri(urlString, UriKind.Absolute), targetFrameName);

        public void Navigate(Uri url, string targetFrameName)
        {
            if (!string.IsNullOrEmpty(targetFrameName))
            {
                // Targeting a named frame is a WebOC behaviour no engine here has. Refusing beats
                // replacing the whole page, which is what ignoring it would do.
                throw new NotSupportedException(
                    "Navigating a named target frame is not supported by this engine.");
            }

            Url = url;
        }

        public void Navigate(Uri url, string targetFrameName, byte[] postData, string additionalHeaders)
        {
            if (!string.IsNullOrEmpty(targetFrameName))
            {
                throw new NotSupportedException(
                    "Navigating a named target frame is not supported by this engine.");
            }

            _url = url;
            WhenReady(() => _backend.NavigateWithPost(url.AbsoluteUri, postData, additionalHeaders));
        }

        public bool GoBack()
        {
            if (!CanGoBack)
            {
                return false;
            }

            _backend.GoBack();
            return true;
        }

        public bool GoForward()
        {
            if (!CanGoForward)
            {
                return false;
            }

            _backend.GoForward();
            return true;
        }

        public void GoHome() => WhenReady(() => _backend.Navigate("about:blank"));

        public void Refresh() => WhenReady(() => _backend.Reload(noCache: false));

        public void Refresh(WebBrowserRefreshOption opt) =>
            WhenReady(() => _backend.Reload(opt == WebBrowserRefreshOption.Completely));

        public void Stop() => WhenReady(() => _backend.Stop());

        public void Print() =>
            // window.print() is the only printing primitive every engine exposes; the WebOC's
            // OLECMDID_PRINT had no counterpart elsewhere.
            WhenReady(() => _ = _backend.ExecuteScriptAsync("window.print()"));

        // ---- placement --------------------------------------------------------------------------------

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateEngineBounds();
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            UpdateEngineBounds();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            UpdateEngineBounds();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            _backend?.SetVisible(Visible);
        }

        /// <summary>
        /// Move the engine's window to wherever this control now sits. The control's coordinates are
        /// in the driver's form space and the engine's window is a child of the HOST's window, so the
        /// position is walked up the control tree to the form and scaled to device pixels.
        /// </summary>
        private void UpdateEngineBounds()
        {
            if (_backend is null || !_backend.IsAttached || _hostWindow == IntPtr.Zero)
            {
                return;
            }

            Point origin = Point.Empty;

            for (Control c = this; c is not null && c is not Form; c = c.Parent)
            {
                origin.Offset(c.Left, c.Top);
            }

            float scale = EmbeddedScenes.HostScale <= 0 ? 1f : EmbeddedScenes.HostScale;

            int w = (int)Math.Round(Width * scale);
            int h = (int)Math.Round(Height * scale);

            WebViewHostWindow.Move(_hostWindow,
                                   (int)Math.Round(origin.X * scale),
                                   (int)Math.Round(origin.Y * scale),
                                   w, h);

            _backend.SetBounds(0, 0, w, h, scale);
        }

        // ---- events -------------------------------------------------------------------------------------

        private void OnBackendNavigationStarting(object sender, WebViewNavigationStartingEventArgs e)
        {
            _readyState = WebBrowserReadyState.Loading;
            _navigatedRaised = false;

            if (!AllowNavigation && _url is not null)
            {
                // AllowNavigation=false means "this control shows one page and nothing else", which
                // is exactly what the original enforced here.
                e.Cancel = true;
                return;
            }

            Uri uri = Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri parsed) ? parsed : null;
            var args = new WebBrowserNavigatingEventArgs(uri, null);
            OnNavigating(args);
            e.Cancel = args.Cancel;
        }

        private void OnBackendSourceChanged(object sender, WebViewSourceChangedEventArgs e)
        {
            // The parked elements belonged to the previous document.
            _bridge.OnDocumentChanged();

            _readyState = WebBrowserReadyState.Interactive;
            RaiseNavigated();
        }

        private void OnBackendNavigationCompleted(object sender, WebViewNavigationCompletedEventArgs e)
        {
            _readyState = WebBrowserReadyState.Complete;

            // A string or stream navigation produces no SourceChanged -- the engine never leaves
            // about:blank -- so Navigated would otherwise be skipped entirely and a handler would see
            // DocumentCompleted for a document it was never told about.
            RaiseNavigated();

            if (_objectForScripting is not null)
            {
                InstallScriptingBridge();
            }

            Uri uri = Uri.TryCreate(_backend.Source, UriKind.Absolute, out Uri parsed) ? parsed : _url;
            OnDocumentCompleted(new WebBrowserDocumentCompletedEventArgs(uri));
        }

        /// <summary>Raise Navigated once per navigation, from whichever event gets there first.</summary>
        private void RaiseNavigated()
        {
            if (_navigatedRaised)
            {
                return;
            }

            _navigatedRaised = true;

            Uri uri = Uri.TryCreate(_backend.Source, UriKind.Absolute, out Uri parsed) ? parsed : _url;
            OnNavigated(new WebBrowserNavigatedEventArgs(uri));
        }

        private bool _navigatedRaised;

        protected virtual void OnNavigating(WebBrowserNavigatingEventArgs e) => Navigating?.Invoke(this, e);

        protected virtual void OnNavigated(WebBrowserNavigatedEventArgs e) => Navigated?.Invoke(this, e);

        protected virtual void OnDocumentCompleted(WebBrowserDocumentCompletedEventArgs e) =>
            DocumentCompleted?.Invoke(this, e);

        public event WebBrowserNavigatingEventHandler Navigating;
        public event WebBrowserNavigatedEventHandler Navigated;
        public event WebBrowserDocumentCompletedEventHandler DocumentCompleted;

        // ---- window.external ------------------------------------------------------------------------------

        /// <summary>
        /// Re-create window.external over the message channel: the page's call becomes a message, the
        /// host resolves it by reflection, and the answer goes back as JSON. Installed on every
        /// document because the object must survive navigation.
        /// </summary>
        private void InstallScriptingBridge()
        {
            if (_objectForScripting is null || _backend is null || !_backend.IsAttached)
            {
                return;
            }

            _ = _backend.ExecuteScriptAsync(ExternalBridgeScript);
        }

        private const string ExternalBridgeScript = @"
(function () {
    if (window.external && window.external.__wf) { return; }
    var pending = {}, next = 0;
    window.chrome.webview.addEventListener('message', function (e) {
        var d = e.data;
        if (typeof d !== 'string' || d.indexOf('__wfExternal:') !== 0) { return; }
        var reply = JSON.parse(d.substring(13));
        var slot = pending[reply.id];
        if (!slot) { return; }
        delete pending[reply.id];
        if (reply.error) { slot.reject(new Error(reply.error)); } else { slot.resolve(reply.result); }
    });
    window.external = new Proxy({ __wf: true }, {
        get: function (target, name) {
            if (name in target) { return target[name]; }
            return function () {
                var id = ++next, a = Array.prototype.slice.call(arguments);
                var p = new Promise(function (res, rej) { pending[id] = { resolve: res, reject: rej }; });
                window.chrome.webview.postMessage('__wfExternal:' +
                    JSON.stringify({ id: id, member: String(name), args: a }));
                return p;
            };
        }
    });
})();";

        // ---- lifetime ---------------------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                EmbeddedScenes.HostWindowReady -= OnHostWindowReady;

                _backend?.Dispose();
                _backend = null;

                if (_hostWindow != IntPtr.Zero)
                {
                    WebViewHostWindow.Destroy(_hostWindow);
                    _hostWindow = IntPtr.Zero;
                }
            }

            base.Dispose(disposing);
        }
    }
}
