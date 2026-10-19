# COM-import rewriting in the launcher

A prebuilt app's assemblies may declare `[ComImport]` interfaces and coclasses. They use them for
Direct3D 9 (Ab3d.DXEngine.Wpf), Media Foundation, the shell (Telerik, WindowsAPICodePack), WebView2
and TSF.

The runtime's built-in COM interop serves them only on Windows: `Marshal.GetObjectForIUnknown`, RCWs,
the interface marshaller, and `GetComInterfaceForObject`. Off Windows every one of those calls throws
`PlatformNotSupportedException`, even when the object behind the pointer is one of ours, such as the
Direct3D shim's D3D9 device.

wpfmac therefore rewrites such assemblies when it loads them. Their COM calls then go through
`ComWrappers` and plain vtable calls, which work on every OS. The rewrite applies on Windows too, so
there is one implementation everywhere. The user approved it (2026-10-19).

- **The rewrite only ever happens in wpfmac.** The app's folder is never written. The rewritten
  image goes to wpfmac's cache.
- **`WPF_COMIMPORT_REWRITE`** chooses what is rewritten:
  - `on` (the default): every app assembly that declares one;
  - `off`: none;
  - a `;`-separated list of assembly names (`*` wildcards): only those.

## 0. Which assemblies

Many assemblies work today on Windows through built-in COM, and use what the rewriter does not
support yet: VARIANT, BSTR, IDispatch, CCWs with events. Examples are WebView2.Core (274 interfaces),
MediaFoundation.dll (162), Telerik, WindowsAPICodePack, TOM and IViewObject. The rewrite must not
take that away. So each assembly is all or nothing:

- **Windows.** An assembly is rewritten only if every ComImport usage in it is one the rewriter fully
  supports. Otherwise it stays on built-in COM, unchanged, with one log line that names the
  constructs it lacks.
- **Off Windows,** where built-in COM does not exist, what is supported is rewritten, and every
  unsupported member throws `NotSupportedException` by name when it is called.
- **Each run reports, per assembly,** whether it was rewritten or left, and why (in the
  compatibility log and `merge-report.txt`).
- **The stack's own assemblies (the overlay) are never rewritten.** They declare no ComImport of
  their own, by rule.
- **The supported set grows from that gap list.** That is the way to one implementation without
  breaking an app that works.

## 1. Where it hooks

`wpfmac run`/`prepare` already writes the app's `deps.json` into the cache (DepsMerger). The rewrite
is a step of that merge:

1. **Scan.** Every assembly the app's deps list (and every `.dll` in its folder that the host would
   probe) is opened with `System.Reflection.Metadata`. Only the TypeDef flags are read. An assembly
   with no `TypeAttributes.Import` type and no reference to one is left as it is, at the cost of
   reading its metadata table.
2. **Rewrite.** The others are rewritten with Mono.Cecil (MIT; its notice ships in the overlay, and
   the payload already carries it for the detours). The result is written to
   `<cache>/comimport/<sha256 of the input>/<Name>.dll`, so a second run of the same binary reuses it.
   Cecil reads obfuscated assemblies fine, because names do not matter to the rewrite.
3. **Point the app at it.**
   - The assembly's entry in the merged deps moves to a synthetic library, `wpfmac.comimport/1.0`,
     whose runtime asset is `<sha256>/<Name>.dll`.
   - `<cache>/comimport` becomes the parent of an `--additionalprobingpath` root.
   - The host then puts the rewritten file on the TPA list where the original was. There is no
     load-time cost and no load-context trick, and `Assembly.Load` by name finds it like any app
     assembly.
4. **Assemblies loaded by path** at run time (`Assembly.LoadFrom` of a plugin folder) are outside the
   deps, and nothing in the runtime lets a hook substitute another image for a load by path. They
   stay on built-in COM, as they are.

A rewritten assembly references one new assembly, `WpfMac.ComInterop.dll`, which ships in the
overlay. The rewrite generates the per-interface code; the runtime part lives in `WpfMac.ComInterop`.

