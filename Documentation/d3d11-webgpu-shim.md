# Direct3D 11 on WebGPU: the d3d11 / dxgi / d3d9 vtable shim

An application that renders with Direct3D 11 can run on this stack's WebGPU renderer, on every head,
without changing the application. The motivating case is app's 3D displays. They render with
Ab3d.DXEngine 7.1 on SharpDX 4.2 and present through WPF's `D3DImage`.

This document is the design. The status of each milestone is at the end.

## What has to work

The research behind this is in the memory note `dxengine-d3dimage-research`: IL scans of Ab3d.DXEngine
and SharpDX, the embedded shaders, and a run of a app 3D display under the launcher.

- **SharpDX** reaches D3D11 through exactly two kinds of call:
  - P/Invokes of `d3d11.dll!D3D11CreateDevice` and `dxgi.dll!CreateDXGIFactory1/2`;
  - `calli` through the returned objects' vtables, by slot number.

  It uses no COM interop at all.
- **Ab3d.DXEngine** additionally P/Invokes:
  - `d3d9.dll!Direct3DCreate9Ex` and `D3DPERF_*`;
  - `dxgi.dll!DXGIGetDebugInterface1`.

  It talks to Direct3D 9 through its own `[ComImport]` interfaces, which is built-in COM interop on
  Windows. It hands the resulting `IDirect3DSurface9` to `D3DImage.SetBackBuffer`.
- **DXEngine's use of the API:**
  - about 160 SharpDX types and 430 members;
  - deferred contexts with command lists;
  - queries;
  - staging readback;
  - MSAA resolve and mip generation;
  - 1D, 2D, 3D and cube textures;
  - structured-buffer views;
  - instancing;
  - swap chains;
  - the legacy DXGI shared handle.
- **Shaders:** 170 precompiled DXBC blobs, all Shader Model 4.0 (79 pixel, 49 vertex, 42 geometry), about
  12,000 instructions and about 60 distinct opcodes, with structured flow control and GS `emit`/`cut`.
  Nothing is compiled at run time.
- **WPF's `D3DImage`** is not implemented by the port today. `SetBackBuffer` P/Invokes the native
  milcore (`InteropDeviceBitmap_Create`), which no longer exists, so it throws
  `DllNotFoundException`. app survives the exception and shows an empty 3D area.

## Rules this has to keep

- No Windows Desktop framework dependency.
- No `[ComImport]` of our own. The COM objects here are plain vtables of `[UnmanagedCallersOnly]`
  function pointers in native memory.
- No native DLL that we build. The Windows export images are generated at run time by the existing
  `ShimImage` writer. Off Windows the existing `libwin32shim` carries the exports.
- Cross-platform: the same managed implementation serves Windows, macOS, Linux and the browser.

## Architecture

```
app (SharpDX / Ab3d)            our code
---------------------           ---------------------------------------------------------------
[DllImport d3d11.dll] -------> generated d3d11 image (ShimImage) --jmp--> D3D11CreateDevice
                                                                           (UnmanagedCallersOnly)
calli vtbl[slot] ------------> D3DDevice / D3DContext / D3DResource ... (managed objects)
                                     |
                                     v
                               WebGPU (wgpu-native): buffers, textures, pipelines, passes
                                     |
D3DImage.SetBackBuffer ------> managed InteropDeviceBitmap ---> scene "external image" primitive
```

### 1. Routing the exports

- **Windows.** `WindowsShim` already builds, per covered system DLL, an image whose exports mirror the
  real DLL's ordinals. Each export jumps either to the real function or through a classifier to a
  managed one. It installs the image per assembly with `NativeLibrary.SetDllImportResolver`.

  The D3D modules add a third kind of entry: **always managed**. The stub's jump target is the
  managed function itself, with no classifier, so the call costs one indirect jump. Exports that
  have no managed implementation keep jumping to the real DLL.

  The implementations register the same way WpfMac.Win32's do. The bootstrap finds them by name, in
  `Microsoft.Wpf.Interop.WebGpu`, type `Microsoft.Wpf.Interop.WebGpu.Direct3D.Exports`, method
  `Register`.
