// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The host<->page bridge's message format, and the settings vocabulary.
//
// WebBrowser.ObjectForScripting used to be an IDispatch object the WebOC reached directly. There is
// no such thing behind a modern engine, so window.external is re-created over the message channel:
// the page posts a JSON envelope naming a member and its arguments, the host invokes it and posts
// the result back. The envelope is a wire format between two pieces of code that cannot see each
// other's types, which makes it exactly the kind of thing that is worth pinning down in a test --
// a reader that silently mis-splits an argument list is a bug that only shows up in someone's app.
//
// These run with no engine, no window and no built fork, for the same reason the scripting tests do.
//

using System;
using MS.Internal.Interop.WebView;
using Xunit;

namespace Wpf.WebView.Tests
{
    public class WebViewBridgeTests
    {
        // ---- the settings the controls actually push ------------------------------------------

        [Fact]
        public void SettingsDefaultToAWorkingBrowser()
        {
            // A page that cannot run script or talk back is not a web view. The defaults have to be
            // the permissive ones; the control tightens them if it wants to.
            var s = new WebViewSettings();

            Assert.True(s.ScriptEnabled);
            Assert.True(s.WebMessageEnabled);
            Assert.True(s.AreDefaultScriptDialogsEnabled);
            Assert.True(s.IsBuiltInErrorPageEnabled);
            Assert.Null(s.UserAgent);
        }

        // ---- error-status vocabulary ------------------------------------------------------------

        [Fact]
        public void ErrorStatusMatchesCoreWebView2Numbering()
        {
            // Microsoft.Web.WebView2.Core re-exposes these by a straight cast rather than a
            // translation table, so the numbering is load-bearing: a renumbering here silently
            // reports the wrong failure reason to every WebView2 application.
            //
            // One Fact rather than a Theory because the enum is internal, and an internal type
            // cannot appear in a public [InlineData] signature.
            Assert.Equal(0, (int)WebViewErrorStatus.Unknown);
            Assert.Equal(2, (int)WebViewErrorStatus.CertificateExpired);
            Assert.Equal(6, (int)WebViewErrorStatus.ServerUnreachable);
            Assert.Equal(7, (int)WebViewErrorStatus.Timeout);
            Assert.Equal(13, (int)WebViewErrorStatus.HostNameNotResolved);
            Assert.Equal(14, (int)WebViewErrorStatus.OperationCanceled);
            Assert.Equal(18, (int)WebViewErrorStatus.ValidProxyAuthenticationRequired);
        }

        // ---- presentation ------------------------------------------------------------------------

        [Fact]
        public void OverlayIsTheDefaultPresentation()
        {
            // Every backend today is an overlay. If a composited backend ever appears, the controls'
            // airspace rules have to change with it, so the default must not drift silently.
            Assert.Equal(0, (int)WebViewPresentation.Overlay);
        }

        // ---- the bridge envelope -------------------------------------------------------------------

        [Fact]
        public void AMemberNameArrivesStillQuotedAndMustBeDecoded()
        {
            // Regression: the envelope's "member" is a JSON string, so reading the raw token yields
            // "\"Greet\"" -- quotes included. Handing that to reflection looks up a member literally
            // called '"Greet"', which fails, and the only place the failure shows up is as a
            // rejected promise inside the page. Decoding is not optional.
            const string rawToken = "\"Greet\"";

            Assert.Equal("Greet", WebViewScript.FromJson(rawToken));
            Assert.NotEqual("Greet", rawToken);
        }

        [Fact]
        public void BridgeArgumentsSurviveTheirTypes()
        {
            // Add(2, 3) must reach the host as two numbers, not as the string "23" or as "2, 3".
            Assert.Equal(2d, WebViewScript.FromJson("2"));
            Assert.Equal(3d, WebViewScript.FromJson("3"));

            // And a string argument keeps any comma inside it, which is what makes splitting an
            // argument list on bare commas wrong.
            Assert.Equal("a,b", WebViewScript.FromJson("\"a,b\""));
        }

        // ---- the event payloads the controls read ------------------------------------------------

        [Fact]
        public void NavigationStartingCarriesWhatNavigatingNeeds()
        {
            // WebBrowser.Navigating is raised from this, and a handler that cancels must be able to.
            var e = new WebViewNavigationStartingEventArgs
            {
                Uri = "https://example.invalid/",
                IsUserInitiated = true,
                IsRedirected = false,
                NavigationId = 7,
            };

            Assert.False(e.Cancel);
            e.Cancel = true;
            Assert.True(e.Cancel);
            Assert.Equal(7ul, e.NavigationId);
        }

        [Fact]
        public void NewWindowRequestedDefaultsToUnhandled()
        {
            // Unhandled means the engine opens its own window, which is the WebOC's behaviour too.
            Assert.False(new WebViewNewWindowRequestedEventArgs().Handled);
        }

        [Fact]
        public void DownloadStartingDefaultsToProceeding()
        {
            Assert.False(new WebViewDownloadStartingEventArgs().Cancel);
        }

        [Fact]
        public void WebMessageCarriesBothFormsIndependently()
        {
            // A JSON message has no string form; the string form being null is how a reader tells
            // the two apart, so it must not be conflated with an empty string.
            var e = new WebViewMessageReceivedEventArgs
            {
                Source = "https://example.invalid/",
                WebMessageAsJson = "{\"a\":1}",
                WebMessageAsString = null,
            };

            Assert.NotNull(e.WebMessageAsJson);
            Assert.Null(e.WebMessageAsString);
        }
    }
}
