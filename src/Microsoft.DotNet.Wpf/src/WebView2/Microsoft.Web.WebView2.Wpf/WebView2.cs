// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Microsoft.Web.WebView2.Wpf.WebView2 -- the WPF WebView2 control, over this fork's engine seam.
//
// Same identity as the real assembly (see the csproj), so an application's existing
// PackageReference and XAML keep working unchanged; same shape too -- it derives from HwndHost, as
// the real control does, which is what lets it sit in a WPF tree and be positioned by layout.
//
// The hosting arrangement is the one System.Windows.Controls.WebBrowser uses in this fork: the
// engine is given an EMPTY child window of its own and fills it edge to edge, so HwndHost keeps
// doing the clipping, the moves and the DPI transition, and the engine never needs to know where
// the control sits on screen. See WebViewHostWindow for why the indirection is worth a window.
//
// Initialisation is asynchronous and, crucially, LAZY in the same way the real control is: an
// application may set Source in XAML long before the control is shown, and HwndHost does not build
// its window until then. Requests made in the meantime are remembered and replayed once the engine
// exists, which is what makes `<wv2:WebView2 Source="https://..."/>` work at all.
//

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using MS.Internal.Interop.WebView;

namespace Microsoft.Web.WebView2.Wpf
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
    public class WebView2 : HwndHost
    {
        private IWebViewBackend _backend;
        private IntPtr _hostWindow;
        private CoreWebView2 _coreWebView2;
        private Task _ready;
        private Action _pending;
        private bool _disposed;

        public WebView2()
        {
        }

        // ---- dependency properties ---------------------------------------------------------------

        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(Uri), typeof(WebView2),
            new PropertyMetadata(null, OnSourceChanged));

        /// <summary>The page to show. Setting this navigates.</summary>
        public Uri Source
        {
            get => (Uri)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (WebView2)d;

            if (e.NewValue is Uri uri)
            {
                control.WhenReady(() => control._backend.Navigate(uri.AbsoluteUri));
            }
        }

        public static readonly DependencyProperty ZoomFactorProperty = DependencyProperty.Register(
            nameof(ZoomFactor), typeof(double), typeof(WebView2),
            new PropertyMetadata(1.0, OnZoomFactorChanged));

        public double ZoomFactor
        {
            get => (double)GetValue(ZoomFactorProperty);
            set => SetValue(ZoomFactorProperty, value);
        }

        private static void OnZoomFactorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (WebView2)d;

            control.WhenReady(() =>
            {
                control._backend.ZoomFactor = (double)e.NewValue;
                control.ZoomFactorChanged?.Invoke(control, EventArgs.Empty);
            });
        }

        public static readonly DependencyProperty DefaultBackgroundColorProperty =
            DependencyProperty.Register(nameof(DefaultBackgroundColor), typeof(Color), typeof(WebView2),
                                        new PropertyMetadata(Colors.White));

        /// <summary>
        /// The colour shown before a page paints. Remembered on every head; only the Windows engine
        /// exposes a switch for it, so elsewhere the engine's own default is what is seen.
        /// </summary>
        public Color DefaultBackgroundColor
        {
            get => (Color)GetValue(DefaultBackgroundColorProperty);
            set => SetValue(DefaultBackgroundColorProperty, value);
        }

        /// <summary>Properties used when the engine is created. Must be set before initialisation.</summary>
        public CoreWebView2CreationProperties CreationProperties { get; set; }

        /// <summary>
        /// The engine, or null until initialisation has completed. Applications normally await
        /// <see cref="EnsureCoreWebView2Async"/> or handle
        /// <see cref="CoreWebView2InitializationCompleted"/> before touching this.
        /// </summary>
        public CoreWebView2 CoreWebView2 => _coreWebView2;

        public bool CanGoBack => _backend is not null && _backend.CanGoBack;

        public bool CanGoForward => _backend is not null && _backend.CanGoForward;

        // ---- hosting -------------------------------------------------------------------------------

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _backend = WebViewBackendFactory.Create();

            if (_backend is null)
            {
                throw new PlatformNotSupportedException(
                    "No web engine is available on this platform, so web content cannot be hosted.");
            }

            _hostWindow = WebViewHostWindow.Create(hwndParent.Handle,
                                                   (int)Math.Max(1, ActualWidth),
                                                   (int)Math.Max(1, ActualHeight));

            if (_hostWindow == IntPtr.Zero)
            {
                _backend.Dispose();
                _backend = null;
                throw new PlatformNotSupportedException(
                    "No web engine is available on this platform, so web content cannot be hosted.");
            }

            _ready = _backend.AttachAsync(_hostWindow);
            _coreWebView2 = new CoreWebView2(_backend);

            // Continued onto the Dispatcher rather than a synchronization-context TaskScheduler: the
            // window is built during the first measure pass, which can run before Dispatcher.Run has
            // installed that context, and asking for the scheduler then throws.
            _ready.ContinueWith(t => Dispatcher.BeginInvoke((Action)(() =>
            {
                CoreWebView2InitializationCompleted?.Invoke(
                    this,
                    new CoreWebView2InitializationCompletedEventArgs(t.Exception?.GetBaseException()));

                if (t.IsFaulted)
                {
                    return;
                }

                Action pending = _pending;
                _pending = null;
                pending?.Invoke();
            })), TaskScheduler.Default);

            return new HandleRef(this, _hostWindow);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _backend?.Dispose();
            _backend = null;
            _coreWebView2 = null;

            if (_hostWindow != IntPtr.Zero)
            {
                WebViewHostWindow.Destroy(_hostWindow);
                _hostWindow = IntPtr.Zero;
            }
        }

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            // HwndHost moves the host window; the engine then fills it.
            base.OnWindowPositionChanged(rcBoundingBox);

            _backend?.SetBounds(0, 0, (int)rcBoundingBox.Width, (int)rcBoundingBox.Height, BackingScale());
        }

        private double BackingScale()
        {
            try
            {
                // VisualTreeHelper.GetDpi, not HwndHost's own GetDpi: the latter is internal to
                // PresentationFramework and this assembly is outside it.
                return VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
            catch
            {
                // A control that has not been through a DPI transition has no scale to report; 1.0
                // is the right answer and is what every non-HiDPI head uses anyway.
                return 1.0;
            }
        }

        // ---- initialisation --------------------------------------------------------------------------

        /// <summary>
        /// Ensure the engine exists. Completes immediately if it already does.
        /// </summary>
        /// <remarks>
        /// The engine cannot be built before the control has a parent window, and HwndHost only
        /// builds one when the control is first shown. Calling this on a control that is not in a
        /// visual tree therefore cannot succeed, and says so rather than hanging.
        /// </remarks>
        public Task EnsureCoreWebView2Async(CoreWebView2Environment environment = null)
        {
            if (_ready is not null)
            {
                return _ready;
            }

            if (!IsLoaded && PresentationSource.FromVisual(this) is null)
            {
                return Task.FromException(new InvalidOperationException(
                    "The control must be in a visual tree before its engine can be created; " +
                    "await this after the control has loaded."));
            }

            // Being in a tree but not yet built means the window is coming on a later layout pass.
            return _ready ?? Task.CompletedTask;
        }

        private void WhenReady(Action action)
        {
            if (_backend is not null && _backend.IsAttached)
            {
                action();
                return;
            }

            // Only the latest request survives, so a Source set three times before the control is
            // shown ends at the third page rather than walking through all three.
            _pending = action;
        }

        // ---- navigation ---------------------------------------------------------------------------------

        public void Reload() => WhenReady(() => _backend.Reload(noCache: false));

        public void GoBack() => WhenReady(() => _backend.GoBack());

        public void GoForward() => WhenReady(() => _backend.GoForward());

        public void Stop() => WhenReady(() => _backend.Stop());

        public void NavigateToString(string htmlContent) =>
            WhenReady(() => _backend.NavigateToString(htmlContent));

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            if (_backend is null || !_backend.IsAttached)
            {
                return Task.FromException<string>(new InvalidOperationException(
                    "The engine is not ready; await EnsureCoreWebView2Async first."));
            }

            return _backend.ExecuteScriptAsync(javaScript);
        }

        // ---- events -------------------------------------------------------------------------------------

        public event EventHandler<CoreWebView2InitializationCompletedEventArgs> CoreWebView2InitializationCompleted;

        public event EventHandler<CoreWebView2NavigationStartingEventArgs> NavigationStarting
        {
            add => Forward(h => _coreWebView2.NavigationStarting += value);
            remove { if (_coreWebView2 is not null) { _coreWebView2.NavigationStarting -= value; } }
        }

        public event EventHandler<CoreWebView2NavigationCompletedEventArgs> NavigationCompleted
        {
            add => Forward(h => _coreWebView2.NavigationCompleted += value);
            remove { if (_coreWebView2 is not null) { _coreWebView2.NavigationCompleted -= value; } }
        }

        public event EventHandler<CoreWebView2SourceChangedEventArgs> SourceChanged
        {
            add => Forward(h => _coreWebView2.SourceChanged += value);
            remove { if (_coreWebView2 is not null) { _coreWebView2.SourceChanged -= value; } }
        }

        public event EventHandler<CoreWebView2WebMessageReceivedEventArgs> WebMessageReceived
        {
            add => Forward(h => _coreWebView2.WebMessageReceived += value);
            remove { if (_coreWebView2 is not null) { _coreWebView2.WebMessageReceived -= value; } }
        }

        public event EventHandler<object> ContentLoading
        {
            add => Forward(h => _coreWebView2.ContentLoading += value);
            remove { if (_coreWebView2 is not null) { _coreWebView2.ContentLoading -= value; } }
        }

        public event EventHandler ZoomFactorChanged;

        /// <summary>
        /// Attach a handler to the engine, now or as soon as it exists.
        /// </summary>
        /// <remarks>
        /// Applications routinely subscribe in a constructor or in XAML, long before the engine is
        /// built. Dropping those handlers would lose the events for the control's very first
        /// navigation, which is usually the only one that matters.
        /// </remarks>
        private void Forward(Action<object> subscribe)
        {
            if (_coreWebView2 is not null)
            {
                subscribe(null);
                return;
            }

            EventHandler<CoreWebView2InitializationCompletedEventArgs> once = null;

            once = (s, e) =>
            {
                CoreWebView2InitializationCompleted -= once;

                if (e.IsSuccess)
                {
                    subscribe(null);
                }
            };

            CoreWebView2InitializationCompleted += once;
        }

        // ---- lifetime -------------------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _backend?.Dispose();
                _backend = null;
                _coreWebView2 = null;
            }

            base.Dispose(disposing);
        }
    }
}
