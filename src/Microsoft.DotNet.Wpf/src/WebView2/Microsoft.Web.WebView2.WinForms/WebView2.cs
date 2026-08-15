// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Microsoft.Web.WebView2.WinForms.WebView2 -- the WinForms WebView2 control, over this fork's engine
// seam.
//
// Same identity as the real assembly, so an application's existing PackageReference and designer
// code bind to it unchanged, and the same shape: a Control that hosts the engine.
//
// THE HANDLE PROBLEM, which is what makes this different from the WPF control. The real control
// passes Control.Handle to CreateCoreWebView2Controller as the parent HWND. On this stack that
// handle is not an OS window at all -- XplatUIWebGpu mints managed handles with `next_handle++` and
// keeps the window tree in managed objects, because every WinForms control here is painted into a
// bitmap and presented through WebGPU. Handing `(IntPtr)7` to a web engine cannot work.
//
// The only real OS window in the process is the host's, published through EmbeddedScenes.HostWindow
// (this is exactly the problem ElementHost solved for hosted WPF, and it is solved the same way).
// So the engine is parented to a child window of THAT, and the control positions it in host
// coordinates -- translating from its own position within the form on every move, resize and scroll.
//
// The consequence is the airspace rule the seam documents generally: the engine's window is a real
// native window over the WebGPU surface, so it is not clipped by WinForms' painted controls and does
// not participate in their z-order. It follows the control's rectangle, and that is all.
//

