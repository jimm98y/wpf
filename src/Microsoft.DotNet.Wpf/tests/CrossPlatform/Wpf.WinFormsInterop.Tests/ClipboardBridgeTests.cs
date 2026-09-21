// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Copy and paste in a hosted WinForms control.
//
// Every Clipboard entry point on the driver was a generated stub: ClipboardOpen returned zero,
// ClipboardStore did nothing, ClipboardRetrieve returned null. Ctrl+C and Ctrl+V in a hosted TextBox
// therefore did nothing whatever -- no error, no message, no effect -- which is the hardest kind of
// missing feature to notice, and the reason this suite asserts the STORE-AND-FETCH round trip
// rather than that the calls do not throw.
//
// The system clipboard itself is not exercised here. It is shared with the rest of the desktop, so
// a test that wrote to it would fight whatever else is running and would fail differently on a
// build machine with no clipboard at all; the bridge is a seam precisely so it can be substituted.
// What is asserted is the driver's own behaviour: which formats go to the system, which stay in
// process, and which of the two wins when both have something.
//

using System;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ClipboardBridgeTests : IDisposable
    {
        /// <summary>Stands in for the desktop clipboard, so the tests do not touch the real one.</summary>
        private sealed class FakeClipboard : IClipboardBridge
        {
            internal string Text;
            internal int Clears;

            public bool TryGetText(out string text) { text = Text; return Text != null; }
            public void SetText(string text) => Text = text;
            public bool ContainsText() => Text != null;
            public void Clear() { Text = null; Clears++; }
        }

        private readonly IClipboardBridge _saved;
        private readonly FakeClipboard _fake = new();
        private readonly XplatUIWebGpu _driver = XplatUIWebGpu.GetInstance();

        public ClipboardBridgeTests()
        {
            _saved = XplatUIWebGpu.ClipboardBridge;
            XplatUIWebGpu.ClipboardBridge = _fake;
        }

        public void Dispose() => XplatUIWebGpu.ClipboardBridge = _saved;

        private int Id(string format) => _driver.ClipboardGetID(_driver.ClipboardOpen(false), format);

        /// <summary>Text goes OUT to the system, so another application can paste it.</summary>
        [Fact]
        public void CopiedTextReachesTheSystemClipboard()
        {
            IntPtr h = _driver.ClipboardOpen(false);
            _driver.ClipboardStore(h, "copied", Id(DataFormats.UnicodeText), null, true);

            Assert.Equal("copied", _fake.Text);
        }

        /// <summary>
        /// And paste reads the system's text back -- including text this process never stored, which
        /// is the whole point of going out to it.
        /// </summary>
        [Fact]
        public void PastedTextComesFromTheSystemClipboard()
        {
            _fake.Text = "from another app";

            IntPtr h = _driver.ClipboardOpen(false);
            object got = _driver.ClipboardRetrieve(h, Id(DataFormats.UnicodeText), null);

            Assert.Equal("from another app", got);
        }

        /// <summary>
        /// When both have something, the SYSTEM wins. Something copied elsewhere since is newer than
        /// whatever this process last stored, and preferring the stale local copy is exactly the bug
        /// that makes paste look broken while looking correct in code.
        /// </summary>
        [Fact]
        public void TheSystemClipboardWinsOverAStaleLocalCopy()
        {
            IntPtr h = _driver.ClipboardOpen(false);
            _driver.ClipboardStore(h, "ours", Id(DataFormats.UnicodeText), null, true);

            _fake.Text = "newer, from elsewhere";

            Assert.Equal("newer, from elsewhere",
                _driver.ClipboardRetrieve(h, Id(DataFormats.UnicodeText), null));
        }

        /// <summary>
        /// A non-text object stays in this process. There is no honest way to hand a live CLR object
        /// to another application, so it must not be silently mangled into bytes -- but copy and
        /// paste of an application's own objects has to work.
        /// </summary>
        [Fact]
        public void AnObjectRoundTripsInProcessWithoutTouchingTheSystem()
        {
            var payload = new object();
            IntPtr h = _driver.ClipboardOpen(false);
            int id = Id("application/x-my-own-thing");

            _driver.ClipboardStore(h, payload, id, null, true);

            Assert.Null(_fake.Text);                       // nothing was pushed to the desktop
            Assert.Same(payload, _driver.ClipboardRetrieve(h, id, null));
        }

        [Fact]
        public void FormatIdsAreStableAcrossLookups()
        {
            Assert.Equal(Id("Text"), Id("Text"));
            Assert.NotEqual(Id("Text"), Id("UnicodeText"));
        }

        /// <summary>
        /// The formats on offer include text the SYSTEM has, even though this process never stored
        /// it. A control asking "is there anything I can paste?" gets its answer from here, so
        /// listing only what we put there ourselves would grey out Paste after an external copy.
        /// </summary>
        [Fact]
        public void SystemTextIsOfferedAsAnAvailableFormat()
        {
            IntPtr h = _driver.ClipboardOpen(false);
            _fake.Text = "available";

            int[] formats = _driver.ClipboardAvailableFormats(h);

            Assert.Contains(Id(DataFormats.UnicodeText), formats);
        }

        [Fact]
        public void ClearingEmptiesBothStores()
        {
            IntPtr h = _driver.ClipboardOpen(false);
            int id = Id("application/x-cleared");
            _driver.ClipboardStore(h, new object(), id, null, true);
            _fake.Text = "something";

            _driver.ClipboardStore(h, null, 0, null, true);      // WinForms' "clear"

            Assert.Equal(1, _fake.Clears);
            Assert.Null(_driver.ClipboardRetrieve(h, id, null));
        }

        /// <summary>
        /// With no host there is no bridge, and the driver must still work in process rather than
        /// throwing: a WinForms app can run with no WPF around it at all.
        /// </summary>
        [Fact]
        public void WithoutABridgeItStillCopiesWithinTheProcess()
        {
            XplatUIWebGpu.ClipboardBridge = null;

            IntPtr h = _driver.ClipboardOpen(false);
            int id = Id("application/x-no-bridge");
            _driver.ClipboardStore(h, "local only", id, null, true);

            Assert.Equal("local only", _driver.ClipboardRetrieve(h, id, null));
        }
    }
}
