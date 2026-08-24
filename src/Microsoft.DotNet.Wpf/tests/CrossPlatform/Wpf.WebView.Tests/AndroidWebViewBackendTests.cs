// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android backend's managed half, driven by a fake head payload.
//
// This suite exists because of how the Android web view is split. The seam cannot touch Java -- it
// P/Invokes the NDK and never JNI -- so android.webkit.WebView is created by the app head, and what
// lives in AndroidWebViewBackend is the part either side of that boundary agrees on: the command
// vocabulary, the lifetime, the event shapes, the error mapping. None of that contains an Android
// type, which means all of it can be exercised here, on any machine, with no device and no payload.
//
// That is the same reasoning DragDropBackendTests gives for testing Android's drag sequencing on a
// developer's laptop, and it is what keeps the genuinely untestable surface down to the Java itself.
//
// The fake payload below is also the clearest specification of the contract a real head must meet:
// it answers exactly the commands in AndroidWebViewCommands and raises exactly the events in
// AndroidWebViewEvents.
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MS.Internal.Interop.WebView;
using Xunit;

namespace Wpf.WebView.Tests
{
    public class AndroidWebViewBackendTests : IDisposable
    {
        private readonly FakeHead _head = new FakeHead();

        public AndroidWebViewBackendTests()
        {
            AppContext.SetData(AndroidWebViewRegistration.InvokeKey,
                               (Func<string, object[], object>)_head.Invoke);
        }

        public void Dispose() => AppContext.SetData(AndroidWebViewRegistration.InvokeKey, null);

        // ---- lifetime ---------------------------------------------------------------------------

        [Fact]
        public async Task AttachCreatesTheViewAndRegistersAnEventSink()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(0x1234));

            Assert.True(backend.IsAttached);
            Assert.Contains(AndroidWebViewCommands.Create, _head.Commands);

