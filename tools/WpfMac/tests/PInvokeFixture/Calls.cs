// P/Invokes as apps declare them, into "testdll" (a Windows DLL as far as the test's export table says),
// and entry points PInvokeRewriterTests call on the rewritten copy.

using System.Runtime.InteropServices;
using System.Text;

namespace PInvokeFixture;

[StructLayout(LayoutKind.Sequential)]
public struct Point { public int X, Y; }

public sealed class ThingHandle : SafeHandle
{
    public ThingHandle() : base(IntPtr.Zero, true) { }
    public override bool IsInvalid => handle == IntPtr.Zero || handle == -1;
    public static int Released;
    protected override bool ReleaseHandle() { Released++; return true; }
}

public static class Calls
{
    [DllImport("testdll.dll", CharSet = CharSet.Unicode)] private static extern int Length(string s);
    [DllImport("testdll.dll", CharSet = CharSet.Ansi)] private static extern int Length2(string s);
    [DllImport("testdll.dll", EntryPoint = "Length", CharSet = CharSet.Ansi)] private static extern int LengthAnsi(string s);
    [DllImport("testdll.dll", EntryPoint = "Length", CharSet = CharSet.Auto)] private static extern int LengthAuto(string s);
    [DllImport("testdll.dll", CharSet = CharSet.Unicode)] private static extern int Greet(StringBuilder sb, int capacity);
    [DllImport("testdll.dll")] private static extern bool IsPositive(int v);
    [DllImport("testdll.dll", EntryPoint = "IsPositive")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool IsPositive1(int v);
    [DllImport("testdll.dll")] private static extern bool Grow(ref Point p);
    [DllImport("testdll.dll")] private static extern int Sum(Point p);
    [DllImport("testdll.dll", SetLastError = true)] private static extern bool FailWith(int error);
    [DllImport("testdll.dll", SetLastError = true)] private static extern bool Missing(IntPtr h);
    [DllImport("testdll.dll", PreserveSig = false)] private static extern void MissingHr();
    [DllImport("testdll.dll")] private static extern void MissingVoid();
    [DllImport("testdll.dll")] private static extern int NotAnExport();
    private delegate int Callback(int v);
    [DllImport("testdll.dll")] private static extern int Call(Callback f, int v);
    [DllImport("testdll.dll")] private static extern ThingHandle OpenThing(int v);
    [DllImport("testdll.dll")] private static extern int ThingValue(ThingHandle h);
    [DllImport("testdll.dll", PreserveSig = false)] private static extern int Hr(int fail);
    [DllImport("testdll.dll")] private static extern int TwoArgs(int a, int b, int c);
    [DllImport("testdll.dll", EntryPoint = "TwoArgs")] private static extern int OneArg(int a);
    [DllImport("testdll.dll")] private static extern int WindowValue(IntPtr hwnd);
    [DllImport("testdll.dll", EntryPoint = "Bridge", CharSet = CharSet.Ansi)] private static extern int Bridge(string s);

    public static int UnicodeString() => Length("héllo");
    public static int AnsiStringExact() => Length2("abc");
    public static int AnsiStringA() => LengthAnsi("abcd");
    public static int AutoString() => LengthAuto("abcde");
    public static int NullString() => Length(null!);
    public static string StringBuilderOut() { var sb = new StringBuilder(16); Greet(sb, sb.Capacity); return sb.ToString(); }
    public static bool BoolReturn(int v) => IsPositive(v);
    public static bool BoolU1Return(int v) => IsPositive1(v);
    public static string RefStruct() { var p = new Point { X = 3, Y = 4 }; bool ok = Grow(ref p); return $"{ok} {p.X},{p.Y}"; }
    public static int StructByValue() => Sum(new Point { X = 5, Y = 6 });
    public static string LastError() { bool ok = FailWith(5); int e = Marshal.GetLastWin32Error(); bool ok2 = FailWith(0); return $"{ok} {e} {Marshal.GetLastWin32Error()} {ok2}"; }
    public static string Unimplemented() { bool r = Missing(1); return $"{r} {Marshal.GetLastWin32Error()}"; }
    public static void UnimplementedHr() => MissingHr();
    public static void UnimplementedVoid() => MissingVoid();
    public static int NotExported() => NotAnExport();
    public static int Callback1(int v) => Call(x => x * 3, v);
    public static string SafeHandles()
    {
        using var h = OpenThing(9);
        int v = ThingValue(h);
        return $"{h.GetType().Name} {h.IsInvalid} {v}";
    }
    public static int HrRetval(int fail) => Hr(fail);
    public static string Arity() => $"{TwoArgs(1, 2, 3)} {OneArg(4)}";
    public static int Routed(IntPtr hwnd) => WindowValue(hwnd);
    public static int AnsiBridge() => Bridge("xyz");
}
