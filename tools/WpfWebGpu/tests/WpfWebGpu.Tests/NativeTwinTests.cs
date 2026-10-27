// A vtable function of the stack's with a managed twin (WgpuInterop's NativeTwins), made a delegate of by an
// app (Ab3d.DXEngine.Wpf's IDirect3DDevice9::CreateTexture): the twin is called with what native code would pass.

using System.Runtime.InteropServices;
using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed unsafe class NativeTwinTests
{
    private delegate int CreateTextureLike(IntPtr self, int w, int h, uint levels, int usage, int format, int pool, out IntPtr texture, ref IntPtr shared);

    [Fact]
    public void ADelegateOfAFunctionWithATwin_CallsTheTwin_WithAddressesForItsReferences()
    {
        var fn = (IntPtr)0x7A000010;
        string? seen = null;
        Func<IntPtr, uint, uint, uint, uint, int, int, IntPtr, IntPtr, int> twin = (self, w, h, levels, usage, format, pool, tex, shared) =>
        {
            seen = $"{self} {w}x{h} {levels} {usage} {format} {pool} {*(IntPtr*)shared}";
            *(IntPtr*)tex = 77;
            *(IntPtr*)shared = 88;
            return 5;
        };
        AppContext.SetData("WpfWebGpu.NativeTwin", (Func<IntPtr, Delegate?>)(p => p == fn ? twin : null));
        try
        {
            var d = (CreateTextureLike)BrowserApis.Marshal_GetDelegateForFunctionPointer(fn, typeof(CreateTextureLike));
            IntPtr shared = 3;
            int hr = d(11, 640, 480, 1, 1, 21, 0, out IntPtr tex, ref shared);
            Assert.Equal(5, hr);
            Assert.Equal("11 640x480 1 1 21 0 3", seen);
            Assert.Equal((IntPtr)77, tex);
            Assert.Equal((IntPtr)88, shared);
        }
        finally { AppContext.SetData("WpfWebGpu.NativeTwin", null); }
    }
}
