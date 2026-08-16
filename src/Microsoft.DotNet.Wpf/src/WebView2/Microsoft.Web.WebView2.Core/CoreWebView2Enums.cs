// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The CoreWebView2 enumerations, with the NUMBERING the real API uses.
//
// The values matter as much as the names. Applications persist them in settings, compare them
// against literals, and switch on them; and the seam's own WebViewErrorStatus was deliberately given
// the same numbering so this layer is a cast rather than a translation table (see the test in
// Wpf.WebView.Tests that pins that). Renumbering anything here silently changes what an existing
// application is told.
//

namespace Microsoft.Web.WebView2.Core
{
    /// <summary>Why a navigation failed.</summary>
    public enum CoreWebView2WebErrorStatus
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

    /// <summary>Which of the browser's processes died.</summary>
    public enum CoreWebView2ProcessFailedKind
    {
        BrowserProcessExited = 0,
        RenderProcessExited = 1,
        RenderProcessUnresponsive = 2,
        FrameRenderProcessExited = 3,
        UtilityProcessExited = 4,
        SandboxHelperProcessExited = 5,
        GpuProcessExited = 6,
        PpapiPluginProcessExited = 7,
        PpapiBrokerProcessExited = 8,
        UnknownProcessExited = 9,
    }

    /// <summary>How far a download has got.</summary>
    public enum CoreWebView2DownloadState
    {
        InProgress = 0,
        Interrupted = 1,
        Completed = 2,
    }

    /// <summary>Which kind of script dialog the page opened.</summary>
    public enum CoreWebView2ScriptDialogKind
    {
        Alert = 0,
        Confirm = 1,
        Prompt = 2,
        Beforeunload = 3,
    }

    /// <summary>What a permission request is asking for.</summary>
    public enum CoreWebView2PermissionKind
    {
        UnknownPermission = 0,
        Microphone = 1,
        Camera = 2,
        Geolocation = 3,
        Notifications = 4,
        OtherSensors = 5,
        ClipboardRead = 6,
    }

    /// <summary>The answer to a permission request.</summary>
    public enum CoreWebView2PermissionState
    {
        Default = 0,
        Allow = 1,
        Deny = 2,
    }
}
