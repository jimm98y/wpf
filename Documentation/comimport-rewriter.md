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

The rewrite happens **in memory**. No rewritten DLL is ever written to disk (the user's decision,
2026-10-19).

1. **Judge, at prepare time.** `wpfmac run`/`prepare` merges the app's deps (DepsMerger), and
   `ComImportStep` then judges every app assembly that has a `TypeAttributes.Import` type. The
   check is a `System.Reflection.Metadata` scan, cheap for the others.
   - The verdict for each (rewritten, or left and why) goes to `merge-report.txt`. Nothing else is
     written.
   - Judging is not cached: GraphWorX's 14 ComImport assemblies take 2.6 s per launch (Telerik.Windows.Controls
     0.8 s, the others 0.03-0.4 s).
   - A chosen assembly leaves the deps (so it is not on the TPA list). Its name and original path go
     to the app's runtimeconfig as `WpfMac.ComImport`.
   - What an earlier wpfmac wrote (`<cache>/comimport/`) is deleted.
2. **Load, at the first request.** The startup hook installs `ComImportLoader` on the default load
   context's `Resolving` event. Every load by name reaches it, because the assembly is on no list
   the runtime probes first: the app's own references, `Assembly.Load`, `Type.GetType` with an
   assembly name.
   - The loader rewrites the original's bytes with Mono.Cecil and loads them with `LoadFromStream`,
     with the symbols when a .pdb lies beside the original.
   - Satellite assemblies keep their deps entries and resolve as before.
   - Cost per launch: Ab3d.DXEngine.Wpf 150-460 ms.
3. **Its own file, as it was.** In a rewritten assembly, these calls are rewritten to
   `Com.AssemblyLocation` and its kin, which answer with the original file for an assembly the loader
   made, and as before for any other. The `Assembly` objects stay the runtime's own, so
   `typeof(X).Assembly == Assembly.Load(name)`.
   - the Assembly members: `Location`, `CodeBase`, `EscapedCodeBase`, `GetName()` (its `CodeBase`);
   - the Module members: `FullyQualifiedName`, `Name`.
4. **Its native libraries.** P/Invokes of a rewritten assembly find libraries in the original's
   folder (`ResolvingUnmanagedDll`), as `DllImportSearchPath.AssemblyDirectory` would. The Windows
   dispatch shim (Direct3D included) reads the original's imports when the assembly has no file of its
   own. Before that fix, DXEngine.Wpf's d3d9 import reached the real d3d9 and its shared surface
   failed.
5. **What it cannot cover:**
   - **Another assembly asking a rewritten one's location.** For example IcoTrace's
     `GetCallingAssembly().CodeBase`, which GwxRuntimeCore relies on to find `Mobile_devices.csv`.
     That caller is not rewritten, so it sees an assembly without a file.
   - **Assemblies loaded by path** (`Assembly.LoadFrom`). They stay as they are.

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
- **Real:** DXEngine's D3D9 path through the rewriter on Windows. The GraphWorX 3D frame must be the
  same as now (120 pixels against real D3D11).
- **Smoke:** GraphWorX and Workbench, under the usual rules. Their other ComImport assemblies (ICONICS,
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
- **GraphWorX's folder on Windows:**
  - Ab3d.DXEngine.Wpf is rewritten (4 interfaces, its Direct3DCreate9Ex P/Invoke, 5 delegates, 11
    call sites).
  - The 14 others stay on built-in COM, each for a named reason: Ab3d.DXEngine (a cast of unknown
    origin), GwxRuntimeCore (IViewObject from a WinForms control's ActiveX), MediaFoundation,
    WebView2.Core/Wpf, WindowsAPICodePack(.Shell), SharpDX, IcoWindowsFormsSpecialized (TOM), and
    Telerik ×5 (CCWs, BSTR, VARIANT, the BCL's ComTypes).
- **Added since the first version:**
  - BSTR in, by reference and as retval.
  - Built-in RCWs adopted at a cast. Off Windows only: on Windows such an assembly stays on built-in
    COM, because the two that flipped this way in GraphWorX did not run as before. Ab3d.DXEngine
    stopped using Direct3D (cause not found yet), and GwxRuntimeCore's location, asked from IcoTrace,
    changed.
  - Visibility judged from what the app's other assemblies actually reference.
  - Assemblies rewritten together with those whose COM types they use (a fixpoint over the app).
  - The in-memory loader, with location redirection.
- **GraphWorX, Windows (2026-10-19):** only Ab3d.DXEngine.Wpf is rewritten. Loaded from memory, its 3D
  frame is identical to the build before the rewriter (0 px; 120 px within 1 code against real
  D3D11), and the bench smoke run and Workbench run are clean.
- **Tests:**
  - `ComRewriterTests`: calls, retvals, out interfaces, VARIANT_BOOL, structs by reference, LPWStr,
    HRESULT exceptions, an unsupported member, casts and identity, Release, and DXEngine's
    delegate-through-a-slot call. They run against a native object of the test's own.
  - `ComImportStepTests`: all or nothing on Windows, partial elsewhere, the switch, nothing written
    under the cache, an earlier wpfmac's copies removed.
  - The in-memory load: the fixture reports its original `Location`, `CodeBase`, `EscapedCodeBase`,
    `GetName().CodeBase` and module names. `typeof(X).Assembly == Assembly.Load(name) ==
    GetExecutingAssembly()` holds, and a P/Invoke finds a library beside the original.
- **Still to run:**
  - the GraphWorX 3D frame through the rewritten DXEngine.Wpf, and the GraphWorX/Workbench smoke
    runs (waiting for the demo licence);
  - the Mac head (M6).
