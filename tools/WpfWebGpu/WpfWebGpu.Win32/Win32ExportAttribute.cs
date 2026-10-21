// The Windows exports a method implements, for the P/Invoke rewriter (Documentation/pinvoke-rewriter.md).
//
// An app's P/Invoke of one of these names (in any Windows DLL that exports it) is rewritten to call the
// method: marshalled as the runtime would marshal the import, then a plain managed call. The rewriter
// reads these from metadata (by the attribute's name, so another assembly -- the Direct3D shim's -- may
// declare its own), which is what lets a head whose app is compiled ahead of time rewrite it at build.
//
// The methods' signatures are the functions' own, in raw form (pointers, handles as nint, BOOL as int),
// as the Windows API metadata gives them.

namespace WpfWebGpu.Win32;

/// <summary>Implements these exports off Windows (macOS, Linux, Android, iOS, the browser): every call.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
internal sealed class Win32ExportAttribute : Attribute
{
    public Win32ExportAttribute(params string[] names) => Names = names;
    public string[] Names { get; }
}

/// <summary>Implements these exports on Windows, where the real ones are underneath: the calls whose handle
/// arguments (<paramref name="mask"/>: bit k argument k is a window, bit 16+k a device context) are the
/// stack's, or every call with the Always bit; any other call reaches the real function.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
internal sealed class Win32ExportOnWindowsAttribute : Attribute
{
    public Win32ExportOnWindowsAttribute(int mask, params string[] names) { Mask = mask; Names = names; }
    public int Mask { get; }
    public string[] Names { get; }
}
