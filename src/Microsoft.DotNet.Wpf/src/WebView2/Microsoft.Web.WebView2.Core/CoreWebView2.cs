// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CoreWebView2 -- the WebView2 object model, over this fork's cross-platform engine seam.
//
// The real Microsoft.Web.WebView2.Core is a COM wrapper that only exists on Windows. This assembly
// carries the SAME identity (name, version and public key token; see the csproj) so an application's
// existing PackageReference keeps compiling and binding, and re-implements the object model on top
// of IWebViewBackend -- which means a WebView2 application runs unchanged on macOS, Linux, Android,
// iOS and in the browser, against whatever engine that head actually has.
//
// Scope, stated plainly. Everything an ordinary application touches is here and really works:
// navigation and history, script evaluation and injection, host<->web messaging, settings, the
// document title, zoom, and the events for all of it. Members whose behaviour is specific to
// Chromium and has no honest counterpart elsewhere -- the DevTools protocol, host-object
// marshalling, the process table -- are present so code compiles and binds, and THROW when called.
// That is deliberate: a silent no-op would leave an application believing it had registered a
// handler or cleared a cookie when it had not.
//

using System;
using System.Threading.Tasks;
using MS.Internal.Interop.WebView;

namespace Microsoft.Web.WebView2.Core
{
    /// <summary>
    /// The engine behind a WebView2 control. Obtained from the control's <c>CoreWebView2</c>
    /// property after <c>EnsureCoreWebView2Async</c> has completed.
    /// </summary>
    public class CoreWebView2
    {
        private readonly IWebViewBackend _backend;
        private readonly CoreWebView2Settings _settings;

