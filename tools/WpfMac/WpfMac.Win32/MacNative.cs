// The macOS APIs the implementations read real answers from.

using System.Runtime.InteropServices;

namespace WpfMac.Win32;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint { public double X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize { public double Width, Height; }

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect { public CGPoint Origin; public CGSize Size; }

internal static partial class MacNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibC = "libc";

    [LibraryImport(CoreGraphics)] internal static partial uint CGMainDisplayID();
    [LibraryImport(CoreGraphics)] internal static partial CGRect CGDisplayBounds(uint display);
    [LibraryImport(CoreGraphics)] internal static unsafe partial int CGGetActiveDisplayList(uint maxDisplays, uint* displays, out uint count);
    [LibraryImport(CoreGraphics)] internal static partial IntPtr CGEventCreate(IntPtr source);
    [LibraryImport(CoreGraphics)] internal static partial CGPoint CGEventGetLocation(IntPtr evt);
    [LibraryImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] internal static partial bool CGEventSourceKeyState(int stateId, ushort key);
    [LibraryImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] internal static partial bool CGEventSourceButtonState(int stateId, int button);
    [LibraryImport(CoreGraphics)] internal static partial ulong CGEventSourceFlagsState(int stateId);
    [LibraryImport(CoreGraphics)] internal static partial int CGWarpMouseCursorPosition(CGPoint point);
    [LibraryImport(CoreFoundation)] internal static partial void CFRelease(IntPtr cf);

    [LibraryImport(LibC, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SysctlByName(string name, void* oldp, ref nuint oldlenp, void* newp, nuint newlen);

    internal const int CombinedSessionState = 0;

    public static unsafe ulong SysctlU64(string name)
    {
        ulong value = 0;
        nuint size = (nuint)sizeof(ulong);
        return SysctlByName(name, &value, ref size, null, 0) == 0 ? value : 0;
    }

    /// <summary>Every display, main first, in CoreGraphics' global coordinates (points, origin top-left of the main display).</summary>
    public static unsafe List<(uint Id, CGRect Bounds)> Displays()
    {
        var result = new List<(uint, CGRect)>();
        uint* ids = stackalloc uint[16];
        if (CGGetActiveDisplayList(16, ids, out uint count) == 0)
        {
            uint main = CGMainDisplayID();
            result.Add((main, CGDisplayBounds(main)));
            for (int i = 0; i < count; i++)
            {
                if (ids[i] != main) result.Add((ids[i], CGDisplayBounds(ids[i])));
            }
        }
        if (result.Count == 0) result.Add((0, new CGRect { Size = new CGSize { Width = 1440, Height = 900 } }));
        return result;
    }

    public static CGPoint CursorPosition()
    {
        IntPtr e = CGEventCreate(IntPtr.Zero);
        if (e == IntPtr.Zero) return default;
        try { return CGEventGetLocation(e); }
        finally { CFRelease(e); }
    }
}
