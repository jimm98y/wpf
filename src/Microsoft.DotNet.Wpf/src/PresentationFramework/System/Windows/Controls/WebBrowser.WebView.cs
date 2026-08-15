// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WebBrowser, driven by a modern web engine instead of the IE WebOC.
//
// The other half of this class is the original ActiveX host, still intact and still reachable on
// Windows through the AppContext switch documented on ActiveXHost.UseLegacyActiveX. Everything here
// is the default path, on every head including Windows: the control keeps its exact public surface
// (which ApiCompat enforces against the reference assembly) and routes it to IWebViewBackend.
//
// The interesting part is not the forwarding, it is the three places where the WebOC's shape does not
// survive contact with an engine that answers asynchronously:
//
//   * NAVIGATION BEFORE THE WINDOW EXISTS. HwndHost does not build its window until the element is
//     first shown, so `new WebBrowser { Source = uri }` legitimately navigates before there is any
//     engine. The WebOC could take it because it was created eagerly; here the request is remembered
//     and replayed once AttachAsync completes.
//   * INVOKESCRIPT IS SYNCHRONOUS. Its signature returns object, not Task<object>, and it is public
//     API that cannot change. Every engine's script evaluation is asynchronous. See the remarks on
//     WebViewInvokeScript for what is done about that and why it is safe here specifically.
//   * OBJECTFORSCRIPTING WAS IDISPATCH. window.external reached a COM object directly. The bridge
//     below re-creates it over the message channel: a script shim collects the call, the host
//     invokes the member by reflection, and the result goes back as JSON.
//

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Navigation;
using MS.Internal.AppModel;
using MS.Internal.Interop.WebView;
using MS.Internal.Utility;
using PackUriHelper = MS.Internal.IO.Packaging.PackUriHelper;

namespace System.Windows.Controls
{
    public sealed partial class WebBrowser
    {
        /// <summary>
        /// A navigation asked for before the engine existed, replayed once it does. HwndHost builds
        /// its window lazily, so this is the normal case for a Source set in an initializer, not an
        /// edge case.
        /// </summary>
        private Action _pendingNavigation;

        /// <summary>The last URI handed to the engine, so Source can answer before the first
        /// SourceChanged arrives and can keep answering null for a string/stream navigation.</summary>
        private Uri _lastRequestedUri;

        private bool _navigatedToStringOrStream;

        // ---- lifetime ---------------------------------------------------------------------------

