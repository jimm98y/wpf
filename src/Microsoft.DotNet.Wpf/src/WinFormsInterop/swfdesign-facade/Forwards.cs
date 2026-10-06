// The System.Drawing assembly IDENTITY, forwarded to the assembly that carries the types.
//
// Anything built against .NET Framework names "System.Drawing, Version=4.0.0.0, Culture=neutral,
// PublicKeyToken=b03f5f7f11d50a3a" -- a third-party control's TypeConverter and Editor
// attributes, a .resx naming the bitmap it holds, a designer's generated code. Without this
// facade that identity binds to the inbox facade instead, which forwards to
// System.Drawing.Common: the caller gets a Bitmap of a DIFFERENT type from the one this
// WinForms uses, and either fails to cast or silently draws nothing.
//
// GENERATED -- see GALLERY_PROBE=forwards. 22 types.

extern alias design;

[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.AnchorEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.AxParameterData))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.AxWrapperGen))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.BorderSidesEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.ComponentDocumentDesigner))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.ComponentTray))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.ControlDesigner))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.DesignerOptions))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.DockEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.DocumentDesigner))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.EventHandlerService))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.FileNameEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.FolderNameEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.IMenuEditorService))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.ImageListCodeDomSerializer))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.MaskDescriptor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.MenuCommands))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.ParentControlDesigner))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.ScrollableControlDesigner))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.SelectionRules))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.ShortcutKeysEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(design::System.Windows.Forms.Design.WindowsFormsDesignerOptionService))]
