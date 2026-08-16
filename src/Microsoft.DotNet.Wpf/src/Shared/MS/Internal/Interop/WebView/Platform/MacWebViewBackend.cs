// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MacWebViewBackend -- the macOS IWebViewBackend, backed by WKWebView.
//
// WKWebView is the system web engine and the only one Apple ships; there is no Chromium to reach and
// no offscreen path worth having (WKWebView can snapshot, but it cannot be driven as a continuous
// texture source), which is the concrete reason the whole seam is an overlay rather than a
// composited surface -- see the remarks on IWebViewBackend.
//
// Interop follows the fork's convention (WgpuInterop MacInterop.cs, CocoaWindow.cs): dlopen the full
// framework path, one typed objc_msgSend alias per call shape, and runtime-registered Objective-C
// classes for the callbacks. Three of those classes are needed here, because WKWebView reports
// everything through delegates rather than through blocks:
//
//   * WKNavigationDelegate  -- didStartProvisionalNavigation / didCommit / didFinish / didFail,
//                              which become NavigationStarting, ContentLoading, SourceChanged and
//                              NavigationCompleted. decidePolicyForNavigationAction is what makes
//                              NavigationStarting.Cancel work at all: it is the only point where a
//                              navigation can still be refused.
//   * WKScriptMessageHandler -- window.webkit.messageHandlers.<name>.postMessage, which is how the
//                              page talks back. The WebView2 vocabulary the rest of the fork speaks
//                              is window.chrome.webview, so a small shim script (installed as a
//                              user script, i.e. at document start) maps one onto the other.
//   * WKUIDelegate          -- createWebViewWithConfiguration, which is window.open. Without it
//                              WKWebView silently drops the request and NewWindowRequested could
//                              never fire.
//
// evaluateJavaScript takes a BLOCK, not a delegate, and there is no synchronous form -- so
// ExecuteScriptAsync goes through ObjCBlock (already in this assembly for NSItemProvider) and
// completes a TaskCompletionSource from the callback.
//
// NOT YET RUN ON A MAC. It compiles as part of PresentationFramework on every head; the selectors,
// class names and delegate signatures below were taken from the WebKit headers rather than recalled,
// but nothing here has been exercised against a real WKWebView.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace MS.Internal.Interop.WebView
{
    [SupportedOSPlatform("macos")]
    internal sealed unsafe class MacWebViewBackend : IWebViewBackend
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string WebKit = "/System/Library/Frameworks/WebKit.framework/WebKit";
        private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

        /// <summary>The name the shim posts through: window.webkit.messageHandlers.wpf.</summary>
        private const string MessageHandlerName = "wpf";

        /// <summary>
        /// Gives the page the same vocabulary every other head speaks. WKWebView has no
        /// window.chrome.webview, so it is built here on top of the message handler; without it the
        /// scripting bridge in WebBrowser (and every WebView2 application) would have to know which
        /// engine it was talking to.
        /// </summary>
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

        private IntPtr _webView;            // WKWebView*
        private IntPtr _navigationDelegate;
        private IntPtr _uiDelegate;
        private IntPtr _messageHandler;
        private IntPtr _hostView;           // the NSView the overlay lives in

        private GCHandle _self;
        private bool _visible = true;
        private WebViewSettings _settings = new WebViewSettings();
        private readonly List<string> _documentStartScripts = new List<string>();
        private TaskCompletionSource<object> _attach;
        private ulong _navigationId;

        public WebViewPresentation Presentation => WebViewPresentation.Overlay;

        public bool IsAttached => _webView != IntPtr.Zero;

        // ---- lifetime ---------------------------------------------------------------------------

        public Task AttachAsync(IntPtr ownerWindow)
        {
            if (_attach is not null)
            {
                return _attach.Task;
            }

            _attach = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The handle is the owner's content NSView, in the same sense
            // NativePlatform.CreateWindowSurface uses it on this head.
            _hostView = ownerWindow;
            _self = GCHandle.Alloc(this);

            dlopen(WebKit, RTLD_NOW);

            IntPtr configuration = Send(Send(Cls("WKWebViewConfiguration"), Sel("alloc")), Sel("init"));
            IntPtr controller = Send(configuration, Sel("userContentController"));

            _messageHandler = CreateHandler(EnsureMessageHandlerClass());
            SendVoidPtrPtr(controller, Sel("addScriptMessageHandler:name:"),
                           _messageHandler, NSString(MessageHandlerName));

            AddUserScript(controller, ChromeWebViewShim);

            IntPtr webView = Send(Cls("WKWebView"), Sel("alloc"));
            _webView = SendPtrRectPtr(webView, Sel("initWithFrame:configuration:"),
                                      new CGRect { X = 0, Y = 0, Width = 1, Height = 1 }, configuration);
            Retain(_webView);

            _navigationDelegate = CreateHandler(EnsureNavigationDelegateClass());
            _uiDelegate = CreateHandler(EnsureUIDelegateClass());
            SendVoidPtr(_webView, Sel("setNavigationDelegate:"), _navigationDelegate);
            SendVoidPtr(_webView, Sel("setUIDelegate:"), _uiDelegate);

            // Above the CAMetalLayer, which MacInterop deliberately puts at zPosition 1.0 so it sits
            // over sibling views. An overlay that does not clear that is drawn but never seen.
            SendVoidBool(_webView, Sel("setWantsLayer:"), true);
            IntPtr layer = Send(_webView, Sel("layer"));

            if (layer != IntPtr.Zero)
            {
                SendVoidDouble(layer, Sel("setZPosition:"), 2.0);
            }

            SendVoidPtrNIntPtr(_hostView, Sel("addSubview:positioned:relativeTo:"),
                               _webView, NSWindowAbove, IntPtr.Zero);

            ApplySettings(_settings);
            SetVisible(_visible);

            // WKWebView is usable the moment it is allocated -- unlike WebView2, which needs two
            // asynchronous steps -- so the task the seam promises completes immediately.
            _attach.TrySetResult(null);
            return _attach.Task;
        }

        public void Detach()
        {
            if (_webView != IntPtr.Zero)
            {
                SendVoidPtr(_webView, Sel("setNavigationDelegate:"), IntPtr.Zero);
                SendVoidPtr(_webView, Sel("setUIDelegate:"), IntPtr.Zero);
                Send(_webView, Sel("removeFromSuperview"));
                Release(_webView);
                _webView = IntPtr.Zero;
            }

            ReleaseHandler(ref _navigationDelegate);
            ReleaseHandler(ref _uiDelegate);
            ReleaseHandler(ref _messageHandler);

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _attach = null;
        }

        public void Dispose() => Detach();

        // ---- placement ----------------------------------------------------------------------------

        public void SetBounds(int x, int y, int width, int height, double scale)
        {
            if (_webView == IntPtr.Zero)
            {
                return;
            }

            // The caller works in device pixels with a top-left origin; AppKit wants points with a
            // BOTTOM-left origin, so the y coordinate is measured from the other end of the host.
            double s = scale <= 0 ? 1.0 : scale;
            CGRect host = SendRect(_hostView, Sel("bounds"));

            double w = width / s;
            double h = height / s;
            double left = x / s;
            double top = y / s;

            SendVoidRect(_webView, Sel("setFrame:"), new CGRect
            {
                X = left,
                Y = host.Height - top - h,
                Width = w,
                Height = h,
            });
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;

            if (_webView != IntPtr.Zero)
            {
                SendVoidBool(_webView, Sel("setHidden:"), !visible);
            }
        }

        // ---- navigation ----------------------------------------------------------------------------

        public void Navigate(string uri)
        {
            RequireAttached();

            IntPtr url = SendPtrRet(Cls("NSURL"), Sel("URLWithString:"), NSString(uri));

            if (url == IntPtr.Zero)
            {
                throw new ArgumentException("Not a valid URL: " + uri, nameof(uri));
            }

            // file: URLs need explicit read access to their directory, or WKWebView loads the page
            // and then refuses every relative resource in it.
            if (SendBool(url, Sel("isFileURL")))
            {
                IntPtr directory = Send(url, Sel("URLByDeletingLastPathComponent"));
                SendPtrPtrPtr(_webView, Sel("loadFileURL:allowingReadAccessToURL:"), url, directory);
                return;
            }

            IntPtr request = SendPtrRet(Cls("NSURLRequest"), Sel("requestWithURL:"), url);
            SendPtrRet(_webView, Sel("loadRequest:"), request);
        }

        public void NavigateToString(string htmlContent)
        {
            RequireAttached();
            SendPtrPtrPtr(_webView, Sel("loadHTMLString:baseURL:"), NSString(htmlContent), IntPtr.Zero);
        }

        public void NavigateWithPost(string uri, byte[] postData, string additionalHeaders)
        {
            RequireAttached();

            if ((postData is null || postData.Length == 0) && string.IsNullOrEmpty(additionalHeaders))
            {
                Navigate(uri);
                return;
            }

            IntPtr url = SendPtrRet(Cls("NSURL"), Sel("URLWithString:"), NSString(uri));

            if (url == IntPtr.Zero)
            {
                throw new ArgumentException("Not a valid URL: " + uri, nameof(uri));
            }

            IntPtr request = SendPtrRet(Send(Cls("NSMutableURLRequest"), Sel("alloc")),
                                        Sel("initWithURL:"), url);

            if (postData is not null && postData.Length > 0)
            {
                SendVoidPtr(request, Sel("setHTTPMethod:"), NSString("POST"));

                fixed (byte* p = postData)
                {
                    IntPtr body = SendPtrPtrNUInt(Cls("NSData"), Sel("dataWithBytes:length:"),
                                                  (IntPtr)p, (nuint)postData.Length);
                    SendVoidPtr(request, Sel("setHTTPBody:"), body);
                }
            }

            // "Name: value" per line, which is the shape WebBrowser.Navigate has always documented.
            if (!string.IsNullOrEmpty(additionalHeaders))
            {
                foreach (string line in additionalHeaders.Split('\n'))
                {
                    string header = line.Trim('\r', ' ');
                    int colon = header.IndexOf(':');

                    if (colon <= 0)
                    {
                        continue;
                    }

                    SendVoidPtrPtr(request, Sel("setValue:forHTTPHeaderField:"),
                                   NSString(header.Substring(colon + 1).Trim()),
                                   NSString(header.Substring(0, colon).Trim()));
                }
            }

            SendPtrRet(_webView, Sel("loadRequest:"), request);
            Release(request);
        }

        public void Reload(bool noCache)
        {
            RequireAttached();
            Send(_webView, noCache ? Sel("reloadFromOrigin") : Sel("reload"));
        }

        public void Stop()
        {
            RequireAttached();
            Send(_webView, Sel("stopLoading"));
        }

        public void GoBack()
        {
            RequireAttached();
            Send(_webView, Sel("goBack"));
        }

        public void GoForward()
        {
            RequireAttached();
            Send(_webView, Sel("goForward"));
        }

        public bool CanGoBack => _webView != IntPtr.Zero && SendBool(_webView, Sel("canGoBack"));

        public bool CanGoForward => _webView != IntPtr.Zero && SendBool(_webView, Sel("canGoForward"));

        public string Source =>
            _webView == IntPtr.Zero ? null : FromNSString(Send(Send(_webView, Sel("URL")), Sel("absoluteString")));

        public string DocumentTitle =>
            _webView == IntPtr.Zero ? null : FromNSString(Send(_webView, Sel("title")));

        // ---- scripting -------------------------------------------------------------------------------

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            RequireAttached();

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = GCHandle.Alloc(tcs);

            IntPtr block = ObjCBlock.Create(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ScriptCompleted,
                GCHandle.ToIntPtr(pending));

            SendVoidPtrPtr(_webView, Sel("evaluateJavaScript:completionHandler:"),
                           NSString(javaScript), block);

            return tcs.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ScriptCompleted(IntPtr block, IntPtr result, IntPtr error)
        {
            GCHandle handle = GCHandle.FromIntPtr(ObjCBlock.ContextOf(block));
            var tcs = (TaskCompletionSource<string>)handle.Target;

            try
            {
                if (error != IntPtr.Zero)
                {
                    tcs.TrySetException(new InvalidOperationException(
                        FromNSString(Send(error, Sel("localizedDescription"))) ?? "Script failed."));
                    return;
                }

                // The seam's contract is JSON, and evaluateJavaScript hands back a native object.
                // NSJSONSerialization is the honest converter -- but it refuses a bare scalar that is
                // not a top-level array or object, so those are written out directly.
                tcs.TrySetResult(ToJson(result));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                handle.Free();
                ObjCBlock.Release(block);
            }
        }

        /// <summary>Convert an Objective-C value from evaluateJavaScript into JSON text.</summary>
        private static string ToJson(IntPtr value)
        {
            if (value == IntPtr.Zero || value == Send(Cls("NSNull"), Sel("null")))
            {
                return "null";
            }

            if (SendBoolPtr(value, Sel("isKindOfClass:"), Cls("NSString")))
            {
                return WebViewScript.WriteJsonString(FromNSString(value));
            }

            if (SendBoolPtr(value, Sel("isKindOfClass:"), Cls("NSNumber")))
            {
                // A JavaScript boolean also arrives as an NSNumber; objCType is the only way to tell
                // true from 1, and a page that returns true expects `true` rather than `1`.
                IntPtr objCType = Send(value, Sel("objCType"));
                string type = Marshal.PtrToStringAnsi(objCType);

                if (type == "c" || type == "B")
                {
                    return SendBool(value, Sel("boolValue")) ? "true" : "false";
                }

                return FromNSString(Send(value, Sel("stringValue")));
            }

            IntPtr data = SendPtrPtrNUIntPtr(Cls("NSJSONSerialization"),
                                             Sel("dataWithJSONObject:options:error:"),
                                             value, 0, IntPtr.Zero);

            if (data == IntPtr.Zero)
            {
                return "null";
            }

            IntPtr str = SendPtrPtrNUInt(Send(Cls("NSString"), Sel("alloc")),
                                         Sel("initWithData:encoding:"), data, 4 /* NSUTF8StringEncoding */);
            string json = FromNSString(str);
            Release(str);
            return json ?? "null";
        }

        public void AddScriptToExecuteOnDocumentCreated(string javaScript)
        {
            RequireAttached();
            _documentStartScripts.Add(javaScript);

            IntPtr configuration = Send(_webView, Sel("configuration"));
            AddUserScript(Send(configuration, Sel("userContentController")), javaScript);
        }

        public void PostWebMessageAsJson(string webMessageAsJson) => DeliverToPage(webMessageAsJson);

        public void PostWebMessageAsString(string webMessageAsString) =>
            DeliverToPage(WebViewScript.WriteJsonString(webMessageAsString));

        /// <summary>
        /// Hand a message to the page's listeners. WKWebView has no postMessage-to-page primitive at
        /// all, so the shim's __deliver is called with the value spliced in as a literal.
        /// </summary>
        private void DeliverToPage(string jsonLiteral)
        {
            RequireAttached();
            _ = ExecuteScriptAsync("window.chrome.webview.__deliver(" + jsonLiteral + ");");
        }

        // ---- configuration ------------------------------------------------------------------------------

        public void ApplySettings(WebViewSettings settings)
        {
            _settings = settings ?? new WebViewSettings();

            if (_webView == IntPtr.Zero)
            {
                return;
            }

            IntPtr configuration = Send(_webView, Sel("configuration"));
            IntPtr preferences = Send(configuration, Sel("preferences"));

            SendVoidBoolPtr(preferences, Sel("setValue:forKey:"),
                            _settings.ScriptEnabled, NSString("javaScriptEnabled"));

            // The context menu and the developer tools are the two the platform actually exposes.
            SendVoidBoolPtr(preferences, Sel("setValue:forKey:"),
                            _settings.AreDevToolsEnabled, NSString("developerExtrasEnabled"));

            if (!string.IsNullOrEmpty(_settings.UserAgent))
            {
                SendVoidPtr(_webView, Sel("setCustomUserAgent:"), NSString(_settings.UserAgent));
            }
        }

        public double ZoomFactor
        {
            get => _webView == IntPtr.Zero ? 1.0 : SendDouble(_webView, Sel("pageZoom"));
            set
            {
                if (_webView != IntPtr.Zero)
                {
                    SendVoidDouble(_webView, Sel("setPageZoom:"), value);
                }
            }
        }

        public Task ClearBrowsingDataAsync()
        {
            RequireAttached();

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            IntPtr store = Send(Send(_webView, Sel("configuration")), Sel("websiteDataStore"));
            IntPtr types = Send(Cls("WKWebsiteDataStore"), Sel("allWebsiteDataTypes"));
            IntPtr since = SendPtrDouble(Cls("NSDate"), Sel("dateWithTimeIntervalSince1970:"), 0);

            var pending = GCHandle.Alloc(tcs);
            IntPtr block = ObjCBlock.Create(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&ClearCompleted,
                GCHandle.ToIntPtr(pending));

            SendVoidPtrPtrPtr(store, Sel("removeDataOfTypes:modifiedSince:completionHandler:"),
                              types, since, block);

            return tcs.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ClearCompleted(IntPtr block)
        {
            GCHandle handle = GCHandle.FromIntPtr(ObjCBlock.ContextOf(block));
            ((TaskCompletionSource<object>)handle.Target).TrySetResult(null);
            handle.Free();
            ObjCBlock.Release(block);
        }

        // ---- delegate callbacks ---------------------------------------------------------------------------

        private static MacWebViewBackend FromDelegate(IntPtr self)
        {
            IntPtr context = object_getIndexedIvars(self);
            IntPtr handle = *(IntPtr*)context;
            return handle == IntPtr.Zero ? null : GCHandle.FromIntPtr(handle).Target as MacWebViewBackend;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidStartProvisionalNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
        {
            MacWebViewBackend b = FromDelegate(self);
            b?.ContentLoading?.Invoke(b, EventArgs.Empty);
        }

        /// <summary>
        /// decidePolicyForNavigationAction: the only place a navigation can still be refused, which
        /// is what makes NavigationStarting.Cancel mean anything on this head.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DecidePolicyForNavigationAction(IntPtr self, IntPtr sel, IntPtr webView,
                                                            IntPtr action, IntPtr decisionHandler)
        {
            MacWebViewBackend b = FromDelegate(self);
            bool cancel = false;

            if (b is not null)
            {
                IntPtr request = Send(action, Sel("request"));
                IntPtr url = Send(request, Sel("URL"));

                var e = new WebViewNavigationStartingEventArgs
                {
                    Uri = FromNSString(Send(url, Sel("absoluteString"))),
                    // WKNavigationTypeLinkActivated(0) / FormSubmitted(1) / BackForward(2) /
                    // Reload(3) / FormResubmitted(4) are user gestures; Other(-1) is not.
                    IsUserInitiated = SendNInt(action, Sel("navigationType")) >= 0,
                    NavigationId = ++b._navigationId,
                };

                b.NavigationStarting?.Invoke(b, e);
                cancel = e.Cancel;
            }

            // WKNavigationActionPolicyCancel = 0, Allow = 1.
            InvokeDecisionHandler(decisionHandler, cancel ? 0 : 1);
        }

        private static void InvokeDecisionHandler(IntPtr block, nint policy)
        {
            // A decision handler is an ordinary block: its code pointer is the fourth word.
            var invoke = (delegate* unmanaged[Cdecl]<IntPtr, nint, void>)(*(IntPtr*)((byte*)block + 3 * IntPtr.Size));
            invoke(block, policy);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidCommitNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
        {
            MacWebViewBackend b = FromDelegate(self);
            b?.SourceChanged?.Invoke(b, new WebViewSourceChangedEventArgs { IsNewDocument = true });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidFinishNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
        {
            MacWebViewBackend b = FromDelegate(self);

            if (b is null)
            {
                return;
            }

            b.DocumentTitleChanged?.Invoke(b, EventArgs.Empty);
            b.NavigationCompleted?.Invoke(b, new WebViewNavigationCompletedEventArgs
            {
                IsSuccess = true,
                WebErrorStatus = WebViewErrorStatus.Unknown,
                NavigationId = b._navigationId,
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidFailNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation, IntPtr error)
        {
            MacWebViewBackend b = FromDelegate(self);

            b?.NavigationCompleted?.Invoke(b, new WebViewNavigationCompletedEventArgs
            {
                IsSuccess = false,
                WebErrorStatus = TranslateError(error),
                NavigationId = b._navigationId,
            });
        }

        /// <summary>
        /// NSURLError codes to the seam's vocabulary. Only the ones with an honest counterpart are
        /// mapped; everything else is Unknown rather than a plausible-looking guess.
        /// </summary>
        private static WebViewErrorStatus TranslateError(IntPtr error)
        {
            if (error == IntPtr.Zero)
            {
                return WebViewErrorStatus.Unknown;
            }

            return (long)SendNInt(error, Sel("code")) switch
            {
                -999 => WebViewErrorStatus.OperationCanceled,      // NSURLErrorCancelled
                -1001 => WebViewErrorStatus.Timeout,               // NSURLErrorTimedOut
                -1003 => WebViewErrorStatus.HostNameNotResolved,   // NSURLErrorCannotFindHost
                -1004 => WebViewErrorStatus.CannotConnect,         // NSURLErrorCannotConnectToHost
                -1005 => WebViewErrorStatus.ConnectionReset,       // NSURLErrorNetworkConnectionLost
                -1009 => WebViewErrorStatus.ServerUnreachable,     // NSURLErrorNotConnectedToInternet
                -1200 => WebViewErrorStatus.CertificateIsInvalid,  // NSURLErrorSecureConnectionFailed
                -1202 => WebViewErrorStatus.CertificateIsInvalid,  // NSURLErrorServerCertificateUntrusted
                _ => WebViewErrorStatus.Unknown,
            };
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidReceiveScriptMessage(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message)
        {
            MacWebViewBackend b = FromDelegate(self);

            if (b is null)
            {
                return;
            }

            string body = FromNSString(Send(message, Sel("body")));

            b.WebMessageReceived?.Invoke(b, new WebViewMessageReceivedEventArgs
            {
                Source = b.Source,
                // The shim always posts a string, so the JSON form is that string encoded.
                WebMessageAsJson = WebViewScript.WriteJsonString(body),
                WebMessageAsString = body,
            });
        }

        /// <summary>window.open. Returning nil tells WebKit not to create a view.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr CreateWebView(IntPtr self, IntPtr sel, IntPtr webView, IntPtr configuration,
                                            IntPtr navigationAction, IntPtr windowFeatures)
        {
            MacWebViewBackend b = FromDelegate(self);

            if (b is null)
            {
                return IntPtr.Zero;
            }

            IntPtr url = Send(Send(navigationAction, Sel("request")), Sel("URL"));

            var e = new WebViewNewWindowRequestedEventArgs
            {
                Uri = FromNSString(Send(url, Sel("absoluteString"))),
                IsUserInitiated = true,
            };

            b.NewWindowRequested?.Invoke(b, e);

            // Unhandled means "open it somewhere". WebKit will not do it for us without a new view,
            // so the closest honest behaviour is to follow the link in place, which is also what the
            // WebOC did for a target it could not honour.
            if (!e.Handled && !string.IsNullOrEmpty(e.Uri))
            {
                b.Navigate(e.Uri);
            }

            return IntPtr.Zero;
        }

        // ---- runtime-registered Objective-C classes ------------------------------------------------------

        private static IntPtr s_navigationDelegateClass;
        private static IntPtr s_messageHandlerClass;
        private static IntPtr s_uiDelegateClass;

        /// <summary>
        /// Allocate an instance of one of the classes below with room for the GCHandle that points
        /// back at the backend. The handle lives in the class's indexed ivars, which is what the
        /// extraBytes argument to objc_allocateClassPair reserved.
        /// </summary>
        private IntPtr CreateHandler(IntPtr cls)
        {
            if (cls == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr instance = Send(Send(cls, Sel("alloc")), Sel("init"));
            *(IntPtr*)object_getIndexedIvars(instance) = GCHandle.ToIntPtr(_self);
            return instance;
        }

        private static void ReleaseHandler(ref IntPtr handler)
        {
            if (handler != IntPtr.Zero)
            {
                Release(handler);
                handler = IntPtr.Zero;
            }
        }

        private static IntPtr EnsureNavigationDelegateClass()
        {
            if (s_navigationDelegateClass != IntPtr.Zero)
            {
                return s_navigationDelegateClass;
            }

            // Re-registering an existing class pair aborts the process, so look it up first (the
            // same guard CocoaWindow.EnsureContentViewClass uses).
            IntPtr existing = objc_getClass("WpfWKNavigationDelegate");

            if (existing != IntPtr.Zero)
            {
                return s_navigationDelegateClass = existing;
            }

            IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "WpfWKNavigationDelegate",
                                                (UIntPtr)IntPtr.Size);

            class_addMethod(cls, Sel("webView:didStartProvisionalNavigation:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidStartProvisionalNavigation,
                "v@:@@");
            class_addMethod(cls, Sel("webView:didCommitNavigation:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidCommitNavigation,
                "v@:@@");
            class_addMethod(cls, Sel("webView:didFinishNavigation:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFinishNavigation,
                "v@:@@");
            class_addMethod(cls, Sel("webView:didFailNavigation:withError:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFailNavigation,
                "v@:@@@");
            class_addMethod(cls, Sel("webView:didFailProvisionalNavigation:withError:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFailNavigation,
                "v@:@@@");
            class_addMethod(cls, Sel("webView:decidePolicyForNavigationAction:decisionHandler:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DecidePolicyForNavigationAction,
                "v@:@@@");

            objc_registerClassPair(cls);
            return s_navigationDelegateClass = cls;
        }

        private static IntPtr EnsureMessageHandlerClass()
        {
            if (s_messageHandlerClass != IntPtr.Zero)
            {
                return s_messageHandlerClass;
            }

            IntPtr existing = objc_getClass("WpfWKScriptMessageHandler");

            if (existing != IntPtr.Zero)
            {
                return s_messageHandlerClass = existing;
            }

            IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "WpfWKScriptMessageHandler",
                                                (UIntPtr)IntPtr.Size);

            class_addMethod(cls, Sel("userContentController:didReceiveScriptMessage:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidReceiveScriptMessage,
                "v@:@@");

            objc_registerClassPair(cls);
            return s_messageHandlerClass = cls;
        }

        private static IntPtr EnsureUIDelegateClass()
        {
            if (s_uiDelegateClass != IntPtr.Zero)
            {
                return s_uiDelegateClass;
            }

            IntPtr existing = objc_getClass("WpfWKUIDelegate");

            if (existing != IntPtr.Zero)
            {
                return s_uiDelegateClass = existing;
            }

            IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "WpfWKUIDelegate", (UIntPtr)IntPtr.Size);

            class_addMethod(cls,
                Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&CreateWebView,
                "@@:@@@@");

            objc_registerClassPair(cls);
            return s_uiDelegateClass = cls;
        }

        private static void AddUserScript(IntPtr controller, string source)
        {
            IntPtr script = Send(Cls("WKUserScript"), Sel("alloc"));

            // injectionTime 0 == AtDocumentStart, forMainFrameOnly NO so frames get the shim too.
            script = SendPtrPtrNIntBool(script, Sel("initWithSource:injectionTime:forMainFrameOnly:"),
                                        NSString(source), 0, false);

            SendVoidPtr(controller, Sel("addUserScript:"), script);
            Release(script);
        }

        private void RequireAttached()
        {
            if (_webView == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The web view is not ready yet; await AttachAsync before using it.");
            }
        }

        // ---- events ------------------------------------------------------------------------------------

        public event EventHandler<WebViewNavigationStartingEventArgs> NavigationStarting;
        public event EventHandler<WebViewSourceChangedEventArgs> SourceChanged;
        public event EventHandler ContentLoading;
        public event EventHandler<WebViewNavigationCompletedEventArgs> NavigationCompleted;
        public event EventHandler<WebViewMessageReceivedEventArgs> WebMessageReceived;
        public event EventHandler<WebViewNewWindowRequestedEventArgs> NewWindowRequested;
        public event EventHandler<WebViewDownloadStartingEventArgs> DownloadStarting;
        public event EventHandler DocumentTitleChanged;
        public event EventHandler<WebViewProcessFailedEventArgs> ProcessFailed;

        // ---- interop ------------------------------------------------------------------------------------

        private const int RTLD_NOW = 2;
        private const nint NSWindowAbove = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect
        {
            public double X, Y, Width, Height;
        }

        private static IntPtr Cls(string name) => objc_getClass(name);

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr Retain(IntPtr o) => o == IntPtr.Zero ? o : Send(o, Sel("retain"));

        private static void Release(IntPtr o)
        {
            if (o != IntPtr.Zero)
            {
                Send(o, Sel("release"));
            }
        }

        private static IntPtr NSString(string s) =>
            s is null ? IntPtr.Zero : SendPtrRet(Cls("NSString"), Sel("stringWithUTF8String:"), Utf8(s));

        private static IntPtr Utf8(string s)
        {
            // Freed by the autorelease pool's copy of the string, not here: stringWithUTF8String:
            // copies the bytes, so the buffer only has to outlive the call.
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
            IntPtr p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        private static string FromNSString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero)
            {
                return null;
            }

            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] private static extern IntPtr object_getIndexedIvars(IntPtr obj);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        // objc_msgSend is variadic in C; declare one typed alias per call shape we use.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b, IntPtr c);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrNIntPtr(IntPtr r, IntPtr s, IntPtr a, nint b, IntPtr c);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBoolPtr(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidDouble(IntPtr r, IntPtr s, double a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRect(IntPtr r, IntPtr s, CGRect a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrNUInt(IntPtr r, IntPtr s, IntPtr a, nuint b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrNUIntPtr(IntPtr r, IntPtr s, IntPtr a, nuint b, IntPtr c);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrNIntBool(IntPtr r, IntPtr s, IntPtr a, nint b, [MarshalAs(UnmanagedType.I1)] bool c);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRectPtr(IntPtr r, IntPtr s, CGRect a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrDouble(IntPtr r, IntPtr s, double a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBool(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBoolPtr(IntPtr r, IntPtr s, IntPtr a);
    }
}
