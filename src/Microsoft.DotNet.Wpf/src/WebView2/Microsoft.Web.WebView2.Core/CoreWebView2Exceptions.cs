// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The exception types the real Microsoft.Web.WebView2.Core throws, so code that CATCHES them
// compiles and binds against this assembly unchanged.
//
// Catch clauses are the easiest part of an API to forget when standing one in: nothing references
// the type except a `catch`, so it is invisible to a search for uses and shows up only as a build
// error in whatever application tries it. An app guarding its web view with
// `catch (WebView2RuntimeNotFoundException)` is being careful, and the careful app should not be the
// one that fails to compile.
//

using System;

namespace Microsoft.Web.WebView2.Core
{
    /// <summary>
    /// Settings for <see cref="CoreWebView2.PrintToPdfAsync"/>. Present for the signature's sake:
    /// nothing here reads it, because none of these backends can paginate.
    /// </summary>
    public class CoreWebView2PrintSettings
    {
        public double ScaleFactor { get; set; } = 1.0;
        public double PageWidth { get; set; } = 8.5;
        public double PageHeight { get; set; } = 11.0;
        public double MarginTop { get; set; }
        public double MarginBottom { get; set; }
        public double MarginLeft { get; set; }
        public double MarginRight { get; set; }
        public bool ShouldPrintBackgrounds { get; set; }
        public bool ShouldPrintSelectionOnly { get; set; }
        public bool ShouldPrintHeaderAndFooter { get; set; }
        public string HeaderTitle { get; set; }
        public string FooterUri { get; set; }
    }

    /// <summary>
    /// Thrown by the real implementation when the WebView2 runtime is not installed. Never thrown
    /// here: every head has its engine built in (WKWebView, WPE WebKit, the browser's own), so
    /// "the runtime is missing" is a Windows-only condition. Present so that the applications which
    /// handle it keep compiling, and their handler simply never runs.
    /// </summary>
    public class WebView2RuntimeNotFoundException : Exception
    {
        public WebView2RuntimeNotFoundException()
            : base("The WebView2 runtime is not installed.")
        {
        }

        public WebView2RuntimeNotFoundException(string message)
            : base(message)
        {
        }

        public WebView2RuntimeNotFoundException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
