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
   - The judge is a pure metadata/IL scan (`ComRewriter.Analyze`): it runs the rewrite's checks
     without generating any code. Code is generated only at load.
   - Verdicts are cached in `comimport-verdicts.json` beside `merge-report.txt`. Each is keyed by
     the assembly's SHA-256 plus the rewriter build (the step's version and the rewriter module's
     MVID, so every changed rewriter re-judges). Only verdicts are cached, never DLLs.
   - **"Used by other assemblies"** (Windows): such an interface might receive built-in RCWs from
     those assemblies. Two kinds of referrer are no longer held against it:
     - an assembly that uses its COM types: it is rewritten with it or left with it. If such a
       "trusted" user is left, the interface's assembly is left too, by the same fixpoint.
     - a clean referrer: it declares no COM types and has no way to make an RCW of its own. That
       means no Marshal RCW maker (GetObjectForIUnknown and kin, Get(Delegate|Function)For...), no
       Type.GetTypeFromCLSID/ProgID, and no P/Invoke whose signature names a COM-declaring
       assembly's types or one of its own delegates. Such a referrer only ever holds the rewritten
       assembly's own objects.
     - app: MediaFoundation's 162 such verdicts are gone (Telerik.Windows.MediaFoundation is
       trusted, Telerik.Windows.Controls.Media is clean). Its 12 remaining reasons are fields of
       class, interface or array type, a System.Drawing.Color by value, and interface or Guid arrays.
   - **Group members that declare no COM types.** An assembly that uses candidates' COM types and can
     make RCWs of its own counts as a referrer that is not clean. It is judged as a forced candidate and
     rewritten with them: its Marshal calls and its P/Invokes with their types become the rewrite's.
     - It is chosen only with a chosen group it uses. If it is left, so is the group.
     - SharpMediaFoundationInterop.WPF is one: GetObjectForIUnknown and a D3D9 P/Invoke.
   - **Built-in interop cannot either.** A member that built-in COM interop cannot marshal either
     (`System.Drawing.Color` by value: "Invalid managed/unmanaged type combination") throws when
     called, as it does there, and does not keep its assembly on built-in COM. Verdicts list such
     members separately.
   - **Awaiting a gate.** `ComImportStep.AwaitingGate` lists assemblies that are rewritable but whose
     first rewrite on Windows waits for an app run that exercises them; the smoke runs do not.
     - They stay on built-in COM, and the verdict says "rewritable, awaiting its gate: ...".
     - An entry leaves the list in its own commit once its gate passes.
     - `WPF_COMIMPORT_FORCE` overrides it.
     - Now listed:
       - IcoServices (Workbench): its Active Directory picker. An IL scan finds nothing that uses its
         two COM interfaces (0 call sites), but a plain Workbench session never loads it (the picker
         does), so it has not yet run rewritten.
     - The released set, run together (cr26, 04:38-04:42, 14 assemblies rewritten in app): bench
       smoke 470 move / 53 resize steps, no settings change; 3D frame 0 px against 15c92fc6e.
     - Progress on them (2026-10-20, 02:59-03:12, after a fresh IcoFwxServer restart; no settings change
       in any run):
       - app bench smoke with Telerik.Windows.Controls forced in, and the Code Pack and RibbonView
         rewritten: Telerik.Windows.Controls and both Code Pack assemblies loaded and rewritten; 437
         move / 77 resize steps.
       - Workbench smoke, the same: Telerik.Windows.Controls and RibbonView (the ribbon) rewritten, clean.
       - 3D frame 0 px against 15c92fc6e with the current rewriter.
       - The smoke parts of their gates have passed; the specific exercises have not.
       - **Telerik.Windows.Controls.RibbonView: gated and released (2026-10-20).** Its COM types are the
         copied WPF Shell-integration ones: ITaskbarList*, ICustomDestinationList, IShellLinkW,
         IObjectCollection, IPropertyStore, and so on. No method uses them except a SHAddToRecentDocs
         wrapper that nothing calls (an IL scan of every body for their members, and for calls whose
         signatures carry them). So its gate is the load and the ribbon: Workbench smoke with it
         rewritten, the ribbon drawn, no exceptions.
       - **Microsoft.WindowsAPICodePack and .Shell: gated and released (2026-10-20).** Besides the
         smoke, a test app on app's own Code Pack DLLs, run stock and under the port with both
         rewritten, gave identical output:
         - a ShellObject's properties through IShellItem2, IPropertyStore and PropVariant (size, type
           text, duration, kind, date modified);
         - a ShellContainer enumerated, and a known folder;
         - NetworkListManager: the coclass methods, and GetNetworks' IEnumerable of INetwork.
       - The Telerik file dialog can't be opened under the port at all. It fails the same way on built-in
         COM: `Window.ShowDialog` -> HwndSource -> `CreateWindowEx` "Invalid window handle", from Telerik's
         chromeless window host. That is a WPF-over-WinForms owner bug of the port, not the rewriter.
         FileDialogs stays held until that is fixed and the dialog has been opened.
       - **Telerik.Windows.Controls: gated and released (2026-10-20).** Its COM is a private copy of
         System.Management (WMI), reached by Telerik's licensing machine id.
         - A test app computing that id (BIOS, disk, processor) failed rewritten, twice:
           - a retyped delegate kept `[MarshalAs(Interface)]` (fa6f0763d);
           - `Activator.CreateInstance` of a rewritten COM class made a plain object (769f3ef97).
         - Now it gives the same result as stock.
         - The app and Workbench smoke runs passed with it rewritten. Telerik's file dialog
           opened and cancelled over app with it and FileDialogs rewritten.
       - **Telerik.Windows.Controls.FileDialogs: gated and released (2026-10-20).** The owner bug was
         fixed (ca27b227e: a WPF window owned by a WinForms driver handle is owned by the form's
         native host window). Then, in app with FileDialogs rewritten (04:27, after a fresh
         IcoFwxServer restart), Telerik's RadOpenFileDialog opened over C:\Windows\Media, browsed
         its folder tree, and was cancelled. No exceptions beyond app's usual ones and the
         tree's access-denied probes of protected folders; no settings change.
       - **MediaFoundation (MF.Net) and Telerik.Windows.MediaFoundation: gated and released
         (2026-10-20).** app has no MF video control: its video control is WPF's MediaElement
         (the port's media backend), and MF.Net's only user is Telerik's RadWebCam. So the gate ran
         a test app on app's own MF.Net and Telerik DLLs, stock and under the port with both
         rewritten:
         - RadWebCam's capture devices and their formats, enumerated only (no camera opened):
           identical. It first failed rewritten: the device names were empty and
           MFCreateDeviceSource returned 0x8007007B. The cause was P/Invokes with C arrays of
           interfaces, which were left to built-in interop (66e97c73c).
         - A local mp4 decoded to RGB32 through IMFSourceReader, both synchronously and with a
           managed IMFSourceReaderCallback (a CCW): all 60 frames identical, by hash and raw dump.
           This first failed because the app's own Marshal.ReleaseComObject threw on a rewritten
           object. Such callers are now group members, and the entry assembly is never rewritten
           (db43c67cc).
         - app bench smoke with both rewritten (cr29, 08:12): 17 assemblies rewritten,
           536 move / 102 resize steps, no exceptions, no settings change.
   - Cost for app (355 assemblies, 14 with ComImport):
     - before this step, judging alone: 3.9 s per prepare;
     - now: 9.2 s on a cold cache (hashing and scanning everything once), 0.93-1.0 s warm.
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
   - **Assemblies loaded by path** (`Assembly.LoadFrom`). They stay as they are.
6. **Callers that ask where a rewritten assembly is (Limit 1, closed by caller rewriting, on every
   OS; the user's decision, 2026-10-20).** For example IcoTrace's `GetCallingAssembly().CodeBase`,
   which GwxRuntimeCore relies on for `Gwx.config.xml` and `Mobile_devices.csv`. An unrewritten
   caller would see an assembly without a file.
   - `LocationCallers` finds such callers from the IL. A caller reads Location, CodeBase,
     EscapedCodeBase, `GetName().CodeBase` or `Module.FullyQualifiedName` of an assembly that is not
     provably its own, or it enumerates `AppDomain.GetAssemblies`. Provably its own means
     `GetExecutingAssembly()`, `typeof(OwnType).Assembly`, `this.GetType().Assembly`, or a local
     assigned once from one of those. `GetEntryAssembly()` is ignored, because the entry assembly
     is never rewritten.
   - A caller joins the rewrite set when it is referenced by a rewritten assembly or calls
     GetAssemblies. This is a closure, so callers of callers are included.
   - A caller is rewritten **for its location calls alone** (`name|path|location` in the list).
     Its own COM types stay on built-in COM.
   - app, measured: 15 of its 355 assemblies ask about assemblies that are not their own.
     - The closure adds five: IcoTrace, IcoCommonCore.Net, Telerik.Windows.Controls, Ab3d.Reader3ds
       and Ab2d.ReaderWmf.
     - Load-time cost: IcoTrace 81 ms, IcoCommonCore.Net 20 ms, Telerik.Windows.Controls 603 ms.
       The COM rewrites cost GwxRuntimeCore 371 ms, DXEngine 189 ms and DXEngine.Wpf 129 ms.
       In total about 1.4 s, spread over first loads.

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
  - **BSTR, VARIANT and SAFEARRAY**, as §6 lists them.
  - **Refused** (`IDispatch` late binding, custom marshallers, and the rest that §6 names): the
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

## 6. Status (2026-10-20)

- **Built:**
  - `WpfMac.ComRewrite` (the rewriter, with Mono.Cecil);
  - `WpfMac.ComInterop` (the runtime, shipped in the overlay);
  - the launcher step (`ComImportStep`). Its verdicts go to `merge-report.txt` and are cached in
    `comimport-verdicts.json`.
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
  - SAFEARRAYs of records (VT_RECORD) or of arrays;
  - a struct in a dispinterface member (it would travel in a VARIANT as VT_RECORD); type information
    (IDispatch::GetTypeInfo answers E_NOTIMPL, as a built-in CCW without a type library);
  - `ref` interfaces;
  - a custom marshaler given to a CCW, an array given to a CCW with no SizeParamIndex/SizeConst, an
    [Out] StringBuilder given to a CCW with no size parameter after it (each stub answers E_NOTIMPL
    and names it); a formatted type with a field of class, interface or non-fixed array type;
  - an IEnumerable or IEnumerator passed in (returned is supported), and given to a CCW.
  - Two more cases are left alone on Windows, because a built-in RCW could reach them and would not
    convert:
    - a cast to a rewritten interface of an object of unknown origin;
    - a COM interface that other assemblies can see.
- **app's folder on Windows (2026-10-20):**
  - 13 assemblies declare COM types. Rewritten for COM: Ab3d.DXEngine, Ab3d.DXEngine.Wpf,
    GwxRuntimeCore, Telerik.Windows.Controls.SyntaxEditor and SharpDX (through the BCL's IEnumString).
  - SharpDX's flip was gated: 3D frame 0 px against 15c92fc6e, bench smoke 495 move / 75 resize steps
    with no settings change, Workbench smoke clean (6 assemblies rewritten there).
  - Left on built-in COM, each for a named reason:
    - MediaFoundation: interfaces other assemblies use, PROPVARIANT classes, StringBuilder;
    - Telerik.Windows.MediaFoundation: uses MediaFoundation's types;
    - WebView2.Core: visible interfaces, IDispatch, ITypeInfo; WebView2.Wpf: a struct by reference;
    - WindowsAPICodePack(.Shell): IEnumerable returns, coclass calls, PropVariant;
    - IcoWindowsFormsSpecialized: uses Mono.System.Drawing's COM types;
    - Telerik.Windows.Controls (WMI: `string[]` by reference, char arrays, delegates called back),
      FileDialogs and RibbonView (by-reference structs, IDispatch, VARIANT, the BCL types above).
- **Added since the first version:**
  - BSTR in, by reference and as retval.
  - **CCWs:** a managed object implementing a rewritten interface can be handed to native code.
    - Each rewritten interface gets a nested `ComCcw` whose `Vtable()` holds `[UnmanagedCallersOnly]`
      stubs. A stub finds the object behind the pointer, converts every supported kind the other way
      round, calls the method, and returns the HRESULT. An exception's own HRESULT is returned, as
      built-in COM does.
    - `WpfMac.ComInterop`'s ComWrappers answers with one entry per rewritten interface the object
      implements.
    - A dual interface's IDispatch slots answer by reflection (see IDispatch below).
    - A method whose signature a stub cannot convert answers E_NOTIMPL. Its class's verdict names it.
    - Judging app's folder now takes 4.5 s per launch, since the stubs are generated while
      judging.
  - VARIANT in (passed by value, as COM does), by reference and as retval (`ComVariant`,
    `Com.ToVariant`/`FromVariant`/`ClearVariant`).
    - Covered: EMPTY, NULL, BOOL, I1–I8, UI1–UI8, INT/UINT, R4, R8, CY, DATE, DECIMAL, BSTR, ERROR
      and UNKNOWN/DISPATCH. Enums go as their underlying type, and `Missing` as
      DISP_E_PARAMNOTFOUND.
    - VT_ARRAY and VT_BYREF are refused by name.
    - With it, Telerik.Windows.Controls.SyntaxEditor and IcoWindowsFormsSpecialized are fully
      supported, but on Windows Limit 1 keeps them: their locations are asked by Telerik.Windows.Controls
      and by IcoTrace.
  - Built-in RCWs adopted at a cast, on every OS.
  - **Limit 1, closed** by caller rewriting (§1.6) on every OS. It replaces the provisional judge
    that had kept such assemblies on built-in COM on Windows.
    - The case behind it: GwxDocument (GwxRuntimeCore) loads Gwx.config.xml from IcoTrace's
      `GetCallingAssembly().CodeBase`. Rewritten, it lost its settings, among them
      `UseStandard3DRenderer`, so app drew its 3D with WPF instead of DXEngine. That was first
      blamed on Ab3d.DXEngine, which is in fact fine rewritten: 0 px against the build before the
      rewriter.
  - `WPF_COMIMPORT_FORCE=<names>` (diagnostics): those are rewritten whatever the verdict.
  - **Formatted types, custom marshalers, StringBuilder** (`Formatted.cs`), as built-in interop
    marshals them in a COM method.
    - `[MarshalAs(LPStruct)]` on a blittable value type is a pointer to a copy. This fixed a real bug:
      MediaFoundation's `Guid guidKey` had been passed by value, which works only by accident on x64.
    - **Formatted classes** (sequential or explicit layout, base classes included) go as a pointer to
      their native layout. They are copied in, and copied back into the same object when `[Out]` or
      blittable (built-in interop pins those, so the callee's writes show).
    - **Non-blittable structs by reference** go through Marshal.StructureToPtr/PtrToStructure, which
      convert strings and nested structs without COM. The buffer is destroyed after the call; a CCW
      writes back with `fDeleteOld`.
    - **C arrays of formatted elements** become one buffer of their layouts.
    - **`char[]`** is UTF-16 in a COM method.
    - **`ICustomMarshaler`** (MediaFoundation's PVMarshaler) follows built-in interop's order:
      GetInstance(cookie) once per type, ManagedToNative, the call, NativeToManaged for an out (by
      value: into the marshaler's remembered object), CleanUpNativeData.
    - **StringBuilder** gets a buffer of its capacity. It is copied in unless `[Out]` alone, and read
      back up to the terminator. UTF-16, or ANSI for `LPStr`.
    - Tests cover every kind forward, and in a CCW where the kind is defined there.
    - app, unsupported members before -> after: MediaFoundation 328 -> 174 (162 of them
      "used by other assemblies"), WindowsAPICodePack.Shell 69 -> 29, RibbonView 18 -> 3,
      Telerik.Windows.Controls 22 -> 9, WebView2.Wpf 1 -> 0 (it stays with WebView2.Core).
      Telerik.Windows.Controls.FileDialogs becomes rewritable and is held for its gate.
  - **The BCL's other COM interfaces, generated.** Every BCL COM interface a module uses, beyond the
    hand-written ones, is covered, together with those their signatures reach (IDataObject reaches
    IEnumFORMATETC, IAdviseSink, IEnumSTATDATA, IMoniker and IEnumMoniker; ITypeInfo reaches ITypeComp
    and ITypeLib).
    - Each gets a ComImpl and a ComCcw generated in the rewritten module (`WpfMac.BclComImpl.*Impl`),
      through the same marshalling as the app's own interfaces. Its signature is imported into the
      module, keeping its marshalling.
    - They are registered with `Com.RegisterGenerated` from the module initializer. ComObject casts
      and the ComWrappers find them there.
    - IDataObject's FORMATETC is blittable. STGMEDIUM's `pUnkForRelease` is an interface field, which
      Com.StructToNative passes itself.
    - A CCW array with no SizeParamIndex or SizeConst is one element, as built-in reverse interop marshals
      it (IEnumFORMATETC.Next's `pceltFetched`).
    - Tests: a native IDataObject (QueryGetData, GetData's STGMEDIUM, EnumFormatEtc -> a native
      IEnumFORMATETC), and a managed IDataObject and IEnumFORMATETC called by native code through the
      generated CCWs.
  - **IEnumerable / IEnumerator returns** (`Enumerable.cs`), as built-in interop's
    EnumerableToDispatchMarshaler and EnumeratorToEnumVariantMarshaler give them. An IEnumerable is the
    object's IEnumVARIANT, asked for directly or through IDispatch's DISPID_NEWENUM. Its elements are
    the VARIANTs it hands out, with ComObjects for interfaces. Both routes are tested.
  - A struct field of type `object` without `[MarshalAs]` is a VARIANT, as built-in struct
    marshalling makes it.
  - **PROPVARIANT housekeeping off Windows** (`PropVariant.cs`). The Win32 shim exports ole32's
    PropVariantClear and PropVariantCopy, and oleaut32's VariantClear, VariantCopy and VariantInit,
    implemented as Windows does them:
    - BSTR, LPSTR/LPWSTR, CLSID, BLOB, CF and VERSIONEDSTREAM are freed or deep-copied; interfaces are
      released or AddRef'd; vectors are handled element by element; arrays through SafeArray.cs.
    - An unknown type gets DISP_E_BADVARTYPE and is left alone. VariantClear refuses the
      PROPVARIANT-only types.
    - The old export only zeroed 16 bytes, which is wrong on 64-bit and freed nothing.
    - The apps' own PropVariant types call these from Dispose (MediaFoundation, the Code Pack).
  - **WebView2:** the overlay replaces the app's Microsoft.Web.WebView2.Core/.Wpf/.WinForms with the
    fork's own port ("replaced ... by the overlay" in merge-report.txt). The vendor assembly is never
    loaded and never judged, and nothing of WebView2 is rewritten.
  - **Structs by value, struct retvals, `ref` classes, interface fields.**
    - A non-blittable struct by value is passed as a generated blittable shadow
      (`WpfMac.Shadows.*`).
      - The shadow has the struct's native size and alignment, computed from the metadata as built-in
        struct marshalling lays it out. Its fields are integers of the alignment's width, so the ABI
        classifies it as the native aggregate.
      - The runtime checks the size against Marshal.SizeOf before every use, so a wrong layout throws
        rather than passing the wrong bytes.
      - A floating-point aggregate (FP registers) is refused.
    - A struct retval goes into the shadow and is converted.
    - A formatted class:
      - by reference (`ref Box`) is T**, as built-in passes it;
      - as a PreserveSig return is a T* that stays the callee's;
      - as an [out, retval] is a caller-allocated T*.
    - `ref StringBuilder` is char**.
    - `out T[]` with SizeParamIndex is the callee's CoTaskMem array. Its length is read after the call
      from the size parameter (by reference or not).
    - A formatted type may contain:
      - an embedded class with layout;
      - an array that is not ByValArray (built-in makes it a SAFEARRAY, as StructureToPtr does);
      - a delegate (a function pointer);
      - an interface, or an object as IUnknown/IDispatch. Com.StructToNative writes and reads these
        itself, through Borrow and Wrap, so StructureToPtr never makes built-in CCWs or RCWs of them.
    - `in` parameters' modreq(InAttribute) is stripped before classifying and emitting.
  - **Other assemblies' COM interfaces** are COM here too (IsCom). Their assembly is rewritten with
    this one (the fixpoint), so their objects are ComObjects:
    - parameters of those types convert;
    - P/Invokes with them are stubbed;
    - casts to them adopt built-in RCWs;
    - a class implementing one uses that assembly's CCW.
  - **A tlbimp coclass's own methods** (NetworkListManagerClass.GetConnectivity, ...) are called as
    the interface method each one implements.
  - **A managed delegate with COM interface parameters** is refused only when it can reach native code:
    the module calls Marshal.GetFunctionPointerForDelegate, or a P/Invoke or COM method takes its type.
    Telerik's WMI delegates, made to throw PlatformNotSupported, never reach native code.
  - Tests: ComUserFixture (no COM types; it casts its own `Marshal.GetObjectForIUnknown` to
    ComImportFixture's ICalc) is judged as a group member and works rewritten with it. On Windows it is
    left with the group.
  - **Gate: SharpMediaFoundationInterop + .WPF** (CsWin32's 155 interfaces, 152 P/Invokes) are
    rewritten as a group. SharpMediaPlayer (big_buck_bunny_480p) and SharpSpatialVideoGUI
    (IMG_7881.MOV) played at full rate (24.1 and 30.0 fps). Paused at the same position, their frames
    are identical (0 px) to the runs with built-in COM.
  - **C arrays of strings, interfaces and VARIANTs** (`CArrays.cs`), `LPArray` with an
    `ArraySubType`.
    - Strings: LPWStr, LPStr or BSTR (BSTR by default in a COM method). Interfaces go as their IID,
      `object` as IUnknown, VARIANT or IDispatch.
    - Elements are converted in, unless `[Out]` alone; read back for `[Out]`; and freed after the
      call (CoTaskMem, SysFreeString, Release, VariantClear).
  - **A CCW's arrays and StringBuilders.**
    - Native code's arrays are given their length by `SizeParamIndex` (that argument, plus
      `SizeConst`) or by `SizeConst` alone. This covers blittable, formatted-struct and element
      arrays: copied in unless `[Out]` alone, written back for `[Out]`, the old elements freed.
    - A StringBuilder's room is the next by-value integer parameter (the cch convention), or else
      the string the buffer holds. Built-in reverse interop cannot know the size either. This choice
      never writes past the caller's buffer, and an `[Out]`-only one never reads it.
  - **Fix:** IsCom of an array, pointer or generic instance resolved to its element. An array of
    COM interfaces would have been passed as one interface. None of the gated assemblies had one in
    a COM signature.
  - **COM events** (`ConnectionPoints.cs`).
    - The BCL's IConnectionPointContainer, IConnectionPoint, IEnumConnectionPoints and
      IEnumConnections are written by hand both ways, as IStream is.
    - **Client side:** a tlbimp-style event interface (`[ComEventInterface(typeof(Source),
      typeof(Provider))]`) gets an I.ComImpl. Each `add_`/`remove_` goes to `Com.EventProvider(this,
      Provider)`: one provider per COM object, made with the object, as built-in interop makes it.
      - The provider is tlbimp's own code. It casts the object to IConnectionPointContainer, finds
        the connection point, and Advises a sink helper.
      - The sink helper is a managed class implementing the source dispinterface, so its CCW answers
        IDispatch.
      - A cast to the event interface asks the object for IConnectionPointContainer (the nested
        `ComEvents` marks it).
    - **Server side:** a managed class with `[ComSourceInterfaces]` gets an IConnectionPointContainer
      CCW entry.
      - Its connection points are made on demand, one per source interface.
      - Advise hooks each .NET event named after a source method. The handler is a compiled
        expression that calls the sink through the source interface: a vtable call, or Invoke for a
        dispinterface.
      - Unadvise unhooks it, and EnumConnections lists the sinks.
    - Test: a managed `[ComSourceInterfaces]` source, seen as a COM object through its own CCW,
      subscribed through the event interface. Every hop goes through native vtables:
      provider -> FindConnectionPoint -> Advise(sink helper) -> the .NET event -> the sink's Invoke
      -> the handler. Unadvise stops the calls.
  - **IDispatch** (`Dispatch.cs`), both ways.
    - **Dispinterfaces** (`InterfaceIsIDispatch`) are called through IDispatch::Invoke.
      - I.ComImpl's bodies box the arguments into an `object[]` and call `Com.DispInvoke` with the
        member's DISPID. The DISPID comes from `[DispId]` on the method or on its property, or else
        from GetIDsOfNames by name, cached per object.
      - Flags: METHOD (plus PROPERTYGET when the method returns a value), PROPERTYGET for a getter,
        and PROPERTYPUT with the named DISPID_PROPERTYPUT for a setter.
      - By-reference arguments go as VT_BYREF|VT_VARIANT and are written back.
      - DISP_E_EXCEPTION becomes a COMException with the server's description and scode (a deferred
        fill-in is called first).
      - The result is converted to the declared type.
      - A cast to a dispinterface accepts an object that answers only IDispatch, as built-in interop
        does. The nested `ComDispatch` type marks the interface.
    - **CCWs:** a managed object's dispinterfaces, and its dual interfaces' slots 3-6, answer
      IDispatch.
      - GetIDsOfNames finds members (case-insensitive) and parameter positions.
      - Invoke goes by reflection over the interface. Members are numbered by `[DispId]`, or else
        0x60020000 + declaration order, as a type library numbers them.
      - Invoke handles positional and named (put) arguments, VT_BYREF written back, omitted optional
        arguments (DISP_E_PARAMNOTFOUND), DISP_E_MEMBERNOTFOUND / BADPARAMCOUNT / TYPEMISMATCH, and
        an exception as DISP_E_EXCEPTION with its message, source and HRESULT in EXCEPINFO.
      - The ComWrappers adds an IDispatch entry: the first such interface's vtable.
    - `object` marshalled as `IDispatch` (in, out, retval) passes the object's IDispatch.
    - Tests:
      - a native IDispatch-only counter: DISPIDs, a property put, VT_BYREF, GetIDsOfNames, and
        DISP_E_EXCEPTION;
      - a managed dispinterface called by native code (event-sink shaped);
      - a managed dual object passed as IDispatch.
  - **SAFEARRAY** (`SafeArray.cs`): an array or `System.Array` parameter of a COM method (COM's
    default, or `[MarshalAs(UnmanagedType.SafeArray)]`), in, `ref`/`out`, as retval, inside a VARIANT
    (VT_ARRAY), and the same in CCW stubs.
    - The descriptor is the SDK's. Its bounds are stored rightmost dimension first and its data is
      column-major.
    - On Windows the arrays are oleaut32's (SafeArrayCreate/SafeArrayDestroy are plain exports, not
      COM), because native callees free and replace them.
    - Elsewhere the same layout is made in managed code, with the VARTYPE 4 bytes before the
      descriptor (FADF_HAVEVARTYPE), as oleaut32 keeps it.
    - Element types: the integers, R4/R8, BOOL, CY, DATE, DECIMAL, BSTR, VARIANT, UNKNOWN and
      DISPATCH. The element VARTYPE comes from `SafeArraySubType`, then from the element type (a COM
      interface: VT_UNKNOWN, or VT_DISPATCH when dual), as built-in interop picks it.
    - `T[]` must be one-dimensional (SafeArrayRankMismatchException otherwise) and is made
      zero-based. `System.Array` keeps the rank and the bounds.
    - In and ref arrays are destroyed after the call, a retval once it is converted.
      A CCW stub frees the caller's old by-reference array when it replaces it, as oleaut32 code
      does.
    - Tests: native code reads the descriptor raw (VT_I4 and FADF_HAVEVARTYPE, a 2-D array's
      column-major order), BSTR retvals, a VARIANT array replaced by reference, a 2x3 VT_R8 matrix in
      a VARIANT, and a managed object's CCW. They run once through oleaut32 and once through the
      managed layout (the AppContext switch `WpfMac.ComInterop.ManagedSafeArrays`).
    - app: nothing flips (Telerik.Windows.Controls' `string[]` by reference is now
      supported, and it still has other reasons).
  - **The BCL's IStream, IBindCtx and IEnumString** (System.Runtime.InteropServices.ComTypes).
    - The rewriter cannot nest a ComImpl or ComCcw in a BCL type, so `WpfMac.ComInterop/ComTypes.cs`
      writes both by hand: a `[DynamicInterfaceCastableImplementation]` interface per type, and a
      vtable of `[UnmanagedCallersOnly]` stubs. `ComObject.ImplOf` and the ComWrappers fall back to
      them.
    - The marshalling matches built-in COM's for these declarations:
      - byte arrays are pinned on a call, and copied (in, and whole back) in a CCW;
      - STATSTG's name is a CoTaskMem string that the receiver frees;
      - IEnumString.Next's strings are CoTaskMem strings that the caller frees;
      - `object` parameters go as IUnknown.
    - `Com.SupportedBclComTypes` is the list the rewriter reads.
    - A rewritten assembly may use these types in its own signatures, P/Invokes (SignatureHasCom)
      and classes (their CCW is WpfMac.ComInterop's).
    - A built-in RCW passed where an interface is expected (Windows) now gives its own native pointer.
    - IBindCtx.GetRunningObjectTable's result casts only when IRunningObjectTable is supported, which
      it is not yet. Otherwise a NotSupportedException names it.
    - Tests: a native IStream (Read in pieces, Stat, Seek, Clone), a managed one read by native code,
      a native IEnumString (Next, Skip, Reset, Clone), a managed one enumerated by native code, and
      ole32's IBindCtx on Windows. Its EnumObjectParam is E_NOTIMPL, so NotImplementedException, as
      built-in COM gives.
  - Visibility judged from what the app's other assemblies actually reference.
  - Assemblies rewritten together with those whose COM types they use (a fixpoint over the app).
  - The in-memory loader, with location redirection.
- **app, Windows (2026-10-20):**
  - Rewritten for COM: Ab3d.DXEngine, Ab3d.DXEngine.Wpf, GwxRuntimeCore and
    Telerik.Windows.Controls.SyntaxEditor.
  - Rewritten as callers: the five of §1.6.
  - Gwx.config.xml and Mobile_devices.csv load, so `UseStandard3DRenderer` is read and the 3D is
    drawn by DXEngine. Its frame is 0 px against the build before the rewriter (15c92fc6e).
  - The bench smoke run and the Workbench smoke run are clean. Workbench rewrites 4 assemblies.
- **Tests:**
  - `ComRewriterTests`: calls, retvals, out interfaces, VARIANT_BOOL, structs by reference, LPWStr,
    HRESULT exceptions, an unsupported member, casts and identity, Release, and DXEngine's
    delegate-through-a-slot call. They run against a native object of the test's own.
  - `ComImportStepTests`: all or nothing on Windows, partial elsewhere, the switch, nothing written
    under the cache, an earlier wpfmac's copies removed.
  - The in-memory load: the fixture reports its original `Location`, `CodeBase`, `EscapedCodeBase`,
    `GetName().CodeBase` and module names. `typeof(X).Assembly == Assembly.Load(name) ==
    GetExecutingAssembly()` holds, and a P/Invoke finds a library beside the original.
  - Caller rewriting: a second fixture (LocationAskerFixture) answers `GetCallingAssembly().CodeBase`
    with the rewritten fixture's original file. The step tests cover the closure, location-only
    entries and the verdict cache (reused when the hash matches, redone when it does not).
- **Still to run:** the Mac head (M6).
