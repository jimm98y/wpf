// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CoreWebView2Settings, over the seam's WebViewSettings.
//
// Every setter pushes the whole settings object down immediately, which is what the real API does
// too: these are not batched, and an application that turns script off expects the very next
// navigation to run none.
//
// The properties that ARE here are the ones every engine can honour. The real API has a handful more
// that only Chromium has (AreBrowserAcceleratorKeysEnabled, IsPasswordAutosaveEnabled, and so on);
// they are kept as ordinary properties that remember what they were set to rather than being
// silently ignored, and their doc comments say which heads act on them.
//

using MS.Internal.Interop.WebView;

namespace Microsoft.Web.WebView2.Core
{
    /// <summary>The engine's per-view settings.</summary>
    public class CoreWebView2Settings
    {
        private readonly IWebViewBackend _backend;
        private readonly WebViewSettings _settings = new WebViewSettings();

        internal CoreWebView2Settings(IWebViewBackend backend)
        {
            _backend = backend;
        }

        private void Push() => _backend.ApplySettings(_settings);

        public bool IsScriptEnabled
        {
            get => _settings.ScriptEnabled;
            set { _settings.ScriptEnabled = value; Push(); }
        }

        public bool IsWebMessageEnabled
        {
            get => _settings.WebMessageEnabled;
            set { _settings.WebMessageEnabled = value; Push(); }
        }

        public bool AreDefaultScriptDialogsEnabled
        {
            get => _settings.AreDefaultScriptDialogsEnabled;
            set { _settings.AreDefaultScriptDialogsEnabled = value; Push(); }
        }

        public bool AreDefaultContextMenusEnabled
        {
            get => _settings.AreDefaultContextMenusEnabled;
            set { _settings.AreDefaultContextMenusEnabled = value; Push(); }
        }

        public bool AreDevToolsEnabled
        {
            get => _settings.AreDevToolsEnabled;
            set { _settings.AreDevToolsEnabled = value; Push(); }
        }

        public bool IsStatusBarEnabled
        {
            get => _settings.IsStatusBarEnabled;
            set { _settings.IsStatusBarEnabled = value; Push(); }
        }

        public bool IsZoomControlEnabled
        {
            get => _settings.IsZoomControlEnabled;
            set { _settings.IsZoomControlEnabled = value; Push(); }
        }

        public bool IsBuiltInErrorPageEnabled
        {
            get => _settings.IsBuiltInErrorPageEnabled;
            set { _settings.IsBuiltInErrorPageEnabled = value; Push(); }
        }

        public string UserAgent
        {
            get => _settings.UserAgent;
            set { _settings.UserAgent = value; Push(); }
        }

        /// <summary>
        /// Whether the page may reach host objects. Always false here: host objects need COM
        /// marshalling, which exists on one of the six heads (see
        /// <see cref="CoreWebView2.AddHostObjectToScript"/>). Assigning true throws rather than
        /// leaving an application believing the channel is open.
        /// </summary>
        public bool AreHostObjectsAllowed
        {
            get => false;
            set
            {
                if (value)
                {
                    throw new System.NotSupportedException(
                        "Host objects are not supported. Use the web-message channel instead.");
                }
            }
        }

        /// <summary>
        /// Whether the browser's own accelerator keys (Ctrl+P, F5, and so on) reach the page.
        /// Honoured by the Windows engine; remembered but not acted on elsewhere, because the other
        /// engines have no equivalent switch.
        /// </summary>
        public bool AreBrowserAcceleratorKeysEnabled { get; set; } = true;

        /// <summary>
        /// Whether the engine offers to save passwords. Honoured by the Windows engine; remembered
        /// elsewhere, where the platform decides this outside the embedded view.
        /// </summary>
        public bool IsPasswordAutosaveEnabled { get; set; }

        /// <summary>
        /// Whether general form autofill is on. Same caveat as
        /// <see cref="IsPasswordAutosaveEnabled"/>.
        /// </summary>
        public bool IsGeneralAutofillEnabled { get; set; } = true;

        /// <summary>
        /// Whether pinch-zoom is enabled. Honoured where the platform exposes it; remembered
        /// otherwise.
        /// </summary>
        public bool IsPinchZoomEnabled { get; set; } = true;

        /// <summary>
        /// Whether swipe navigation is enabled. Honoured where the platform exposes it; remembered
        /// otherwise.
        /// </summary>
        public bool IsSwipeNavigationEnabled { get; set; } = true;

        /// <summary>
        /// Whether the page may be reloaded by a gesture. Honoured where the platform exposes it;
        /// remembered otherwise.
        /// </summary>
        public bool IsReputationCheckingRequired { get; set; } = true;

        /// <summary>
        /// The hidden PDF toolbar items. Remembered; only the Windows engine has a PDF viewer whose
        /// toolbar can be configured.
        /// </summary>
        public uint HiddenPdfToolbarItems { get; set; }
    }
}
