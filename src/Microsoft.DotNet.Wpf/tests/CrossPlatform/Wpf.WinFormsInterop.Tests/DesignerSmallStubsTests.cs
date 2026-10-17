// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The last small System.Design stubs, which threw NotImplementedException in Mono: the FileName, BorderSides,
// ShortcutKeys, Binary and FolderName editors, ControlBindingsConverter's binding properties, the ImageList and
// type serializers, DesignerActionList/DesignerActionMethodItem, AxParameterData, and DataGridTableStyle's edit
// forwarding. AxWrapperGen says, as AxHost does, that ActiveX is not part of this stack.
//

using System;
using System.CodeDom;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms.Design;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DesignerSmallStubsTests
    {
        private sealed class EditorService : SWF.Design.IWindowsFormsEditorService, IServiceProvider
        {
            public Action<SWF.Control>? OnDropDown;
            public Func<SWF.Form, SWF.DialogResult>? OnDialog;
            public object? GetService(Type t) => t == typeof(SWF.Design.IWindowsFormsEditorService) ? this : null;
            public void CloseDropDown() { }
            public void DropDownControl(SWF.Control? control) => OnDropDown?.Invoke(control!);
            public SWF.DialogResult ShowDialog(SWF.Form dialog) => OnDialog?.Invoke(dialog) ?? SWF.DialogResult.Cancel;
        }

        private static T Internal<T>(object owner, string name)
            => (T)owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;

        // ---- FileNameEditor ----

        private sealed class FileNames : FileNameEditor
        {
            public void Init(SWF.OpenFileDialog d) => InitializeDialog(d);
        }

        [Fact]
        public void FileNameEditor_IsModal_InitializesItsDialog_AndNeedsAnEditorService()
        {
            var editor = new FileNames();
            Assert.Equal(System.Drawing.Design.UITypeEditorEditStyle.Modal, editor.GetEditStyle(null));
            using var dialog = new SWF.OpenFileDialog();
            editor.Init(dialog);
            Assert.Equal("All Files(*.*)|*.*", dialog.Filter);
            Assert.Equal("Open File", dialog.Title);
            Assert.Throws<ArgumentNullException>(() => editor.Init(null!));
            // No editor service: no dialog, and the value comes back.
            Assert.Equal("a.txt", editor.EditValue(null, new ServiceContainer(), "a.txt"));
        }

        // ---- BorderSidesEditor ----

        [Fact]
        public void BorderSidesEditor_ChecksTheSides_AllAndNoneFollow()
        {
            var editor = new BorderSidesEditor();
            Assert.Equal(System.Drawing.Design.UITypeEditorEditStyle.DropDown, editor.GetEditStyle(null));
            var svc = new EditorService();
            var seen = new System.Collections.Generic.List<object?>();
            svc.OnDropDown = ui =>
            {
                var top = Internal<SWF.CheckBox>(ui, "TopBox");
                var none = Internal<SWF.CheckBox>(ui, "NoneBox");
                var all = Internal<SWF.CheckBox>(ui, "AllBox");
                var left = Internal<SWF.CheckBox>(ui, "LeftBox");
                Assert.True(top.Checked);
                Assert.False(none.Checked);
                Assert.False(all.Checked);

                all.Checked = true;                   // All checks the four
                Assert.True(left.Checked && Internal<SWF.CheckBox>(ui, "RightBox").Checked && Internal<SWF.CheckBox>(ui, "BottomBox").Checked);
                seen.Add(Internal<object>(ui, "Value"));

                left.Checked = false;                 // a side cleared clears All
                Assert.False(all.Checked);
                seen.Add(Internal<object>(ui, "Value"));

                none.Checked = true;                  // None clears everything
                Assert.False(top.Checked);
                seen.Add(Internal<object>(ui, "Value"));

                top.Checked = true;                   // a side checked clears None
                Assert.False(none.Checked);
            };
            object result = editor.EditValue(null, svc, SWF.ToolStripStatusLabelBorderSides.Top);
            Assert.Equal(SWF.ToolStripStatusLabelBorderSides.All, seen[0]);
            Assert.Equal(SWF.ToolStripStatusLabelBorderSides.Top | SWF.ToolStripStatusLabelBorderSides.Bottom | SWF.ToolStripStatusLabelBorderSides.Right, seen[1]);
            Assert.Equal(SWF.ToolStripStatusLabelBorderSides.None, seen[2]);
            Assert.Equal(SWF.ToolStripStatusLabelBorderSides.Top, result);
            Assert.Equal(SWF.ToolStripStatusLabelBorderSides.Left, editor.EditValue(null, new ServiceContainer(), SWF.ToolStripStatusLabelBorderSides.Left));
        }

        // ---- ShortcutKeysEditor ----

        [Fact]
        public void ShortcutKeysEditor_ComposesModifiersAndKey_ResetsAndKeepsAnUnknownKey()
        {
            var editor = new ShortcutKeysEditor();
            Assert.Equal(System.Drawing.Design.UITypeEditorEditStyle.DropDown, editor.GetEditStyle(null));
            var svc = new EditorService();

            svc.OnDropDown = ui =>
            {
                Assert.True(Internal<SWF.CheckBox>(ui, "CtrlBox").Checked);
                Assert.False(Internal<SWF.CheckBox>(ui, "AltBox").Checked);
                Assert.Equal("S", Internal<SWF.ComboBox>(ui, "KeyBox").SelectedItem);
                Internal<SWF.CheckBox>(ui, "ShiftBox").Checked = true;
                Internal<SWF.ComboBox>(ui, "KeyBox").SelectedItem = "F5";
            };
            Assert.Equal(SWF.Keys.Control | SWF.Keys.Shift | SWF.Keys.F5, editor.EditValue(null, svc, SWF.Keys.Control | SWF.Keys.S));

            svc.OnDropDown = ui => Internal<SWF.Button>(ui, "ResetButton").PerformClick();
            Assert.Equal(SWF.Keys.None, editor.EditValue(null, svc, SWF.Keys.Alt | SWF.Keys.A));

            // A key the list does not offer shows as "(Unknown)" and is kept.
            svc.OnDropDown = ui =>
            {
                Assert.Equal("(Unknown)", Internal<SWF.ComboBox>(ui, "KeyBox").SelectedItem);
                Internal<SWF.CheckBox>(ui, "AltBox").Checked = true;
            };
            Assert.Equal(SWF.Keys.Alt | SWF.Keys.Control | SWF.Keys.LaunchMail, editor.EditValue(null, svc, SWF.Keys.Control | SWF.Keys.LaunchMail));
            Assert.Equal(SWF.Keys.F1, editor.EditValue(null, new ServiceContainer(), SWF.Keys.F1));
        }

        // ---- BinaryEditor ----

        [Fact]
        public void BinaryEditor_ShowsTheBytes_OfAnArrayOrAStream_AndLeavesTheValue()
        {
            var editor = new BinaryEditor();
            Assert.Equal(System.Drawing.Design.UITypeEditorEditStyle.Modal, editor.GetEditStyle(null));
            byte[] data = { 0x48, 0x69, 0x00, 0xFF };
            byte[]? shown = null;
            DisplayMode? mode = null;
            var svc = new EditorService
            {
                OnDialog = form =>
                {
                    var viewer = Internal<ByteViewer>(form, "Viewer");
                    shown = viewer.GetBytes();
                    Internal<SWF.RadioButton>(form, "HexButton").Checked = true;
                    mode = viewer.GetDisplayMode();
                    return SWF.DialogResult.OK;
                },
            };
            Assert.Same(data, editor.EditValue(null, svc, data));
            Assert.Equal(data, shown);
            Assert.Equal(DisplayMode.Hexdump, mode);

            using var stream = new MemoryStream(data);
            stream.Position = 2;
            Assert.Same(stream, editor.EditValue(null, svc, stream));
            Assert.Equal(data, shown);
            Assert.Equal(0, stream.Position);

            Assert.Same(data, editor.EditValue(null, new ServiceContainer(), data));
        }

        // ---- FolderNameEditor ----

        private sealed class Folders : FolderNameEditor
        {
            private FolderBrowser? _browser;
            public object Browser => _browser!;
            public string Path => _browser!.DirectoryPath;
            protected override void InitializeDialog(FolderBrowser folderBrowser)
            {
                _browser = folderBrowser;
                folderBrowser.Description = "Pick one";
                folderBrowser.StartLocation = FolderBrowserFolder.MyDocuments;
            }
            public void Init() => InitializeDialog(new FolderBrowser());
            public void StartAtPrinters() => _browser!.StartLocation = FolderBrowserFolder.Printers;
        }

        [Fact]
        public void FolderNameEditor_FolderBrowser_IsAFolderBrowserDialog()
        {
            var editor = new Folders();
            editor.Init();
            MethodInfo create = editor.Browser.GetType().GetMethod("CreateDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using (var dialog = (SWF.FolderBrowserDialog)create.Invoke(editor.Browser, null)!)
            {
                Assert.Equal("Pick one", dialog.Description);
                Assert.Equal(Environment.SpecialFolder.MyDocuments, dialog.RootFolder);
            }
            Assert.Equal(string.Empty, editor.Path);
            // A CSIDL Environment.SpecialFolder does not name (the printers folder) starts at the desktop.
            editor.StartAtPrinters();
            using (var dialog = (SWF.FolderBrowserDialog)create.Invoke(editor.Browser, null)!)
                Assert.Equal(Environment.SpecialFolder.Desktop, dialog.RootFolder);
        }

        // ---- ControlBindingsConverter ----

        private sealed class Source { public string Name { get; set; } = "n"; }

        [Fact]
        public void ControlBindingsConverter_ListsBindableProperties_WhoseValueIsTheBinding()
        {
            var control = new SWF.TextBox();
            TypeConverter converter = TypeDescriptor.GetConverter(control.DataBindings);
            Assert.Equal("ControlBindingsConverter", converter.GetType().Name);
            Assert.True(converter.GetPropertiesSupported(null));
            Assert.Equal(string.Empty, converter.ConvertTo(control.DataBindings, typeof(string)));

            PropertyDescriptorCollection props = converter.GetProperties(null, control.DataBindings, null)!;
            PropertyDescriptor text = props["Text"]!;
            Assert.NotNull(text);
            Assert.Null(props["Location"]);            // not [Bindable]
            Assert.Equal(typeof(SWF.Binding), text.PropertyType);
            Assert.True(text.IsReadOnly);
            Assert.Null(text.GetValue(control.DataBindings));
            Assert.False(text.CanResetValue(control.DataBindings));

            var binding = new SWF.Binding("Text", new Source(), "Name");
            text.SetValue(control.DataBindings, binding);
            Assert.Same(binding, text.GetValue(control.DataBindings));
            Assert.True(text.CanResetValue(control.DataBindings));
            var other = new SWF.Binding("Text", new Source(), "Name");
            text.SetValue(control.DataBindings, other);   // replaces
            Assert.Single(control.DataBindings.Cast<SWF.Binding>());
            text.ResetValue(control.DataBindings);
            Assert.Empty(control.DataBindings.Cast<SWF.Binding>());

            Assert.Empty(converter.GetProperties(null, "not bindings", null)!.Cast<PropertyDescriptor>());
        }

        // ---- ImageListCodeDomSerializer and TypeCodeDomSerializer ----

        [Fact]
        public void ImageListSerializer_WritesTheImageKeys()
        {
            using var surface = new DesignSurface();
            surface.BeginLoad(typeof(SWF.Form));
            var host = (IDesignerHost)surface.GetService(typeof(IDesignerHost))!;
            var images = (SWF.ImageList)host.CreateComponent(typeof(SWF.ImageList), "imageList1");
            images.Images.Add("first", new System.Drawing.Bitmap(4, 4));
            images.Images.Add(new System.Drawing.Bitmap(4, 4));
            images.Images.Add("third", new System.Drawing.Bitmap(4, 4));

            var session = new DesignerSerializationManager(host);
            IDesignerSerializationManager manager = session;
            using (session.CreateSession())
            {
                manager.Context.Push(new RootContext(new CodeThisReferenceExpression(), host.RootComponent));
                var statements = (CodeStatementCollection)new ImageListCodeDomSerializer().Serialize(manager, images);
                var keys = statements.OfType<CodeExpressionStatement>()
                    .Select(s => s.Expression).OfType<CodeMethodInvokeExpression>()
                    .Where(m => m.Method.MethodName == "SetKeyName")
                    .Select(m => ((int)((CodePrimitiveExpression)m.Parameters[0]).Value!, (string)((CodePrimitiveExpression)m.Parameters[1]).Value!))
                    .ToArray();
                Assert.Equal(new[] { (0, "first"), (2, "third") }, keys);
            }
            Assert.Throws<ArgumentNullException>(() => new ImageListCodeDomSerializer().Serialize(null!, images));
        }

        /// <summary>Names resolved against the WinForms assembly, as a designer's type resolution service would.</summary>
        private sealed class Types : ITypeResolutionService
        {
            public Assembly? GetAssembly(AssemblyName name) => null;
            public Assembly? GetAssembly(AssemblyName name, bool throwOnError) => null;
            public Type? GetType(string name) => GetType(name, false, false);
            public Type? GetType(string name, bool throwOnError) => GetType(name, throwOnError, false);
            public Type? GetType(string name, bool throwOnError, bool ignoreCase) => typeof(SWF.Form).Assembly.GetType(name, false, ignoreCase) ?? Type.GetType(name, false, ignoreCase);
            public void ReferenceAssembly(AssemblyName name) { }
            public string? GetPathOfAssembly(AssemblyName name) => null;
        }

        [Fact]
        public void TypeCodeDomSerializer_WritesADeclaration_AndReadsItBack()
        {
            using var surface = new DesignSurface();
            surface.BeginLoad(typeof(SWF.Form));
            var host = (IDesignerHost)surface.GetService(typeof(IDesignerHost))!;
            var root = (SWF.Form)host.RootComponent;
            var button = (SWF.Button)host.CreateComponent(typeof(SWF.Button), "button1");
            button.Text = "Press";
            root.Controls.Add(button);
            root.Text = "Hello";

            CodeTypeDeclaration declaration;
            var session = new DesignerSerializationManager(host);
            using (session.CreateSession())
                declaration = new TypeCodeDomSerializer().Serialize(session, root, new object[] { button });

            Assert.Equal(typeof(SWF.Form).FullName, declaration.BaseTypes[0].BaseType);
            CodeMemberField field = declaration.Members.OfType<CodeMemberField>().Single();
            Assert.Equal("button1", field.Name);
            CodeConstructor ctor = declaration.Members.OfType<CodeConstructor>().Single();
            Assert.NotEmpty(ctor.Statements);

            // Read back, without a designer: a new Form with the button.
            var services = new ServiceContainer();
            services.AddService(typeof(ITypeResolutionService), new Types());
            var loader = new DesignerSerializationManager(services);
            object loaded;
            using (loader.CreateSession())
                loaded = new TypeCodeDomSerializer().Deserialize(loader, declaration);
            var form = Assert.IsType<SWF.Form>(loaded);
            Assert.Equal("Hello", form.Text);
            SWF.Control child = Assert.Single(form.Controls.Cast<SWF.Control>());
            Assert.Equal("Press", child.Text);

            var bad = new CodeTypeDeclaration("X");
            bad.BaseTypes.Add("No.Such.Type");
            using (loader.CreateSession())
                Assert.Throws<CodeDomSerializerException>(() => new TypeCodeDomSerializer().Deserialize(loader, bad));
        }

        // ---- DesignerActionList / DesignerActionMethodItem ----

        private sealed class Actions : DesignerActionList
        {
            public int Ran;
            public Actions(IComponent c) : base(c) { }
            private void DoIt() => Ran++;
        }

        [Fact]
        public void DesignerActionMethodItem_InvokesTheListsMethod_AndTheListAsksItsComponentsSite()
        {
            var component = new Component();
            new Container().Add(component, "c");
            var list = new Actions(component);
            Assert.NotNull(list.GetService(typeof(IContainer)));
            Assert.Null(new Actions(new Component()).GetService(typeof(IContainer)));

            new DesignerActionMethodItem(list, "DoIt", "Do it").Invoke();
            Assert.Equal(1, list.Ran);
            Assert.Throws<InvalidOperationException>(() => new DesignerActionMethodItem(list, "Missing", "x").Invoke());
            Assert.Throws<InvalidOperationException>(() => new DesignerActionMethodItem(null!, "DoIt", "x").Invoke());
        }

        // ---- AxParameterData / AxWrapperGen ----

        public class Outer { public class Inner { } }
        public static void Sample(int value, ref string text, out Outer.Inner inner, [System.Runtime.InteropServices.Optional] object maybe) { inner = null!; }

        [Fact]
        public void AxParameterData_DescribesParameters()
        {
            ParameterInfo[] infos = typeof(DesignerSmallStubsTests).GetMethod(nameof(Sample))!.GetParameters();
            AxParameterData[] data = AxParameterData.Convert(infos);
            Assert.Equal(4, data.Length);
            Assert.Equal(System.CodeDom.FieldDirection.In, data[0].Direction);
            Assert.Equal("System.Int32", data[0].TypeName);
            Assert.True(data[1].IsByRef);
            Assert.Equal(System.CodeDom.FieldDirection.Ref, data[1].Direction);
            Assert.Equal("System.String", data[1].TypeName);
            Assert.True(data[2].IsOut);
            Assert.Equal(System.CodeDom.FieldDirection.Out, data[2].Direction);
            Assert.Equal(typeof(Outer.Inner).FullName!.Replace('+', '.'), data[2].TypeName);
            Assert.True(data[3].IsOptional);

            AxParameterData[] flat = AxParameterData.Convert(infos, ignoreByRefs: true);
            Assert.False(flat[1].IsByRef);
            Assert.Equal(System.CodeDom.FieldDirection.In, flat[2].Direction);
            Assert.Empty(AxParameterData.Convert(null!));

            var named = new AxParameterData("Value", typeof(int));
            Assert.Equal("value", named.Name);   // the first letter lowered
            Assert.Equal("System.Int32", named.TypeName);
            Assert.Equal("My.Type", new AxParameterData("x", "My.Type").TypeName);

            Assert.Throws<PlatformNotSupportedException>(() => new AxWrapperGen(typeof(object)));
        }

        // ---- DataGridTableStyle ----

        [Fact]
        public void DataGridTableStyle_EditsThroughItsGrid_AndWithoutOneDoesNot()
        {
            var style = new SWF.DataGridTableStyle();
            var column = new SWF.DataGridTextBoxColumn();
            Assert.False(style.BeginEdit(column, 0));
            Assert.False(style.EndEdit(column, 0, false));
        }
    }
}
