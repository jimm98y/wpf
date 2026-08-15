// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// IOSWebViewBackend -- the iOS IWebViewBackend, backed by WKWebView.
//
// The same engine as macOS, and deliberately the same structure: three runtime-registered
// Objective-C classes (navigation delegate, script-message handler, UI delegate), the same
// window.chrome.webview shim so a page sees one vocabulary on every head, and evaluateJavaScript
// through ObjCBlock. Read MacWebViewBackend first; this file documents only where iOS differs, and
// it differs in five places that matter:
//
//   1. libobjc lives at /usr/lib/libobjc.dylib here, not /usr/lib/libobjc.A.dylib. That alone
//      forces a separate class, because a DllImport path is fixed at compile time -- the same
//      reason IosInterop and MacInterop are separate in WgpuInterop.
//   2. UIKit's origin is TOP-LEFT, like the seam's own coordinates, so SetBounds is a straight copy
//      rather than the y-flip AppKit needs.
//   3. addSubview: has no positioned:relativeTo: form. Ordering above the Metal-backed view is done
//      purely with the layer's zPosition (UIKitWindow's WpfMetalView IS a CAMetalLayer-backed view).
//   4. WKWebView has no pageZoom on iOS -- it is a macOS-only property. Zoom goes through the
//      page's own text-size adjustment instead, which is what it means on a touch platform.
//   5. Developer tools are `inspectable` (iOS 16.4 and later), not `developerExtrasEnabled`, and are
//      set through the KVC path so an older system simply ignores them rather than throwing.
//
// NOT YET RUN ON A DEVICE. It compiles into every head; the selectors were taken from the WebKit and
// UIKit headers rather than recalled, but nothing here has been exercised against a real WKWebView.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace MS.Internal.Interop.WebView
{
    [SupportedOSPlatform("ios")]
    internal sealed unsafe class IOSWebViewBackend : IWebViewBackend
    {
        private const string ObjC = "/usr/lib/libobjc.dylib";
        private const string WebKit = "/System/Library/Frameworks/WebKit.framework/WebKit";

        private const string MessageHandlerName = "wpf";

        /// <summary>Identical to the macOS shim; see MacWebViewBackend for why it exists.</summary>
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

        private IntPtr _webView;
        private IntPtr _navigationDelegate;
        private IntPtr _uiDelegate;
        private IntPtr _messageHandler;
        private IntPtr _hostView;

        private GCHandle _self;
        private bool _visible = true;
        private double _zoom = 1.0;
        private WebViewSettings _settings = new WebViewSettings();
        private TaskCompletionSource<object> _attach;
        private ulong _navigationId;

        public WebViewPresentation Presentation => WebViewPresentation.Overlay;

        public bool IsAttached => _webView != IntPtr.Zero;

        public Task AttachAsync(IntPtr ownerWindow)
        {
            if (_attach is not null)
            {
                return _attach.Task;
            }

            _attach = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            // On this head the window handle IS a UIView* (see UIKitWindow).
            _hostView = ownerWindow;
            _self = GCHandle.Alloc(this);

            dlopen(WebKit, RTLD_NOW);

            IntPtr configuration = Send(Send(Cls("WKWebViewConfiguration"), Sel("alloc")), Sel("init"));
            IntPtr controller = Send(configuration, Sel("userContentController"));

            _messageHandler = CreateHandler(EnsureMessageHandlerClass());
            SendVoidPtrPtr(controller, Sel("addScriptMessageHandler:name:"),
                           _messageHandler, NSString(MessageHandlerName));

            AddUserScript(controller, ChromeWebViewShim);

            // Inline media, and no automatic full-screen takeover: an embedded view inside an
            // application is not a media player, and the defaults are the other way round on iOS.
            SendVoidBool(configuration, Sel("setAllowsInlineMediaPlayback:"), true);

            IntPtr webView = Send(Cls("WKWebView"), Sel("alloc"));
            _webView = SendPtrRectPtr(webView, Sel("initWithFrame:configuration:"),
                                      new CGRect { X = 0, Y = 0, Width = 1, Height = 1 }, configuration);
            Retain(_webView);

            _navigationDelegate = CreateHandler(EnsureNavigationDelegateClass());
            _uiDelegate = CreateHandler(EnsureUIDelegateClass());
            SendVoidPtr(_webView, Sel("setNavigationDelegate:"), _navigationDelegate);
            SendVoidPtr(_webView, Sel("setUIDelegate:"), _uiDelegate);

            // Above the Metal-backed view. There is no positioned:relativeTo: on iOS, so the layer's
            // zPosition is the whole of the ordering.
            IntPtr layer = Send(_webView, Sel("layer"));

            if (layer != IntPtr.Zero)
            {
                SendVoidDouble(layer, Sel("setZPosition:"), 2.0);
            }

            SendVoidPtr(_hostView, Sel("addSubview:"), _webView);

            ApplySettings(_settings);
            SetVisible(_visible);

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

            // No y-flip: UIKit measures from the top-left, exactly as the caller does.
            double s = scale <= 0 ? 1.0 : scale;

            SendVoidRect(_webView, Sel("setFrame:"), new CGRect
            {
                X = x / s,
                Y = y / s,
                Width = width / s,
                Height = height / s,
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

        // ---- navigation ---------------------------------------------------------------------------

        public void Navigate(string uri)
        {
            RequireAttached();

            IntPtr url = SendPtrRet(Cls("NSURL"), Sel("URLWithString:"), NSString(uri));

            if (url == IntPtr.Zero)
            {
                throw new ArgumentException("Not a valid URL: " + uri, nameof(uri));
            }

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

        // ---- scripting ------------------------------------------------------------------------------

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

        /// <summary>See MacWebViewBackend.ToJson: NSJSONSerialization refuses a bare scalar.</summary>
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
                // true from 1, and a page returning true expects `true` rather than `1`.
                string type = Marshal.PtrToStringAnsi(Send(value, Sel("objCType")));

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
            AddUserScript(Send(Send(_webView, Sel("configuration")), Sel("userContentController")), javaScript);
        }

        public void PostWebMessageAsJson(string webMessageAsJson) => DeliverToPage(webMessageAsJson);

        public void PostWebMessageAsString(string webMessageAsString) =>
            DeliverToPage(WebViewScript.WriteJsonString(webMessageAsString));

        private void DeliverToPage(string jsonLiteral)
        {
            RequireAttached();
            _ = ExecuteScriptAsync("window.chrome.webview.__deliver(" + jsonLiteral + ");");
        }

        // ---- configuration ----------------------------------------------------------------------------

        public void ApplySettings(WebViewSettings settings)
        {
            _settings = settings ?? new WebViewSettings();

            if (_webView == IntPtr.Zero)
            {
                return;
            }

            IntPtr preferences = Send(Send(_webView, Sel("configuration")), Sel("preferences"));

            SendVoidBoolPtr(preferences, Sel("setValue:forKey:"),
                            _settings.ScriptEnabled, NSString("javaScriptEnabled"));

            // `inspectable` is iOS 16.4 and later. Set through KVC so an older system ignores an
            // unknown key rather than failing the whole settings push.
            SendVoidBoolPtr(_webView, Sel("setValue:forKey:"),
                            _settings.AreDevToolsEnabled, NSString("inspectable"));

            if (!string.IsNullOrEmpty(_settings.UserAgent))
            {
                SendVoidPtr(_webView, Sel("setCustomUserAgent:"), NSString(_settings.UserAgent));
            }
        }

        /// <summary>
        /// WKWebView has no pageZoom on iOS -- it is macOS-only -- so this is the page's own text
        /// scaling, which is what zoom means on a touch platform. Reported from the last value set
        /// rather than read back, because the page can be pinch-zoomed independently and that is a
        /// different thing from the zoom an application asked for.
        /// </summary>
        public double ZoomFactor
        {
            get => _zoom;
            set
            {
                _zoom = value <= 0 ? 1.0 : value;

                if (_webView != IntPtr.Zero)
                {
                    _ = ExecuteScriptAsync(
                        "document.documentElement.style.webkitTextSizeAdjust='" +
                        (int)Math.Round(_zoom * 100) + "%'");
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

        // ---- delegate callbacks -------------------------------------------------------------------------

        private static IOSWebViewBackend FromDelegate(IntPtr self)
        {
            IntPtr handle = *(IntPtr*)object_getIndexedIvars(self);
            return handle == IntPtr.Zero ? null : GCHandle.FromIntPtr(handle).Target as IOSWebViewBackend;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidStartProvisionalNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
        {
            IOSWebViewBackend b = FromDelegate(self);
            b?.ContentLoading?.Invoke(b, EventArgs.Empty);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DecidePolicyForNavigationAction(IntPtr self, IntPtr sel, IntPtr webView,
                                                            IntPtr action, IntPtr decisionHandler)
        {
            IOSWebViewBackend b = FromDelegate(self);
            bool cancel = false;

            if (b is not null)
            {
                IntPtr url = Send(Send(action, Sel("request")), Sel("URL"));

                var e = new WebViewNavigationStartingEventArgs
                {
                    Uri = FromNSString(Send(url, Sel("absoluteString"))),
                    IsUserInitiated = SendNInt(action, Sel("navigationType")) >= 0,
                    NavigationId = ++b._navigationId,
                };

                b.NavigationStarting?.Invoke(b, e);
                cancel = e.Cancel;
            }

            // WKNavigationActionPolicyCancel = 0, Allow = 1.
            var invoke = (delegate* unmanaged[Cdecl]<IntPtr, nint, void>)
                (*(IntPtr*)((byte*)decisionHandler + 3 * IntPtr.Size));
            invoke(decisionHandler, cancel ? 0 : 1);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidCommitNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
        {
            IOSWebViewBackend b = FromDelegate(self);
            b?.SourceChanged?.Invoke(b, new WebViewSourceChangedEventArgs { IsNewDocument = true });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidFinishNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
        {
            IOSWebViewBackend b = FromDelegate(self);

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
            IOSWebViewBackend b = FromDelegate(self);

            b?.NavigationCompleted?.Invoke(b, new WebViewNavigationCompletedEventArgs
            {
                IsSuccess = false,
                WebErrorStatus = TranslateError(error),
                NavigationId = b._navigationId,
            });
        }

        /// <summary>NSURLError codes, as MacWebViewBackend maps them.</summary>
        private static WebViewErrorStatus TranslateError(IntPtr error)
        {
            if (error == IntPtr.Zero)
            {
                return WebViewErrorStatus.Unknown;
            }

            return (long)SendNInt(error, Sel("code")) switch
            {
                -999 => WebViewErrorStatus.OperationCanceled,
                -1001 => WebViewErrorStatus.Timeout,
                -1003 => WebViewErrorStatus.HostNameNotResolved,
                -1004 => WebViewErrorStatus.CannotConnect,
                -1005 => WebViewErrorStatus.ConnectionReset,
                -1009 => WebViewErrorStatus.ServerUnreachable,
                -1200 => WebViewErrorStatus.CertificateIsInvalid,
                -1202 => WebViewErrorStatus.CertificateIsInvalid,
                _ => WebViewErrorStatus.Unknown,
            };
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidReceiveScriptMessage(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message)
        {
            IOSWebViewBackend b = FromDelegate(self);

            if (b is null)
            {
                return;
            }

            string body = FromNSString(Send(message, Sel("body")));

            b.WebMessageReceived?.Invoke(b, new WebViewMessageReceivedEventArgs
            {
                Source = b.Source,
                WebMessageAsJson = WebViewScript.WriteJsonString(body),
                WebMessageAsString = body,
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr CreateWebView(IntPtr self, IntPtr sel, IntPtr webView, IntPtr configuration,
                                            IntPtr navigationAction, IntPtr windowFeatures)
        {
            IOSWebViewBackend b = FromDelegate(self);

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

            // Unhandled means "open it somewhere"; there is no second window to open it in, so the
            // closest honest behaviour is to follow it in place.
            if (!e.Handled && !string.IsNullOrEmpty(e.Uri))
            {
                b.Navigate(e.Uri);
            }

            return IntPtr.Zero;
        }

        // ---- runtime-registered classes --------------------------------------------------------------------

        private static IntPtr s_navigationDelegateClass;
        private static IntPtr s_messageHandlerClass;
        private static IntPtr s_uiDelegateClass;

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

            // Re-registering an existing class pair aborts the process, so look it up first.
            IntPtr existing = objc_getClass("WpfIosWKNavigationDelegate");

            if (existing != IntPtr.Zero)
            {
                return s_navigationDelegateClass = existing;
            }

            IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "WpfIosWKNavigationDelegate",
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

            IntPtr existing = objc_getClass("WpfIosWKScriptMessageHandler");

            if (existing != IntPtr.Zero)
            {
                return s_messageHandlerClass = existing;
            }

            IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "WpfIosWKScriptMessageHandler",
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

            IntPtr existing = objc_getClass("WpfIosWKUIDelegate");

            if (existing != IntPtr.Zero)
            {
                return s_uiDelegateClass = existing;
            }

            IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "WpfIosWKUIDelegate", (UIntPtr)IntPtr.Size);

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

        // ---- events -------------------------------------------------------------------------------------

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

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b, IntPtr c);
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
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBool(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBoolPtr(IntPtr r, IntPtr s, IntPtr a);
    }
}
