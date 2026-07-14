// Minimal System.Drawing.Design surface for the WinForms port. The real designer stack
// (UITypeEditor host, drop-down/modal editor UI) is a design-time concern; at runtime WinForms
// only needs these types to exist as the base class / enum its property editors derive from.
// A production port would ship a complete System.Drawing.Design assembly.

using System.ComponentModel;
using System.Drawing;

namespace System.Drawing.Design
{
    public enum UITypeEditorEditStyle
    {
        None = 1,
        Modal = 2,
        DropDown = 3,
    }

    public class UITypeEditor
    {
        public UITypeEditor() { }

        public virtual object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value) => value;

        public virtual object EditValue(IServiceProvider provider, object value) => EditValue(null, provider, value);

        public virtual UITypeEditorEditStyle GetEditStyle() => GetEditStyle(null);

        public virtual UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.None;

        public virtual bool GetPaintValueSupported() => GetPaintValueSupported(null);

        public virtual bool GetPaintValueSupported(ITypeDescriptorContext context) => false;

        public virtual bool IsDropDownResizable => false;

        public virtual void PaintValue(PaintValueEventArgs e) { }

        public void PaintValue(object value, Graphics canvas, Rectangle rectangle)
            => PaintValue(new PaintValueEventArgs(null, value, canvas, rectangle));
    }

    public class PaintValueEventArgs : EventArgs
    {
        public PaintValueEventArgs(ITypeDescriptorContext context, object value, Graphics graphics, Rectangle bounds)
        {
            Context = context;
            Value = value;
            Graphics = graphics;
            Bounds = bounds;
        }

        public ITypeDescriptorContext Context { get; }
        public object Value { get; }
        public Graphics Graphics { get; }
        public Rectangle Bounds { get; }
    }
}
