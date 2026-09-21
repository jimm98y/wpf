// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The data object a text editor builds for Copy, Cut and drag.
//
// All three go through one method (TextEditorCopyPaste._CreateDataObject), and it used to say
// `new DataObject()` unconditionally. That type builds a native OLE adapter in its CONSTRUCTOR
// (GlobalInterfaceTable -> CoCreateInstance -> OLE32.dll), so off Windows every one of them threw
// DllNotFoundException -- Ctrl+C in a TextBox included. Clipboard itself had already been routed
// around the same problem; its main caller had not, and nothing here noticed because no test
// pressed Copy.
//
// The tests reach the data object through the public DataObjectCopying event, which is handed the
// very object the editor just filled in. Cancelling from the handler stops the command before it
// touches the system clipboard, so most of these assert real behaviour without needing one -- and
// the two that do want the clipboard are the ones actually about the round trip.
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Xunit;

namespace Wpf.Document.Tests
{
    public class EditorDataObjectTests
    {
        /// <summary>
        ///  Runs Copy on <paramref name="editor"/> and returns the data object it built, without
        ///  letting the command reach the clipboard.
        /// </summary>
        private static IDataObject CopyingDataObject(Control editor)
        {
            IDataObject? captured = null;
            DataObject.AddCopyingHandler(editor, (_, e) =>
            {
                captured = e.DataObject;
                e.CancelCommand();
            });

            ApplicationCommands.Copy.Execute(parameter: null, target: editor);

            Assert.NotNull(captured);
            return captured!;
        }

        private static TextBox PlainEditor(string text)
        {
            var box = new TextBox { Text = text };
            box.SelectAll();
            return box;
        }

        private static RichTextBox RichEditor(string text)
        {
            var box = new RichTextBox(new FlowDocument(new Paragraph(new Run(text))));
            box.SelectAll();
            return box;
        }

        /// <summary>
        ///  Copy from a plain TextBox produces a data object at all. This is the test that fails
        ///  with DllNotFoundException off Windows if the construction gate regresses -- the
        ///  assertions past it are almost beside the point.
        /// </summary>
        [Fact]
        public void PlainCopyBuildsADataObject()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                IDataObject data = CopyingDataObject(PlainEditor("copy me"));

                Assert.True(data.GetDataPresent(DataFormats.UnicodeText));
                Assert.Equal("copy me", data.GetData(DataFormats.UnicodeText));
                return null;
            });
        }

        /// <summary>
        ///  Text is readable under every name WPF treats as interchangeable, whichever one it was
        ///  stored under. Windows gets this from DataObject's autoConvert and the off-Windows store
        ///  has to match it, or a paste handler that asks for Text finds nothing after a copy that
        ///  wrote UnicodeText.
        /// </summary>
        [Fact]
        public void TextIsReadableUnderItsAliases()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                IDataObject data = CopyingDataObject(PlainEditor("aliased"));

                Assert.Equal("aliased", data.GetData(DataFormats.Text));
                Assert.Equal("aliased", data.GetData(DataFormats.UnicodeText));
                Assert.Equal("aliased", data.GetData(DataFormats.StringFormat));

                // Whatever GetDataPresent answers for, GetFormats has to list -- a caller that
                // enumerates and copies (Clipboard.SetDataObject does exactly that off Windows)
                // sees only what is listed.
                string[] formats = data.GetFormats();
                Assert.Contains(DataFormats.UnicodeText, formats);
                Assert.Contains(DataFormats.Text, formats);
                return null;
            });
        }

        /// <summary>
        ///  A rich editor adds the rich formats. This walks the whole body of the method -- the
        ///  WPF payload, the RTF conversion, and the SettingData event raised per format -- rather
        ///  than just its first line.
        /// </summary>
        [Fact]
        public void RichCopyCarriesRichFormats()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                IDataObject data = CopyingDataObject(RichEditor("rich content"));

                Assert.Contains("rich content", (string)data.GetData(DataFormats.UnicodeText)!);
                Assert.True(data.GetDataPresent(DataFormats.Xaml), "Xaml missing");
                Assert.True(data.GetDataPresent(DataFormats.Rtf), "Rtf missing");

                // No XamlPackage: the WPF package is only built when the range holds images, and
                // this selection is text.
                Assert.False(data.GetDataPresent(DataFormats.XamlPackage));

                string rtf = Assert.IsType<string>(data.GetData(DataFormats.Rtf));
                Assert.StartsWith(@"{\rtf", rtf, StringComparison.Ordinal);
                return null;
            });
        }

        /// <summary>
        ///  Every format the editor claims to have set is actually retrievable. Cheap, and it
        ///  catches a store whose GetFormats and GetData disagree.
        /// </summary>
        [Fact]
        public void EveryAdvertisedFormatCanBeRead()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                IDataObject data = CopyingDataObject(RichEditor("readable"));

                foreach (string format in data.GetFormats())
                {
                    Assert.True(data.GetDataPresent(format), $"{format} listed but not present");
                    Assert.NotNull(data.GetData(format));
                }
                return null;
            });
        }

        /// <summary>
        ///  The SettingData event sees the same object the Copying event later gets. Applications
        ///  use that event to veto a format, and it is only meaningful if what they are inspecting
        ///  is the object being filled in.
        /// </summary>
        [Fact]
        public void SettingDataSeesTheObjectBeingFilled()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                TextBox editor = PlainEditor("same instance");

                IDataObject? whileSetting = null;
                DataObject.AddSettingDataHandler(editor, (_, e) => whileSetting ??= e.DataObject);

                IDataObject copied = CopyingDataObject(editor);

                Assert.NotNull(whileSetting);
                Assert.Same(whileSetting, copied);
                return null;
            });
        }

        /// <summary>
        ///  Cancelling from the Copying handler abandons the command, leaving the clipboard alone.
        ///  The other tests here rely on that, so it is worth asserting rather than assuming.
        /// </summary>
        [Fact]
        public void CancellingCopyLeavesTheClipboardAlone()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                Clipboard.SetText("previous contents");

                TextBox editor = PlainEditor("not this");
                DataObject.AddCopyingHandler(editor, (_, e) => e.CancelCommand());
                ApplicationCommands.Copy.Execute(parameter: null, target: editor);

                Assert.Equal("previous contents", Clipboard.GetText());
                return null;
            });
        }

        /// <summary>
        ///  The whole round trip, clipboard included: Copy then Paste into a second editor. This is
        ///  the one that proves the data object the editor built is one the clipboard can carry --
        ///  off Windows those are two different implementations meeting for the first time.
        /// </summary>
        [Fact]
        public void CopyThenPasteMovesTextBetweenEditors()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                ApplicationCommands.Copy.Execute(parameter: null, target: PlainEditor("round trip"));

                var destination = new TextBox();
                ApplicationCommands.Paste.Execute(parameter: null, target: destination);

                Assert.Equal("round trip", destination.Text);
                return null;
            });
        }

        /// <summary>
        ///  Cut copies AND deletes. It builds its data object the same way Copy does, so a
        ///  construction failure would take the deletion with it -- silently losing the selection
        ///  is a worse outcome than not copying it.
        /// </summary>
        [Fact]
        public void CutRemovesTheSelectionAndCopiesIt()
        {
            DocumentHarness.Sta<object?>(() =>
            {
                var editor = new TextBox { Text = "keep cut" };
                editor.Select(5, 3);

                ApplicationCommands.Cut.Execute(parameter: null, target: editor);

                Assert.Equal("keep ", editor.Text);
                Assert.Equal("cut", Clipboard.GetText());
                return null;
            });
        }
    }
}
