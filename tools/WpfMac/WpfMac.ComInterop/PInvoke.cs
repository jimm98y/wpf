// What an app's Windows P/Invoke, rewritten by wpfmac's P/Invoke rewriter onto the stack's managed
// implementation (Documentation/pinvoke-rewriter.md), calls for what the runtime's marshalling would do
// that a plain managed call cannot: a SafeHandle held for the call, a delegate as a function pointer, the
// last error as SetLastError captures it.

using System.Runtime.InteropServices;

namespace WpfMac.ComInterop;

public static class PInvoke
{
    /// <summary>A SafeHandle argument: held (DangerousAddRef) for the call, as the runtime holds it. Null throws
    /// as the runtime does.</summary>
    public static IntPtr AddRef(SafeHandle? handle, ref bool added)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.DangerousAddRef(ref added);
        return handle.DangerousGetHandle();
    }

    public static void Release(SafeHandle? handle, bool added)
    {
        if (added) handle!.DangerousRelease();
    }

    /// <summary>A SafeHandle the function returns (or puts in an out parameter): a new instance of the
    /// declared type that owns <paramref name="value"/>.</summary>
    public static SafeHandle NewSafeHandle(IntPtr value, RuntimeTypeHandle type)
    {
        var h = (SafeHandle)Activator.CreateInstance(Type.GetTypeFromHandle(type)!, nonPublic: true)!;
        Marshal.InitHandle(h, value);
        return h;
    }

    /// <summary>The value of a handle argument, whatever its declared type (routing on Windows).</summary>
    public static IntPtr HandleOf(SafeHandle? handle) => handle == null ? IntPtr.Zero : handle.DangerousGetHandle();

    /// <summary>A delegate argument: the function pointer the runtime hands native code for it (null: 0).</summary>
    public static IntPtr DelegateToNative(Delegate? d) => d == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(d);

    /// <summary>A function pointer returned as a delegate type.</summary>
    public static Delegate? DelegateFromNative(IntPtr p, RuntimeTypeHandle type) =>
        p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, Type.GetTypeFromHandle(type)!);

    /// <summary>A returned ANSI string: copied; the memory is CoTaskMem the caller owns, freed as the runtime frees it.</summary>
    public static string? TakeAnsi(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringAnsi(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>SetLastError = true, before the call: the error is cleared, as the runtime clears it.</summary>
    public static void BeginLastError() => Marshal.SetLastSystemError(0);

    /// <summary>SetLastError = true, after the call: what GetLastWin32Error reads.</summary>
    public static void EndLastError() => Marshal.SetLastPInvokeError(Marshal.GetLastSystemError());

    /// <summary>A P/Invoke of a function no Windows DLL exports: what calling it throws there.</summary>
    public static Exception EntryPointNotFound(string module, string name)
    {
        Com.LogOnce("noentry:" + module + "!" + name, $"pinvoke: {module}!{name} is no Windows export; the call throws EntryPointNotFoundException as on Windows");
        return new EntryPointNotFoundException($"Unable to find an entry point named '{name}' in DLL '{module}'.");
    }

    /// <summary>A P/Invoke the rewriter cannot marshal (its reason logged when called).</summary>
    public static Exception Unsupported(string member, string why)
    {
        Com.LogOnce("pinvoke-unsupported:" + member, $"pinvoke: {member} was called; its signature is not supported by the P/Invoke rewriter ({why})");
        return new NotSupportedException($"P/Invoke {member}: {why} is not supported by wpfmac's P/Invoke rewriter");
    }
}