- **Which assemblies get it.** Not every app assembly that imports d3d11. The default set is
  `SharpDX*` and `Ab3d.*`. The environment variable `WPF_D3D11_SHIM` changes it:
  - `0` turns it off;
  - `*` means every app assembly;
  - a `;`-separated list of name patterns replaces the default;
  - a leading `+` adds to the default.

  Code that needs the real Direct3D 11 for its own sake keeps it. SharpMediaFoundationInterop's
  CsWin32 D3D11 is the example: Media Foundation's hardware decoders need a real device. It is not
  in the default set.
- **macOS and Linux.** `libwin32shim` gains the d3d11, dxgi and d3d9 exports, and their slots point at
  the same managed functions (`Implementations.RegisterAll`). Built-in COM interop does not exist
  off Windows, so DXEngine's `[ComImport]` D3D9 path cannot run there. DXEngine has a
  WriteableBitmap present path ("Switching to using WritableBitmap"), and milestone 6 probes that it
  falls back to it.

### 2. The COM object model

Every object is a managed class derived from `ComObject`. Its native identity is a block allocated
with `NativeMemory`:

```
[ vtable* ][ GCHandle -> owner ][ interface index ]    one block per exposed interface
```

- `QueryInterface` looks the IID up in the owner's interface list and returns that interface's block.
  A texture, for instance, exposes `ID3D11Texture2D`, `ID3D11Resource`, `ID3D11DeviceChild`,
  `IDXGIResource`, `IDXGISurface` and `IDXGIObject`. Every block answers `IUnknown`, and
  `IUnknown` is always the first block, so COM identity holds.
- `AddRef` and `Release` count on the owner. While the count is above zero the `GCHandle` is strong,
  and the native memory is freed when the count reaches zero.
- **Vtables** are built once per interface from `[UnmanagedCallersOnly]` statics, using the default
  platform calling convention. On x64 and arm64 that is the COM convention on Windows, and it is what
  SharpDX's `calli` uses elsewhere. A slot with no implementation points at one of a numbered family
  of stubs. The stub logs `interface!Method not implemented` once and returns `E_NOTIMPL`, or does
  nothing for a void method. Both are safe for every signature on these ABIs, because the caller
  cleans up the arguments.
- **Slot numbers** come from the Windows SDK headers' MIDL vtables (`d3d11*.h`, `dxgi*.h`, `d3d9.h`).
  They are generated into `Direct3D/Interfaces.g.cs` with each interface's IID, so a slot is named,
  never counted by hand.
- **Exceptions** never cross the boundary. Every export and method body catches, logs and returns
  `E_FAIL`, or `DXGI_ERROR_DEVICE_REMOVED` once the WebGPU device is lost.

### 3. Devices and threads

- `D3D11CreateDevice` returns a `D3DDevice` over a `WgpuContext`. That is the WPF composition device
  of the calling thread when the thread has a dispatcher with a sink, so a texture it renders can
  be composited with no copy, and otherwise a private one.
- The device reports feature level 11_0. The adapter and output descriptions come from the WebGPU
  adapter.
- D3D11 is free-threaded and DXEngine renders from worker threads into deferred contexts. Device
  methods are thread-safe. Each context is used by one thread at a time, as D3D11 requires.
- **Deferred contexts** record their calls into a `D3DCommandList`. `ExecuteCommandList` replays it
  on the immediate context.

### 4. Resources

| D3D11 | WebGPU |
|---|---|
| Buffer (vertex / index / constant / structured) | `GPUBuffer` with the matching usages + CopySrc/CopyDst. Constant buffers are padded to 16 bytes. |
| Texture1D / 2D / 3D, arrays, cubes, mips, MSAA | `GPUTexture` of the same dimension; cube = 2D array with 6n layers. Typeless DXGI formats take the format of their first view. |
| RTV / DSV / SRV / UAV | `GPUTextureView`, or a buffer binding for buffer SRVs |
| Usage STAGING | a `MapRead`/`MapWrite` buffer laid out with 256-byte row pitch. `Map` returns its mapped range and that pitch. |
| Usage DYNAMIC + `Map(WRITE_DISCARD)` | a CPU shadow. `Unmap` uploads it in command order (below). |
| `UpdateSubresource` | an upload buffer copied in command order |
| `CopyResource` / `CopySubresourceRegion` | `copyTextureToTexture`, `copyTextureToBuffer`, `copyBufferToBuffer` |
| `ResolveSubresource` | a render pass with a resolve target |
| `GenerateMips` | a blit chain (one render pass per level) |
| Queries | event = submission done, occlusion = occlusion query set, timestamps where the adapter has them, else 0 with `D3D11_QUERY_DATA_TIMESTAMP_DISJOINT.Disjoint = TRUE` |

