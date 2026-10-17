// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ObjectSelectorEditor, ByteViewer, LocalizationExtenderProvider, DataGridColumnStyle's header accessible object
// and AxHost: Mono stubs throwing NotImplementedException, ported from dotnet/winforms (LocalizationExtenderProvider
// and the DataGrid family from the .NET Framework, which .NET no longer has). AxHost now says, as a
// PlatformNotSupportedException, that ActiveX is not part of this stack.
//

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms.Design;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DesignerEditorsTests
    {
        private sealed class ColorChooser : ObjectSelectorEditor
        {
            public int Fills;
            protected override void FillTreeWithData(Selector selector, ITypeDescriptorContext? context, IServiceProvider provider)
            {
                base.FillTreeWithData(selector, context, provider);
                Fills++;
                var warm = selector.AddNode("Warm", null, null);
                selector.AddNode("Red", "red", warm);
                selector.AddNode("Blue", "blue", null);
            }
            public Selector MakeSelector() => new Selector(this);
            public object? Prev { get => prevValue; set => prevValue = value; }
        }

        private sealed class EditorService : SWF.Design.IWindowsFormsEditorService, IServiceProvider
        {
            public Action<SWF.Control>? OnDropDown;
            public int Closed;
            public object? GetService(Type t) => t == typeof(SWF.Design.IWindowsFormsEditorService) ? this : null;
            public void CloseDropDown() => Closed++;
            public void DropDownControl(SWF.Control? control) => OnDropDown?.Invoke(control!);
            public SWF.DialogResult ShowDialog(SWF.Form dialog) => SWF.DialogResult.None;
        }

        [Fact]
        public void ObjectSelectorEditor_FillsItsTree_SelectsTheValue_AndTakesTheChosenNode()
        {
            var editor = new ColorChooser();
            Assert.Equal(System.Drawing.Design.UITypeEditorEditStyle.DropDown, editor.GetEditStyle(null));
            // No editor service: the value comes back untouched.
            Assert.Equal("red", editor.EditValue(null, new EditorService { OnDropDown = null }.GetService(typeof(object)) as IServiceProvider ?? new ServiceContainer(), "red"));

            var svc = new EditorService();
            svc.OnDropDown = control =>
            {
                var tree = (ObjectSelectorEditor.Selector)control;
                // The starting value is selected (its parent expanded); choosing another commits it and closes.
                Assert.Equal("Red", tree.SelectedNode?.Text);
                tree.SelectedNode = tree.Nodes[1];
                editor.SetValue(((ObjectSelectorEditor.SelectorNode)tree.SelectedNode).value);
            };
            object? result = editor.EditValue(null, svc, "red");
            Assert.Equal("blue", result);
            Assert.Equal(1, editor.Fills);

            var selector = editor.MakeSelector();
            Assert.True(selector.FullRowSelect);
            ObjectSelectorEditor.ApplyTreeViewThemeStyles(selector);
            Assert.True(selector.HotTracking);
            Assert.False(selector.ShowLines);
        }

        [Fact]
        public void ByteViewer_HoldsBytes_AndPicksADisplayMode()
        {
            using var viewer = new ByteViewer();
            Assert.Equal(DisplayMode.Hexdump, viewer.GetDisplayMode());
            Assert.Empty(viewer.GetBytes());
            byte[] text = System.Text.Encoding.ASCII.GetBytes("Hello, this is plain text for the viewer.");
            viewer.SetBytes(text);
            Assert.Same(text, viewer.GetBytes());
            viewer.SetDisplayMode(DisplayMode.Ansi);
            Assert.StartsWith("Hello", (string)typeof(ByteViewer).GetProperty("DisplayedText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!);
            // Auto reads pairs of ASCII letters as printable CJK characters and picks Unicode, as .NET does: kept.
            viewer.SetDisplayMode(DisplayMode.Auto);
            Assert.Equal(DisplayMode.Auto, viewer.GetDisplayMode());

            byte[] unicode = new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0, (byte)'!', 0, (byte)'?', 0 };
            viewer.SetBytes(unicode);
            viewer.SetDisplayMode(DisplayMode.Unicode);
            Assert.Throws<InvalidEnumArgumentException>(() => viewer.SetDisplayMode((DisplayMode)99));
            Assert.Throws<ArgumentNullException>(() => viewer.SetBytes(null!));

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"byteviewer-{Guid.NewGuid():N}.bin");
            try
            {
                viewer.SaveToFile(path);
                using var other = new ByteViewer();
                other.SetFile(path);
                Assert.Equal(unicode, other.GetBytes());
                other.SetStartLine(5);   // past the end: back to 0, without throwing
            }
            finally { System.IO.File.Delete(path); }

            // Painting a hex dump does not throw.
            using var bmp = new System.Drawing.Bitmap(viewer.Width, viewer.Height);
            viewer.SetDisplayMode(DisplayMode.Hexdump);
            viewer.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
        }

#pragma warning disable CS0618 // LocalizationExtenderProvider is obsolete
        [Fact]
        public void LocalizationExtenderProvider_ExtendsTheRoot_AndTiesLanguageToLocalizable()
        {
            var root = new Component();
            var provider = new LocalizationExtenderProvider(null!, root);
            Assert.True(provider.CanExtend(root));
            Assert.False(provider.CanExtend(new Component()));
            Assert.Equal(CultureInfo.InvariantCulture, provider.GetLanguage(root));
            Assert.False(provider.GetLocalizable(root));
            Assert.False(provider.ShouldSerializeLanguage(root));

            provider.SetLanguage(root, new CultureInfo("de-DE"));
            Assert.True(provider.GetLocalizable(root));
            Assert.True(provider.ShouldSerializeLanguage(root));
            Assert.Equal("de-DE", provider.GetLoadLanguage(root).Name);

            provider.SetLocalizable(root, false);
            Assert.Equal(CultureInfo.InvariantCulture, provider.GetLanguage(root));
            provider.ResetLanguage(root);
            provider.Dispose();
        }
#pragma warning restore CS0618

        [Fact]
        public void DataGridColumnHeader_IsAccessible()
        {
            var style = new SWF.DataGridTextBoxColumn { HeaderText = "Name", MappingName = "Name" };
            SWF.AccessibleObject header = style.HeaderAccessibleObject;
            Assert.Equal("Name", header.Name);
            Assert.Equal(SWF.AccessibleRole.ColumnHeader, header.Role);
            Assert.Equal(System.Drawing.Rectangle.Empty, header.Bounds);   // not in a grid
            Assert.Null(header.Navigate(SWF.AccessibleNavigation.Right));
        }

        private sealed class SomeActiveX : SWF.AxHost
        {
            public SomeActiveX() : base("8856F961-340A-11D0-A96B-00C04FD705A2") { }
        }

        [Fact]
        public void AxHost_SaysActiveXIsNotSupported()
        {
            var ax = new SomeActiveX();
            var e = Assert.ThrowsAny<PlatformNotSupportedException>(() => ax.DoVerb(0));
            Assert.Contains("ActiveX", e.Message);
        }
    }
}
