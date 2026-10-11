// The file editors, forwarded to WinForms -- not generated with Forwards.cs, and taken OUT of that
// list: .NET's System.Drawing.Design identity forwards them to its designer assembly, because they
// open a file dialog. The copies our System.Drawing used to carry could not, so an Image or Icon
// property's editor did nothing at all.

extern alias swf;

[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(swf::System.Drawing.Design.ImageEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(swf::System.Drawing.Design.BitmapEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(swf::System.Drawing.Design.MetafileEditor))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(swf::System.Drawing.Design.IconEditor))]
