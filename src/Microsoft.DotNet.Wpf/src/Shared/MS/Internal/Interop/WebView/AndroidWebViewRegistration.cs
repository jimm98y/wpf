// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// How the Android head payload hands its web-view implementation to the seam.
//
// Every other Android capability -- dialogs, printing, the clipboard, drag and drop -- uses a public
// IAndroid*Host interface declared in WindowsBase which the app's payload implements. This one
// cannot, for a reason worth writing down because it is not obvious:
//
//   The web-view seam is LINK-COMPILED into three assemblies (PresentationFramework,
//   System.Windows.Forms and Microsoft.Web.WebView2.Core) because those three cannot reference one
//   another. Only the first references WindowsBase. An interface declared alongside the seam would
//   therefore exist as THREE distinct types, and an application would have to register with each --
//   including ones it may not even have loaded.
//
// And the payload really is the only place this can live. The engine seam P/Invokes the NDK and
// never JNI (see AndroidInterop's header: "creating views ... is done by the app head, which is a
// .NET-for-Android assembly and has the Java bindings for free"). android.webkit.WebView is a Java
// view: constructing it, giving it a WebViewClient and calling evaluateJavascript all need those
// bindings. The seam cannot do it, and hand-writing that much JNI here would defeat the rule.
//
// So registration goes through AppContext, whose data is process-wide and therefore common to all
// three copies, carrying DELEGATES rather than an object. Delegates over primitives and string are
// BCL types, so every copy can call them; an object of a payload type could not even be cast.
//
// The payload registers once, early -- in its Application or MainActivity -- like this:
//
//     AppContext.SetData("MS.Internal.Interop.WebView.Android.Invoke",
//         (Func<string, object[], object>)MyAndroidWebViewHost.Invoke);
//
// and answers the commands named in AndroidWebViewCommands below. One dispatcher rather than twenty
// typed slots keeps the contract in one place and lets it grow without the payload and the seam
// having to be updated in lockstep: an unknown command is a NotSupportedException, not a missing
// method that fails to bind.
//

using System;

namespace MS.Internal.Interop.WebView
{
    /// <summary>The command names <see cref="AndroidWebViewRegistration"/> dispatches.</summary>
    /// <remarks>
    /// Every command takes the view id as its first argument, except <see cref="Create"/>, which
    /// returns one. Ids are the payload's to define -- the seam only ever passes them back.
    /// </remarks>
    internal static class AndroidWebViewCommands
    {
        /// <summary>(parentWindowHandle, x, y, width, height) -> int view id, or 0 on failure.</summary>
        internal const string Create = "create";

        /// <summary>(id) -> null. Removes the view from its parent.</summary>
        internal const string Destroy = "destroy";

        /// <summary>(id, x, y, width, height) -> null. Device pixels, relative to the parent.</summary>
        internal const string SetBounds = "setBounds";

        /// <summary>(id, bool) -> null.</summary>
        internal const string SetVisible = "setVisible";

        /// <summary>(id, string uri) -> null.</summary>
        internal const string Navigate = "navigate";

        /// <summary>(id, string html) -> null.</summary>
        internal const string NavigateToString = "navigateToString";

        /// <summary>(id, string uri, byte[] postData, string headers) -> null.</summary>
        internal const string NavigateWithPost = "navigateWithPost";

        /// <summary>(id, bool noCache) -> null.</summary>
        internal const string Reload = "reload";

        /// <summary>(id) -> null.</summary>
        internal const string Stop = "stop";

        /// <summary>(id) -> null.</summary>
        internal const string GoBack = "goBack";

        /// <summary>(id) -> null.</summary>
        internal const string GoForward = "goForward";

        /// <summary>(id) -> bool.</summary>
        internal const string CanGoBack = "canGoBack";

        /// <summary>(id) -> bool.</summary>
        internal const string CanGoForward = "canGoForward";

        /// <summary>(id) -> string, or null.</summary>
        internal const string GetSource = "getSource";

        /// <summary>(id) -> string, or null.</summary>
        internal const string GetTitle = "getTitle";