## 2. Interfaces

For each `[ComImport]` interface `I` (with `Guid` G):

- **Keep the interface itself.** `TypeAttributes.Import` and `[InterfaceType]` are removed, and
  `[Guid]` stays. Fields, parameters and casts typed `I` keep working, and nothing outside the
  assembly sees a change of type identity.
- **Generate `I.Impl`**, a nested `[DynamicInterfaceCastableImplementation]` interface, `I.Impl : I`.
  It holds one default method per method of `I`, explicitly implementing it. Each body:
  1. asks `WpfMac.ComInterop` for `this`'s native pointer for G. A wrapper keeps a QueryInterface
     result per Guid.
  2. loads the arguments marshalled as built-in COM interop would marshal them (below);
  3. `calli unmanaged stdcall` through vtable slot *k*. *k* is 3 + the method's index in declaration
     order for `InterfaceIsIUnknown`, and 7 + index for `InterfaceIsDual`/`InterfaceIsIDispatch`
     (after IDispatch's four). Methods of a base interface are not counted: a C# ComImport interface
     redeclares them, as COM interop itself requires.
  4. without `PreserveSig`, turns a failed HRESULT into `Marshal.GetExceptionForHR` and returns the
     `[out, retval]` value; with it, returns the HRESULT or the declared value.
- **Marshalling** covers what these assemblies use, and refuses the rest:
  - **Blittables** and blittable structs are passed as they are, and `ref`/`out` and arrays as
    pinned pointers.
  - **`bool`** is marshalled as a 4-byte BOOL unless `[MarshalAs]` says otherwise.
  - **Strings** marshalled as `LPWStr`/`LPStr` are converted, and freed after the call.
  - **Interfaces:** an interface parameter (`I`, `object` marshalled as `IUnknown`/`Interface`) is
    passed as its native pointer. It is borrowed, as the built-in marshaller borrows it: no
    AddRef. `out`/`ref` interfaces receive the native pointer and wrap it. The wrapper takes over
    the reference the callee returned.
  - **Refused** (VARIANT, BSTR, SAFEARRAY, `IDispatch` late binding, custom marshallers): the
    method's `I.Impl` body throws `NotSupportedException` naming the method and the parameter, and
    the compatibility log says so once. A call is never marshalled wrong.

## 3. Objects

- **`WpfMac.ComInterop.Wrappers : ComWrappers`.** `GetOrCreateObjectForComInstance` gives one managed
  object per native identity, a `ComObject : IDynamicInterfaceCastable`.
  - Its `IsInterfaceImplemented(I)` finds `I.Impl` (registered by the rewritten assembly's module
    initializer, with I's Guid) and QueryInterfaces for G.
  - A C# cast (`(I)o`, `o as I`) is therefore a QueryInterface, as it is for an RCW.
  - The wrapper releases its references when it is collected, or at
    `ReleaseComObject`/`FinalReleaseComObject`.
- **Call sites rewritten in the assembly's IL:**
  - **P/Invokes, and `[UnmanagedFunctionPointer]` delegates, whose parameters or return are
    ComImport interfaces.** The native signature gets `IntPtr`, and the call site converts: an object
    becomes its pointer for the parameter's Guid, kept alive for the call; a returned or `out`
    pointer is wrapped. Ab3d.DXEngine.Wpf calls D3D9 this way. It reads vtable slots through
    `GetComInterfaceForObject` and calls them through delegates whose parameters are its ComImport
    interfaces.
  - `Marshal.GetObjectForIUnknown`, `GetTypedObjectForIUnknown`, `GetUniqueObjectForIUnknown`.
  - `GetIUnknownForObject`, `GetComInterfaceForObject` (on a wrapper: its pointer for the Guid,
    AddRef'd).
  - `ReleaseComObject`, `FinalReleaseComObject`, `IsComObject`.
  - `new C()` of a ComImport coclass becomes `CoCreateInstance` of its CLSID through the OS where
    there is one. Off Windows, the shims that register class factories serve it, and anything else
    fails with the CLSID logged.
- **Managed objects passed to native code as a ComImport interface (CCWs).**
  `ComputeVtables` builds the vtable from the same declaration order, with reverse stubs. This is
  done for what the scanned apps need. A managed implementation passed where no stub was generated is
  logged and refused.

## 4. Tests

- **Synthetic** (`tools/WpfMac/tests`): a test assembly with ComImport interfaces is rewritten and
  run against:
  - a small COM object of our own (an `UnmanagedCallersOnly` vtable);
  - the Direct3D shim's D3D9.

  The checks cover calls and slot numbers, HRESULT exceptions vs PreserveSig, `out` interfaces,
  casts (QueryInterface) and their failure, Release, and a refused signature. They run on Windows
  and on the Mac and Linux heads when reachable.
- **Real:** DXEngine's D3D9 path through the rewriter on Windows. The app 3D frame must be the
  same as now (120 pixels against real D3D11).
- **Smoke:** app and Workbench, under the usual rules. Their other ComImport assemblies (app,
  Telerik, Media Foundation, WebView2) are rewritten too.

## 5. Off Windows (M6)

With the rewriter, DXEngine's D3DImage path no longer needs built-in COM. What remains for macOS and
Linux is what the shim needs there anyway: d3d11/dxgi/d3d9 routed to it (`libwin32shim`) and a WebGPU
adapter.

## 6. Status (2026-10-19)

- **Built:**
  - `WpfMac.ComRewrite` (the rewriter, with Mono.Cecil);
  - `WpfMac.ComInterop` (the runtime, shipped in the overlay);
  - the launcher step (`ComImportStep`). Its verdicts go to `merge-report.txt` and the rewritten
    copies to `<cache>/comimport/`.
- **Supported:**
  - IUnknown and dual interfaces;
  - blittables and blittable structs, by value and by reference;
  - `bool` as BOOL, VARIANT_BOOL or U1, by value and by reference;
  - `LPWStr`/`LPStr` strings in;
  - C arrays of blittables;
  - interfaces in, out and as retval;
  - PreserveSig or HRESULT plus retval;
  - P/Invokes and delegates with interface parameters;
  - the `Marshal` COM members;
  - `new` of a coclass.
- **Not supported yet** (each named in the verdict):
  - VARIANT, BSTR, SAFEARRAY;
  - IDispatch-only interfaces;
  - CCWs (managed classes implementing a COM interface) and COM events;
  - `ref` interfaces;
  - formatted classes passed by reference;
  - COM types of another assembly or the BCL's `ComTypes`.
  - Two more cases are left alone on Windows, because a built-in RCW could reach them and would not
    convert:
    - a cast to a rewritten interface of an object of unknown origin;
    - a COM interface that other assemblies can see.
- **app's folder on Windows:**
  - Ab3d.DXEngine.Wpf is rewritten (4 interfaces, its Direct3DCreate9Ex P/Invoke, 5 delegates, 11
    call sites).
  - The 14 others stay on built-in COM, each for a named reason: Ab3d.DXEngine (a cast of unknown
    origin), GwxRuntimeCore (IViewObject from a WinForms control's ActiveX), MediaFoundation,
    WebView2.Core/Wpf, WindowsAPICodePack(.Shell), SharpDX, IcoWindowsFormsSpecialized (TOM), and
    Telerik ×5 (CCWs, BSTR, VARIANT, the BCL's ComTypes).
- **Tests:**
  - `ComRewriterTests`: calls, retvals, out interfaces, VARIANT_BOOL, structs by reference, LPWStr,
    HRESULT exceptions, an unsupported member, casts and identity, Release, and DXEngine's
    delegate-through-a-slot call. They run against a native object of the test's own.
  - `ComImportStepTests`: all or nothing on Windows, partial elsewhere, the switch.
- **Still to run:**
  - the app 3D frame through the rewritten DXEngine.Wpf, and the app/Workbench smoke
    runs (waiting for the demo licence);
  - the Mac head (M6).
