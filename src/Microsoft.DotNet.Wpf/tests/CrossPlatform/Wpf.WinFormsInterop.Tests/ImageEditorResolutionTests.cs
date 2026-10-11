// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An Image property's editor, as a PropertyGrid finds it: through the [Editor] attribute on Image,
// which names "System.Drawing.Design.ImageEditor, System.Drawing.Design". That identity forwarded to a
// copy in our System.Drawing that had no EditValue -- System.Drawing cannot open a file dialog -- so a
// Picture property never opened anything. It now forwards to WinForms' ImageEditor, as .NET's forwards
// to its designer assembly.
//

using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ImageEditorResolutionTests
    {
        [Fact]
        public void ImagesDeclaredEditor_IsWinFormsFileEditor()
        {
            var editor = TypeDescriptor.GetEditor(typeof(Image), typeof(UITypeEditor)) as UITypeEditor;
            Assert.NotNull(editor);
            Assert.Equal(typeof(Form).Assembly, editor.GetType().Assembly);
            Assert.Equal(UITypeEditorEditStyle.Modal, editor.GetEditStyle(null));
            Assert.NotEqual(typeof(UITypeEditor), editor.GetType().GetMethod(nameof(UITypeEditor.EditValue),
                new[] { typeof(ITypeDescriptorContext), typeof(System.IServiceProvider), typeof(object) })!.DeclaringType);
        }

        [Fact]
        public void IconsDeclaredEditor_IsWinFormsFileEditor()
        {
            var editor = TypeDescriptor.GetEditor(typeof(Icon), typeof(UITypeEditor)) as UITypeEditor;
            Assert.NotNull(editor);
            Assert.Equal(typeof(Form).Assembly, editor.GetType().Assembly);
        }
    }
}
