// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The platform-neutral vocabulary the web-view seam speaks: settings, error codes and the event
// payloads every backend raises. Nothing here touches an OS type, which is the point -- these are
// the types the navigation state machine, the WPF WebBrowser, the WinForms WebBrowser and the
// CoreWebView2 object model all agree on, and they are what makes the logic above the seam testable
// on a machine that has no web engine at all (see Wpf.WebView.Tests).
//
// Names and enum VALUES deliberately mirror the WebView2 API (CoreWebView2WebErrorStatus and
// friends). Microsoft.Web.WebView2.Core re-exposes these as its own public enums by a straight cast,
// so keeping the numbering identical means that layer is a rename and not a translation table. The
// WPF/WinForms WebBrowser controls, whose public surface predates WebView2, map onto the same
// vocabulary at their own edge.
//
// These are compiled into every consuming assembly by source (see the $(WpfSharedDir) glob in
// PresentationFramework, System.Windows.Forms and Microsoft.Web.WebView2.Core), exactly as
// UnsafeNativeMethodsCLR.cs already is. No instance of any of these types ever crosses an assembly
// boundary, so three internal copies is correct rather than merely tolerable.
//

using System;

namespace MS.Internal.Interop.WebView
{
    /// <summary>
    /// Why a navigation failed. Values match CoreWebView2WebErrorStatus so
    /// Microsoft.Web.WebView2.Core can cast rather than translate.
    /// </summary>
    internal enum WebViewErrorStatus
    {
        Unknown = 0,
        CertificateCommonNameIsIncorrect = 1,
        CertificateExpired = 2,
        ClientCertificateContainsErrors = 3,
        CertificateRevoked = 4,
        CertificateIsInvalid = 5,
        ServerUnreachable = 6,
        Timeout = 7,
        ErrorHttpInvalidServerResponse = 8,
        ConnectionAborted = 9,
        ConnectionReset = 10,
        Disconnected = 11,
        CannotConnect = 12,
        HostNameNotResolved = 13,
        OperationCanceled = 14,
        RedirectFailed = 15,
        UnexpectedError = 16,
        ValidAuthenticationCredentialsRequired = 17,
        ValidProxyAuthenticationRequired = 18,
    }

    /// <summary>
    /// How a backend delivers its pixels. Every head this fork supports uses <see cref="Overlay"/>:
    /// the engine's own native view sits above the WebGPU surface. The enum exists so the controls
    /// can ask rather than assume -- an offscreen-compositing backend (the shape IMediaBackend uses
    /// for video) would answer differently, and the airspace rules the controls enforce depend on
    /// the answer.
    /// </summary>
    internal enum WebViewPresentation
    {
        /// <summary>A native child view above the surface. Cannot be transformed, made translucent
        /// or z-ordered by WPF, and cannot live inside a Popup.</summary>
        Overlay = 0,

        /// <summary>Frames are pulled and composited into the scene. No backend does this today.</summary>
        Composited = 1,
    }

    /// <summary>
    /// The knobs the controls expose that every engine can actually honour. Deliberately small:
    /// a setting nothing can implement does not belong here, it belongs at the edge of the layer
    /// that has to answer for it.
    /// </summary>
    internal sealed class WebViewSettings
    {
        public bool ScriptEnabled = true;
        public bool WebMessageEnabled = true;
        public bool AreDefaultScriptDialogsEnabled = true;
        public bool AreDefaultContextMenusEnabled = true;
        public bool AreDevToolsEnabled = true;
        public bool IsStatusBarEnabled = true;
        public bool IsZoomControlEnabled = true;
        public bool IsBuiltInErrorPageEnabled = true;
        public string UserAgent;
    }

    /// <summary>Raised before a navigation begins; set <see cref="Cancel"/> to stop it.</summary>
    internal sealed class WebViewNavigationStartingEventArgs : EventArgs
    {
        public string Uri;
        public bool IsUserInitiated;
        public bool IsRedirected;
        public ulong NavigationId;
        public bool Cancel;
    }

    /// <summary>Raised when a navigation finishes, successfully or not.</summary>
    internal sealed class WebViewNavigationCompletedEventArgs : EventArgs
    {
        public ulong NavigationId;
        public bool IsSuccess;
        public WebViewErrorStatus WebErrorStatus;
        public int HttpStatusCode;
    }

    /// <summary>Raised when the current document's URI changes, including same-document changes.</summary>
    internal sealed class WebViewSourceChangedEventArgs : EventArgs
    {
        public bool IsNewDocument;
    }

    /// <summary>
    /// A message posted from the page. The JSON form is canonical -- every engine can carry a
    /// string, and a string message is represented as a JSON string so one channel serves both.
    /// </summary>
    internal sealed class WebViewMessageReceivedEventArgs : EventArgs
    {
        public string Source;
        public string WebMessageAsJson;

        /// <summary>The message as a plain string, or null when it was not a JSON string.</summary>
        public string WebMessageAsString;
    }

    /// <summary>Raised when the page asks for a new window (window.open, target=_blank).</summary>
    internal sealed class WebViewNewWindowRequestedEventArgs : EventArgs
    {
        public string Uri;
        public bool IsUserInitiated;

        /// <summary>Set by the handler to say it dealt with the request itself.</summary>
        public bool Handled;
    }

    /// <summary>Raised when a download begins; set <see cref="Cancel"/> to refuse it.</summary>
    internal sealed class WebViewDownloadStartingEventArgs : EventArgs
    {
        public string Uri;
        public string ResultFilePath;
        public bool Cancel;
    }

    /// <summary>
    /// Raised when the engine's own process dies. Every engine here is multi-process except the
    /// browser head, so a control that ignores this can sit showing a dead page forever.
    /// </summary>
    internal sealed class WebViewProcessFailedEventArgs : EventArgs
    {
        public bool IsBrowserProcess;
        public string Reason;
    }
}
