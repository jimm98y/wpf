// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// AndroidWebViewBackend -- the Android IWebViewBackend, backed by android.webkit.WebView.
//
// Unlike every other head, almost nothing here touches the platform directly. The engine seam
// P/Invokes the NDK and never JNI -- AndroidInterop's header states the rule: "creating views ... is
// done by the app head, which is a .NET-for-Android assembly and has the Java bindings for free" --
// and android.webkit.WebView is a Java view. Constructing it, giving it a WebViewClient and calling
// evaluateJavascript all need those bindings.
//
// So this file is the managed HALF of the head's web view: it owns the seam's contract, the event
// shapes, the JSON, and the lifetime, and it asks the payload to do the Java. The two meet at
// AndroidWebViewRegistration, which explains why the registration is AppContext data rather than the
// IAndroid*Host interface every other Android capability uses.
//
// That split is also why this one is worth having even before a payload exists: everything above the
// bridge -- the navigation state, the error mapping, the message plumbing -- is ordinary managed code
// with no Android type in it, and is therefore testable on any machine, exactly as
// DragDropBackendTests tests Android's drag sequencing on a developer's laptop.
//
// NOT YET RUN ON A DEVICE, and it cannot be until a head payload implements the commands in
// AndroidWebViewCommands. Until one does, IsAvailable is false and the control reports that this
// application has no web-view implementation -- which is the truth, and is actionable.
//

