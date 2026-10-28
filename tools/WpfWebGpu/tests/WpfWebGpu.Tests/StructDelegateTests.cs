// A formatted struct's delegate field (WpfWebGpu.ComInterop Formatted.cs) is a function pointer in the native
// layout, the one Callbacks makes for it -- a window class's lpfnWndProc, which Marshal could only make with
// the runtime's thunk, and Mono on WebAssembly has none for an app's delegate type (Telerik's WindowChrome
// registering its message window: Workbench did not start).

using System.Runtime.InteropServices;
using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class StructDelegateTests
{
    public delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClass
    {
        public int cbSize;
        public int style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [Fact]
    public void ADelegateField_IsThePointerCallbacksMakesForIt()
    {
        WndProc proc = (h, m, w, l) => (IntPtr)(m + 1);
        var wc = new WindowClass { cbSize = 1, style = 2, lpfnWndProc = proc, cbClsExtra = 3, lpszClassName = "Message" };

        IntPtr p = Com.StructToNative(wc, typeof(WindowClass).TypeHandle);
        try
        {
            IntPtr fn = Marshal.ReadIntPtr(p, (int)Marshal.OffsetOf<WindowClass>(nameof(WindowClass.lpfnWndProc)));
            Assert.NotEqual(IntPtr.Zero, fn);
            Assert.Equal(Callbacks.ToNative(proc), fn);
            Assert.Equal(3, Marshal.ReadInt32(p, (int)Marshal.OffsetOf<WindowClass>(nameof(WindowClass.cbClsExtra))));

            var back = (WindowClass)Com.StructFromNative(p, null, typeof(WindowClass).TypeHandle)!;
            Assert.Equal("Message", back.lpszClassName);
            Assert.Equal((IntPtr)8, back.lpfnWndProc(IntPtr.Zero, 7, IntPtr.Zero, IntPtr.Zero));
        }
        finally { Com.FreeStruct(p, typeof(WindowClass).TypeHandle); }
    }
}
