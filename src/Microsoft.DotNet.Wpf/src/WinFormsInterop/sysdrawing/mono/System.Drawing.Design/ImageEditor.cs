// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// System.Drawing.Design.ImageEditor / IconEditor.
//
// Mono never had these - its System.Drawing.Design carries only ToolboxItem - but they are part of
// the framework's System.Drawing.Design and applications do use them. SharpDevelop's forms designer,
// for one, subclasses both purely to reach the protected CreateFilterEntry and build the file filter
// for its image picker, which is exactly what they are for.
//
// The shape below follows the documented API: the extension list and description are virtual so a
// derived editor can narrow them, and CreateFilterEntry is the protected static that turns an
// instance into one "Description (*.a;*.b)|*.a;*.b" entry.
//

using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace System.Drawing.Design
{
    /// <summary>Edits <see cref="Image"/> values by picking a file.</summary>
    public class ImageEditor : UITypeEditor
    {
        public ImageEditor()
        {
        }

        /// <summary>The extensions this editor accepts, without the dot.</summary>
        protected virtual string[] GetExtensions() => new[]
        {
            "bmp", "gif", "jpg", "jpeg", "png", "ico", "emf", "wmf"
        };

        /// <summary>The human-readable half of the filter entry.</summary>
        protected virtual string GetFileDialogDescription() => "All image files";

        /// <summary>Joins extensions into a "*.a;*.b" pattern.</summary>
        protected static string CreateExtensionsString(string[] extensions, string sep)
            => CreateExtensionsStringCore(extensions, sep);

        // Shared by IconEditor, whose own CreateExtensionsString is a separate protected static.
        internal static string CreateExtensionsStringCore(string[] extensions, string sep)
        {
            if (extensions is null || extensions.Length == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(extensions.Length);
            foreach (string e in extensions)
            {
                parts.Add("*." + e);
            }

            return string.Join(sep, parts);
        }

        /// <summary>One OpenFileDialog filter entry for <paramref name="e"/>.</summary>
        protected static string CreateFilterEntry(ImageEditor e)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            string exts = CreateExtensionsString(e.GetExtensions(), ";");
            return e.GetFileDialogDescription() + "(" + exts + ")|" + exts;
        }

        protected virtual Image LoadFromStream(Stream stream) => Image.FromStream(stream);

        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)
            => UITypeEditorEditStyle.Modal;

        public override bool GetPaintValueSupported(ITypeDescriptorContext context) => true;

        public override void PaintValue(PaintValueEventArgs e)
        {
            if (e?.Value is Image image && e.Graphics != null)
            {
                e.Graphics.DrawImage(image, e.Bounds);
            }
        }
    }

    /// <summary>Edits <see cref="Icon"/> values by picking a file.</summary>
    public class IconEditor : UITypeEditor
    {
        public IconEditor()
        {
        }

        protected virtual string[] GetExtensions() => new[] { "ico" };

        protected virtual string GetFileDialogDescription() => "Icon files";

        protected static string CreateExtensionsString(string[] extensions, string sep)
            => ImageEditor.CreateExtensionsStringCore(extensions, sep);

        protected static string CreateFilterEntry(IconEditor e)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            string exts = ImageEditor.CreateExtensionsStringCore(e.GetExtensions(), ";");
            return e.GetFileDialogDescription() + "(" + exts + ")|" + exts;
        }

        protected virtual Icon LoadFromStream(Stream stream) => new Icon(stream);

        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)
            => UITypeEditorEditStyle.Modal;

        public override bool GetPaintValueSupported(ITypeDescriptorContext context) => true;

        public override void PaintValue(PaintValueEventArgs e)
        {
            if (e?.Value is Icon icon && e.Graphics != null)
            {
                e.Graphics.DrawIcon(icon, e.Bounds);
            }
        }
    }
}
