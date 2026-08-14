// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android drag-and-drop backend's TARGET half -- the part that turns android.view.DragEvent into
// the DragEnter/DragOver/DragLeave/Drop the WPF side consumes.
//
// It is testable from any machine, and that is not an accident of this suite: everything between the
// Java listener and PlatformDragDrop.Target is ordinary managed code with no Android type in it, for
// the same reason AndroidClipboard's is (WindowsBase cannot reference Mono.Android at all). What
// cannot be covered here is the listener itself, which lives in the head payload and needs a device.
//
// The sequencing is the substance. Android's event set is not WPF's: ACTION_DRAG_ENTERED carries no
// position and so cannot raise DragEnter, ACTION_DROP can arrive with no location before it, and a
// drag crossing between two of the application's windows has to leave one before entering the next.
// Each of those is a rule a drop handler is entitled to rely on and none of them is visible in the
// Android API, which is what makes them worth pinning down.
//
// The other half of the substance is WHEN data can be read. Android hands over a drag's ClipData
// only at the drop -- deliberately, so an app cannot read what is merely passing over it -- so a
// read during DragOver has nothing to answer with, and one during the drop has everything.
//

using System;
using System.Collections.Generic;
using System.Text;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed class DragDropBackendTests : IDisposable
    {
        private static readonly IntPtr WindowA = new IntPtr(0x1001);
        private static readonly IntPtr WindowB = new IntPtr(0x1002);

        private static readonly string[] TextOnly = { "text/plain" };
        private static readonly string[] TextAndFiles = { "text/plain", "text/uri-list" };

        // DragDropEffects, as the seam passes them.
        private const int EffectNone = 0;
        private const int EffectCopy = 1;

        private readonly IPlatformDropTarget _previous = PlatformDragDrop.Target;
        private readonly RecordingTarget _target = new RecordingTarget();

        public DragDropBackendTests() => PlatformDragDrop.Target = _target;

        public void Dispose()
        {
            // Leave no drag in flight: the backend's "which window am I inside" is static, and a test
            // that ends mid-drag would hand the next one a drag it never started.
            AndroidDragDrop.NotifyDragEnded(WindowA);
            AndroidDragDrop.NotifyDragEnded(WindowB);
            PlatformDragDrop.Target = _previous;
        }

        // ---- sequencing -----------------------------------------------------------------

        /// <summary>
        ///  The first location inside a view is the DragEnter; every one after it is a DragOver. This
        ///  is the whole reason ACTION_DRAG_ENTERED is not what raises the enter -- it carries no
        ///  position, and DragEnter needs one to find the element under the pointer.
        /// </summary>
        [Fact]
        public void TheFirstLocationEntersAndTheRestMove()
        {
            AndroidDragDrop.NotifyDragStarted(WindowA, TextOnly);

            AndroidDragDrop.NotifyDragLocation(WindowA, 10, 20, TextOnly);
            AndroidDragDrop.NotifyDragLocation(WindowA, 11, 21, TextOnly);
            AndroidDragDrop.NotifyDragLocation(WindowA, 12, 22, TextOnly);

            Assert.Equal(new[] { "enter(10,20)", "over(11,21)", "over(12,22)" }, _target.Calls);
        }

        [Fact]
        public void ExitingRaisesDragLeave()
        {
            AndroidDragDrop.NotifyDragLocation(WindowA, 10, 20, TextOnly);
            AndroidDragDrop.NotifyDragExited(WindowA);

            Assert.Equal(new[] { "enter(10,20)", "leave" }, _target.Calls);
        }

        /// <summary>
        ///  A drag moving from one of the application's windows to another leaves the first before
        ///  entering the second, which is the pair a drop handler is entitled to see. Android sends
        ///  the exit and the next location as unrelated events on two different views.
        /// </summary>
        [Fact]
        public void CrossingBetweenTwoWindowsLeavesBeforeEntering()
        {
            AndroidDragDrop.NotifyDragLocation(WindowA, 10, 20, TextOnly);
            AndroidDragDrop.NotifyDragLocation(WindowB, 30, 40, TextOnly);

            Assert.Equal(new[] { "enter(10,20)", "leave", "enter(30,40)" }, _target.Calls);
            Assert.Equal(new[] { WindowA, WindowA, WindowB }, _target.Handles);
        }

        /// <summary>
        ///  A drag released the instant it crosses into a view gets ACTION_DROP with no
        ///  ACTION_DRAG_LOCATION before it. Dropping onto a window that was never entered would give
        ///  the drop no element to land on, so the enter is synthesised first.
        /// </summary>
        [Fact]
        public void ADropWithNoLocationBeforeItStillEnters()
        {
            AndroidDragDrop.NotifyDrop(WindowA, 5, 6, TextOnly, new[] { "hello" }, null);

            Assert.Equal(new[] { "enter(5,6)", "drop(5,6)" }, _target.Calls);
        }

        [Fact]
        public void ADropAfterALocationDoesNotEnterTwice()
        {
            AndroidDragDrop.NotifyDragLocation(WindowA, 5, 6, TextOnly);
            AndroidDragDrop.NotifyDrop(WindowA, 5, 6, TextOnly, new[] { "hello" }, null);

            Assert.Equal(new[] { "enter(5,6)", "drop(5,6)" }, _target.Calls);
        }

        /// <summary>
        ///  Android reports what the listener returned back to the drag's SOURCE, so "did WPF take
        ///  it" has to be the answer -- not "did WPF see it".
        /// </summary>
        [Fact]
        public void TheDropIsAcceptedOnlyWhenTheTargetChoseAnEffect()
        {
            _target.Effect = EffectCopy;
            Assert.True(AndroidDragDrop.NotifyDrop(WindowA, 1, 2, TextOnly, new[] { "x" }, null));

            _target.Effect = EffectNone;
            Assert.False(AndroidDragDrop.NotifyDrop(WindowB, 1, 2, TextOnly, new[] { "x" }, null));
        }

        /// <summary>
        ///  A view whose ACTION_DRAG_STARTED is declined is sent no further events for that drag --
        ///  including the drop -- so declining when no WPF target is installed has to be exactly that
        ///  case and no other.
        /// </summary>
        [Fact]
        public void ADragIsDeclinedOnlyWhenThereIsNoTarget()
        {
            Assert.True(AndroidDragDrop.NotifyDragStarted(WindowA, TextOnly));

            PlatformDragDrop.Target = null;
            Assert.False(AndroidDragDrop.NotifyDragStarted(WindowA, TextOnly));
        }

        // ---- what can be read, and when -------------------------------------------------

        [Fact]
        public void TheDroppedTextIsReadableAsPlainText()
        {
            AndroidDragDrop.NotifyDrop(WindowA, 1, 2, TextOnly, new[] { "hello" }, null);

            Assert.Equal("hello", Utf8(_target.ReadAtDrop("text/plain")));
        }

        /// <summary>
        ///  Files arrive as ClipData URI items and leave as a text/uri-list, which is what the WPF
        ///  side turns into DataFormats.FileDrop. RFC 2483 asks for CRLF between the entries.
        /// </summary>
        [Fact]
        public void DroppedUrisAreReadableAsAUriList()
        {
            AndroidDragDrop.NotifyDrop(WindowA, 1, 2, TextAndFiles, null,
                                       new[] { "file:///tmp/one.txt", "file:///tmp/two.txt" });

            Assert.Equal("file:///tmp/one.txt\r\nfile:///tmp/two.txt\r\n",
                         Utf8(_target.ReadAtDrop("text/uri-list")));
        }

        /// <summary>
        ///  A type the drag advertised but Android cannot carry without a content provider reads as
        ///  ABSENT, not as present-and-empty: a handler that asks for a PNG and is handed zero bytes
        ///  has been told the drag contained an empty image, which it did not.
        /// </summary>
        [Fact]
        public void ATypeWithNothingBehindItReadsAsAbsent()
        {
            AndroidDragDrop.NotifyDrop(WindowA, 1, 2, new[] { "image/png" }, null, null);

            Assert.Null(_target.ReadAtDrop("image/png"));
        }

        /// <summary>
        ///  Nothing is readable before the drop, because Android hands over no ClipData before one.
        ///  The types are still advertised throughout, which is what keeps GetDataPresent working in
        ///  a DragOver handler -- it consults the list, not the bytes.
        /// </summary>
        [Fact]
        public void NothingIsReadableBeforeTheDrop()
        {
            AndroidDragDrop.NotifyDragLocation(WindowA, 1, 2, TextOnly);

            Assert.Equal(TextOnly, _target.OfferedTypes);
            Assert.Null(_target.Read("text/plain"));
        }

        /// <summary>And nothing is readable after it either: the ClipData is valid for the length of
        /// the drop callback and no longer.</summary>
        [Fact]
        public void NothingIsReadableAfterTheDrop()
        {
            AndroidDragDrop.NotifyDrop(WindowA, 1, 2, TextOnly, new[] { "hello" }, null);

            Assert.NotNull(_target.ReadAtDrop("text/plain"));
            Assert.Null(_target.Read("text/plain"));
        }

        // ---- availability ---------------------------------------------------------------

        /// <summary>
        ///  Mirrors AndroidClipboard: the backend needs both the platform and a head that installed
        ///  itself, because startDragAndDrop needs a View that only the payload has.
        /// </summary>
        [Fact]
        public void TheBackendIsAvailableOnlyWithAHostOnAndroid()
        {
            Assert.Equal(OperatingSystem.IsAndroid() && AndroidDragDrop.Host is not null,
                         AndroidDragDrop.IsAvailable);
        }

        private static string Utf8(byte[] bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);

        /// <summary>
        ///  Records what the backend did, and keeps the read callback it was handed so a test can ask
        ///  the questions a drop handler would -- both while the drop is running and after it.
        /// </summary>
        private sealed class RecordingTarget : IPlatformDropTarget
        {
            internal readonly List<string> Calls = new List<string>();
            internal readonly List<IntPtr> Handles = new List<IntPtr>();
            internal string[] OfferedTypes = Array.Empty<string>();
            internal int Effect = EffectCopy;

            private Func<string, byte[]> _read;
            private readonly Dictionary<string, byte[]> _atDrop = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            /// <summary>What a read of <paramref name="mime"/> answered DURING the drop.</summary>
            internal byte[] ReadAtDrop(string mime) => _atDrop.TryGetValue(mime, out byte[] bytes) ? bytes : null;

            /// <summary>What a read answers right now.</summary>
            internal byte[] Read(string mime) => _read?.Invoke(mime);

            public int DragEnter(IntPtr windowHandle, int screenX, int screenY, string[] mimeTypes,
                                 Func<string, byte[]> read, int allowedEffects)
            {
                Calls.Add($"enter({screenX},{screenY})");
                Handles.Add(windowHandle);
                OfferedTypes = mimeTypes;
                _read = read;
                return Effect;
            }

            public int DragOver(IntPtr windowHandle, int screenX, int screenY, int allowedEffects)
            {
                Calls.Add($"over({screenX},{screenY})");
                Handles.Add(windowHandle);
                return Effect;
            }

            public void DragLeave(IntPtr windowHandle)
            {
                Calls.Add("leave");
                Handles.Add(windowHandle);
            }

            public int Drop(IntPtr windowHandle, int screenX, int screenY, int allowedEffects)
            {
                Calls.Add($"drop({screenX},{screenY})");
                Handles.Add(windowHandle);

                // Read everything the drag offered while the drop is still running, which is the only
                // moment there is anything to read.
                foreach (string mime in OfferedTypes) _atDrop[mime] = _read?.Invoke(mime);
                return Effect;
            }
        }
    }
}