**Command order.** WebGPU's `queue.writeBuffer` lands before everything in the next submission, but
D3D11's update-then-draw must stay ordered. So every upload issued between draws is a
copy-from-upload-buffer recorded in the open command encoder, never a queue write.

**Readback.** `Map` of a staging resource submits pending work and waits on the map with
`wgpuDevicePoll(wait)`. `D3D11_MAP_FLAG_DO_NOT_WAIT` returns `DXGI_ERROR_WAS_STILL_DRAWING` while it
is still pending.

### 5. State to pipelines

- The context keeps D3D11's state as D3D11 has it: IA, shaders, constant buffers, SRVs and samplers per
  stage, rasterizer, blend, depth-stencil, render targets, viewports and scissors.
- At a draw, the state that WebGPU bakes into a pipeline forms a key, cached in a dictionary:
  - the shaders;
  - the input layout plus vertex strides;
  - topology class;
  - rasterizer;
  - blend and sample mask;
  - depth-stencil;
  - render-target and depth formats;
  - sample count.
- Dynamic state (viewport, scissor, blend factor, stencil ref, vertex and index buffers) is set on the
  pass.
- One render pass stays open while the bound targets are unchanged. Binding other targets, a clear, a
  copy, a `Map` or a `Flush` ends it.
- **Clears** are passes with `loadOp = clear`. A clear of a view that is bound in the open pass ends
  the pass first.
- **Bindings.** Each stage's constant buffers, SRVs and samplers go to one bind group per stage, at
  WGSL `@group(stage) @binding(slot)` with offsets for each kind of binding. The translator emits the
  same numbering.
- **Semantic differences handled here:**
  - D3D's pixel centres and depth range already match WebGPU: [0,1] depth, top-left origin.
  - The viewport y axis is the same.
  - Triangle fans do not exist in D3D11.
  - Strips with a cut index map to `stripIndexFormat`.

### 6. Shaders: DXBC to WGSL

`DxbcTranslator` follows `D3D9ShaderTranslator`:

1. Read the `ISGN`/`OSGN`/`PCSG` signatures, `RDEF` (constant-buffer layouts, for naming only) and
   the `SHDR`/`SHEX` token stream.
2. Decode every instruction into an operand model: register file, index dimensions with relative
   addressing, swizzle and mask, modifiers, `_sat`, and immediates.
3. Emit WGSL:
   - `array<vec4<f32>, N>` for temporaries and indexable temporaries, so bit-casts are explicit;
   - integer operations through `bitcast`;
   - constant buffers as `array<vec4<f32>, N>` uniform bindings;
   - structured control flow (`if`/`else`/`loop`/`breakc`/`switch`) directly.
4. Cache the result by a hash of the bytecode, and the shader module by the hash plus the input
   layout it was linked against.

Shader Model 5 adds UAVs, compute and more integer opcodes. These follow on demand, behind the same
operand model.

**Geometry shaders.** WebGPU has no geometry stage, so a draw with a GS runs in three steps:

1. A compute pass runs the vertex shader and the geometry shader. The GS is translated to a compute
   kernel with an invocation per input primitive, and `emit` and `cut` append vertices to a storage
   buffer. The buffer is sized by `dcl_maxout`, and strips are re-emitted as lists.
2. An indirect-args buffer receives the vertex count.
3. The draw becomes a plain `drawIndirect` with a pass-through vertex shader.

DXEngine's 42 geometry shaders draw thick lines, wireframe and particles, and all of them are small.

### 7. Direct3D 9Ex and presentation

DXEngine's default present path is this sequence:

