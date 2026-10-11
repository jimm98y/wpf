// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The automation tree WPF exposes: what a screen reader actually reads.
//
// AutomationPeer is the model underneath UI Automation. On Windows a UIA client reaches it through
// WM_GETOBJECT and ElementProxy (see WmGetObjectTests); on the other platforms the same peers back
// the Cocoa, UIKit and Android accessibility bridges. So these are the platform-neutral half.
//
// The assertions are about what is EXPOSED -- control types, names, values, text -- rather than
// about which peer class produced it, because that is what a reader consumes.
//
// NOT covered here, deliberately: IsOffscreen, which is what a reader consults before announcing
// something and is therefore how collapsed content avoids being read aloud. It is only meaningful
// for an element inside a hosted window -- a standalone laid-out element reports offscreen whether
// it is visible or not, because it has no PresentationSource to be on-screen in -- and this suite
// can create exactly one window (see WmGetObjectTests). A test asserting it here would have passed
// for the wrong reason. It is a real gap, and it wants a hosted-window fixture to close properly.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using Xunit;

namespace Wpf.Accessibility.Tests
{
    public class AutomationPeerTests
    {
        [Fact]
        public void ATextBlockIsExposedAsTextWithItsContent()
        {
            var probe = AccessibilityHarness.Probe(
                () => new TextBlock { Text = "hello reader" },
                p => (Type: p.GetAutomationControlType(), Name: p.GetName()));

            Assert.Equal(AutomationControlType.Text, probe.Type);
            Assert.Equal("hello reader", probe.Name);
        }

        /// <summary>
        /// Japanese, because the text stack under this is the one the port rewrote. A name that
        /// arrived mangled or empty would be read out wrong by a screen reader while looking
        /// perfect on screen.
        /// </summary>
        [Fact]
        public void AJapaneseNameSurvivesToTheAutomationTree()
        {
            string name = AccessibilityHarness.Probe(
                () => new TextBlock { Text = "日本語のテキスト" }, p => p.GetName());

            Assert.Equal("日本語のテキスト", name);
        }

        [Fact]
        public void AButtonIsExposedAsAnInvokableButton()
        {
            var probe = AccessibilityHarness.Probe(
                () => new Button { Content = "Save" },
                p => (Type: p.GetAutomationControlType(),
                      Name: p.GetName(),
                      CanInvoke: p.GetPattern(PatternInterface.Invoke) is IInvokeProvider));

            Assert.Equal(AutomationControlType.Button, probe.Type);
            Assert.Equal("Save", probe.Name);
            Assert.True(probe.CanInvoke,
                "a Button must offer the Invoke pattern, or a reader cannot activate it");
        }

        [Fact]
        public void AnAutomationPropertiesNameOverridesTheContent()
        {
            string name = AccessibilityHarness.Probe(
                () =>
                {
                    var b = new Button { Content = "X" };
                    AutomationProperties.SetName(b, "Close the dialog");
                    return b;
                },
                p => p.GetName());

            Assert.Equal("Close the dialog", name);
        }

        [Fact]
        public void ATextBoxExposesItsTextThroughTheValuePattern()
        {
            var probe = AccessibilityHarness.Probe(
                () => new TextBox { Text = "ここに入力" },
                p => (Type: p.GetAutomationControlType(),
                      Value: (p.GetPattern(PatternInterface.Value) as IValueProvider)?.Value));

            Assert.Equal(AutomationControlType.Edit, probe.Type);
            Assert.True(probe.Value is not null, "a TextBox must offer the Value pattern");
            Assert.Equal("ここに入力", probe.Value);
        }

        [Fact]
        public void APanelExposesItsChildrenInOrder()
        {
            List<string> names = AccessibilityHarness.ChildNames(() => new UIElement[]
            {
                new TextBlock { Text = "first" },
                new TextBlock { Text = "second" },
                new Button { Content = "third" },
            });

            Assert.True(names.Count >= 3, $"expected three children, got [{string.Join(", ", names)}]");
            Assert.Equal(new[] { "first", "second", "third" }, names.GetRange(0, 3));
        }

        /// <summary>
        /// FlowDocument content, which is where this port replaced the native PTS engine outright.
        /// The automation text comes from the text container rather than from layout, so this also
        /// says the two have not drifted apart: a table whose cells render but are missing from the
        /// automation text is unreadable to a screen reader while looking perfectly fine on screen.
        ///
        /// The layout side of the same document is Wpf.Document.Tests' BlockLayoutTests.
        /// </summary>
        [Fact]
        public void AFlowDocumentExposesItsTextIncludingTableCells()
        {
            string text = AccessibilityHarness.DocumentText(() =>
            {
                var doc = new FlowDocument();
                doc.Blocks.Add(new Paragraph(new Run("intro paragraph")));

                var table = new Table();
                table.Columns.Add(new TableColumn());
                table.Columns.Add(new TableColumn());
                var group = new TableRowGroup();
                var row = new TableRow();
                row.Cells.Add(new TableCell(new Paragraph(new Run("cell one"))));
                row.Cells.Add(new TableCell(new Paragraph(new Run("cell two"))));
                group.Rows.Add(row);
                table.RowGroups.Add(group);
                doc.Blocks.Add(table);

                return doc;
            });

            Assert.Contains("intro paragraph", text, StringComparison.Ordinal);
            Assert.Contains("cell one", text, StringComparison.Ordinal);
            Assert.Contains("cell two", text, StringComparison.Ordinal);
        }
    }
}