        internal override void OnWebViewAttaching(IWebViewBackend backend, Task ready)
        {
            // Subscribed here rather than after awaiting `ready`: the engine can begin its first
            // navigation as soon as it exists, and a handler attached later would miss the
            // Navigating event for it.
            backend.NavigationStarting += OnBackendNavigationStarting;
            backend.SourceChanged += OnBackendSourceChanged;
            backend.NavigationCompleted += OnBackendNavigationCompleted;
            backend.WebMessageReceived += OnBackendWebMessageReceived;

            // Continued onto the Dispatcher rather than through
            // TaskScheduler.FromCurrentSynchronizationContext: the window is built during the first
            // measure pass, which can run before Dispatcher.Run has installed its synchronization
            // context, and asking for a scheduler then throws. The Dispatcher itself is valid from
            // the moment the element exists.
            ready.ContinueWith(
                t => Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (t.IsFaulted)
                    {
                        // Nothing can be shown, and swallowing this would leave a permanently blank
                        // control with no explanation anywhere.
                        throw new InvalidOperationException(
                            SR.WebBrowserNoEngine, t.Exception?.GetBaseException());
                    }

                    InstallScriptingBridge();

                    Action pending = _pendingNavigation;
                    _pendingNavigation = null;
                    pending?.Invoke();
                })),
                TaskScheduler.Default);
        }

        /// <summary>Run <paramref name="action"/> now if the engine is ready, or when it becomes ready.</summary>
        private void WhenReady(Action action)
        {
            if (WebViewBackend is not null && WebViewBackend.IsAttached)
            {
                action();
                return;
            }

            // Only the most recent request survives: a control whose Source is set three times
            // before it is shown should end up at the third page, not walk through all three.
            _pendingNavigation = action;
        }

        // ---- navigation --------------------------------------------------------------------------

        private void WebViewDoNavigate(Uri source, string targetFrameName, byte[] postData,
                                       string additionalHeaders, bool ignoreEscaping)
        {
            // Same normalisation the WebOC path does, and for the same reasons -- these are part of
            // the control's documented behaviour, not of the engine underneath it.
            if (source is null)
            {
                // Source = null, or a string/stream navigation that routes through about:blank.
                Stream stream = DocumentStream;

                if (stream is not null)
                {
                    NavigateToStreamContent(stream);
                    return;
                }

                _lastRequestedUri = null;
                _navigatedToStringOrStream = false;
                WhenReady(() => WebViewBackend.Navigate(AboutBlankUriString));
                return;
            }

            if (!source.IsAbsoluteUri)
            {
                throw new ArgumentException(SR.AbsoluteUriOnly, nameof(source));
            }

            if (PackUriHelper.IsPackUri(source))
            {
                source = BaseUriHelper.ConvertPackUriToAbsoluteExternallyVisibleUri(source);
            }

            // See the WebOC path: the string overloads exist so a URI carrying invalid UTF-8
            // sequences reaches the engine intact rather than being silently sanitised.
            string uri = ignoreEscaping ? source.AbsoluteUri : BindUriHelper.UriToString(source);

            _lastRequestedUri = source;
            _navigatedToStringOrStream = false;
            CleanInternalState();

            if (!string.IsNullOrEmpty(targetFrameName))
            {
                // Targeting a named frame means "navigate that frame, or open a window if it does
                // not exist", which is a WebOC behaviour with no equivalent in the engines here.
                // Refusing beats navigating the top frame instead: the caller asked for one frame to
                // change and would get the whole page replaced.
                throw new NotSupportedException(SR.WebBrowserTargetFrameNotSupported);
            }

            bool hasBody = (postData is not null && postData.Length > 0) ||
                           !string.IsNullOrEmpty(additionalHeaders);

            WhenReady(() =>
            {
                if (hasBody)
                {
                    WebViewBackend.NavigateWithPost(uri, postData, additionalHeaders);
                }
                else
                {
                    WebViewBackend.Navigate(uri);
                }
            });
        }

        /// <summary>
        /// NavigateToStream/NavigateToString, which the WebOC implemented by navigating to
        /// about:blank and then pushing the bytes in through IPersistStreamInit. Every engine here
        /// takes the content directly, so the round trip is unnecessary.
        /// </summary>
        private void NavigateToStreamContent(Stream stream)
        {
            string html;

            // Left open deliberately: NavigateToStream's caller owns the stream, and the WebOC never
            // closed it either.
            long origin = stream.CanSeek ? stream.Position : 0;

            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                                                 bufferSize: 4096, leaveOpen: true))
            {
                html = reader.ReadToEnd();
            }

            if (stream.CanSeek)
            {
                stream.Position = origin;
            }

            _lastRequestedUri = null;
            _navigatedToStringOrStream = true;
            NavigatingToAboutBlank = true;

            WhenReady(() => WebViewBackend.NavigateToString(html));
        }

        private void WebViewGoBack() => WhenReady(() => WebViewBackend.GoBack());

        private void WebViewGoForward() => WhenReady(() => WebViewBackend.GoForward());

        private void WebViewRefresh(bool noCache) => WhenReady(() => WebViewBackend.Reload(noCache));

        private Uri WebViewSource
        {
            get
            {
                // A string/stream navigation reports null, exactly as the WebOC path does -- the
                // about:blank it really sits on is an implementation detail the control has always
                // hidden.
                if (_navigatedToStringOrStream)
                {
                    return null;
                }

                string current = WebViewBackend?.Source;

                if (string.IsNullOrEmpty(current) || current == AboutBlankUriString)
                {
                    return _lastRequestedUri;
                }

                return Uri.TryCreate(current, UriKind.Absolute, out Uri parsed) ? parsed : _lastRequestedUri;
            }
        }

        // ---- events -------------------------------------------------------------------------------

        private void OnBackendNavigationStarting(object sender, WebViewNavigationStartingEventArgs e)
        {
            Uri source = _navigatedToStringOrStream || NavigatingToAboutBlank
                ? null
                : ToUriOrNull(e.Uri);

            var args = new NavigatingCancelEventArgs(
                source, null, null, null, NavigationMode.New, null, null, true);

            // Reentrancy guard, exactly as the WebOC path uses: a handler that starts a new
            // navigation must cancel this one rather than have both run.
            Guid lastNavigation = LastNavigation;
            LastNavigation = Guid.NewGuid();
            Guid thisNavigation = LastNavigation;

            OnNavigating(args);

            e.Cancel = args.Cancel || LastNavigation != thisNavigation;
        }

        private void OnBackendSourceChanged(object sender, WebViewSourceChangedEventArgs e)
        {
            OnNavigated(new NavigationEventArgs(WebViewSource, null, null, null, null, true));
        }

        private void OnBackendNavigationCompleted(object sender, WebViewNavigationCompletedEventArgs e)
        {
            // SourceChanged does not fire for a NavigateToString, so without this a string
            // navigation would raise LoadCompleted having never raised Navigated.
            if (_navigatedToStringOrStream)
            {
                OnNavigated(new NavigationEventArgs(null, null, null, null, null, true));
            }

            OnLoadCompleted(new NavigationEventArgs(WebViewSource, null, null, null, null, true));
        }

        private static Uri ToUriOrNull(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) ? parsed : null;

        // ---- scripting -----------------------------------------------------------------------------

        /// <summary>
        /// InvokeScript, whose signature returns <see cref="object"/> and therefore cannot become
        /// asynchronous without breaking every caller.
        /// </summary>
        /// <remarks>
        /// The engine answers asynchronously, so the result has to be waited for. That is safe here
        /// and would not be in general: the wait pumps the dispatcher rather than blocking it (a
        /// plain .Result would deadlock, because the engine delivers its completion through this
        /// very thread's message loop). Reentrancy during the pump is the same exposure the WebOC
        /// had -- IDispatchEx::InvokeEx also ran script that could call back into the application.
        /// </remarks>
        private object WebViewInvokeScript(string scriptName, object[] args)
        {
            if (WebViewBackend is null || !WebViewBackend.IsAttached)
            {
                throw new InvalidOperationException(SR.CannotInvokeScript);
            }

            string expression = WebViewScript.BuildInvokeExpression(scriptName, args);
            Task<string> pending = WebViewBackend.ExecuteScriptAsync(expression);

            var frame = new System.Windows.Threading.DispatcherFrame();

            pending.ContinueWith(
                _ => Dispatcher.BeginInvoke((Action)(() => frame.Continue = false)),
                TaskScheduler.Default);

            System.Windows.Threading.Dispatcher.PushFrame(frame);

            if (pending.IsFaulted)
            {
                throw new InvalidOperationException(SR.CannotInvokeScript, pending.Exception?.GetBaseException());
            }

            return WebViewScript.FromJson(pending.Result);
        }

        /// <summary>
        /// The name the page sees. window.external is what the WebOC exposed; window.chrome.webview
        /// is what a WebView2 page expects, and both reach the same object here.
        /// </summary>
        private const string ScriptingBridgeMessagePrefix = "__wpfExternal:";

        private void WebViewObjectForScriptingChanged()
        {
            if (WebViewBackend is not null && WebViewBackend.IsAttached)
            {
                InstallScriptingBridge();
            }
        }

        /// <summary>
        /// Re-create window.external over the message channel. The shim turns any member access into
        /// a message; the host resolves it by reflection and posts the result back, which resolves
        /// the promise the page is holding.
        /// </summary>
        private void InstallScriptingBridge()
        {
            if (_objectForScripting is null || WebViewBackend is null)
            {
                return;
            }

            WebViewBackend.AddScriptToExecuteOnDocumentCreated(@"
(function () {
    if (window.external && window.external.__wpf) { return; }
    var pending = {}, next = 0;
    window.chrome.webview.addEventListener('message', function (e) {
        var d = e.data;
        if (typeof d !== 'string' || d.indexOf('" + ScriptingBridgeMessagePrefix + @"') !== 0) { return; }
        var reply = JSON.parse(d.substring(" + ScriptingBridgeMessagePrefix.Length + @"));
        var slot = pending[reply.id];
        if (!slot) { return; }
        delete pending[reply.id];
        if (reply.error) { slot.reject(new Error(reply.error)); } else { slot.resolve(reply.result); }
    });
    window.external = new Proxy({ __wpf: true }, {
        get: function (target, name) {
            if (name in target) { return target[name]; }
            return function () {
                var id = ++next, a = Array.prototype.slice.call(arguments);
                var p = new Promise(function (res, rej) { pending[id] = { resolve: res, reject: rej }; });
                window.chrome.webview.postMessage('" + ScriptingBridgeMessagePrefix + @"' +
                    JSON.stringify({ id: id, member: String(name), args: a }));
                return p;
            };
        }
    });
})();");
        }

        private void OnBackendWebMessageReceived(object sender, WebViewMessageReceivedEventArgs e)
        {
            string text = e.WebMessageAsString;

            if (text is null || !text.StartsWith(ScriptingBridgeMessagePrefix, StringComparison.Ordinal))
            {
                return;
            }

            object target = _objectForScripting;

            if (target is null)
            {
                return;
            }

            string payload = text.Substring(ScriptingBridgeMessagePrefix.Length);

            // The id is a number and travels as-is; the member is a JSON *string*, so it arrives
            // still quoted and has to be decoded before it can name a member. Passing the raw token
            // to reflection looks up a member literally called "\"Greet\"", which fails in a way
            // that surfaces to the page as a rejected promise and nowhere else.
            string id = ReadJsonMember(payload, "id");
            string member = WebViewScript.FromJson(ReadJsonMember(payload, "member")) as string;

            if (string.IsNullOrEmpty(member))
            {
                return;
            }

            string reply;

            try
            {
                object result = InvokeScriptingMember(target, member, payload);
                reply = "{\"id\":" + id + ",\"result\":" + WebViewScript.ToJson(result) + "}";
            }
            catch (Exception ex)
            {
                // The page is holding a promise; rejecting it is the only way the script author
                // learns that the host member threw.
                reply = "{\"id\":" + id + ",\"error\":" +
                        WebViewScript.WriteJsonString(ex.GetBaseException().Message) + "}";
            }

            WebViewBackend.PostWebMessageAsString(ScriptingBridgeMessagePrefix + reply);
        }

        private static object InvokeScriptingMember(object target, string member, string payload)
        {
            MethodInfo method = target.GetType().GetMethod(
                member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (method is null)
            {
                PropertyInfo property = target.GetType().GetProperty(
                    member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (property is null)
                {
                    throw new MissingMemberException(target.GetType().FullName, member);
                }

                return property.GetValue(target);
            }

            ParameterInfo[] parameters = method.GetParameters();
            List<string> raw = ReadJsonArray(payload);
            var converted = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                object value = i < raw.Count ? WebViewScript.FromJson(raw[i]) : null;

                converted[i] = value is null
                    ? null
                    : Convert.ChangeType(value, parameters[i].ParameterType,
                                         System.Globalization.CultureInfo.InvariantCulture);
            }

            return method.Invoke(target, converted);
        }

        /// <summary>
        /// Read one scalar member out of the bridge's own message. This is not a general JSON
        /// parser and does not need to be: the only JSON it ever sees is the object the shim above
        /// constructs, whose shape is fixed.
        /// </summary>
        private static string ReadJsonMember(string json, string name)
        {
            int at = json.IndexOf("\"" + name + "\":", StringComparison.Ordinal);

            if (at < 0)
            {
                return "null";
            }

            int start = at + name.Length + 3;
            int end = start;
            bool inString = false;

            while (end < json.Length)
            {
                char c = json[end];

                if (c == '"' && (end == start || json[end - 1] != '\\'))
                {
                    inString = !inString;
                }
                else if (!inString && (c == ',' || c == '}'))
                {
                    break;
                }

                end++;
            }

            return json.Substring(start, end - start).Trim();
        }

        /// <summary>The elements of the shim's "args" array, still as JSON text.</summary>
        private static List<string> ReadJsonArray(string json)
        {
            var items = new List<string>();
            int at = json.IndexOf("\"args\":[", StringComparison.Ordinal);

            if (at < 0)
            {
                return items;
            }

            int i = at + "\"args\":[".Length;
            int depth = 0;
            bool inString = false;
            var current = new StringBuilder();

            for (; i < json.Length; i++)
            {
                char c = json[i];

                if (c == '"' && json[i - 1] != '\\')
                {
                    inString = !inString;
                }

                if (!inString)
                {
                    if (c == '[' || c == '{')
                    {
                        depth++;
                    }
                    else if (c == ']' && depth == 0)
                    {
                        break;
                    }
                    else if (c == ']' || c == '}')
                    {
                        depth--;
                    }
                    else if (c == ',' && depth == 0)
                    {
                        items.Add(current.ToString().Trim());
                        current.Clear();
                        continue;
                    }
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                items.Add(current.ToString().Trim());
            }

            return items;
        }
    }
}