            // The sink must be registered during attach, not later: a page can begin loading
            // immediately and its first events would otherwise be dropped.
            Assert.Contains(AndroidWebViewCommands.SetEventSink, _head.Commands);
            Assert.True(_head.Commands.IndexOf(AndroidWebViewCommands.SetEventSink) >
                        _head.Commands.IndexOf(AndroidWebViewCommands.Create));
        }

        [Fact]
        public async Task AttachPassesTheOwnerWindowThrough()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(0x99));

            Assert.Equal(new IntPtr(0x99), _head.CreateArgs[0]);
        }

        [Fact]
        public async Task DetachDestroysTheView()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));
            backend.Detach();

            Assert.False(backend.IsAttached);
            Assert.Contains(AndroidWebViewCommands.Destroy, _head.Commands);
        }

        [Fact]
        public async Task DetachSurvivesAPayloadThatHasGoneAway()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            // The application tore its head down first. Detach must not throw out of a Dispose.
            AppContext.SetData(AndroidWebViewRegistration.InvokeKey, null);

            backend.Detach();
            Assert.False(backend.IsAttached);
        }

        [Fact]
        public void UsingTheBackendBeforeAttachIsAnError()
        {
            var backend = new AndroidWebViewBackend();
            Assert.Throws<InvalidOperationException>(() => backend.Navigate("https://example.invalid/"));
        }

        // ---- events ------------------------------------------------------------------------------

        [Fact]
        public async Task PageLifecycleBecomesTheSeamsEvents()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            var order = new List<string>();
            backend.NavigationStarting += (s, e) => order.Add("starting:" + e.Uri);
            backend.ContentLoading += (s, e) => order.Add("loading");
            backend.SourceChanged += (s, e) => order.Add("source");
            backend.NavigationCompleted += (s, e) => order.Add("completed:" + e.IsSuccess);

            _head.Raise(AndroidWebViewEvents.NavigationStarting, "https://example.invalid/");
            _head.Raise(AndroidWebViewEvents.ContentLoading, "https://example.invalid/");
            _head.Raise(AndroidWebViewEvents.NavigationCompleted, "https://example.invalid/");

            Assert.Equal(
                new[] { "starting:https://example.invalid/", "loading", "source", "completed:True" },
                order);
        }

        [Fact]
        public async Task NavigationIdsIncreasePerNavigation()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            var ids = new List<ulong>();
            backend.NavigationStarting += (s, e) => ids.Add(e.NavigationId);

            _head.Raise(AndroidWebViewEvents.NavigationStarting, "a");
            _head.Raise(AndroidWebViewEvents.NavigationStarting, "b");

            Assert.Equal(2, ids.Count);
            Assert.True(ids[1] > ids[0]);
        }

        [Fact]
        public async Task AFailedNavigationCompletesUnsuccessfully()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            WebViewNavigationCompletedEventArgs completed = null;
            backend.NavigationCompleted += (s, e) => completed = e;

            _head.Raise(AndroidWebViewEvents.NavigationFailed, "net::ERR_NAME_NOT_RESOLVED");

            Assert.NotNull(completed);
            Assert.False(completed.IsSuccess);
            Assert.Equal(WebViewErrorStatus.HostNameNotResolved, completed.WebErrorStatus);
        }

        [Fact]
        public async Task ErrorDescriptionsMapToTheSeamsVocabulary()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            WebViewErrorStatus last = WebViewErrorStatus.Unknown;
            backend.NavigationCompleted += (s, e) => last = e.WebErrorStatus;

            // WebViewClient reports a description, not a code the seam shares, so only the
            // unambiguous ones are mapped -- and anything else must stay Unknown rather than be
            // guessed into a plausible-looking status.
            _head.Raise(AndroidWebViewEvents.NavigationFailed, "net::ERR_CONNECTION_TIMED_OUT");
            Assert.Equal(WebViewErrorStatus.Timeout, last);

            _head.Raise(AndroidWebViewEvents.NavigationFailed, "net::ERR_CONNECTION_REFUSED");
            Assert.Equal(WebViewErrorStatus.CannotConnect, last);

            _head.Raise(AndroidWebViewEvents.NavigationFailed, "net::ERR_INTERNET_DISCONNECTED");
            Assert.Equal(WebViewErrorStatus.ServerUnreachable, last);

            _head.Raise(AndroidWebViewEvents.NavigationFailed, "SSL handshake failed");
            Assert.Equal(WebViewErrorStatus.CertificateIsInvalid, last);

            _head.Raise(AndroidWebViewEvents.NavigationFailed, "something nobody has seen before");
            Assert.Equal(WebViewErrorStatus.Unknown, last);
        }

        [Fact]
        public async Task AnUnknownEventFromANewerPayloadIsIgnored()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            // The payload ships separately from this assembly, so a newer one WILL raise events this
            // build has never heard of. That must not be fatal.
            _head.Raise("somethingFromTheFuture", "payload");
        }

        [Fact]
        public async Task AWebMessageCarriesBothForms()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            WebViewMessageReceivedEventArgs received = null;
            backend.WebMessageReceived += (s, e) => received = e;

            _head.Raise(AndroidWebViewEvents.WebMessage, "hello \"world\"");

            Assert.NotNull(received);
            Assert.Equal("hello \"world\"", received.WebMessageAsString);

            // The JSON form must be the string ENCODED, not the raw text -- a consumer parsing it
            // would otherwise choke on the embedded quotes.
            Assert.Equal("hello \"world\"", WebViewScript.FromJson(received.WebMessageAsJson));
        }

        [Fact]
        public async Task AnUnhandledNewWindowFollowsTheLinkInPlace()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            _head.Commands.Clear();
            _head.Raise(AndroidWebViewEvents.NewWindow, "https://example.invalid/popup");

            // There is no second view to open it in, so following it in place is the closest honest
            // behaviour -- and the same choice the WKWebView heads make.
            Assert.Contains(AndroidWebViewCommands.Navigate, _head.Commands);
        }

        [Fact]
        public async Task AHandledNewWindowIsLeftAlone()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));
            backend.NewWindowRequested += (s, e) => e.Handled = true;

            _head.Commands.Clear();
            _head.Raise(AndroidWebViewEvents.NewWindow, "https://example.invalid/popup");

            Assert.DoesNotContain(AndroidWebViewCommands.Navigate, _head.Commands);
        }

        // ---- scripting ------------------------------------------------------------------------------

        [Fact]
        public async Task ExecuteScriptCompletesFromThePayloadsCallback()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            _head.ScriptResult = "\"answer\"";
            string json = await backend.ExecuteScriptAsync("document.title");

            Assert.Equal("\"answer\"", json);
            Assert.Equal("answer", WebViewScript.FromJson(json));
        }

        [Fact]
        public async Task ANullScriptResultBecomesJsonNull()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            // evaluateJavascript hands back a Java null for a void expression; "null" is the JSON
            // the seam promises, and callers parse it.
            _head.ScriptResult = null;
            Assert.Equal("null", await backend.ExecuteScriptAsync("void 0"));
        }

        [Fact]
        public async Task PostingAStringMessageEncodesItAsJson()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            backend.PostWebMessageAsString("a\"b");

            Assert.Equal("\"a\\\"b\"", _head.LastPostedMessage);
        }

        // ---- capture ---------------------------------------------------------------------------------

        [Fact]
        public async Task CaptureReturnsWhateverThePayloadEncoded()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            _head.CaptureResult = new byte[] { 0x89, 0x50, 0x4E, 0x47 };   // a PNG signature
            byte[] bytes = await backend.CapturePreviewAsync(png: true);

            Assert.Equal(_head.CaptureResult, bytes);
        }

        [Fact]
        public async Task TheRequestedFormatReachesThePayload()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            _head.CaptureResult = new byte[] { 1 };

            await backend.CapturePreviewAsync(png: false);
            Assert.False((bool)_head.CaptureArgs[1]);

            await backend.CapturePreviewAsync(png: true);
            Assert.True((bool)_head.CaptureArgs[1]);
        }

        [Fact]
        public async Task AnEmptyCaptureIsAFailureRatherThanAnEmptyImage()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            // This is the hardware-layer case: the view draws BLANK into a software canvas and the
            // payload has nothing to hand back. Zero bytes must not surface as a valid image, or a
            // caller writes an empty file and believes it captured something.
            _head.CaptureResult = Array.Empty<byte>();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => backend.CapturePreviewAsync(png: true));
        }

        [Fact]
        public async Task ANullCaptureIsAlsoAFailure()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            _head.CaptureResult = null;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => backend.CapturePreviewAsync(png: true));
        }

        // ---- navigation ---------------------------------------------------------------------------------

        [Fact]
        public async Task APlainNavigateIsUsedWhenThereIsNoBody()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            _head.Commands.Clear();
            backend.NavigateWithPost("https://example.invalid/", null, null);

            // No body and no headers is an ordinary navigation; sending it down the POST path would
            // make the head build a request it does not need.
            Assert.Contains(AndroidWebViewCommands.Navigate, _head.Commands);
            Assert.DoesNotContain(AndroidWebViewCommands.NavigateWithPost, _head.Commands);
        }

        [Fact]
        public async Task ABodyGoesDownThePostPath()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            _head.Commands.Clear();
            backend.NavigateWithPost("https://example.invalid/", new byte[] { 1, 2, 3 }, null);

            Assert.Contains(AndroidWebViewCommands.NavigateWithPost, _head.Commands);
        }

        [Fact]
        public async Task SourceFallsBackToWhatWasAskedFor()
        {
            var backend = new AndroidWebViewBackend();
            await backend.AttachAsync(new IntPtr(1));

            // A view that has not committed a page yet reports nothing; the last requested URI is a
            // better answer than null for a caller asking where the control is pointed.
            _head.SourceResult = null;
            backend.Navigate("https://example.invalid/asked");

            Assert.Equal("https://example.invalid/asked", backend.Source);
        }

        // ---- registration ----------------------------------------------------------------------------------

        [Fact]
        public async Task WithNoPayloadRegisteredAttachFailsWithSomethingActionable()
        {
            AppContext.SetData(AndroidWebViewRegistration.InvokeKey, null);

            var backend = new AndroidWebViewBackend();

            // Not a NullReferenceException and not silence: the application is missing a piece of
            // its own head, and the message has to say which.
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => backend.AttachAsync(new IntPtr(1)));
        }

        /// <summary>
        /// A stand-in for the Android head payload: answers the command vocabulary and can raise the
        /// event vocabulary. Also the clearest statement of what a real payload must implement.
        /// </summary>
        private sealed class FakeHead
        {
            internal readonly List<string> Commands = new List<string>();
            internal object[] CreateArgs = Array.Empty<object>();
            internal string ScriptResult = "null";
            internal string SourceResult;
            internal string LastPostedMessage;
            internal byte[] CaptureResult = Array.Empty<byte>();
            internal object[] CaptureArgs = Array.Empty<object>();

            private Action<string, string> _sink;

            internal void Raise(string kind, string payload) => _sink?.Invoke(kind, payload);

            internal object Invoke(string command, object[] args)
            {
                Commands.Add(command);

                switch (command)
                {
                    case AndroidWebViewCommands.Create:
                        CreateArgs = args;
                        return 7;                        // any non-zero id

                    case AndroidWebViewCommands.SetEventSink:
                        _sink = (Action<string, string>)args[1];
                        return null;

                    case AndroidWebViewCommands.ExecuteScript:
                        ((Action<string>)args[2])(ScriptResult);
                        return null;

                    case AndroidWebViewCommands.CapturePreview:
                        CaptureArgs = args;
                        ((Action<byte[]>)args[2])(CaptureResult);
                        return null;

                    case AndroidWebViewCommands.PostMessage:
                        LastPostedMessage = (string)args[1];
                        return null;

                    case AndroidWebViewCommands.GetSource:
                        return SourceResult;

                    case AndroidWebViewCommands.GetTitle:
                        return "title";

                    case AndroidWebViewCommands.CanGoBack:
                    case AndroidWebViewCommands.CanGoForward:
                        return false;

                    default:
                        return null;
                }
            }
        }
    }
}
