//
// Mono keeps these assembly-name constants in mcs/build/common/Consts.cs, shared by every
// class library at build time. It is `internal`, so the copy compiled into System.Windows.Forms
// is not visible here — System.Design needs its own. Only the names Native.cs actually loads
// are restated; the values are Mono's.
//
static class Consts
{
	public const string AssemblySystem_Drawing =
		"System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
	public const string AssemblySystem_Windows_Forms =
		"System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
	public const string AssemblyMicrosoft_VSDesigner =
		"Microsoft.VSDesigner, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
}
