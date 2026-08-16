// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WindowsWebViewBackend -- the Windows IWebViewBackend, backed by the WebView2 (Edge) runtime.
//
// This is what takes WebBrowser off the IE WebOC on Windows. The old path activated shdocvw through
// COM and drove it with an OLE in-place-activation state machine; the engine behind it is dead, and
// the hosting mechanism could never have worked anywhere else. WebView2 replaces both: the runtime
// is an OS component on Windows 11, it parents itself to an HWND, and it exposes the same
// navigate/script/message vocabulary every other head's engine does.
//
// The ABI work lives next door in WindowsWebViewInterop.cs -- flat loader export plus vtable
// indexing, no COM interop. What lives here is the lifetime, which is the part that is easy to get
// wrong:
//
//   * Creation is TWO asynchronous steps (environment, then controller), each completing on the UI
//     thread's message loop. AttachAsync therefore returns a task that completes only after the
//     second one, because everything else on this interface is invalid until the controller exists.
//   * The handlers we hand to WebView2 are COM objects it holds across that asynchronous work, so
//     they are kept in fields. A handler that was only a local would be collected while the runtime
//     still held a pointer to it.
//   * Every event registration is kept with its token so Detach can unregister. Without that, a
//     control that is unloaded and reloaded accumulates handlers and raises each event twice, then
//     three times.
//
// USER DATA FOLDER. WebView2 refuses to start if it cannot write one, and its default is beside the
// executable -- which is read-only for an installed application. We therefore place it under
// LocalApplicationData keyed by the process name, which is what the WebView2 documentation
// recommends for exactly this reason.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace MS.Internal.Interop.WebView
{
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsWebViewBackend : IWebViewBackend
    {
        private static readonly Guid IID_EnvironmentCompleted = new Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d");
        private static readonly Guid IID_ControllerCompleted = new Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c");
        private static readonly Guid IID_ExecuteScriptCompleted = new Guid("49511172-cc67-4bca-9923-137112f4c4cc");
        private static readonly Guid IID_AddScriptCompleted = new Guid("b99369f3-9b11-47b5-bc6f-8e7895fcea17");
        private static readonly Guid IID_NavigationStarting = new Guid("9adbe429-f36d-432b-9ddc-f8881fbd76e3");
        private static readonly Guid IID_NavigationCompleted = new Guid("d33a35bf-1c49-4f98-93ab-006e0533fe1c");
        private static readonly Guid IID_SourceChanged = new Guid("3c067f9f-5388-4772-8b48-79f7ef1ab37c");
        private static readonly Guid IID_ContentLoading = new Guid("364471e7-f2be-4910-bdba-d72077d51c4b");
        private static readonly Guid IID_WebMessageReceived = new Guid("57213f19-00e6-49fa-8e07-898ea01ecbd2");
        private static readonly Guid IID_NewWindowRequested = new Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651");
        private static readonly Guid IID_DocumentTitleChanged = new Guid("f5f2b923-953e-4042-9f95-f3a118e1afd4");
        private static readonly Guid IID_ProcessFailed = new Guid("79e0aea4-990b-42d9-aa1d-0fcc2e5bc7f1");
        private static readonly Guid IID_DownloadStarting = new Guid("efedc989-c396-41ca-83f7-07f845a55724");
        private static readonly Guid IID_ICoreWebView2_4 = new Guid("20d02d59-6df2-42dc-bd06-f98a694b1302");
        private static readonly Guid IID_CapturePreviewCompleted = new Guid("697e05e9-3d8f-45fa-96f4-8ffe1ededaf5");

        /// <summary>add_DownloadStarting on ICoreWebView2_4 (remove_ is the next slot).</summary>
        private const int SlotAddDownloadStarting = 75;

        private IntPtr _environment;
        private IntPtr _controller;
        private IntPtr _webview;

        private IntPtr _webview4;      // ICoreWebView2_4, only for DownloadStarting
        private long _downloadToken;

        private IntPtr _ownerWindow;
        private bool _visible = true;
        private WebViewSettings _settings = new WebViewSettings();

        // Held for as long as the runtime might call them (see the file remarks).
        private readonly List<ComThunk> _liveThunks = new List<ComThunk>();
        private readonly List<KeyValuePair<int, long>> _eventTokens = new List<KeyValuePair<int, long>>();

        private TaskCompletionSource<object> _attach;

        public WebViewPresentation Presentation => WebViewPresentation.Overlay;

        public bool IsAttached => _webview != IntPtr.Zero;

        public Task AttachAsync(IntPtr ownerWindow)
        {
            if (ownerWindow == IntPtr.Zero)
            {
                throw new ArgumentException("A web view needs a parent window.", nameof(ownerWindow));
            }

            if (_attach != null)
            {
                return _attach.Task;
            }

            // WebView2 is an STA COM server, and creation fails with RPC_E_CHANGED_MODE (0x80010106)
            // on an MTA thread. A WPF or WinForms UI thread is already STA, so reaching this means
            // the caller built the control on a worker thread -- worth saying plainly, because the
            // raw HRESULT points at COM apartments rather than at what the caller did wrong.
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                throw new InvalidOperationException(
                    "A web view must be created on an STA thread; this thread is " +
                    Thread.CurrentThread.GetApartmentState() + ".");
            }

            _ownerWindow = ownerWindow;
            _attach = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            ComThunk envDone = ComThunk.ForCompleted(IID_EnvironmentCompleted, OnEnvironmentCreated);
            _liveThunks.Add(envDone);

            int hr = WebView2Interop.CreateCoreWebView2EnvironmentWithOptions(
                null, UserDataFolder, IntPtr.Zero, envDone.Pointer);

            if (hr < 0)
            {
                // The overwhelmingly common cause is that the Evergreen runtime is not installed.
                // Saying so beats surfacing a bare HRESULT from a loader the app never referenced.
                _attach.TrySetException(new PlatformNotSupportedException(
                    "The Microsoft Edge WebView2 Runtime could not be started (0x" +
                    hr.ToString("x8") + "). Install the Evergreen WebView2 Runtime.",
                    System.Runtime.InteropServices.Marshal.GetExceptionForHR(hr)));
            }

            return _attach.Task;
        }

        private int OnEnvironmentCreated(int errorCode, IntPtr environment)
        {
            if (errorCode < 0 || environment == IntPtr.Zero)
            {
                _attach.TrySetException(new InvalidOperationException(
                    "WebView2 environment creation failed (0x" + errorCode.ToString("x8") + ")."));
                return 0;
            }

            _environment = environment;
            WebView2Interop.AddRef(_environment);

            ComThunk ctlDone = ComThunk.ForCompleted(IID_ControllerCompleted, OnControllerCreated);
            _liveThunks.Add(ctlDone);

            int hr = WebView2Interop.Environment_CreateCoreWebView2Controller(
                _environment, _ownerWindow, ctlDone.Pointer);

            if (hr < 0)
            {
                _attach.TrySetException(
                    System.Runtime.InteropServices.Marshal.GetExceptionForHR(hr));
            }

            return 0;
        }

        private int OnControllerCreated(int errorCode, IntPtr controller)
        {
            if (errorCode < 0 || controller == IntPtr.Zero)
            {
                _attach.TrySetException(new InvalidOperationException(
                    "WebView2 controller creation failed (0x" + errorCode.ToString("x8") + ")."));
                return 0;
            }

            _controller = controller;
            WebView2Interop.AddRef(_controller);
            _webview = WebView2Interop.Controller_GetCoreWebView2(_controller);

            ApplySettings(_settings);
            WebView2Interop.Controller_PutIsVisible(_controller, _visible);
            RegisterEvents();

            _attach.TrySetResult(null);
            return 0;
        }

        private void RegisterEvents()
        {
            Add(WebView2Interop.SlotAddNavigationStarting, IID_NavigationStarting, (s, a) =>
            {
                var e = new WebViewNavigationStartingEventArgs
                {
                    Uri = WebView2Interop.NavStarting_GetUri(a),
                    IsUserInitiated = WebView2Interop.NavStarting_GetIsUserInitiated(a),
                    IsRedirected = WebView2Interop.NavStarting_GetIsRedirected(a),
                    NavigationId = WebView2Interop.NavStarting_GetNavigationId(a),
                };

                NavigationStarting?.Invoke(this, e);

                // Only push a cancel back: writing false would override a decision another
                // subscriber (or the engine itself) had already made.
                if (e.Cancel)
                {
                    WebView2Interop.NavStarting_PutCancel(a, true);
                }

                return 0;
            });

            Add(WebView2Interop.SlotAddContentLoading, IID_ContentLoading, (s, a) =>
            {
                ContentLoading?.Invoke(this, EventArgs.Empty);
                return 0;
            });

            Add(WebView2Interop.SlotAddSourceChanged, IID_SourceChanged, (s, a) =>
            {
                SourceChanged?.Invoke(this, new WebViewSourceChangedEventArgs
                {
                    IsNewDocument = WebView2Interop.SourceChanged_GetIsNewDocument(a),
                });
                return 0;
            });

            Add(WebView2Interop.SlotAddNavigationCompleted, IID_NavigationCompleted, (s, a) =>
            {
                NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedEventArgs
                {
                    IsSuccess = WebView2Interop.NavCompleted_GetIsSuccess(a),
                    WebErrorStatus = (WebViewErrorStatus)WebView2Interop.NavCompleted_GetWebErrorStatus(a),
                    NavigationId = WebView2Interop.NavCompleted_GetNavigationId(a),
                });
                return 0;
            });

            Add(WebView2Interop.SlotAddWebMessageReceived, IID_WebMessageReceived, (s, a) =>
            {
                WebMessageReceived?.Invoke(this, new WebViewMessageReceivedEventArgs
                {
                    Source = WebView2Interop.WebMessage_GetSource(a),
                    WebMessageAsJson = WebView2Interop.WebMessage_GetAsJson(a),
                    WebMessageAsString = WebView2Interop.WebMessage_TryGetAsString(a),
                });
                return 0;
            });

            Add(WebView2Interop.SlotAddNewWindowRequested, IID_NewWindowRequested, (s, a) =>
            {
                var e = new WebViewNewWindowRequestedEventArgs
                {
                    Uri = WebView2Interop.NewWindow_GetUri(a),
                    IsUserInitiated = WebView2Interop.NewWindow_GetIsUserInitiated(a),
                };

                NewWindowRequested?.Invoke(this, e);

                if (e.Handled)
                {
                    WebView2Interop.NewWindow_PutHandled(a, true);
                }

                return 0;
            });

            Add(WebView2Interop.SlotAddDocumentTitleChanged, IID_DocumentTitleChanged, (s, a) =>
            {
                DocumentTitleChanged?.Invoke(this, EventArgs.Empty);
                return 0;
            });

            RegisterDownloadStarting();

            Add(WebView2Interop.SlotAddProcessFailed, IID_ProcessFailed, (s, a) =>
            {
                int kind = WebView2Interop.ProcessFailed_GetKind(a);
                ProcessFailed?.Invoke(this, new WebViewProcessFailedEventArgs
                {
                    // Kind 0 is COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED.
                    IsBrowserProcess = kind == 0,
                    Reason = "COREWEBVIEW2_PROCESS_FAILED_KIND " + kind,
                });
                return 0;
            });
        }

        /// <summary>
        /// DownloadStarting lives on ICoreWebView2_4, not on the original ICoreWebView2, so it is
        /// reached through a QueryInterface and registered on that pointer. An older runtime simply
        /// does not answer for the interface; the event then never fires, which is the truth about
        /// that machine rather than a silent failure on ours.
        /// </summary>
        private void RegisterDownloadStarting()
        {
            IntPtr wv4 = WebView2Interop.QueryInterface(_webview, IID_ICoreWebView2_4);

            if (wv4 == IntPtr.Zero)
            {
                return;
            }

            try
            {
                ComThunk thunk = ComThunk.ForEvent(IID_DownloadStarting, (s, a) =>
                {
                    IntPtr op = WebView2Interop.DownloadStarting_GetOperation(a);

                    var e = new WebViewDownloadStartingEventArgs
                    {
                        Uri = op == IntPtr.Zero ? null : WebView2Interop.DownloadOperation_GetUri(op),
                        ResultFilePath = WebView2Interop.DownloadStarting_GetResultFilePath(a),
                    };

                    if (op != IntPtr.Zero)
                    {
                        WebView2Interop.Release(op);
                    }

                    string original = e.ResultFilePath;
                    DownloadStarting?.Invoke(this, e);

                    if (e.Cancel)
                    {
                        WebView2Interop.DownloadStarting_PutCancel(a, true);
                    }
                    else if (!string.Equals(e.ResultFilePath, original, StringComparison.Ordinal) &&
                             !string.IsNullOrEmpty(e.ResultFilePath))
                    {
                        WebView2Interop.DownloadStarting_PutResultFilePath(a, e.ResultFilePath);
                    }

                    return 0;
                });

                _liveThunks.Add(thunk);

                long token = WebView2Interop.WebView_AddEvent(wv4, SlotAddDownloadStarting, thunk.Pointer);

                // Recorded against the ICoreWebView2_4 pointer, which Detach needs in order to
                // unregister from the same object it registered on.
                _downloadToken = token;
                _webview4 = wv4;
                wv4 = IntPtr.Zero;   // ownership moved to the field
            }
            finally
            {
                if (wv4 != IntPtr.Zero)
                {
                    WebView2Interop.Release(wv4);
                }
            }
        }

        private void Add(int slot, Guid iid, ComThunk.EventCallback callback)
        {
            ComThunk thunk = ComThunk.ForEvent(iid, callback);
            _liveThunks.Add(thunk);
            long token = WebView2Interop.WebView_AddEvent(_webview, slot, thunk.Pointer);
            _eventTokens.Add(new KeyValuePair<int, long>(slot, token));
        }

        // ---- placement --------------------------------------------------------------------------

        public void SetBounds(int x, int y, int width, int height, double scale)
        {
            if (_controller == IntPtr.Zero)
            {
                return;
            }

            // WebView2 takes device pixels relative to the parent HWND's client area, which is
            // exactly what the caller supplies, so the scale is not needed here -- the engine reads
            // the window's own DPI. It is part of the seam for the engines that think in points.
            WebView2Interop.Controller_PutBounds(_controller, new WebView2Interop.RECT
            {
                Left = x,
                Top = y,
                Right = x + Math.Max(0, width),
                Bottom = y + Math.Max(0, height),
            });
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;

            if (_controller != IntPtr.Zero)
            {
                WebView2Interop.Controller_PutIsVisible(_controller, visible);
            }
        }

        // ---- navigation --------------------------------------------------------------------------

        public void Navigate(string uri)
        {
            RequireAttached();
            WebView2Interop.ThrowIfFailed(WebView2Interop.WebView_Navigate(_webview, uri));
        }

        public void NavigateToString(string htmlContent)
        {
            RequireAttached();
            WebView2Interop.ThrowIfFailed(WebView2Interop.WebView_NavigateToString(_webview, htmlContent));
        }

        public void NavigateWithPost(string uri, byte[] postData, string additionalHeaders)
        {
            RequireAttached();

            // A POST needs a WebResourceRequest built from the environment
            // (ICoreWebView2Environment2.CreateWebResourceRequest) and NavigateWithWebResourceRequest
            // on ICoreWebView2_2. Until those are bound, refuse rather than silently downgrading to a
            // GET -- a form post that quietly became a page load is a data-loss bug, not a cosmetic
            // one. A plain navigation with no body still goes the normal route.
            if ((postData == null || postData.Length == 0) && string.IsNullOrEmpty(additionalHeaders))
            {
                Navigate(uri);
                return;
            }

            throw new NotSupportedException(
                "Navigating with POST data or additional headers is not implemented on this head yet.");
        }

        public void Reload(bool noCache)
        {
            RequireAttached();

            // WebView2 has no no-cache reload; location.reload(true) is the documented equivalent
            // and is what a caller asking for one actually wants.
            if (noCache)
            {
                _ = ExecuteScriptAsync("location.reload(true)");
                return;
            }

            WebView2Interop.WebView_Reload(_webview);
        }

        public void Stop()
        {
            RequireAttached();
            WebView2Interop.WebView_Stop(_webview);
        }

        public void GoBack()
        {
            RequireAttached();
            WebView2Interop.WebView_GoBack(_webview);
        }

        public void GoForward()
        {
            RequireAttached();
            WebView2Interop.WebView_GoForward(_webview);
        }

        public bool CanGoBack => _webview != IntPtr.Zero && WebView2Interop.WebView_GetCanGoBack(_webview);

        public bool CanGoForward => _webview != IntPtr.Zero && WebView2Interop.WebView_GetCanGoForward(_webview);

        public string Source => _webview == IntPtr.Zero ? null : WebView2Interop.WebView_GetSource(_webview);

        public string DocumentTitle =>
            _webview == IntPtr.Zero ? null : WebView2Interop.WebView_GetDocumentTitle(_webview);

        // ---- scripting ----------------------------------------------------------------------------

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            RequireAttached();

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ComThunk done = null;

            done = ComThunk.ForCompleted(IID_ExecuteScriptCompleted, (hr, resultJson) =>
            {
                // Unlike the creation handlers, this one is called exactly once, so it can retire
                // itself instead of living for the lifetime of the control.
                _liveThunks.Remove(done);

                if (hr < 0)
                {
                    tcs.TrySetException(System.Runtime.InteropServices.Marshal.GetExceptionForHR(hr));
                }
                else
                {
                    // NOT TakeString: this string belongs to the caller of Invoke, not to us.
                    tcs.TrySetResult(System.Runtime.InteropServices.Marshal.PtrToStringUni(resultJson));
                }

                return 0;
            });

            _liveThunks.Add(done);
            WebView2Interop.ThrowIfFailed(WebView2Interop.WebView_ExecuteScript(_webview, javaScript, done.Pointer));
            return tcs.Task;
        }

        public void AddScriptToExecuteOnDocumentCreated(string javaScript)
        {
            RequireAttached();

            ComThunk done = ComThunk.ForCompleted(IID_AddScriptCompleted, (hr, id) => 0);
            _liveThunks.Add(done);

            WebView2Interop.ThrowIfFailed(
                WebView2Interop.WebView_AddScriptToExecuteOnDocumentCreated(_webview, javaScript, done.Pointer));
        }

        public void PostWebMessageAsJson(string webMessageAsJson)
        {
            RequireAttached();
            WebView2Interop.ThrowIfFailed(WebView2Interop.WebView_PostWebMessageAsJson(_webview, webMessageAsJson));
        }

        public void PostWebMessageAsString(string webMessageAsString)
        {
            RequireAttached();
            WebView2Interop.ThrowIfFailed(WebView2Interop.WebView_PostWebMessageAsString(_webview, webMessageAsString));
        }

        // ---- configuration --------------------------------------------------------------------------

        public void ApplySettings(WebViewSettings settings)
        {
            _settings = settings ?? new WebViewSettings();

            if (_webview == IntPtr.Zero)
            {
                return;   // re-applied from OnControllerCreated
            }

            IntPtr s = WebView2Interop.WebView_GetSettings(_webview);

            try
            {
                WebView2Interop.Settings_Apply(s, _settings);
            }
            finally
            {
                WebView2Interop.Release(s);
            }
        }

        public double ZoomFactor
        {
            get => _controller == IntPtr.Zero ? 1.0 : WebView2Interop.Controller_GetZoomFactor(_controller);
            set
            {
                if (_controller != IntPtr.Zero)
                {
                    WebView2Interop.Controller_PutZoomFactor(_controller, value);
                }
            }
        }

        public Task<byte[]> CapturePreviewAsync(bool png)
        {
            RequireAttached();

            // A memory-backed IStream from shlwapi rather than one of our own: the engine only ever
            // writes to it, and authoring IStream would be fourteen vtable slots of no behaviour.
            IntPtr stream = WebView2Interop.SHCreateMemStream(IntPtr.Zero, 0);

            if (stream == IntPtr.Zero)
            {
                return Task.FromException<byte[]>(new OutOfMemoryException(
                    "Could not allocate a stream for the capture."));
            }

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            ComThunk done = null;

            done = ComThunk.ForCompleted(IID_CapturePreviewCompleted, (hr, _) =>
            {
                // Called exactly once, so it retires itself and releases the stream here rather than
                // living for the lifetime of the control.
                _liveThunks.Remove(done);

                try
                {
                    if (hr < 0)
                    {
                        tcs.TrySetException(System.Runtime.InteropServices.Marshal.GetExceptionForHR(hr));
                    }
                    else
                    {
                        tcs.TrySetResult(WebView2Interop.ReadStream(stream));
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    // The stream is ours to release. The THUNK is not disposed here: WebView2 is
                    // still inside Invoke on it, and COM calls Release the moment this returns -- on
                    // memory Dispose would already have freed. That is an access violation, not a
                    // leak, and it is why ExecuteScriptAsync only unlists its handler too. Detach
                    // disposes whatever is still listed.
                    WebView2Interop.Release(stream);
                }

                return 0;
            });

            _liveThunks.Add(done);

            // COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT: PNG 0, JPEG 1.
            int hrStart = WebView2Interop.WebView_CapturePreview(_webview, png ? 0 : 1, stream, done.Pointer);

            if (hrStart < 0)
            {
                // Safe to dispose here, unlike in the callback: the engine never took the handler,
                // so nothing is going to call into it.
                _liveThunks.Remove(done);
                WebView2Interop.Release(stream);
                done.Dispose();
                WebView2Interop.ThrowIfFailed(hrStart);
            }

            return tcs.Task;
        }

        public Task ClearBrowsingDataAsync()
        {
            RequireAttached();

            // ICoreWebView2Profile2.ClearBrowsingDataAsync is the real answer and needs the profile
            // interface bound. Refuse until then rather than reporting success for work that did not
            // happen -- a caller clearing cookies is usually doing it for a reason.
            throw new NotSupportedException(
                "Clearing browsing data is not implemented on this head yet.");
        }

        // ---- lifetime -------------------------------------------------------------------------------

        public void Detach()
        {
            if (_webview != IntPtr.Zero)
            {
                foreach (KeyValuePair<int, long> t in _eventTokens)
                {
                    // remove_X always sits immediately after add_X in these vtables.
                    RemoveEvent(t.Key + 1, t.Value);
                }
            }

            _eventTokens.Clear();

            if (_webview4 != IntPtr.Zero)
            {
                RemoveEventOn(_webview4, SlotAddDownloadStarting + 1, _downloadToken);
                WebView2Interop.ReleaseAndClear(ref _webview4);
                _downloadToken = 0;
            }

            if (_controller != IntPtr.Zero)
            {
                WebView2Interop.Controller_Close(_controller);
            }

            _webview = IntPtr.Zero;
            WebView2Interop.ReleaseAndClear(ref _controller);
            WebView2Interop.ReleaseAndClear(ref _environment);

            foreach (ComThunk t in _liveThunks)
            {
                t.Dispose();
            }

            _liveThunks.Clear();
            _attach = null;
        }

        private void RemoveEvent(int slot, long token) => RemoveEventOn(_webview, slot, token);

        private static unsafe void RemoveEventOn(IntPtr obj, int slot, long token)
        {
            if (obj != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)
                    WebView2Interop.Vtbl(obj)[slot])(obj, token);
            }
        }

        public void Dispose() => Detach();

        private void RequireAttached()
        {
            if (_webview == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The web view is not ready yet; await AttachAsync before using it.");
            }
        }

        /// <summary>
        /// Where WebView2 keeps its profile. Its own default is beside the executable, which is
        /// read-only for an installed application and makes the runtime refuse to start.
        /// </summary>
        private static string UserDataFolder
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string name = AppDomain.CurrentDomain.FriendlyName;

                if (string.IsNullOrEmpty(name))
                {
                    name = "WpfWebView2";
                }

                return Path.Combine(root, name, "WebView2");
            }
        }

        // ---- events ---------------------------------------------------------------------------------

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
