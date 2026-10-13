using System.Runtime.InteropServices;
using WpfMac.Bootstrap.Win32;
using Xunit;

namespace WpfMac.Tests;

/// <summary>
/// The DLL wpfmac generates on Windows to stand in for user32 towards an app (WpfMac.Bootstrap's
/// ShimImage): loaded for real, called through its exports, on this machine's architecture.
/// </summary>
public unsafe class ShimImageTests
{
    private const nint Ours = 0x5150;

    [UnmanagedCallersOnly]
    private static nint Classify(byte* entry, nint* saved)
    {
        nint first = *(nint*)((byte*)saved + ShimImage.ArgumentOffset(0, RuntimeInformation.ProcessArchitecture));
        return first == Ours ? *(nint*)(entry + 8) : *(nint*)entry;
    }

    // SetWindowPos's shape: seven arguments, so the dispatcher must hand all of them on untouched.
    [UnmanagedCallersOnly]
    private static int Managed(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags) =>
        (int)(hwnd + after + x * 10 + y * 100 + cx * 1000 + cy * 10000 + flags * 100000);

    [Fact]
    public void Exports_reach_the_real_function_or_the_managed_one_by_handle()
    {
        if (!OperatingSystem.IsWindows()) return;
        IntPtr user32 = NativeLibrary.Load("user32.dll");
        var exports = new List<ShimImage.Export>
        {
            new(10, "GetSystemMetrics", true, false),
            new(11, null, false, false),                      // a gap in the ordinals
            new(12, "SetWindowPos", true, true),
        };
        var layout = ShimImage.Build("wpfmac-test.dll", 10, exports, RuntimeInformation.ProcessArchitecture);
        string path = Path.Combine(Path.GetTempPath(), $"wpfmac-shimtest-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(path, layout.Image);
        IntPtr shim = NativeLibrary.Load(path);

        byte* data = (byte*)shim + layout.DataRva;
        *(IntPtr*)(data + ShimImage.ClassifierOffset) = (IntPtr)(delegate* unmanaged<byte*, nint*, nint>)&Classify;
        byte* e0 = data + ShimImage.TableOffset, e2 = e0 + 2 * ShimImage.EntrySize;
        *(IntPtr*)e0 = NativeLibrary.GetExport(user32, "GetSystemMetrics");
        *(IntPtr*)e2 = NativeLibrary.GetExport(user32, "SetWindowPos");
        *(IntPtr*)(e2 + 8) = (IntPtr)(delegate* unmanaged<nint, nint, int, int, int, int, uint, int>)&Managed;

        // A plain export is the real function.
        var metrics = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(shim, "GetSystemMetrics");
        var realMetrics = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(user32, "GetSystemMetrics");
        Assert.Equal(realMetrics(0), metrics(0));

        // A dispatched one: the managed function for our handle, with every argument intact...
        var swp = (delegate* unmanaged<nint, nint, int, int, int, int, uint, int>)NativeLibrary.GetExport(shim, "SetWindowPos");
        Assert.Equal((int)(Ours + 1 + 20 + 300 + 4000 + 50000 + 600000), swp(Ours, 1, 2, 3, 4, 5, 6));
        // ...and the real one for any other: an invalid window fails as user32 fails.
        Assert.Equal(0, swp(0x7FFF0001, 0, 0, 0, 0, 0, 0x17));

        // Ordinal imports find the same functions.
        Assert.Equal(NativeLibrary.GetExport(shim, "GetSystemMetrics"), GetProcAddress(shim, 10));
        Assert.Equal(IntPtr.Zero, GetProcAddress(shim, 11));
    }

    [DllImport("kernel32", EntryPoint = "GetProcAddress")]
    private static extern IntPtr GetProcAddress(IntPtr module, nint ordinal);
}
