// Types .NET's System.Windows.Forms DEFINES and Mono keeps elsewhere, forwarded to where they are.
//
// .NET moved System.Drawing.Design's editor types (UITypeEditor and friends) into
// System.Windows.Forms.dll, so a library compiled against it names
// [System.Windows.Forms]System.Drawing.Design.UITypeEditor; here they live in Mono.System.Drawing,
// as they did in .NET Framework's System.Drawing.Design. Without these forwards every property whose
// [Editor] names such a type fails to resolve it.
//
// GENERATED: the public types of .NET 10's System.Windows.Forms reference assembly that this
// assembly does not define but another WinForms-stack assembly does.

[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::System.Drawing.Design.IPropertyValueUIService))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::System.Drawing.Design.PaintValueEventArgs))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::System.Drawing.Design.PropertyValueUIHandler))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::System.Drawing.Design.PropertyValueUIItem))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::System.Drawing.Design.PropertyValueUIItemInvokeHandler))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::System.Drawing.Design.UITypeEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::System.Drawing.Design.UITypeEditorEditStyle))]