1. D3D11 texture with the legacy shared flag.
2. `IDXGIResource::GetSharedHandle`.
3. `Direct3DCreate9Ex`, then `CreateDeviceEx`.
4. `CreateTexture(..., &sharedHandle)`.
5. `GetSurfaceLevel`.
6. `D3DImage.SetBackBuffer`.

The shim returns its own objects at every step:

- **Shared handles** are tokens in a process-wide table that maps a handle to a texture.
- **The D3D9 shim** (`Direct3DCreate9Ex`, `IDirect3D9Ex`, `IDirect3DDevice9Ex`, `IDirect3DTexture9`,
  `IDirect3DSurface9`) resolves such a token back to the same WebGPU texture.
- **On Windows** DXEngine's `[ComImport]` wrappers sit over these vtables through built-in COM. The
  QueryInterfaces the runtime makes for `IMarshal`, `IAgileObject` and the like return
  `E_NOINTERFACE`.
- **Off Windows** there is no built-in COM. DXEngine's WriteableBitmap path is expected to serve there;
  milestone 6 verifies this.

### 8. D3DImage in the port

`D3DImage` is needed by more than DXEngine: SharpMediaFoundationInterop's `GpuMediaControl` and any
app that renders into it. The native `InteropDeviceBitmap` P/Invokes are replaced by a managed
implementation. The DUCE commands `MILCMD_D3DIMAGE` and `MILCMD_D3DIMAGE_PRESENT` become a scene
primitive, `ExternalImage`, that names a WebGPU texture, a version and a dirty rectangle.

- **The surface is the shim's** (`IDirect3DSurface9` from section 7): the primitive references the
  shim texture directly, so there is no copy. The present event (`_canWriteEvent`) is set once the
  renderer has sampled that version.
- **The surface is a real Direct3D 9 surface** (Windows): it is read through its vtable (`GetDesc`,
  `GetDevice`, then `CreateOffscreenPlainSurface(SYSTEMMEM)`, `GetRenderTargetData`, `LockRect`) and
  uploaded with `queue.writeTexture` into the primitive's texture. This is WPF's own software-
  fallback copy, done for every present.
  - wgpu-native's C API has no way to import an external D3D texture, so a zero-copy path for
    native surfaces is not possible without native code.
  - `IsFrontBufferAvailable` stays true and `enableSoftwareFallback` makes no difference.
- **First-class GPU images.** A port-only `WebGpuImage : ImageSource` exposes the composition device and
  a texture, with D3DImage's lock semantics, for new code that renders with WebGPU directly. It is
  the same `ExternalImage` primitive.

## Milestones

Each milestone is one or more commits with tests. The WgpuInterop, WinForms and pixel gates stay
green throughout.

| | Scope | Test |
|---|---|---|
| M1 | Device, immediate context, buffers, 2D textures, RTV, input layout, the VS/PS subset of the translator, clear plus triangle, staging `Map` | WgpuInterop.Tests `Direct3D/*`: readback against expected pixels, and the same calls on real D3D11 WARP on Windows (same slots, same results) |
| M2 | D3DImage: shim surface (no copy) and real D3D9 surface (copy); D3D9Ex shim | minimal D3DImage sample against stock; SpatialVideoConverter's GpuMediaControl plays under the launcher |
| M3 | Full SM4 translator plus GS emulation | all 170 DXEngine shaders translate and pass WebGPU validation; pixel tests of representative ones against WARP |
| M4 | DXEngine in a plain WPF test app (box, lines, PBR material) under the launcher | frame against stock WPF with real D3D11 |
| M5 | app 3D display with the shim | frame of `ThreeD.gdfx` plus models |
| M6 | Off Windows (Mac head, or the dx12/WARP/GL backends), plus the WriteableBitmap fallback probe | the M1/M3 tests on each backend; probe log |

## Status

- **M1:** device, immediate context, buffers, 2D textures, render-target views, input layouts, the
  vertex/pixel subset of the shader translator, clear plus a triangle, and staging `Map`.
  `Direct3D/D3D11ShimTests` (WgpuInterop.Tests) checks the readback against expected pixels and
  against real D3D11 WARP on Windows through the same vtable slots.
- **Not wired yet:** the d3d11/dxgi entries in the launcher's export images (Windows) and in
  `libwin32shim` (other heads). Until then the shim is reached only through its managed entry points.
