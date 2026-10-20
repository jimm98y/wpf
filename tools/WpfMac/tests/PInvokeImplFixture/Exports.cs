// What PInvokeRewriterTests rewrite PInvokeFixture's P/Invokes onto: plain static methods with the
// functions' raw signatures, tagged as WpfMac.Win32's are.

using System.Runtime.InteropServices;

namespace PInvokeImplFixture;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class Win32ExportAttribute : Attribute { public Win32ExportAttribute(params string[] names) { } }

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class Win32ExportOnWindowsAttribute : Attribute { public Win32ExportOnWindowsAttribute(int mask, params string[] names) { } }

[StructLayout(LayoutKind.Sequential)]
public struct PT { public int X, Y; }

public static unsafe class Exports
{
    public static int LastUnimplemented;

    // the helpers a rewritten P/Invoke calls (WpfMac.Win32's Exports has the real ones)
    public static long Unimplemented(string module, string name, int failure)
    {
        LastUnimplemented++;
        Marshal.SetLastPInvokeError(120);
        return failure == 2 ? unchecked((int)0x80004001) : failure == 5 ? -1 : 0;
    }

    public static bool OnStack(nint h) => h == 0x5747;

    public static void FlushDC(nint hdc) { }

    [Win32Export("LengthW")] public static int LengthW(char* s) => s == null ? -1 : new string(s).Length;

    [Win32Export("LengthA")] public static int LengthA(byte* s) => s == null ? -1 : Marshal.PtrToStringUTF8((IntPtr)s)!.Length + 1000;

    [Win32Export("GreetW")]
    public static int GreetW(char* buffer, int capacity)
    {
        string text = "hello";
        int n = Math.Min(text.Length, capacity - 1);
        for (int i = 0; i < n; i++) buffer[i] = text[i];
        buffer[n] = '\0';
        return n;
    }

    [Win32Export("IsPositive")] public static int IsPositive(int v) => v > 0 ? 1 : 0;

    [Win32Export("Grow")] public static int Grow(PT* p) { p->X *= 2; p->Y *= 2; return 1; }

    [Win32Export("Sum")] public static int Sum(PT p) => p.X + p.Y;

    [Win32Export("FailWith")] public static int FailWith(int error) { Marshal.SetLastSystemError(error); return 0; }

    [Win32Export("Call")] public static int Call(delegate* unmanaged<int, int> f, int v) => f(v);

    [Win32Export("OpenThing")] public static nint OpenThing(int v) => v;

    [Win32Export("ThingValue")] public static int ThingValue(nint h) => (int)h;

    [Win32Export("Hr")] public static int Hr(int fail, int* result) { if (fail != 0) return unchecked((int)0x80004005); *result = 42; return 0; }

    [Win32Export("TwoArgs")] public static int TwoArgs(int a, int b) => a * 10 + b;

    [Win32ExportOnWindows(1, "WindowValue")] public static int WindowValue(nint hwnd) => 7;

    [Win32Export("BridgeW")] public static int BridgeW(char* s) => new string(s).Length + 2000;
}
