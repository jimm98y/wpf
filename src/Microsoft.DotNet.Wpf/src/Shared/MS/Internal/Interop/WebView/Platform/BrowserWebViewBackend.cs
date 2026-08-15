// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// BrowserWebViewBackend -- the WebAssembly IWebViewBackend, backed by an <iframe>.
//
// The JS half is the 'wpfBrowserWebView' module (web/browser-webview.js), registered in the app
// bootstrap exactly as wpfBrowserMedia and wpfBrowserWindow are.
//
// This head is why the seam is an overlay. An iframe's pixels cannot be read back into a canvas at
// any price -- there is no API, cross-origin or not -- so a composited design could not have served
// the browser at all. See the remarks on IWebViewBackend.
//
// It is also the head with the least authority over its content, and that is not a gap in this
// implementation but the browser's security model:
//
//   * A CROSS-ORIGIN page cannot be scripted, its title cannot be read, and its history cannot be
//     walked. ExecuteScriptAsync therefore throws a specific, named error for cross-origin content
//     rather than returning null -- an application asking a page a question deserves to be told the
//     browser refused, not handed an answer that looks like "undefined".
//   * There is no document-start script hook for a frame an embedder does not control. Injected
//     scripts are re-run on every load instead, which is as close as the platform allows and is
//     enough for the message bridge the controls build on.
//   * GoBack/GoForward would need the frame's own session history, which is cross-origin-protected.
//     They throw; CanGoBack/CanGoForward answer false rather than lying.
//
// Messages are QUEUED on the JS side and drained here on a timer tick, not pushed. A pushed
// callback would arrive on the browser's own event turn rather than the thread the control lives
// on, and every consumer of this seam expects its events on the thread that created it.
//

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace MS.Internal.Interop.WebView
{
    [SupportedOSPlatform("browser")]
    internal sealed partial class BrowserWebViewBackend : IWebViewBackend
    {
        /// <summary>What executeScript answers when the browser will not let us in.</summary>
        private const string CrossOriginSentinel = " cross-origin";

        private int _handle;
        private bool _attached;
        private bool _visible = true;
        private double _zoom = 1.0;
        private string _lastRequestedUri;
        private bool _wasReady;
        private ulong _navigationId;
        private Timer _pump;
        private TaskCompletionSource<object> _attach;

        public WebViewPresentation Presentation => WebViewPresentation.Overlay;

        public bool IsAttached => _attached;

        public Task AttachAsync(IntPtr ownerWindow)
        {
            if (_attach is not null)
            {
                return _attach.Task;
            }

            _attach = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The handle is the synthetic one WebViewHostWindow minted; the frame is keyed by it, so
            // nothing managed ever holds a DOM object.
            _handle = (int)ownerWindow;
            Js.CreateFrame(_handle);
            _attached = true;

            Js.SetVisible(_handle, _visible);

            // One timer drains messages and notices load transitions: the browser has no callback
            // that would land on the UI thread by itself.
            //
            // System.Threading.Timer, not DispatcherTimer, and deliberately: this file is compiled
            // into Microsoft.Web.WebView2.Core as well, which references no WPF at all, so the seam
            // must not name a WPF type. It is still correct here -- on WebAssembly the runtime
            // implements this timer over setTimeout on the single main thread, which is the same
            // thread the control and the DOM live on.
            _pump = new Timer(OnPump, null, dueTime: 16, period: 16);

            // An iframe exists the moment it is created; there is no asynchronous start-up to wait
            // for, unlike WebView2's environment and controller.
            _attach.TrySetResult(null);
            return _attach.Task;
        }

        public void Detach()
        {
            _pump?.Dispose();
            _pump = null;

            if (_attached)
            {
                Js.DestroyFrame(_handle);
                _attached = false;
            }

            _attach = null;
        }

        public void Dispose() => Detach();

        // ---- the pump ------------------------------------------------------------------------------

        private void OnPump(object state)
        {
            if (!_attached)
            {
                return;
            }

            // Load transitions. The browser gives an embedder no "navigation starting" for a frame's
            // own in-page navigation, so ContentLoading/NavigationCompleted are derived from the
            // frame's readiness changing -- which is what an embedder can actually observe.
            bool ready = Js.IsReady(_handle);

            if (ready && !_wasReady)
            {
                _wasReady = true;

                DocumentTitleChanged?.Invoke(this, EventArgs.Empty);
                SourceChanged?.Invoke(this, new WebViewSourceChangedEventArgs { IsNewDocument = true });
                NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedEventArgs
                {
                    IsSuccess = true,
                    WebErrorStatus = WebViewErrorStatus.Unknown,
                    NavigationId = _navigationId,
                });
            }
            else if (!ready && _wasReady)
            {
                _wasReady = false;
                ContentLoading?.Invoke(this, EventArgs.Empty);
            }

            // Messages, one per tick at most so a chatty page cannot starve the frame.
            for (int i = 0; i < 16; i++)
            {
                string message = Js.TakeMessage();

                if (message is null)
                {
                    break;
                }

                int space = message.IndexOf(' ');

                if (space <= 0 ||
                    !int.TryParse(message.AsSpan(0, space), out int handle) ||
                    handle != _handle)
                {
                    continue;
                }

                string body = message.Substring(space + 1);

                WebMessageReceived?.Invoke(this, new WebViewMessageReceivedEventArgs
                {
                    Source = Js.GetSource(_handle),
                    WebMessageAsJson = WebViewScript.WriteJsonString(body),
                    WebMessageAsString = body,
                });
            }
        }

        // ---- placement -------------------------------------------------------------------------------

        public void SetBounds(int x, int y, int width, int height, double scale)
        {
            if (_attached)
            {
                Js.SetBounds(_handle, x, y, width, height, scale);
            }
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;

            if (_attached)
            {
                Js.SetVisible(_handle, visible);
            }
        }

        // ---- navigation ------------------------------------------------------------------------------

        public void Navigate(string uri)
        {
            RequireAttached();
            RaiseNavigationStarting(uri);
            _lastRequestedUri = uri;
            _wasReady = false;
            Js.Navigate(_handle, uri);
        }

        public void NavigateToString(string htmlContent)
        {
            RequireAttached();
            RaiseNavigationStarting(null);
            _lastRequestedUri = null;
            _wasReady = false;
            Js.NavigateToString(_handle, htmlContent);
        }

        /// <summary>
        /// A frame can only be navigated by URL; an embedder cannot give it a request body. Refused
        /// rather than downgraded to a GET, which would turn a form post into a page load.
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
                "The browser does not let an embedder navigate a frame with POST data or custom " +
                "headers. Post a form from inside the page instead.");
        }

        private void RaiseNavigationStarting(string uri)
        {
            var e = new WebViewNavigationStartingEventArgs
            {
                Uri = uri,
                IsUserInitiated = false,
                NavigationId = ++_navigationId,
            };

            NavigationStarting?.Invoke(this, e);

            if (e.Cancel)
            {
                // The only navigation an embedder can cancel is one it started itself -- there is no
                // hook for the frame's own. Honouring it here is still worth doing: it is the case
                // an application's own Source assignment goes through.
                throw new OperationCanceledException("The navigation was cancelled by a handler.");
            }
        }

        public void Reload(bool noCache)
        {
            RequireAttached();
            _wasReady = false;
            Js.Reload(_handle);
        }

        public void Stop()
        {
            RequireAttached();
            Js.Stop(_handle);
        }

        /// <summary>
        /// A frame's session history is cross-origin-protected, so an embedder cannot walk it.
        /// </summary>
        public void GoBack() =>
            throw new NotSupportedException(
                "The browser does not expose a frame's session history to its embedder.");

        public void GoForward() =>
            throw new NotSupportedException(
                "The browser does not expose a frame's session history to its embedder.");

        // False rather than a guess: see GoBack.
        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public string Source => _attached ? (Js.GetSource(_handle) ?? _lastRequestedUri) : _lastRequestedUri;

        /// <summary>Null for a cross-origin page, which is the browser's answer, not ours.</summary>
        public string DocumentTitle => _attached ? Js.GetTitle(_handle) : null;

        // ---- scripting --------------------------------------------------------------------------------

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            RequireAttached();

            string result;

            try
            {
                result = Js.ExecuteScript(_handle, javaScript);
            }
            catch (JSException ex)
            {
                // The script itself threw inside the page.
                return Task.FromException<string>(new InvalidOperationException(ex.Message, ex));
            }

            if (result == CrossOriginSentinel)
            {
                return Task.FromException<string>(new InvalidOperationException(
                    "The browser will not let script run in a cross-origin frame. Only same-origin " +
                    "content and content loaded with NavigateToString can be scripted."));
            }

            // Synchronous underneath -- an iframe eval returns immediately -- but the seam is async
            // everywhere, so the completed task is the shape callers expect.
            return Task.FromResult(result);
        }

        public void AddScriptToExecuteOnDocumentCreated(string javaScript)
        {
            RequireAttached();
            Js.AddDocumentScript(_handle, javaScript);
        }

        public void PostWebMessageAsJson(string webMessageAsJson)
        {
            RequireAttached();
            Js.PostMessage(_handle, webMessageAsJson);
        }

        public void PostWebMessageAsString(string webMessageAsString)
        {
            RequireAttached();
            Js.PostMessage(_handle, WebViewScript.WriteJsonString(webMessageAsString));
        }

        // ---- configuration ------------------------------------------------------------------------------

        /// <summary>
        /// Nothing here has a browser equivalent an embedder may set on a frame: script, dialogs,
        /// context menus and the rest belong to the page. Accepted and remembered so a control can
        /// push settings uniformly, rather than throwing on every head that lacks a knob.
        /// </summary>
        public void ApplySettings(WebViewSettings settings)
        {
        }

        /// <summary>Applied as a CSS zoom on the frame's own document; cross-origin content ignores it.</summary>
        public double ZoomFactor
        {
            get => _zoom;
            set
            {
                _zoom = value <= 0 ? 1.0 : value;

                if (_attached)
                {
                    try
                    {
                        Js.ExecuteScript(_handle,
                            "document.documentElement.style.zoom='" + _zoom.ToString(
                                System.Globalization.CultureInfo.InvariantCulture) + "'");
                    }
                    catch (JSException)
                    {
                        // Cross-origin: the page's zoom is not ours to set.
                    }
                }
            }
        }

        public Task ClearBrowsingDataAsync()
        {
            RequireAttached();
            Js.ClearData();
            return Task.CompletedTask;
        }

        private void RequireAttached()
        {
            if (!_attached)
            {
                throw new InvalidOperationException(
                    "The web view is not ready yet; await AttachAsync before using it.");
            }
        }

        // ---- events ---------------------------------------------------------------------------------------

        public event EventHandler<WebViewNavigationStartingEventArgs> NavigationStarting;
        public event EventHandler<WebViewSourceChangedEventArgs> SourceChanged;
        public event EventHandler ContentLoading;
        public event EventHandler<WebViewNavigationCompletedEventArgs> NavigationCompleted;
        public event EventHandler<WebViewMessageReceivedEventArgs> WebMessageReceived;
        public event EventHandler<WebViewNewWindowRequestedEventArgs> NewWindowRequested;
        public event EventHandler<WebViewDownloadStartingEventArgs> DownloadStarting;
        public event EventHandler DocumentTitleChanged;
        public event EventHandler<WebViewProcessFailedEventArgs> ProcessFailed;

        // ================================================================ JS interop ====

        private static partial class Js
        {
            private const string Module = "wpfBrowserWebView";

            [JSImport("createFrame", Module)] internal static partial void CreateFrame(int handle);
            [JSImport("destroyFrame", Module)] internal static partial void DestroyFrame(int handle);
            [JSImport("setBounds", Module)] internal static partial void SetBounds(int handle, int x, int y, int width, int height, double scale);
            [JSImport("setVisible", Module)] internal static partial void SetVisible(int handle, bool visible);
            [JSImport("navigate", Module)] internal static partial void Navigate(int handle, string uri);
            [JSImport("navigateToString", Module)] internal static partial void NavigateToString(int handle, string html);
            [JSImport("reload", Module)] internal static partial void Reload(int handle);
            [JSImport("stop", Module)] internal static partial void Stop(int handle);
            [JSImport("isReady", Module)] internal static partial bool IsReady(int handle);
            [JSImport("getSource", Module)] internal static partial string GetSource(int handle);
            [JSImport("getTitle", Module)] internal static partial string GetTitle(int handle);
            [JSImport("executeScript", Module)] internal static partial string ExecuteScript(int handle, string script);
            [JSImport("addDocumentScript", Module)] internal static partial void AddDocumentScript(int handle, string script);
            [JSImport("postMessage", Module)] internal static partial void PostMessage(int handle, string json);
            [JSImport("takeMessage", Module)] internal static partial string TakeMessage();
            [JSImport("clearData", Module)] internal static partial void ClearData();
        }
    }
}
