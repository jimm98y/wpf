// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Drag and drop across the WPF/WinForms boundary.
//
// WPF's own drag and drop works on every head. WinForms controls hosted in a WindowsFormsHost took
// no part in it: XplatUIWebGpu never overrode SetAllowDrop or StartDrag, so both fell through to
// XplatUIDriver's base implementations, which print "Drag and Drop is currently not supported on
// this platform" and do nothing. A hosted control with AllowDrop could not be dropped on, and
// DoDragDrop returned None without starting anything.
//
// What is tested here is the CROSSING, because that is where the mistakes live. The two stacks
// define separate IDataObject types and separate DragDropEffects enums with no relationship between
// them, and adapting one to the other is the kind of code that reads as obviously correct in both
// directions while quietly dropping a format, converting a value twice, or wrapping an object that
// was already wrapped. Whether AppKit then delivers the drag is the platform backend's business and
// is covered where that lives.
//

using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class DragDropBridgeTests
    {
        /// <summary>A data object that records what was asked of it, standing in for either side.</summary>
        private sealed class RecordingWinFormsData : IDataObject
        {
            internal readonly System.Collections.Generic.List<string> Requested = new();
            private readonly System.Collections.Generic.Dictionary<string, object> _data = new();

            internal RecordingWinFormsData(string format, object value) => _data[format] = value;

            public object GetData(string format) { Requested.Add(format); return _data.TryGetValue(format, out object v) ? v : null; }
            public object GetData(string format, bool autoConvert) => GetData(format);
            public object GetData(Type format) => GetData(format.FullName);

            public bool GetDataPresent(string format) => _data.ContainsKey(format);
            public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);
            public bool GetDataPresent(Type format) => GetDataPresent(format.FullName);

            public string[] GetFormats() => new System.Collections.Generic.List<string>(_data.Keys).ToArray();
            public string[] GetFormats(bool autoConvert) => GetFormats();

            public void SetData(object data) => _data[data.GetType().FullName] = data;
            public void SetData(string format, bool autoConvert, object data) => _data[format] = data;
            public void SetData(string format, object data) => _data[format] = data;
            public void SetData(Type format, object data) => _data[format.FullName] = data;
        }

        /// <summary>
        /// The same, on the WPF side. Written out rather than using System.Windows.DataObject
        /// because that type implements interfaces from System.Private.Windows.Core, and merely
        /// naming it would drag a reference to that assembly into this one.
        /// </summary>
        private sealed class RecordingWpfData : System.Windows.IDataObject
        {
            private readonly System.Collections.Generic.Dictionary<string, object> _data = new();

            internal RecordingWpfData(string format, object value) => _data[format] = value;

            public object GetData(string format) => _data.TryGetValue(format, out object v) ? v : null;
            public object GetData(string format, bool autoConvert) => GetData(format);
            public object GetData(Type format) => GetData(format.FullName);

            public bool GetDataPresent(string format) => _data.ContainsKey(format);
            public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);
            public bool GetDataPresent(Type format) => GetDataPresent(format.FullName);

            public string[] GetFormats() => new System.Collections.Generic.List<string>(_data.Keys).ToArray();
            public string[] GetFormats(bool autoConvert) => GetFormats();

            public void SetData(object data) => _data[data.GetType().FullName] = data;
            public void SetData(string format, object data) => _data[format] = data;
            public void SetData(string format, object data, bool autoConvert) => _data[format] = data;
            public void SetData(Type format, object data) => _data[format.FullName] = data;
        }

        // ---- the adapters -----------------------------------------------------------------

        /// <summary>
        /// A WinForms payload seen through WPF eyes keeps its format name and its value. Both stacks
        /// use the Win32 clipboard names, so nothing should be translated -- and a translation layer
        /// that "helpfully" renamed something would show up here as a missing format.
        /// </summary>
        [Fact]
        public void AWinFormsPayloadReadsThroughAsWpf()
        {
            var source = new RecordingWinFormsData(DataFormats.Text, "hello");

            System.Windows.IDataObject wpf = DragDropData.ToWpf(source);

            Assert.True(wpf.GetDataPresent(DataFormats.Text));
            Assert.Equal("hello", wpf.GetData(DataFormats.Text));
            Assert.Contains(DataFormats.Text, wpf.GetFormats());
            Assert.Contains(DataFormats.Text, source.Requested);
        }

        /// <summary>
        /// The payload must be FORWARDED, not copied: a drag can carry a live object, and formats are
        /// produced lazily by design. Asking the adapter for one format must reach the original and
        /// must not have pulled the others across in advance.
        /// </summary>
        [Fact]
        public void TheAdapterForwardsRatherThanCopying()
        {
            var payload = new object();
            var source = new RecordingWinFormsData("custom", payload);

            System.Windows.IDataObject wpf = DragDropData.ToWpf(source);

            Assert.Empty(source.Requested);              // nothing fetched until asked
            Assert.Same(payload, wpf.GetData("custom")); // the same instance, not a copy
        }

        /// <summary>
        /// A payload that crosses and comes back must arrive as ITSELF, not as an adapter wrapping an
        /// adapter. A WinForms control dragging onto another WinForms control in the same window is
        /// exactly that round trip, and each extra layer is one more chance to lose a format.
        /// </summary>
        [Fact]
        public void ARoundTripUnwrapsInsteadOfStacking()
        {
            var original = new RecordingWinFormsData(DataFormats.Text, "there");

            System.Windows.IDataObject asWpf = DragDropData.ToWpf(original);
            IDataObject backAgain = DragDropData.ToWinForms(asWpf);

            Assert.Same(original, backAgain);
        }

        [Fact]
        public void AWpfPayloadRoundTripsTheOtherWayToo()
        {
            var wpf = new RecordingWpfData(DataFormats.Text, "wpf side");

            IDataObject asWinForms = DragDropData.ToWinForms(wpf);
            System.Windows.IDataObject backAgain = DragDropData.ToWpf(asWinForms);

            Assert.Same(wpf, backAgain);
            Assert.Equal("wpf side", asWinForms.GetData(DataFormats.Text));
        }

        /// <summary>
        /// Control.DoDragDrop takes a plain object, not necessarily a data object -- dragging a
        /// string is the common case -- and that has to become something WPF can carry.
        /// </summary>
        [Fact]
        public void ABarePayloadBecomesADataObject()
        {
            System.Windows.IDataObject wpf = DragDropData.ToWpf("just a string");

            Assert.NotNull(wpf);
            Assert.Equal("just a string", wpf.GetData(typeof(string)));
        }

        [Fact]
        public void NullStaysNull()
        {
            Assert.Null(DragDropData.ToWpf(null));
            Assert.Null(DragDropData.ToWinForms(null));
        }

        // ---- the driver's side ------------------------------------------------------------

        /// <summary>
        /// SetAllowDrop used to be the base-class stub that only printed a message. The driver has to
        /// remember which windows asked, because that is what decides whether a drag over the host
        /// has anywhere to go.
        /// </summary>
        [Fact]
        public void TheDriverRemembersWhichWindowsAcceptDrops()
        {
            var driver = XplatUIWebGpu.GetInstance();
            var handle = new IntPtr(0x4001);

            bool before = driver.HasDropTargets;
            driver.SetAllowDrop(handle, true);
            Assert.True(driver.HasDropTargets);

            driver.SetAllowDrop(handle, false);
            Assert.Equal(before, driver.HasDropTargets);
        }

        /// <summary>
        /// StartDrag with nothing hosting returns None rather than pretending. The drag has to be
        /// started by WPF against a WPF element; with no host there is no element and no drag.
        /// </summary>
        [Fact]
        public void StartingADragWithNoHostReturnsNone()
        {
            var driver = XplatUIWebGpu.GetInstance();
            Func<object, DragDropEffects, DragDropEffects> saved = driver.StartDragRequested;
            driver.StartDragRequested = null;
            try
            {
                Assert.Equal(DragDropEffects.None,
                    driver.StartDrag(new IntPtr(0x4002), "payload", DragDropEffects.Copy));
            }
            finally
            {
                driver.StartDragRequested = saved;
            }
        }

        /// <summary>
        /// And with a host, the request reaches it with the payload and the allowed effects intact,
        /// and the host's answer comes back as the result of DoDragDrop.
        /// </summary>
        [Fact]
        public void StartingADragAsksTheHostAndReturnsItsAnswer()
        {
            var driver = XplatUIWebGpu.GetInstance();
            Func<object, DragDropEffects, DragDropEffects> saved = driver.StartDragRequested;

            object seenData = null;
            DragDropEffects seenAllowed = DragDropEffects.None;
            driver.StartDragRequested = (data, allowed) =>
            {
                seenData = data;
                seenAllowed = allowed;
                return DragDropEffects.Move;
            };
            try
            {
                DragDropEffects result = driver.StartDrag(
                    new IntPtr(0x4003), "dragged", DragDropEffects.Copy | DragDropEffects.Move);

                Assert.Equal("dragged", seenData);
                Assert.Equal(DragDropEffects.Copy | DragDropEffects.Move, seenAllowed);
                Assert.Equal(DragDropEffects.Move, result);
            }
            finally
            {
                driver.StartDragRequested = saved;
            }
        }

        /// <summary>
        /// The two DragDropEffects enums are distinct types carrying the same Win32 DROPEFFECT bits.
        /// The bridge casts between them, so the values had better line up -- a mismatch would show
        /// as a drag that reports Copy where the user asked for Move.
        /// </summary>
        [Theory]
        [InlineData((int)DragDropEffects.None)]
        [InlineData((int)DragDropEffects.Copy)]
        [InlineData((int)DragDropEffects.Move)]
        [InlineData((int)DragDropEffects.Link)]
        [InlineData((int)DragDropEffects.Scroll)]
        [InlineData((int)DragDropEffects.All)]
        public void TheTwoEffectsEnumsAgreeValueForValue(int value)
        {
            var winForms = (DragDropEffects)value;
            var wpf = (System.Windows.DragDropEffects)value;

            Assert.Equal(winForms.ToString(), wpf.ToString());
        }
    }
}
