// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// IWebViewBackend -- the cross-platform seam for web hosting, on EVERY platform.
//
// WPF's WebBrowser was a wrapper over the IE WebOC: a shdocvw ActiveX control hosted through
// ActiveXHost/HwndHost and driven by IWebBrowser2. That is Windows-only three times over (COM
// activation, an OLE in-place-activation state machine, and SetParent on a child HWND), and it is
// also a dead engine. WinForms had no web control here at all -- Mono's was pruned when swf/ was
// vendored. So every head now owns an IWebViewBackend instead: the platform's own web engine,
// wrapped in the smallest contract that all of them can actually honour.
//
// Implementations: WindowsWebViewBackend (WebView2 / Edge), MacWebViewBackend and IOSWebViewBackend
// (WKWebView), LinuxWebViewBackend (WPE WebKit on a wl_subsurface), AndroidWebViewBackend
// (android.webkit.WebView), BrowserWebViewBackend (an <iframe> in the host document).
//
// PRESENTATION IS AN OVERLAY, NOT A COMPOSITED TEXTURE. Unlike IMediaBackend -- which pulls decoded
// frames and hands them to the compositor -- a web view is the engine's own native view, placed
// above the WebGPU surface and moved to follow the control's arrange bounds. That is not a shortcut:
// an <iframe> cannot be read back at all, and WKWebView has no supported continuous offscreen
// capture, so a composited design could not serve two of the six heads. It also means the engine
// keeps its own input, IME, scrolling, hit-testing and accessibility, which is the overwhelming
// majority of what a browser does. The cost is the airspace rule WPF already documents for this
// control: no WPF transform, opacity or z-order applies to it, and it cannot live inside a Popup
// (WebBrowser has thrown CannotBeInsidePopup since long before this fork).
//
// EVERYTHING IS ASYNC because the engines disagree about creation: WebView2 builds its environment
// and controller through completion handlers, while WKWebView is ready the moment it is allocated.
// Modelling the union as async and letting the synchronous engines complete immediately keeps one
// shape; modelling it as sync would mean blocking a UI thread on Windows, which deadlocks.
//
// The factory returns null where no backend exists for the platform. Callers must treat that as
// "this head cannot host web content" and say so -- a control that silently renders nothing is
// worse than one that throws.
//

using System;
using System.Threading.Tasks;

namespace MS.Internal.Interop.WebView
{
    /// <summary>
    /// A platform web engine hosted as a native overlay view. One instance backs one control.
    /// All members must be called on the thread that created the instance; events are raised there
    /// too, so consumers never marshal.
    /// </summary>
    internal interface IWebViewBackend : IDisposable
    {
        // ---- lifetime and placement ----------------------------------------------------------

        /// <summary>How this backend puts pixels on the screen. Every backend today is
        /// <see cref="WebViewPresentation.Overlay"/>.</summary>
        WebViewPresentation Presentation { get; }

        /// <summary>
        /// Create the engine and its native view above <paramref name="ownerWindow"/>, whose meaning
        /// is the platform's window handle in the same sense NativePlatform.CreateWindowSurface uses
        /// it (an HWND on Windows, an NSView* on macOS, a wl_surface* on Linux, and so on).
        /// Completes once the engine is ready to navigate.
        /// </summary>
        Task AttachAsync(IntPtr ownerWindow);

        /// <summary>Tear down the native view, leaving the instance disposable but unusable.</summary>
        void Detach();

        /// <summary>True once <see cref="AttachAsync"/> has completed and navigation is possible.</summary>
        bool IsAttached { get; }

        /// <summary>
        /// Position the overlay in DEVICE PIXELS relative to the owner window's client area -- the
        /// units ActiveXHost.OnWindowPositionChanged already delivers on every head.
        /// <paramref name="scale"/> is the backing scale, which engines that think in points
        /// (WKWebView) need in order to lay the page out at the right size.
        /// </summary>
        void SetBounds(int x, int y, int width, int height, double scale);

        /// <summary>Show or hide the overlay, for Visibility and for a control that is unloaded but
        /// not disposed.</summary>
        void SetVisible(bool visible);

        // ---- navigation -----------------------------------------------------------------------

        void Navigate(string uri);
        void NavigateToString(string htmlContent);

        /// <summary>
        /// Navigate with an HTTP body and/or extra headers, for WebBrowser.Navigate's postData and
        /// additionalHeaders overloads. Backends that cannot express this must throw rather than
        /// quietly downgrade to a GET, or a form post silently becomes a page load.
        /// </summary>
        void NavigateWithPost(string uri, byte[] postData, string additionalHeaders);

        void Reload(bool noCache);
        void Stop();
        void GoBack();
        void GoForward();

        bool CanGoBack { get; }
        bool CanGoForward { get; }

        /// <summary>The document's current URI, or null before the first navigation.</summary>
        string Source { get; }

        string DocumentTitle { get; }

        // ---- scripting and messaging ----------------------------------------------------------

        /// <summary>
        /// Evaluate JavaScript in the top frame and return its result as JSON ("null" when the
        /// expression yields undefined). This is the primitive everything else is built on:
        /// WebBrowser.InvokeScript composes a call expression and evaluates it here, and the
        /// WinForms HtmlDocument object model is entirely script underneath.
        /// </summary>
        Task<string> ExecuteScriptAsync(string javaScript);

        /// <summary>
        /// Run script in every document before its own scripts run. Used to install the host bridge
        /// (window.chrome.webview, and window.external for WebBrowser.ObjectForScripting) so it is
        /// present no matter where the page navigates.
        /// </summary>
        void AddScriptToExecuteOnDocumentCreated(string javaScript);

        void PostWebMessageAsJson(string webMessageAsJson);
        void PostWebMessageAsString(string webMessageAsString);

        // ---- configuration ---------------------------------------------------------------------

        /// <summary>Push the current settings to the engine. Called once after attach and again
        /// whenever a setting changes.</summary>
        void ApplySettings(WebViewSettings settings);

        /// <summary>Zoom factor, 1.0 being unscaled.</summary>
        double ZoomFactor { get; set; }

        /// <summary>Drop cookies, cache and other origin-scoped storage for this profile.</summary>
        Task ClearBrowsingDataAsync();

        // ---- events (raised on the creating thread) ---------------------------------------------

        event EventHandler<WebViewNavigationStartingEventArgs> NavigationStarting;
        event EventHandler<WebViewSourceChangedEventArgs> SourceChanged;
        event EventHandler ContentLoading;
        event EventHandler<WebViewNavigationCompletedEventArgs> NavigationCompleted;
        event EventHandler<WebViewMessageReceivedEventArgs> WebMessageReceived;
        event EventHandler<WebViewNewWindowRequestedEventArgs> NewWindowRequested;
        event EventHandler<WebViewDownloadStartingEventArgs> DownloadStarting;
        event EventHandler DocumentTitleChanged;
        event EventHandler<WebViewProcessFailedEventArgs> ProcessFailed;
    }
}