        internal CoreWebView2(IWebViewBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _settings = new CoreWebView2Settings(backend);

            _backend.NavigationStarting += (s, e) =>
                NavigationStarting?.Invoke(this, new CoreWebView2NavigationStartingEventArgs(e));

            _backend.ContentLoading += (s, e) =>
                ContentLoading?.Invoke(this, EventArgs.Empty);

            _backend.SourceChanged += (s, e) =>
                SourceChanged?.Invoke(this, new CoreWebView2SourceChangedEventArgs(e));

            _backend.NavigationCompleted += (s, e) =>
                NavigationCompleted?.Invoke(this, new CoreWebView2NavigationCompletedEventArgs(e));

            _backend.WebMessageReceived += (s, e) =>
                WebMessageReceived?.Invoke(this, new CoreWebView2WebMessageReceivedEventArgs(e));

            _backend.NewWindowRequested += (s, e) =>
                NewWindowRequested?.Invoke(this, new CoreWebView2NewWindowRequestedEventArgs(e));

            _backend.DownloadStarting += (s, e) =>
                DownloadStarting?.Invoke(this, new CoreWebView2DownloadStartingEventArgs(e));

            _backend.DocumentTitleChanged += (s, e) =>
                DocumentTitleChanged?.Invoke(this, EventArgs.Empty);

            _backend.ProcessFailed += (s, e) =>
                ProcessFailed?.Invoke(this, new CoreWebView2ProcessFailedEventArgs(e));

            // HistoryChanged has no seam event of its own: every engine reports history implicitly,
            // through the same points at which CanGoBack/CanGoForward can have changed.
            _backend.NavigationCompleted += (s, e) => HistoryChanged?.Invoke(this, EventArgs.Empty);
            _backend.SourceChanged += (s, e) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        internal IWebViewBackend Backend => _backend;

        // ---- navigation ---------------------------------------------------------------------

        public string Source => _backend.Source ?? "about:blank";

        public bool CanGoBack => _backend.CanGoBack;

        public bool CanGoForward => _backend.CanGoForward;

        public string DocumentTitle => _backend.DocumentTitle ?? string.Empty;

        public void Navigate(string uri) => _backend.Navigate(uri);

        public void NavigateToString(string htmlContent) => _backend.NavigateToString(htmlContent);

        public void GoBack() => _backend.GoBack();

        public void GoForward() => _backend.GoForward();

        public void Reload() => _backend.Reload(noCache: false);

        public void Stop() => _backend.Stop();

        // ---- scripting and messaging ----------------------------------------------------------

        /// <summary>Evaluate JavaScript in the top frame; the result is JSON.</summary>
        public Task<string> ExecuteScriptAsync(string javaScript) => _backend.ExecuteScriptAsync(javaScript);

        /// <summary>
        /// Run script in every document before its own scripts run. The real API returns the id the
        /// script was registered under so it can be removed again; ids are issued here in sequence
        /// for the same purpose.
        /// </summary>
        public Task<string> AddScriptToExecuteOnDocumentCreatedAsync(string javaScript)
        {
            _backend.AddScriptToExecuteOnDocumentCreated(javaScript);
            return Task.FromResult((++_scriptId).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private int _scriptId;

        /// <summary>
        /// Remove a document-created script. Not implemented: no engine behind this seam can
        /// withdraw an injected script once a document has it, and pretending otherwise would leave
        /// the script running while the application believed it gone.
        /// </summary>
        public Task RemoveScriptToExecuteOnDocumentCreatedAsync(string id) =>
            throw new NotSupportedException(
                "Removing a document-created script is not supported by this engine.");

        public void PostWebMessageAsJson(string webMessageAsJson) =>
            _backend.PostWebMessageAsJson(webMessageAsJson);

        public void PostWebMessageAsString(string webMessageAsString) =>
            _backend.PostWebMessageAsString(webMessageAsString);

        // ---- configuration -----------------------------------------------------------------------

        public CoreWebView2Settings Settings => _settings;

        /// <summary>
        /// Drop cookies, cache and other origin-scoped storage. The real API hangs this off
        /// CoreWebView2Profile; it is offered here directly because the profile object it belongs to
        /// exposes little else this seam can honour.
        /// </summary>
        public Task ClearBrowsingDataAsync() => _backend.ClearBrowsingDataAsync();

        /// <summary>
        /// Write an image of what the view is showing into <paramref name="imageStream"/>.
        /// </summary>
        /// <remarks>
        /// A still, and only a still: an overlay contributes no pixels to the surrounding scene, so
        /// this is the one way its content can become part of a drawing -- which is what makes a web
        /// view printable, and what puts one on 3D geometry. It is not a way to composite a live
        /// view.
        /// </remarks>
        public async Task CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat imageFormat,
                                              System.IO.Stream imageStream)
        {
            if (imageStream is null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            byte[] bytes = await _backend.CapturePreviewAsync(
                imageFormat == CoreWebView2CapturePreviewImageFormat.Png).ConfigureAwait(true);

            await imageStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(true);
        }

        /// <summary>
        /// Print the current page to a PDF file. Returns true on success.
        /// </summary>
        /// <remarks>
        /// Printing a web view means rendering it at paper size with pagination, which is the
        /// browser engine's own job and is reachable on none of these backends through the small
        /// surface they expose. Returning false rather than throwing because the real one returns
        /// false for an ordinary failure (an unwritable path), so callers already handle it, and
        /// there is nothing exceptional about a head that cannot print.
        /// </remarks>
        public Task<bool> PrintToPdfAsync(string resultFilePath,
                                          CoreWebView2PrintSettings printSettings = null)
            => Task.FromResult(false);

        // ---- deliberately unimplemented ------------------------------------------------------------
        //
        // Present so existing code compiles and binds, and throwing rather than no-opping so an
        // application is never told that something happened when it did not.

        /// <summary>
        /// Expose a managed object to the page as <c>window.chrome.webview.hostObjects</c>. Not
        /// implemented: the real one marshals through COM/IDispatch, which exists on exactly one of
        /// the six heads. Use <see cref="PostWebMessageAsJson"/> and
        /// <see cref="WebMessageReceived"/>, which work everywhere.
        /// </summary>
        public void AddHostObjectToScript(string name, object rawObject) =>
            throw new NotSupportedException(
                "Host objects are not supported. Use the web-message channel instead.");

        public void RemoveHostObjectFromScript(string name) =>
            throw new NotSupportedException(
                "Host objects are not supported. Use the web-message channel instead.");

        /// <summary>
        /// Call a Chrome DevTools Protocol method. Not implemented: the protocol is Chromium's, and
        /// WebKit's equivalent is neither the same protocol nor reachable from an embedded view.
        /// </summary>
        public Task<string> CallDevToolsProtocolMethodAsync(string methodName, string parametersAsJson) =>
            throw new NotSupportedException(
                "The DevTools protocol is not available through this engine seam.");

        public void OpenDevToolsWindow() =>
            throw new NotSupportedException("Opening a DevTools window is not supported on this head.");

        /// <summary>
        /// The browser process id. Not implemented: several heads run the engine in-process, where
        /// the honest answer would be this process's own id and would mislead any caller using it to
        /// find the engine.
        /// </summary>
        public uint BrowserProcessId =>
            throw new NotSupportedException("The engine does not expose a browser process id.");

        // ---- events ----------------------------------------------------------------------------------

        public event EventHandler<CoreWebView2NavigationStartingEventArgs> NavigationStarting;
        public event EventHandler<object> ContentLoading;
        public event EventHandler<CoreWebView2SourceChangedEventArgs> SourceChanged;
        public event EventHandler<CoreWebView2NavigationCompletedEventArgs> NavigationCompleted;
        public event EventHandler<CoreWebView2WebMessageReceivedEventArgs> WebMessageReceived;
        public event EventHandler<CoreWebView2NewWindowRequestedEventArgs> NewWindowRequested;
        public event EventHandler<CoreWebView2DownloadStartingEventArgs> DownloadStarting;
        public event EventHandler<object> DocumentTitleChanged;
        public event EventHandler<CoreWebView2ProcessFailedEventArgs> ProcessFailed;
        public event EventHandler<object> HistoryChanged;
    }
}