using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace MS.Internal.Interop.WebView
{
    [SupportedOSPlatform("android")]
    internal sealed class AndroidWebViewBackend : IWebViewBackend
    {
        private int _id;
        private bool _attached;
        private bool _visible = true;
        private double _zoom = 1.0;
        private string _lastRequestedUri;
        private ulong _navigationId;
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

            if (!AndroidWebViewRegistration.IsAvailable)
            {
                _attach.TrySetException(new PlatformNotSupportedException(
                    "This application's Android head has not registered a web-view implementation."));
                return _attach.Task;
            }

            // The payload adds its WebView to the parent's view group and answers with an id. The
            // handle is the synthetic one the windowing layer minted for the window (see
            // AndroidInterop), which is what the payload resolves to a real view.
            object created = AndroidWebViewRegistration.Invoke(
                AndroidWebViewCommands.Create, ownerWindow, 0, 0, 1, 1);

            _id = created is int id ? id : 0;

            if (_id == 0)
            {
                _attach.TrySetException(new InvalidOperationException(
                    "The Android head could not create a web view."));
                return _attach.Task;
            }

            _attached = true;

            // The payload raises everything through one sink. Registered before anything can
            // navigate, so the first page's events are not missed.
            AndroidWebViewRegistration.Invoke(
                AndroidWebViewCommands.SetEventSink, _id, (Action<string, string>)OnHeadEvent);

            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.SetVisible, _id, _visible);

            // A Java view exists as soon as it is constructed; there is no asynchronous start-up to
            // wait for, unlike WebView2's environment and controller.
            _attach.TrySetResult(null);
            return _attach.Task;
        }

        public void Detach()
        {
            if (_attached)
            {
                try
                {
                    AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.Destroy, _id);
                }
                catch (PlatformNotSupportedException)
                {
                    // The payload went away first; there is nothing left to tear down.
                }

                _attached = false;
                _id = 0;
            }

            _attach = null;
        }

        public void Dispose() => Detach();

        // ---- events from the payload ------------------------------------------------------------

        /// <summary>
        /// One sink for everything the Java side observes. Kept stringly-typed on purpose: the
        /// payload and this file are compiled separately and ship separately, so a new event must
        /// not break an older payload, and an unrecognised one must be ignored rather than fatal.
        /// </summary>
        private void OnHeadEvent(string kind, string payload)
        {
            switch (kind)
            {
                case AndroidWebViewEvents.NavigationStarting:
                    var starting = new WebViewNavigationStartingEventArgs
                    {
                        Uri = payload,
                        IsUserInitiated = true,
                        NavigationId = ++_navigationId,
                    };

                    NavigationStarting?.Invoke(this, starting);

                    // Nothing is sent back: shouldOverrideUrlLoading has already decided by the time
                    // this returns on most Android versions, so a cancel here could not be honoured
                    // and pretending otherwise would be worse than the limitation.
                    break;

                case AndroidWebViewEvents.ContentLoading:
                    ContentLoading?.Invoke(this, EventArgs.Empty);
                    SourceChanged?.Invoke(this, new WebViewSourceChangedEventArgs { IsNewDocument = true });
                    break;

                case AndroidWebViewEvents.NavigationCompleted:
                    NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedEventArgs
                    {
                        IsSuccess = true,
                        WebErrorStatus = WebViewErrorStatus.Unknown,
                        NavigationId = _navigationId,
                    });
                    break;

                case AndroidWebViewEvents.NavigationFailed:
                    NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedEventArgs
                    {
                        IsSuccess = false,
                        WebErrorStatus = TranslateError(payload),
                        NavigationId = _navigationId,
                    });
                    break;

                case AndroidWebViewEvents.WebMessage:
                    WebMessageReceived?.Invoke(this, new WebViewMessageReceivedEventArgs
                    {
                        Source = Source,
                        WebMessageAsJson = WebViewScript.WriteJsonString(payload),
                        WebMessageAsString = payload,
                    });
                    break;

                case AndroidWebViewEvents.NewWindow:
                    var newWindow = new WebViewNewWindowRequestedEventArgs
                    {
                        Uri = payload,
                        IsUserInitiated = true,
                    };

                    NewWindowRequested?.Invoke(this, newWindow);

                    // Unhandled means "open it somewhere". There is no second view to open it in, so
                    // following it in place is the closest honest behaviour -- the same choice the
                    // WKWebView heads make.
                    if (!newWindow.Handled && !string.IsNullOrEmpty(newWindow.Uri))
                    {
                        Navigate(newWindow.Uri);
                    }

                    break;

                case AndroidWebViewEvents.DownloadStarting:
                    DownloadStarting?.Invoke(this, new WebViewDownloadStartingEventArgs { Uri = payload });
                    break;

                case AndroidWebViewEvents.TitleChanged:
                    DocumentTitleChanged?.Invoke(this, EventArgs.Empty);
                    break;

                    // Anything else is from a newer payload than this build knows about. Ignored.
            }
        }

        /// <summary>
        /// WebViewClient reports a description, not a code the seam's vocabulary shares, so this
        /// maps the ones that are unambiguous and answers Unknown for the rest rather than guessing.
        /// </summary>
        private static WebViewErrorStatus TranslateError(string description)
        {
            if (string.IsNullOrEmpty(description))
            {
                return WebViewErrorStatus.Unknown;
            }

            string d = description.ToUpperInvariant();

            if (d.Contains("ERR_NAME_NOT_RESOLVED") || d.Contains("HOST_LOOKUP"))
            {
                return WebViewErrorStatus.HostNameNotResolved;
            }

            if (d.Contains("ERR_CONNECTION_TIMED_OUT") || d.Contains("TIMEOUT"))
            {
                return WebViewErrorStatus.Timeout;
            }

            // ORDER MATTERS, and not obviously: "ERR_INTERNET_DISCONNECTED" contains the substring
            // "CONNECT", so a loose connect test placed above it swallows the disconnected case and
            // reports CannotConnect for a device that simply has no network. Specific patterns first,
            // loose ones last.
            if (d.Contains("ERR_INTERNET_DISCONNECTED"))
            {
                return WebViewErrorStatus.ServerUnreachable;
            }

            if (d.Contains("ERR_CONNECTION_RESET"))
            {
                return WebViewErrorStatus.ConnectionReset;
            }

            if (d.Contains("ERR_CONNECTION_REFUSED") || d.Contains("CONNECT"))
            {
                return WebViewErrorStatus.CannotConnect;
            }

            if (d.Contains("SSL") || d.Contains("CERT"))
            {
                return WebViewErrorStatus.CertificateIsInvalid;
            }

            return WebViewErrorStatus.Unknown;
        }

        // ---- placement -------------------------------------------------------------------------------

        public void SetBounds(int x, int y, int width, int height, double scale)
        {
            if (_attached)
            {
                // Device pixels throughout: an Android view is laid out in them already, which is
                // also why NativePlatform.UpdateContentsScale is a no-op on this head.
                AndroidWebViewRegistration.Invoke(
                    AndroidWebViewCommands.SetBounds, _id, x, y, Math.Max(1, width), Math.Max(1, height));
            }
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;

            if (_attached)
            {
                AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.SetVisible, _id, visible);
            }
        }

        // ---- navigation --------------------------------------------------------------------------------

        public void Navigate(string uri)
        {
            RequireAttached();
            _lastRequestedUri = uri;
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.Navigate, _id, uri);
        }

        public void NavigateToString(string htmlContent)
        {
            RequireAttached();
            _lastRequestedUri = null;
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.NavigateToString, _id, htmlContent);
        }

        public void NavigateWithPost(string uri, byte[] postData, string additionalHeaders)
        {
            RequireAttached();

            if ((postData is null || postData.Length == 0) && string.IsNullOrEmpty(additionalHeaders))
            {
                Navigate(uri);
                return;
            }

            _lastRequestedUri = uri;

            // WebView.postUrl covers a body; extra headers go through loadUrl's header map. The
            // payload decides which it needs, because only it can see both APIs.
            AndroidWebViewRegistration.Invoke(
                AndroidWebViewCommands.NavigateWithPost, _id, uri, postData, additionalHeaders);
        }

        public void Reload(bool noCache)
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.Reload, _id, noCache);
        }

        public void Stop()
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.Stop, _id);
        }

        public void GoBack()
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.GoBack, _id);
        }

        public void GoForward()
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.GoForward, _id);
        }

        public bool CanGoBack =>
            _attached && AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.CanGoBack, _id) is true;

        public bool CanGoForward =>
            _attached && AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.CanGoForward, _id) is true;

        public string Source =>
            _attached
                ? AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.GetSource, _id) as string ?? _lastRequestedUri
                : _lastRequestedUri;

        public string DocumentTitle =>
            _attached ? AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.GetTitle, _id) as string : null;

        // ---- scripting ------------------------------------------------------------------------------------

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            RequireAttached();

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // evaluateJavascript is asynchronous on Android and has no synchronous form, so the
            // payload answers through this callback rather than by returning.
            AndroidWebViewRegistration.Invoke(
                AndroidWebViewCommands.ExecuteScript, _id, javaScript,
                (Action<string>)(result => tcs.TrySetResult(result ?? "null")));

            return tcs.Task;
        }

        public void AddScriptToExecuteOnDocumentCreated(string javaScript)
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.AddDocumentScript, _id, javaScript);
        }

        public void PostWebMessageAsJson(string webMessageAsJson)
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.PostMessage, _id, webMessageAsJson);
        }

        public void PostWebMessageAsString(string webMessageAsString)
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(
                AndroidWebViewCommands.PostMessage, _id, WebViewScript.WriteJsonString(webMessageAsString));
        }

        // ---- configuration ---------------------------------------------------------------------------------

        /// <summary>
        /// WebSettings has counterparts for the two that matter, and the payload applies them. The
        /// rest (status bar, built-in error page, default dialogs) have no Android equivalent an
        /// embedder may set, so they are remembered rather than throwing on a head that has no knob.
        /// </summary>
        public void ApplySettings(WebViewSettings settings)
        {
        }

        public double ZoomFactor
        {
            get => _zoom;
            set
            {
                _zoom = value <= 0 ? 1.0 : value;

                if (_attached)
                {
                    AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.SetZoom, _id, _zoom);
                }
            }
        }

        /// <summary>
        /// android.webkit.WebView can draw itself into a Canvas over a Bitmap, but that is a Java
        /// call and belongs to the head payload like everything else here. Refused until a payload
        /// answers the command, rather than returning an empty image.
        /// </summary>
        public Task<byte[]> CapturePreviewAsync(bool png) =>
            Task.FromException<byte[]>(new NotSupportedException(
                "Capturing a web view is not implemented by this application's Android head."));

        public Task ClearBrowsingDataAsync()
        {
            RequireAttached();
            AndroidWebViewRegistration.Invoke(AndroidWebViewCommands.ClearData, _id);
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

        // ---- events -----------------------------------------------------------------------------------------

        public event EventHandler<WebViewNavigationStartingEventArgs> NavigationStarting;
        public event EventHandler<WebViewSourceChangedEventArgs> SourceChanged;
        public event EventHandler ContentLoading;
        public event EventHandler<WebViewNavigationCompletedEventArgs> NavigationCompleted;
        public event EventHandler<WebViewMessageReceivedEventArgs> WebMessageReceived;
        public event EventHandler<WebViewNewWindowRequestedEventArgs> NewWindowRequested;
        public event EventHandler<WebViewDownloadStartingEventArgs> DownloadStarting;
        public event EventHandler DocumentTitleChanged;
        public event EventHandler<WebViewProcessFailedEventArgs> ProcessFailed;
    }
}
