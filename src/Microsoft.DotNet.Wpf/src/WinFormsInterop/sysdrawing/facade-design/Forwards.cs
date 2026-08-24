// The System.Drawing assembly IDENTITY, forwarded to the assembly that carries the types.
//
// Anything built against .NET Framework names "System.Drawing, Version=4.0.0.0, Culture=neutral,
// PublicKeyToken=b03f5f7f11d50a3a" -- a third-party control's TypeConverter and Editor
// attributes, a .resx naming the bitmap it holds, a designer's generated code. Without this
// facade that identity binds to the inbox facade instead, which forwards to
// System.Drawing.Common: the caller gets a Bitmap of a DIFFERENT type from the one this
// WinForms uses, and either fails to cast or silently draws nothing.
//
// GENERATED -- see GALLERY_PROBE=forwards. 20 types.

extern alias mono;

[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.CategoryNameCollection))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.IPropertyValueUIService))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.IToolboxItemProvider))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.IToolboxService))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.IToolboxUser))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.IconEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ImageEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.PaintValueEventArgs))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.PropertyValueUIHandler))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.PropertyValueUIItem))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.PropertyValueUIItemInvokeHandler))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ToolboxComponentsCreatedEventArgs))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ToolboxComponentsCreatedEventHandler))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ToolboxComponentsCreatingEventArgs))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ToolboxComponentsCreatingEventHandler))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ToolboxItem))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ToolboxItemCollection))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.ToolboxItemCreatorCallback))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.UITypeEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(mono::System.Drawing.Design.UITypeEditorEditStyle))]
