# The P/Invoke rewriter

An unmodified Windows app P/Invokes user32, kernel32, gdi32, uxtheme, d3d11 and the rest of Windows.
The stack implements those functions in managed code: `tools/WpfMac/WpfMac.Win32` for the Win32 API,
and `Microsoft.Wpf.Interop.WebGpu`'s `Direct3D/Exports.cs` for Direct3D. The P/Invoke rewriter
connects the two. Each of an app's P/Invokes of a Windows DLL becomes a managed method that marshals
its arguments as the runtime would and calls the implementation directly.

There is no native library and no function pointer, and no code is generated at run time. That is
what lets the same rewrite run on every head:

| head | when | how |
|---|---|---|
| macOS, Linux, Windows (wpfmac) | when the assembly loads | `ComImportLoader`, in memory; the result is cached in the app's cache folder |
| iOS, Android, the browser (AOT) | at build | the same `ComRewriter.RewriteToMemory`, over the app's assemblies before AOT compilation (not wired into those builds yet) |

It replaces two mechanisms:

- `libwin32shim` (macOS): about 20,000 native arm64 stubs that jumped through slots into
  `[UnmanagedCallersOnly]` methods;
- `ShimImage`/`WindowsShim` (Windows): mirror DLLs written at run time, whose stubs asked a classifier
  which function to jump to.

## The implementations

A method implements exports by its attributes, which the rewriter matches by name in metadata, so
each implementation assembly declares its own:

```csharp
[Win32Export("GetWindowRect")]                    // off Windows: every call
[Win32ExportOnWindows(A0, "GetWindowRect")]       // Windows: calls whose argument 0 is the stack's window
public static int GetWindowRect(nint hwnd, RECT* rect) { ... }
```

The methods are `public static` and take the function's raw signature: pointers, handles as `nint`,
`BOOL` as `int`, as the Windows metadata gives them. The masks of `Win32ExportOnWindows` work as follows:

- bit k says argument k is a window;
- bit 16+k says argument k is a device context, which is flushed first (see below);
- bit 15 (`Always`) runs the implementation on every call.

A mask with device-context bits alone names no implementation. Those GDI functions stay the real
ones, after the drawing a `Graphics` has pending on the DC has been written to it.

## Every Windows export

`WpfMac.Win32` embeds `win32_exports.tsv`, which `WpfMac.Win32Native` (win32shim-gen) generates from
the Windows API metadata plus `extra-exports.txt`. It lists every export (module, name and failure
value) and every DLL name. The rewriter reads the table from the assembly's metadata.
`LoadLibrary`/`GetModuleHandle` read it at run time to recognize Windows DLLs.

## What a P/Invoke becomes

1. **The DLL.** The rewriter normalizes the name (`USER32.dll` → `user32`). A DLL the table does not
   know is not Windows's, so its P/Invokes are left alone (SkiaSharp's `libSkiaSharp`, CoreFoundation).
2. **The entry point**, found as Windows finds it:
   - `ExactSpelling` uses the name only;
   - Unicode and Auto (Auto is Unicode on Windows) try `nameW` and then `name`;
   - Ansi tries `name` and then `nameA`;
   - an ordinal (`#135`) is looked up in the table as it is.

   If no Windows DLL exports the entry point, the call throws `EntryPointNotFoundException`, as there.
3. **Off Windows:**
   - **The function has an implementation.** The arguments are marshalled with the COM rewriter's
     engine:
     - strings and StringBuilders by `CharSet`, unless `MarshalAs` says otherwise; `bool` as BOOL;
     - blittable structs by reference, pinned; formatted structs and classes through their native
       layout; arrays;
     - `SafeHandle`s are held for the call; `HandleRef`s are kept alive; delegates become function
       pointers (`Marshal.GetFunctionPointerForDelegate`).

     The implementation is called directly. Its parameter types are met as the native call would
     meet them: integers are widened or narrowed, and the app's struct is read as the implementation's
     struct of the same layout. A parameter count other than the function's is handled as the native
     ABI handles it: extra arguments are dropped and missing ones are zero.

     `SetLastError = true` clears the error before the call and captures it after, as the runtime
     does. `PreserveSig = false` throws a failed HRESULT and returns the `[out, retval]`.
   - **An ANSI import of a function that has only a W implementation.** It is called as Unicode when
     no argument can carry text past the marshaller: only numbers, handles, strings, StringBuilders
     and delegates.
   - **No implementation.** The call logs once (`win32 shim: module!name is not implemented`) and
     returns the function's failure value (`FALSE`/`NULL` with `ERROR_CALL_NOT_IMPLEMENTED`,
     `E_NOTIMPL`, `INVALID_HANDLE_VALUE`, ...), as on a Windows that lacked it.
   - **A signature the engine cannot marshal.** The call throws `NotSupportedException` by name (logged).
4. **On Windows**, only the functions with a `Win32ExportOnWindows` are rewritten. The original import
   is kept as `name<native>`. The new body flushes the masked device contexts, then asks
   `Exports.OnStack` about each masked window argument. The stack's window gets the
   implementation; any other handle gets `name<native>`, the real function.

Direct3D (`d3d11`, `dxgi`, `d3d9`, `d3dcompiler_47`) is `Always`: on Windows too, the app gets the
shim. `WPF_D3D11_SHIM` narrows that (`0`/`off`: none; `A*;B`: those assemblies).

## Which assemblies

The launcher (`ComImportStep`) lists every app assembly that P/Invokes a Windows DLL. On Windows it
lists only those that P/Invoke a routed function. Some are never listed:

- the stack's own assemblies, whose Windows P/Invokes are guarded;
- the runtime's;
- the entry assembly, which the host loads before the startup hook;
- mixed-mode (C++/CLI) assemblies, which Cecil cannot write.

`WPF_PINVOKE_REWRITE` (`off`, or a `;`-separated list of name patterns) narrows the selection. An
assembly that also declares COM types is rewritten for both in one pass: the COM rewrite first, whose
`name<native>` imports the P/Invoke pass then rewrites.

## What a Windows DLL is at run time

A Windows DLL has no file here. `LoadLibrary("user32")` and `GetModuleHandle` return a handle of its
own (64 KB aligned, as Windows' are), so a check that the DLL is present passes, and
`GetModuleFileName` returns `C:\Windows\System32\user32.dll`. `GetProcAddress` on such a handle finds
no native entry point and returns `NULL` with `ERROR_PROC_NOT_FOUND`, logged, as on a Windows whose
DLL lacks the function.

## Not covered

- An ordinal import's meaning on Windows depends on the DLL build, so on Windows those are left alone.
- Callbacks on the AOT heads: `Marshal.GetFunctionPointerForDelegate` needs the delegate type's
  reverse stub, which an AOT compiler generates only for delegate types it can see marshalled.
- Struct marshalling uses `Marshal.StructureToPtr`, so a struct with `CharSet.Auto` strings is UTF-8
  off Windows, where Windows would use UTF-16.

## Tests

`tools/WpfMac/tests/WpfMac.Tests/PInvokeRewriterTests.cs` rewrites `PInvokeFixture` onto
`PInvokeImplFixture`, which has its own small export table, and runs the result. The tests cover the
entry-point lookup, every marshalling kind above, the failure values, `EntryPointNotFoundException`,
and the Windows routing to both the implementation and the real function.