        /// <summary>
        /// (id, string script, Action&lt;string&gt; completed) -> null. The payload calls
        /// <c>completed</c> with the result as JSON; evaluateJavascript is asynchronous on Android
        /// and there is no synchronous form of it.
        /// </summary>
        internal const string ExecuteScript = "executeScript";

        /// <summary>(id, string script) -> null. Re-run on every page load.</summary>
        internal const string AddDocumentScript = "addDocumentScript";

        /// <summary>(id, string json) -> null. Delivered to the page as a chrome.webview message.</summary>
        internal const string PostMessage = "postMessage";

        /// <summary>(id, double) -> null.</summary>
        internal const string SetZoom = "setZoom";

        /// <summary>(id) -> null. Clears cookies, cache and storage.</summary>
        internal const string ClearData = "clearData";

        /// <summary>
        /// (id, Action&lt;string, string&gt; sink) -> null. The payload raises (kind, payload) for
        /// the events named in <see cref="AndroidWebViewEvents"/>.
        /// </summary>
        internal const string SetEventSink = "setEventSink";
    }

    /// <summary>The event kinds the payload raises through the sink.</summary>
    internal static class AndroidWebViewEvents
    {
        /// <summary>Payload: the URI. Raised from shouldOverrideUrlLoading / onPageStarted.</summary>
        internal const string NavigationStarting = "navigationStarting";

        /// <summary>Payload: the URI. Raised from onPageStarted.</summary>
        internal const string ContentLoading = "contentLoading";

        /// <summary>Payload: the URI. Raised from onPageFinished.</summary>
        internal const string NavigationCompleted = "navigationCompleted";

        /// <summary>Payload: the WebViewClient error description. Raised from onReceivedError.</summary>
        internal const string NavigationFailed = "navigationFailed";

        /// <summary>Payload: the message text, from the JavaScript interface.</summary>
        internal const string WebMessage = "webMessage";

        /// <summary>Payload: the URI. Raised from onCreateWindow.</summary>
        internal const string NewWindow = "newWindow";

        /// <summary>Payload: the URI. Raised from setDownloadListener.</summary>
        internal const string DownloadStarting = "downloadStarting";

        /// <summary>Payload: the new title, from onReceivedTitle.</summary>
        internal const string TitleChanged = "titleChanged";
    }

    /// <summary>
    /// The bridge to the Android head payload. Absent outside an Android app, and absent on Android
    /// until the payload registers -- <see cref="IsAvailable"/> is how the backend tells.
    /// </summary>
    internal static class AndroidWebViewRegistration
    {
        /// <summary>
        /// The AppContext key the payload sets. Spelled out rather than derived from a type name so
        /// that renaming a class here can never silently break an already-shipped payload.
        /// </summary>
        internal const string InvokeKey = "MS.Internal.Interop.WebView.Android.Invoke";

        /// <summary>Whether a payload has registered an implementation.</summary>
        internal static bool IsAvailable => Resolve() is not null;

        /// <summary>
        /// Read the registration. Deliberately NOT cached.
        /// </summary>
        /// <remarks>
        /// Caching the first non-null result looks free and is not: a payload that registers a
        /// second time -- replacing its implementation, or a test installing a fake -- would be
        /// silently ignored for the life of the process, and the symptom is a control that appears
        /// to work while talking to something that no longer exists. AppContext.GetData is a
        /// dictionary lookup against a handful of entries, which is nothing beside the JNI hop every
        /// one of these commands is about to make.
        /// </remarks>
        private static Func<string, object[], object> Resolve() =>
            AppContext.GetData(InvokeKey) as Func<string, object[], object>;

        /// <summary>Send a command to the payload.</summary>
        internal static object Invoke(string command, params object[] args)
        {
            Func<string, object[], object> invoke = Resolve();

            if (invoke is null)
            {
                throw new PlatformNotSupportedException(
                    "This application's Android head has not registered a web-view implementation. " +
                    "Set AppContext data '" + InvokeKey + "' to a Func<string, object[], object> " +
                    "before showing a control that hosts web content.");
            }

            return invoke(command, args ?? Array.Empty<object>());
        }
    }
}
