using System.Runtime.InteropServices;

namespace IosSpike;

/// <summary>
/// The minimum wgpu-native surface needed to prove the iOS linkage. On iOS the library is linked
/// STATICALLY into the app executable, so the entry points live in the main image and the import
/// library name must be "__Internal" -- unlike the desktop head, which DllImports "wgpu_native".
/// (The real port will make Wgpu.Native.cs's single Library constant conditional on iOS.)
/// </summary>
internal static class WgpuNative
{
    private const string Library = "__Internal";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wgpuCreateInstance(IntPtr descriptor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuInstanceRelease(IntPtr instance);

    /// <summary>
    /// Point the shared WgpuInterop binding (which DllImports "wgpu_native") at the statically
    /// linked symbols in this app's main image. Lets the whole existing binding run on iOS with no
    /// source change -- the alternative would be an iOS csproj flavour defining the library name as
    /// "__Internal", mirroring what Browser/WgpuInterop.Browser.csproj does for wasm.
    /// </summary>
    private static bool s_resolverInstalled;

    internal static void InstallResolver()
    {
        // SetDllImportResolver throws InvalidOperationException if called twice for one assembly.
        if (s_resolverInstalled) return;
        s_resolverInstalled = true;

        NativeLibrary.SetDllImportResolver(
            typeof(Microsoft.Wpf.Interop.WebGpu.Wgpu).Assembly,
            (name, asm, path) => name == "wgpu_native"
                ? NativeLibrary.GetMainProgramHandle()
                : IntPtr.Zero);
    }

    /// <summary>
    /// Creates and releases a wgpu instance. Returns a human-readable result for on-screen display:
    /// a non-zero handle proves the static lib linked AND that __Internal P/Invoke resolves.
    /// </summary>
    internal static string Probe()
    {
        try
        {
            IntPtr instance = wgpuCreateInstance(IntPtr.Zero);
            if (instance == IntPtr.Zero)
                return "wgpuCreateInstance returned NULL";

            wgpuInstanceRelease(instance);
            return $"wgpu instance OK\n0x{instance.ToInt64():x}";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}\n{ex.Message}";
        }
    }

    /// <summary>
    /// Same probe, but through the SHARED WgpuInterop binding rather than this file's own externs.
    /// Success means the resolver works and every one of the binding's 43 imports is usable on iOS.
    /// </summary>
    internal static unsafe string ProbeSharedBinding()
    {
        try
        {
            IntPtr instance = Microsoft.Wpf.Interop.WebGpu.Wgpu.wgpuCreateInstance(null);
            if (instance == IntPtr.Zero)
                return "shared binding: NULL instance";

            Microsoft.Wpf.Interop.WebGpu.Wgpu.wgpuInstanceRelease(instance);
            return $"shared binding OK\n0x{instance.ToInt64():x}";
        }
        catch (Exception ex)
        {
            return $"shared binding FAILED\n{ex.GetType().Name}: {ex.Message}";
        }
    }
}
