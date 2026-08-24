// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;

namespace System.Windows.Forms
{
    /// <summary>
    /// Data for <see cref="WebBrowser.NewWindowRequested"/>: a page asked for a new window, and
    /// this says where it was going.
    /// </summary>
    /// <remarks>
    /// The original WinForms WebBrowser raised NewWindow with a bare CancelEventArgs, which is why
    /// hosts that needed the URL sank DWebBrowserEvents2.NewWindow3 on the Internet Explorer
    /// ActiveX control instead. The engines behind this control report the URL directly, so there
    /// is no reason to make anyone do that again.
    /// </remarks>
    public class WebBrowserNewWindowRequestedEventArgs : CancelEventArgs
    {
        public WebBrowserNewWindowRequestedEventArgs(string url, bool isUserInitiated)
        {
            Url = url;
            IsUserInitiated = isUserInitiated;
        }

        /// <summary>The URL the new window would have shown.</summary>
        public string Url { get; }

        /// <summary>Whether a user gesture triggered the request, rather than script alone.</summary>
        public bool IsUserInitiated { get; }
    }
}