using System;
using System.ComponentModel;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Microsoft.Web.WebView2.WinForms
{
    /// <summary>Properties used when the control creates its engine.</summary>
    public class CoreWebView2CreationProperties
    {
        public string BrowserExecutableFolder { get; set; }

        public string UserDataFolder { get; set; }

        public string Language { get; set; }

        public string AdditionalBrowserArguments { get; set; }
    }

    /// <summary>A control that hosts web content.</summary>
    public class WebView2 : Control
    {
        private CoreWebView2Host _host;
        private Task _ready;
        private Action _pending;
        private Uri _source;
        private bool _disposed;

        public WebView2()
        {
            // The engine cannot exist before the host's real window does. In a WinForms app that
            // window is created by the host as the form comes up, which may be after this control.
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
            if (Environment.GetEnvironmentVariable("WV2_TRACE") == "1")
                Console.WriteLine("[wv2] HostWindowReady, host=0x" + EmbeddedScenes.HostWindow.ToString("x"));
            EmbeddedScenes.HostWindowReady -= OnHostWindowReady;
            BeginInitialize();
        }

        // ---- properties ---------------------------------------------------------------------------

        /// <summary>The page to show. Setting this navigates.</summary>
        public Uri Source
        {
            get => _source;
            set
            {
                _source = value;

                if (value is not null)
                {
                    WhenReady(() => _host.CoreWebView2.Navigate(value.AbsoluteUri));
                }
            }
        }

        /// <summary>The engine, or null until initialisation has completed.</summary>
        [Browsable(false)]
        public CoreWebView2 CoreWebView2 => _host?.CoreWebView2;

        [Browsable(false)]
        public bool CanGoBack => _host is not null && _host.CoreWebView2.CanGoBack;

        [Browsable(false)]
        public bool CanGoForward => _host is not null && _host.CoreWebView2.CanGoForward;

        public double ZoomFactor
        {
            get => _host?.ZoomFactor ?? 1.0;
            set => WhenReady(() => _host.ZoomFactor = value);
        }

        /// <summary>
        /// The colour shown before a page paints. Remembered on every head; only the Windows engine
        /// exposes a switch for it.
        /// </summary>
        public Color DefaultBackgroundColor { get; set; } = Color.White;

        public CoreWebView2CreationProperties CreationProperties { get; set; }

        // ---- initialisation -------------------------------------------------------------------------

        /// <summary>Ensure the engine exists. Completes immediately if it already does.</summary>
        /// <remarks>
        /// This must NOT fail merely because the host window does not exist yet. On this stack the
        /// real window is created by the presentation host, which runs AFTER Form.Shown -- so the
        /// most natural place for an application to await this is exactly the moment when the window
        /// is still missing. The returned task therefore completes whenever initialisation finishes,
        /// however long the window takes to arrive.
        /// </remarks>
        public Task EnsureCoreWebView2Async(CoreWebView2Environment environment = null)
        {
            BeginInitialize();
            return _initialized.Task;
        }

        private readonly TaskCompletionSource<object> _initialized =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private void BeginInitialize()
        {
            // Nothing to do yet if the host's window has not been published; the HostWindowReady
            // subscription in the constructor brings us back here when it is.
            if (_ready is not null || _disposed || EmbeddedScenes.HostWindow == IntPtr.Zero)
            {
                return;
            }

            if (Environment.GetEnvironmentVariable("WV2_TRACE") == "1")
                Console.WriteLine("[wv2] BeginInitialize creating host");

            _host = CoreWebView2Host.Create(EmbeddedScenes.HostWindow,
                                            Math.Max(1, Width), Math.Max(1, Height));

            if (Environment.GetEnvironmentVariable("WV2_TRACE") == "1")
                Console.WriteLine("[wv2] host=" + (_host is null ? "null" : "created, hwnd=0x" + _host.HostWindow.ToString("x")));

            if (_host is null)
            {
                RaiseInitializationCompleted(new PlatformNotSupportedException(
                    "No web engine is available on this platform, so web content cannot be hosted."));
                return;
            }

            _ready = _host.Ready;

            _ready.ContinueWith(t =>
            {
                RaiseInitializationCompleted(t.Exception?.GetBaseException());

                if (t.IsFaulted)
                {
                    return;
                }

                UpdateEngineBounds();

                Action pending = _pending;
                _pending = null;
                pending?.Invoke();
            }, TaskScheduler.Default);
        }

        private void RaiseInitializationCompleted(Exception error)
        {
            // Marshalled onto the control's thread when there is one: the seam completes its task on
            // whichever thread the engine answered on, and a WinForms handler must not run there.
            void Raise()
            {
                if (error is null)
                {
                    _initialized.TrySetResult(null);
                }
                else
                {
                    _initialized.TrySetException(error);
                }

                CoreWebView2InitializationCompleted?.Invoke(
                    this, new CoreWebView2InitializationCompletedEventArgs(error));
            }

            if (InvokeRequired)
            {
                BeginInvoke((Action)Raise);
            }
            else
            {
                Raise();
            }
        }

        private void WhenReady(Action action)
        {
            if (_host is not null && _host.IsAttached)
            {
                action();
                return;
            }

            // Only the latest request survives, so a Source assigned repeatedly before the form is
            // shown ends at the last one rather than walking through all of them.
            _pending = action;
        }

        // ---- placement -------------------------------------------------------------------------------

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

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            _host?.SetVisible(Visible);
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            UpdateEngineBounds();
        }

        /// <summary>
        /// Move the engine's window to wherever this control now sits.
        /// </summary>
        /// <remarks>
        /// The control's own coordinates are in the driver's form space; the engine's window is a
        /// child of the HOST's window, so the position has to be walked up the control tree to the
        /// form and then scaled to device pixels. This is the same translation ElementHost performs
        /// for a hosted WPF tree, and it has to run on every move, resize and reparent -- a WinForms
        /// control can be scrolled by its container without either event firing on the control
        /// itself, which is why OnParentChanged is included.
        /// </remarks>
        private void UpdateEngineBounds()
        {
            if (_host is null || !_host.IsAttached)
            {
                return;
            }

            Point origin = Point.Empty;

            for (Control c = this; c is not null && c is not Form; c = c.Parent)
            {
                origin.Offset(c.Left, c.Top);
            }

            float scale = EmbeddedScenes.HostScale <= 0 ? 1f : EmbeddedScenes.HostScale;

            _host.Move((int)Math.Round(origin.X * scale),
                       (int)Math.Round(origin.Y * scale),
                       (int)Math.Round(Width * scale),
                       (int)Math.Round(Height * scale));
        }

        // ---- navigation --------------------------------------------------------------------------------

        public void Reload() => WhenReady(() => _host.CoreWebView2.Reload());

        public void GoBack() => WhenReady(() => _host.CoreWebView2.GoBack());

        public void GoForward() => WhenReady(() => _host.CoreWebView2.GoForward());

        public void Stop() => WhenReady(() => _host.CoreWebView2.Stop());

        public void NavigateToString(string htmlContent) =>
            WhenReady(() => _host.CoreWebView2.NavigateToString(htmlContent));

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            if (_host is null || !_host.IsAttached)
            {
                return Task.FromException<string>(new InvalidOperationException(
                    "The engine is not ready; await EnsureCoreWebView2Async first."));
            }

            return _host.CoreWebView2.ExecuteScriptAsync(javaScript);
        }

        // ---- events -----------------------------------------------------------------------------------

        public event EventHandler<CoreWebView2InitializationCompletedEventArgs> CoreWebView2InitializationCompleted;

        public event EventHandler<CoreWebView2NavigationStartingEventArgs> NavigationStarting
        {
            add => Forward(() => _host.CoreWebView2.NavigationStarting += value);
            remove { if (_host?.CoreWebView2 is not null) { _host.CoreWebView2.NavigationStarting -= value; } }
        }

        public event EventHandler<CoreWebView2NavigationCompletedEventArgs> NavigationCompleted
        {
            add => Forward(() => _host.CoreWebView2.NavigationCompleted += value);
            remove { if (_host?.CoreWebView2 is not null) { _host.CoreWebView2.NavigationCompleted -= value; } }
        }

        public event EventHandler<CoreWebView2SourceChangedEventArgs> SourceChanged
        {
            add => Forward(() => _host.CoreWebView2.SourceChanged += value);
            remove { if (_host?.CoreWebView2 is not null) { _host.CoreWebView2.SourceChanged -= value; } }
        }

        public event EventHandler<CoreWebView2WebMessageReceivedEventArgs> WebMessageReceived
        {
            add => Forward(() => _host.CoreWebView2.WebMessageReceived += value);
            remove { if (_host?.CoreWebView2 is not null) { _host.CoreWebView2.WebMessageReceived -= value; } }
        }

        public event EventHandler<object> ContentLoading
        {
            add => Forward(() => _host.CoreWebView2.ContentLoading += value);
            remove { if (_host?.CoreWebView2 is not null) { _host.CoreWebView2.ContentLoading -= value; } }
        }

        /// <summary>
        /// Attach a handler to the engine, now or as soon as it exists -- applications subscribe in
        /// InitializeComponent, long before the form is shown, and dropping those handlers would
        /// lose the events for the control's first navigation.
        /// </summary>
        private void Forward(Action subscribe)
        {
            if (_host?.CoreWebView2 is not null)
            {
                subscribe();
                return;
            }

            EventHandler<CoreWebView2InitializationCompletedEventArgs> once = null;

            once = (s, e) =>
            {
                CoreWebView2InitializationCompleted -= once;

                if (e.IsSuccess)
                {
                    subscribe();
                }
            };

            CoreWebView2InitializationCompleted += once;
        }

        // ---- lifetime ------------------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                EmbeddedScenes.HostWindowReady -= OnHostWindowReady;

                _host?.Dispose();
                _host = null;
            }

            base.Dispose(disposing);
        }
    }
}
