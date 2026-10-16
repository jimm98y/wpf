// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A read-only TextBox still copies: Ctrl+C and the context menu's Copy put its selection on the
// clipboard, as Windows' edit control does. The unhandled-exception dialog's "Exception details" box
// is one, and copying from it did nothing.
//

using System;
using System.Drawing;
using System.Linq;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ReadOnlyTextBoxCopyTests : IDisposable
    {
        private sealed class FakeClipboard : SWF.IClipboardBridge
        {
            internal string? Text;
            public bool TryGetText(out string text) { text = Text!; return Text != null; }
            public void SetText(string text) => Text = text;
            public bool ContainsText() => Text != null;
            public void Clear() => Text = null;
        }

        private readonly SWF.IClipboardBridge _saved = SWF.XplatUIWebGpu.ClipboardBridge;
        private readonly FakeClipboard _fake = new();
        private readonly SWF.XplatUIWebGpu _driver = SWF.XplatUIWebGpu.GetInstance();

        public ReadOnlyTextBoxCopyTests() => SWF.XplatUIWebGpu.ClipboardBridge = _fake;
        public void Dispose() => SWF.XplatUIWebGpu.ClipboardBridge = _saved;

        private static (SWF.Form, SWF.TextBox) Box()
        {
            var form = new SWF.Form { ClientSize = new Size(300, 200) };
            var box = new SWF.TextBox { Multiline = true, ReadOnly = true, Left = 5, Top = 5, Width = 280, Height = 150,
                Text = "System.InvalidCastException: details\r\n   at Somewhere()" };
            form.Controls.Add(box);
            form.CreateControl();
            _ = box.Handle;
            return (form, box);
        }

        [Fact]
        public void CopyFromAReadOnlyTextBox_ReachesTheClipboard()
        {
            var (form, box) = Box();
            using (form)
            {
                box.SelectAll();
                box.Copy();
                Assert.Equal(box.Text, _fake.Text);
            }
        }

        [Fact]
        public void CtrlC_InAReadOnlyTextBox_Copies()
        {
            var (form, box) = Box();
            using (form)
            {
                box.Focus();
                box.SelectAll();
                _driver.SetFocus(box.Handle);
                // The host reports the modifiers it reads from the keyboard; the key itself arrives as
                // WM_KEYDOWN with Ctrl held.
                _driver.SetModifierKeys(SWF.Keys.Control);
                try
                {
                    _driver.InjectKeyDown('C');
                    _driver.InjectKeyUp('C');
                }
                finally { _driver.SetModifierKeys(SWF.Keys.None); }
                SWF.Application.DoEvents();
                Assert.Equal(box.Text, _fake.Text);
            }
        }

        [Fact]
        public void TheContextMenusCopy_InAReadOnlyTextBox_Copies()
        {
            var (form, box) = Box();
            using (form)
            {
                box.SelectAll();
                // What a right-click shows: the menu the control itself provides.
                SWF.Menu? legacy = box.ContextMenu;
                if (legacy != null)
                {
                    var item = legacy.MenuItems.Cast<SWF.MenuItem>().First(i => i.Text.Replace("&", "") == "Copy");
                    typeof(SWF.ContextMenu).GetMethod("OnPopup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(legacy, new object[] { EventArgs.Empty });
                    Assert.True(item.Enabled, "Copy is disabled in a read-only box with a selection");
                    item.PerformClick();
                }
                else
                {
                    var menu = box.DefaultContextMenuStrip;
                    var copy = menu.Items.OfType<SWF.ToolStripItem>().First(i => i.Text?.Replace("&", "") == "Copy");
                    Assert.True(copy.Enabled, "Copy is disabled in a read-only box with a selection");
                    copy.PerformClick();
                }
                Assert.Equal(box.Text, _fake.Text);
            }
        }

    }
}
