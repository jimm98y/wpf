// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The CoreWebView2 event-argument types.
//
// Each one wraps the corresponding payload from the cross-platform seam. They are thin by design:
// the seam already speaks this vocabulary (its enums were numbered to match), so the work here is
// naming rather than translating, and the settable members write back through to the seam's object
// so a handler's decision actually reaches the engine.
//

using System;
using MS.Internal.Interop.WebView;

namespace Microsoft.Web.WebView2.Core
{
    /// <summary>Raised before a navigation begins. Set <see cref="Cancel"/> to stop it.</summary>
    public class CoreWebView2NavigationStartingEventArgs : EventArgs
    {
        private readonly WebViewNavigationStartingEventArgs _inner;

        internal CoreWebView2NavigationStartingEventArgs(WebViewNavigationStartingEventArgs inner)
        {
            _inner = inner;
        }

        public string Uri => _inner.Uri;

        public bool IsUserInitiated => _inner.IsUserInitiated;

        public bool IsRedirected => _inner.IsRedirected;

        public ulong NavigationId => _inner.NavigationId;

        /// <summary>Writes straight through, so a handler that cancels really does cancel.</summary>
        public bool Cancel
        {
            get => _inner.Cancel;
            set => _inner.Cancel = value;
        }
    }

    /// <summary>Raised when a navigation finishes, successfully or not.</summary>
    public class CoreWebView2NavigationCompletedEventArgs : EventArgs
    {
        private readonly WebViewNavigationCompletedEventArgs _inner;

        internal CoreWebView2NavigationCompletedEventArgs(WebViewNavigationCompletedEventArgs inner)
        {
            _inner = inner;
        }

        public bool IsSuccess => _inner.IsSuccess;

        // A straight cast, not a lookup table: the seam's enum was numbered to match this one.
        public CoreWebView2WebErrorStatus WebErrorStatus => (CoreWebView2WebErrorStatus)_inner.WebErrorStatus;

        public ulong NavigationId => _inner.NavigationId;

        public int HttpStatusCode => _inner.HttpStatusCode;
    }

    /// <summary>Raised when the document's URI changes, including same-document changes.</summary>
    public class CoreWebView2SourceChangedEventArgs : EventArgs
    {
        private readonly WebViewSourceChangedEventArgs _inner;

        internal CoreWebView2SourceChangedEventArgs(WebViewSourceChangedEventArgs inner)
        {
            _inner = inner;
        }

        public bool IsNewDocument => _inner.IsNewDocument;
    }

    /// <summary>A message posted from the page.</summary>
    public class CoreWebView2WebMessageReceivedEventArgs : EventArgs
    {
        private readonly WebViewMessageReceivedEventArgs _inner;

        internal CoreWebView2WebMessageReceivedEventArgs(WebViewMessageReceivedEventArgs inner)
        {
            _inner = inner;
        }

        public string Source => _inner.Source;

        public string WebMessageAsJson => _inner.WebMessageAsJson;

        /// <summary>
        /// The message as a string. Throws when it was not one, which is the real API's behaviour:
        /// returning null would make a JSON message indistinguishable from the string "null".
        /// </summary>
        public string TryGetWebMessageAsString()
        {
            if (_inner.WebMessageAsString is null)
            {
                throw new ArgumentException("The web message is not a string.");
            }

            return _inner.WebMessageAsString;
        }
    }

    /// <summary>Raised when the page asks for a new window (window.open, target=_blank).</summary>
    public class CoreWebView2NewWindowRequestedEventArgs : EventArgs
    {
        private readonly WebViewNewWindowRequestedEventArgs _inner;

        internal CoreWebView2NewWindowRequestedEventArgs(WebViewNewWindowRequestedEventArgs inner)
        {
            _inner = inner;
        }

        public string Uri => _inner.Uri;

        public bool IsUserInitiated => _inner.IsUserInitiated;

        public bool Handled
        {
            get => _inner.Handled;
            set => _inner.Handled = value;
        }

        /// <summary>
        /// The view the new window should open into. Assigning another CoreWebView2 here is
        /// unimplemented: it means handing an in-flight navigation to a second engine instance, and
        /// only the Windows engine has any notion of it. Setting it would silently do nothing, so
        /// it refuses instead.
        /// </summary>
        public CoreWebView2 NewWindow
        {
            get => null;
            set => throw new NotSupportedException(
                "Redirecting a new window into another CoreWebView2 is not implemented. " +
                "Set Handled and navigate a view of your own instead.");
        }
    }

    /// <summary>Raised when a download begins.</summary>
    public class CoreWebView2DownloadStartingEventArgs : EventArgs
    {
        private readonly WebViewDownloadStartingEventArgs _inner;

        internal CoreWebView2DownloadStartingEventArgs(WebViewDownloadStartingEventArgs inner)
        {
            _inner = inner;
        }

        public bool Cancel
        {
            get => _inner.Cancel;
            set => _inner.Cancel = value;
        }

        /// <summary>Where the file will be written; assign to redirect it.</summary>
        public string ResultFilePath
        {
            get => _inner.ResultFilePath;
            set => _inner.ResultFilePath = value;
        }

        /// <summary>Set to suppress the engine's own download UI.</summary>
        public bool Handled { get; set; }

        public string Uri => _inner.Uri;
    }

    /// <summary>Raised when one of the engine's processes dies.</summary>
    public class CoreWebView2ProcessFailedEventArgs : EventArgs
    {
        private readonly WebViewProcessFailedEventArgs _inner;

        internal CoreWebView2ProcessFailedEventArgs(WebViewProcessFailedEventArgs inner)
        {
            _inner = inner;
        }

        public CoreWebView2ProcessFailedKind ProcessFailedKind =>
            _inner.IsBrowserProcess
                ? CoreWebView2ProcessFailedKind.BrowserProcessExited
                : CoreWebView2ProcessFailedKind.RenderProcessExited;
    }

    /// <summary>Raised when the engine finishes creating itself, successfully or not.</summary>
    public class CoreWebView2InitializationCompletedEventArgs : EventArgs
    {
        internal CoreWebView2InitializationCompletedEventArgs(Exception error)
        {
            InitializationException = error;
        }

        public bool IsSuccess => InitializationException is null;

        public Exception InitializationException { get; }
    }
}
