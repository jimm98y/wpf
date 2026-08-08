// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser input-method channel's event mapping (DOM composition events).
//
// The DOM half cannot be exercised without a browser, but the translation from queued composition
// events to the updates the editor acts on is plain managed code, and it is where the mistakes live:
// a compositionend carrying the final text has to arrive as a COMMIT, not as one more preedit, or
// the composition never terminates and the text is left underlined forever. The k values here are
// the ones browser-window.js pushes.
//

using System;
using System.Collections.Generic;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    [Collection("BrowserTextInput")]   // one process-wide channel; the tests share it
    public class BrowserTextInputTests
    {
        private const int CompositionStart = 0;
        private const int CompositionUpdate = 1;
        private const int CompositionEnd = 2;

        [Fact]
        public void CompositionStartAloneShowsNothing()
        {
            // compositionstart carries no text. Raising an update for it would open and immediately
            // close a composition in the editor on every keystroke that starts one.
            List<BrowserImeUpdate> updates = Capture(() =>
                BrowserTextInput.DispatchQueuedEvent(CompositionStart, ""));

            Assert.Empty(updates);
            Assert.True(BrowserTextInput.IsComposing);

            BrowserTextInput.DispatchQueuedEvent(CompositionEnd, "");
        }

        [Fact]
        public void CompositionUpdateBecomesAPreedit()
        {
            List<BrowserImeUpdate> updates = Capture(() =>
            {
                BrowserTextInput.DispatchQueuedEvent(CompositionStart, "");
                BrowserTextInput.DispatchQueuedEvent(CompositionUpdate, "にほんご");
            });

            BrowserImeUpdate update = Assert.Single(updates);
            Assert.Equal("にほんご", update.PreeditText);
            Assert.Null(update.CommitText);
            Assert.True(BrowserTextInput.IsComposing);

            BrowserTextInput.DispatchQueuedEvent(CompositionEnd, "");
        }

        [Fact]
        public void CompositionEndCommitsAndStopsComposing()
        {
            BrowserTextInput.DispatchQueuedEvent(CompositionStart, "");
            BrowserTextInput.DispatchQueuedEvent(CompositionUpdate, "にほんご");

            List<BrowserImeUpdate> updates = Capture(() =>
                BrowserTextInput.DispatchQueuedEvent(CompositionEnd, "日本語"));

            BrowserImeUpdate update = Assert.Single(updates);
            Assert.Equal("日本語", update.CommitText);
            Assert.Null(update.PreeditText);
            Assert.False(BrowserTextInput.IsComposing);
        }

        [Fact]
        public void CancelledCompositionEndsWithNoText()
        {
            BrowserTextInput.DispatchQueuedEvent(CompositionStart, "");
            BrowserTextInput.DispatchQueuedEvent(CompositionUpdate, "にほん");

            // Escape: the browser ends the composition with an empty string. Both fields null is how
            // ImmComposition is told to drop the composition rather than commit an empty one.
            List<BrowserImeUpdate> updates = Capture(() =>
                BrowserTextInput.DispatchQueuedEvent(CompositionEnd, ""));

            BrowserImeUpdate update = Assert.Single(updates);
            Assert.Null(update.CommitText);
            Assert.Null(update.PreeditText);
            Assert.False(BrowserTextInput.IsComposing);
        }

        [Fact]
        public void EmptyUpdateDoesNotMasqueradeAsText()
        {
            // Deleting the last kana leaves an empty preedit; it must arrive as "no composition"
            // rather than as an empty string, which would be inserted as text.
            List<BrowserImeUpdate> updates = Capture(() =>
            {
                BrowserTextInput.DispatchQueuedEvent(CompositionStart, "");
                BrowserTextInput.DispatchQueuedEvent(CompositionUpdate, "");
            });

            BrowserImeUpdate update = Assert.Single(updates);
            Assert.Null(update.PreeditText);
            Assert.Null(update.CommitText);

            BrowserTextInput.DispatchQueuedEvent(CompositionEnd, "");
        }

        [Fact]
        public void SurrogatePairsSurviveTheTransport()
        {
            // A composition can produce characters outside the BMP; the transport is a string all
            // the way through, so this is really a guard against anyone "helpfully" indexing by char.
            const string beyondBmp = "\U00020BB7\U0001F600";

            List<BrowserImeUpdate> updates = Capture(() =>
            {
                BrowserTextInput.DispatchQueuedEvent(CompositionStart, "");
                BrowserTextInput.DispatchQueuedEvent(CompositionEnd, beyondBmp);
            });

            Assert.Equal(beyondBmp, Assert.Single(updates).CommitText);
        }

        private static List<BrowserImeUpdate> Capture(Action action)
        {
            var updates = new List<BrowserImeUpdate>();
            void Handler(BrowserImeUpdate update) => updates.Add(update);

            BrowserTextInput.ImeUpdate += Handler;
            try { action(); }
            finally { BrowserTextInput.ImeUpdate -= Handler; }

            return updates;
        }
    }
}
