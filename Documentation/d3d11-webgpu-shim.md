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
- **Which assemblies get it: every app assembly, on every OS** (the user's decision, 2026-10-19: one
  implementation, ours, on macOS, Linux and Windows alike). Every d3d11, dxgi, d3d9 and
  d3dcompiler_47 import of an app assembly lands in the shim, on Windows too, where the real Direct3D
  is present. `WPF_D3D11_SHIM` remains as a diagnostic and as the tests' oracle switch, not a product
  mode:
  - `0` (or `off`) gives Windows' real Direct3D back, the comparison runs of the milestones;
  - a `;`-separated list of name patterns (`SharpDX*;Ab3d.*`) narrows it to those assemblies;
  - unset, `all` or `1`: every app assembly.

  An app that hands the shim's device to native code -- Media Foundation through
  `IMFDXGIDeviceManager`, as SharpMediaFoundationInterop does -- has that native code call the
  shim's vtables (section 8, video).
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

- **The surface is the shim's** (`IDirect3DSurface9` from section 7): **zero-copy.**
  - **Decided (2026-10-19, the user, replacing "the copy path stays"): device sharing, per UI thread**
    (`Composition/ThreadGpu.cs`). Each UI thread has one WebGPU device, and these all use it:
    - the thread's WPF sink. The sink asks for the device from its render thread, but it is
      the owner UI thread's device, and it is made on the UI thread when the sink is constructed;
    - the thread's WinForms windows and popups, which also composite an ElementHost's WPF scene;
    - the Direct3D shim devices made on the thread (`D3D11CreateDevice`, `CreateDeviceEx` of
      Direct3D9Ex).
  - A thread that composites nothing (a decoder's worker) uses the primary device for its shim
    devices: that is the first WPF sink thread's device. Its shared handles then still meet the
    D3DImage on the UI thread.
  - Shared resources never cross devices. `OpenSharedResource` and the D3D9 shared-handle open
    refuse a texture of another device, with a log line. A shim surface shown on another thread's
    compositor takes the copy path.
  - Every device is made as the shim wants it (`D3DDevice.CreateGpu`): the adapter's limits, the
    features D3D11 takes for granted, and validation errors logged rather than fatal. The last user
    of a device lets go of it.
  - `WPF_D3DIMAGE_SHARE=0` gives the shim devices a device of their own.
  - This replaced a first version in which the first compositor to ask took the shim's single
    device. In app that was the splash screen's thread, so DXEngine's frames, made on the main
    thread, never shared a device with the window that showed them.
  - A present (`IMilCompositionSink.SendSurface`, made under D3DImage's lock) flushes the shim's
    immediate context, then on the one queue runs a pass that un-premultiplies (to the integer, as the
    copy path does) or, for X8R8G8B8, makes the surface opaque. The pass writes into a scratch texture,
    and the dirty rectangle is copied from there into the image's texture. No readback, no upload.
  - **The image's texture is the back buffer's front buffer**, as milcore's `CInteropDeviceBitmap`
    keeps one (`ExternalImage`, keyed by `ManagedInteropBitmap.Key`):
    - it is kept across frames of one size, so a dirty rectangle is copied alone;
    - a D3DImage resource that leaves its channel and comes back in the same batch (its visual
      re-rendered) still shows the last frame;
    - it is let go of when no resource shows it at the end of a batch, or on a resize.
  - A synchronous channel (RenderTargetBitmap, printing) always takes the whole back buffer, as
    `CopyBackBuffer` does, and leaves the dirty rectangle to the window's channel.
  - **Inside an ElementHost** the WinForms window draws the hosted WPF scene itself, on its thread's
    device, which is the WPF sink's device too. Two rules keep this safe:
    - While some WinForms window draws with another thread's device (a splash screen still up),
      `SendSurface` and `SendBitmapRegion` decline, and D3DImage sends the whole bitmap, which any
      device can upload.
    - The renderer draws an `ExternalImage` only on the device that made it (`ExternalImage.Device`),
      and draws its transparent placeholder anywhere else.
    - Before these rules, app with `WPF_D3D11_SHIM=0` aborted in wgpu ("TextureView no longer
      alive"), and with the shim its 3D view stayed white.
  - The scene carries the texture itself (`ImageBrush.External`, with a version), so there is no
    content hash and no image-cache lookup.
  - `D3DImageZeroCopyTests` covers:
    - the zero-copy path is taken (no readback counted), translucent and X8R8G8B8;
    - in a window, the next frame copies its dirty rectangle only;
    - the pixels are identical to the copy path's over an alpha and colour gradient;
    - a second UI thread falls back to the copy path;
    - the app releasing its surface and device mid-frame is harmless, and so are a resize and
      `SetBackBuffer(null)`.
- **The surface is a real Direct3D 9 surface** (Windows): it is read through its vtable (`GetDesc`,
  `GetDevice`, then `CreateOffscreenPlainSurface(SYSTEMMEM)`, `GetRenderTargetData`, `LockRect`) and
  uploaded with `queue.writeTexture` into the primitive's texture. This is WPF's own software-
  fallback copy, done for every present. Only the dirty rectangle is read and written
  (`SendBitmapRegion`), into the same front-buffer texture; a new texture takes the whole frame first.
  - wgpu-native's C API has no way to import an external D3D texture, so a zero-copy path for
    native surfaces is not possible without native code.
  - `IsFrontBufferAvailable` stays true and `enableSoftwareFallback` makes no difference.
- **First-class GPU images.** A port-only `WebGpuImage : ImageSource` exposes the composition device and
  a texture, with D3DImage's lock semantics, for new code that renders with WebGPU directly. It is
  the same `ExternalImage` primitive.
- **Measured (2026-10-19, the shim, off screen, muted, 10 s after a 6 s warm-up).**
  - SpatialVideoConverter's player shows IMG_7881's two 1080p HEVC eyes as one 3840x1080 picture at
    30 fps.
    - The copy path managed 6.7-10.7 fps at 3.1-3.3 cores. The compositor's render thread was
      saturated: SendBitmap took 22%, and the image cache's GetOrCreateImageView took 16% (it looks at
      every byte of the 16 MB frame).
    - Zero-copy: 30.0-30.1 fps, the video's own rate, at 2.4-2.6 cores; what is left is the CPU decode
      and the app's upload.
    - The paused frame is identical to the copy path's, to the pixel.
  - SharpMediaPlayer, Big Buck Bunny (480p H.264, 24 fps): 24.1 fps, 0.36 cores, both ways (the frame is
    small); the frames are identical.

**Video (2026-10-19).** WebGPU has no video decode. The shim's device answers
`ID3D11VideoDevice` and its context `ID3D11VideoContext` as a **video processor only**:
- No decoder profiles. Media Foundation's H.264 decoder, handed such a device through
  `IMFDXGIDeviceManager`, refuses it (`MFT_MESSAGE_SET_D3D_MANAGER`: E_NOINTERFACE without the video
  interfaces, E_FAIL with no profiles or no H.264 one; `MediaFoundationProbeTests`), and decodes on the
  CPU to system memory; the app then uploads the frames. Reporting a profile would mean decoding
  DXVA's bitstream buffers ourselves.
- NV12, P010 and P016 textures are stored as their bytes lie in memory (`D3DTexture2D.PlaneRows`: the
  luma rows, then the interleaved chroma rows), so `Map`, `UpdateSubresource` and
  `CopySubresourceRegion` (a box in luma texels covers both planes) are D3D's layout.
- `VideoProcessorBlt` is a render pass of the shim's own (D3DVideoProcessor.cs): background over the
  target rectangle, the stream's source rectangle over its destination rectangle, rotated, sampled
  bilinearly with chroma centred between the luma texels, converted by the stream's matrix (BT.601,
  BT.709; BT.2020 through the DXGI colour spaces) and range. `D3DVideoProcessorTests` compares it with
  the hardware's video processor (Parallels' D3D11; WARP has none): scaled, rotated, rectangles and
  P010 within a code (mean 0.02-0.06); 1:1 within two codes (mean 0.56, the hardware's 1:1 chroma
  filter is not its scaling one); the matrices exact (flat chroma: a code, mean 0.05). The hardware
  reads a 16-bit texel as a fraction of 65535 with the 8-bit ranges, and the shim does the same. It
  ignores the output's RGB_Range bit (limited RGB); the shim follows the documented meaning.

### 9. Translating any application's shaders

Translation has to work for any application's shaders, not only for the shaders we have tested. Coverage comes
from the bytecode specification (the DXBC token format and the SM4/SM5 instruction set), and the
corpora check that coverage.

**The container.** Every chunk is read:

- `SHDR`/`SHEX`, the token stream;
- `ISGN`/`OSGN`/`PCSG` and their 5.0/5.1 forms `ISG1`/`OSG5`/`OSG1`/`PSG1`;
- `RDEF`, the resource bindings and constant-buffer layouts, also used by `D3DReflect`;
- `STAT` and `SFI0`, the required features (doubles, 64 UAVs, typed UAV loads, ...);
- `IFCE`, class linkage.

An unknown chunk is logged once and skipped.

**The decoder.** It decodes every opcode with its operands:

- operand types, 0–3 index dimensions with immediate, relative or combined addressing;
- swizzle, select and mask modes, negate/abs modifiers, `_sat`;
- extended opcode tokens: sample offsets, resource dimension and return type, `precise`;
- every declaration;
- the immediate constant buffer;
- interface and function tables.

The decoder is table-driven, one row per opcode: its operand count and kinds, which of them are
destinations, and the opcode's class. So nothing can be skipped silently. An opcode with no emitter
fails the translation and names itself.

**The emitter, by area.**

- **ALU.** Float, integer and bit operations, conversions, f16 pack/unpack, the `msad`/`bfi`/`ubfe`
  family, and `swapc`. Registers stay `vec4<f32>` bits, with `bitcast` at integer operations.
- **Doubles.** `shader-f64` when the adapter has it, which wgpu has natively on Vulkan/Metal/DX12.
  Otherwise an emulation with two f32 values, logged once as lower precision.
- **Control flow.** `if`, `loop`, `switch`, `break`/`continue`/`ret` with their conditional forms,
  `call`/`callc` (functions), and `discard`.
- **Resources.**
  - Textures of every dimension, including multisampled and arrays.
  - `sample`, `_b`, `_l`, `_d`, `_c`, `_c_lz`, `gather4` with offset and compare variants, `ld`, `ld_ms`.
  - `resinfo`, `sampleinfo`, `samplepos`, `lod`.
  - UAVs: typed `RWTexture*`, raw and structured buffers, loads and stores, and every atomic and
    immediate atomic (on `atomic<u32>` storage). WGSL has no texture atomics: an R32 typed UAV
    texture used atomically is bound as its mirror, a buffer of the view's texels (a 256-byte header
    with the view's size, rows 256 bytes apart, one scratch word for accesses outside the view),
    which the device copies in before the dispatch and back after.
  - Append/consume counters, as atomics on a hidden counter buffer.
  - Groupshared memory (`var<workgroup>`), barriers (`sync_*`), and derivatives, including coarse and
    fine.
- **Interpolation modifiers.** linear/centroid/sample/noperspective/constant become
  `@interpolate(...)`.
- **System values.** `SV_*` map to builtins where WebGPU has them. The rest are emulated:
  - `SV_PrimitiveID` from the vertex index, or from the GS/HS expansion.
  - `SV_RenderTargetArrayIndex` and `SV_ViewportArrayIndex` through the GS path, by layered passes.
  - `SV_CullDistance` (WGSL has none): each vertex sends the pixel shader, per distance, 0 when its
    distance is negative and 1 when not, interpolated linear and centroid. Over a primitive where
    every vertex sent 0 that is exactly 0 (a sum of zeros), and inside any other it is above 0, so
    the pixel shader discards where it is 0. That culls the whole primitives D3D culls and keeps
    whole the ones it keeps. Without a pixel shader, or with no inter-stage variable left (WebGPU
    counts front_facing, sample_index and primitive_index against the 16, so front_facing is now
    declared only when read), each cull distance becomes a clip distance of -1 at a vertex whose
    distance is negative and +1e30 at any other: a primitive whose vertices are all negative is
    clipped away whole, and any other keeps everything but a sliver within 1e-30 of its negative
    vertices, which in float is nothing. That needs the clip-distances feature and at most 8 clip
    and cull distances together; past that it is not applied, with a log line. WARP tests: no pixel
    shader (a depth-only pass, from the vertex shader and from a geometry shader), and a pixel
    shader with 16 inputs. A clip or cull distance the pixel shader reads is linked like any
    value.
- **Stages.**
  - VS, PS and CS natively.
  - GS through the compute expansion (section 6).
  - HS/DS through compute: the hull shader's control-point and patch-constant phases run as compute
    kernels. A managed port of the D3D11 fixed-function tessellator (integer, pow2 and fractional
    partitioning; triangle, quad and isoline domains) runs on the GPU as a compute pass. The domain
    shader runs as the vertex shader of the draw that follows.

**What WebGPU cannot express.** Each case has a defined fallback and one log line, and never wrong
code:

- doubles without `shader-f64` (emulated exactly in integer arithmetic; see Status);
- `SV_ViewportArrayIndex` beyond 1 (layered passes);
- class linkage (each interface call site is specialized at bind time from the bound class
  instances);
- 64-bit atomics (refused, with the reason);
- stencil reference output from PS (refused);
- conservative rasterization (ignored).

A refused shader makes its draw a no-op and logs why. It does not abort.

**DXIL (SM6, DXC's output).** The input is LLVM 3.7 bitcode in a `DXIL` container chunk:

1. A managed bitcode reader: blocks, abbreviations, the value/type/metadata tables.
2. The `dx.op` intrinsics, about 250.
3. A structurizer for the unstructured SSA control-flow graph (a stackifier, as Binaryen and naga's
   SPIR-V reader do).
4. Emission through the same WGSL back end as DXBC.

SM6 wave intrinsics map to WGSL subgroups where the adapter has them; otherwise the shader is
refused. The alternative is a native dxil-spirv (Valve) with naga's SPIR-V front end. It is smaller
to write, but it means a native dependency for every head, which the rules here keep out.

**Runtime compilation.** Applications call `D3DCompile`/`D3DCompileFromFile` and `D3DReflect`.

- **Every OS, Windows included (the user's decisions, 2026-10-19):** the d3dcompiler_47 exports are
  the shim's. `D3DCompile`, `D3DCompile2` and `D3DCompileFromFile` answer `E_FAIL` with one clear log
  line until there is a compiler of our own; there is no DXC package and no managed HLSL front end.
  An app that compiles HLSL at run time ships the DXBC instead: SharpMediaFoundationInterop now embeds
  its renderer's shaders, compiled ahead of time by fxc (branch `features/precompiled-shaders` of that
  repo; the bytecode is byte for byte what D3DCompile made of the same source). `D3DReflect` is
  managed (D3DShaderReflection): ID3D11ShaderReflection read from the blob's chunks (RDEF,
  signatures, STAT, SFI0, the declarations). It matches d3dcompiler_47 field for field over the
  corpus and DXEngine's shaders (D3DReflectTests): descriptions and instruction counts, constant
  buffers, variables with defaults, type trees with class-linkage subtypes, bindings, parameters,
  and lookups of names that are not there.
- Weighed and set aside: `D3DCompile` through DXC as an optional native package (its DXIL taken by
  the DXIL path), and a managed HLSL front end (about 30k lines to reach fxc's dialect).

**Tests for generality.**

- **Corpora, checked in as bytecode with their sources.**
  - Per-opcode HLSL tests written here, one small shader per instruction family and stage, compiled
    with fxc (`/T *_4_0`, `*_4_1`, `*_5_0`) by a script and checked in as data.
  - Microsoft's samples, MIT-licensed (DirectX-Graphics-Samples, the D3D11 samples), compiled the
    same way.
  - DXEngine's 170, from the application, through `DXBC_CORPUS` (not checked in).
- **Every shader** is translated, its WGSL validated by naga at module creation, and its pipeline
  created.
- **Representative shaders run** against WARP, pixel- or buffer-exact within half-float tolerance:
  - VS/PS through `DxbcPixelParity`: inputs from reflection, a generated partner stage, point
    sampling;
  - CS through UAV buffers;
  - GS and HS/DS through the generated partner.
- **An opcode-coverage report.** Every opcode in every corpus is counted against the emitter table.
  The test fails on any that is unhandled, and lists the ones the corpora never exercise.

**Size.**

| Part | Lines (approx.) |
|---|---|
| DXBC decoder, all chunks and opcodes | 1,500 |
| Emitter: ALU, integer, doubles, control flow | 2,000 |
| Resources, UAVs, atomics, groupshared, sampling | 2,000 |
| CS on the device side (dispatch, UAV binding, counters) | 800 |
| GS compute expansion, translator and runtime | 1,500 |
| HS/DS: the tessellator port and runtime | 3,000 |
| Fallbacks: class linkage, viewport arrays, f64 emulation | 1,000 |
| DXIL: bitcode reader, dx.op, structurizer | 7,000 |
| Managed D3DReflect (DXC dropped) | 400 |
| Tests, the per-opcode corpus, the coverage report | 3,500 |
| **Total** | **about 23,000** |

**Order.**

1. The table-driven decoder and the coverage report (they say what is missing).
2. ALU and resources.
3. CS and UAVs.
4. GS.
5. HS/DS.
6. Doubles and the fallbacks.
7. DXIL.
8. Managed D3DReflect, and `D3DCompile` answering `E_FAIL` off Windows.

Each step is its own commit with its corpus tests.

## Milestones

Each milestone is one or more commits with tests. The WgpuInterop, WinForms and pixel gates stay
green throughout.

| | Scope | Test |
|---|---|---|
| M1 | Device, immediate context, buffers, 2D textures, RTV, input layout, the VS/PS subset of the translator, clear plus triangle, staging `Map` | WgpuInterop.Tests `Direct3D/*`: readback against expected pixels, and the same calls on real D3D11 WARP on Windows (same slots, same results) |
| M2 | D3DImage: shim surface (no copy) and real D3D9 surface (copy); D3D9Ex shim | minimal D3DImage sample against stock; SpatialVideoConverter's GpuMediaControl plays under the launcher |
| M3 | A general DXBC translator: SM4.0/4.1/5.0 by the specification, all stages (section 9) | the corpus suites of section 9; DXEngine's 170 shaders are one corpus among several |
| M3b | DXIL (SM6) input; runtime HLSL compilation (D3DCompile and D3DReflect) | section 9 |
| M4 | DXEngine in a plain WPF test app (box, lines, PBR material) under the launcher | frame against stock WPF with real D3D11 |
| M5 | app 3D display with the shim | frame of `ThreeD.gdfx` plus models |
| M6 | Off Windows (Mac head, or the dx12/WARP/GL backends), plus the WriteableBitmap fallback probe | the M1/M3 tests on each backend; probe log |

## Status

- **M1:** device, immediate context, buffers, 2D textures, render-target views, input layouts, the
  vertex/pixel subset of the shader translator, clear plus a triangle, and staging `Map`.
  `Direct3D/D3D11ShimTests` (WgpuInterop.Tests) checks the readback against expected pixels and
  against real D3D11 WARP on Windows through the same vtable slots.
- **M2:** done.
  - D3D9Ex shim (095d5e223) and managed D3DImage on the copy path (f087d629c), with sharing both ways.
  - The launcher routes d3d11/dxgi/d3d9/d3dcompiler_47 (`WPF_D3D11_SHIM`, default every app assembly
    on every OS since 2026-10-19; it was `SharpDX*;Ab3d.DXEngine*`) and fills `libwin32shim` elsewhere.
  - Shim surfaces are copied on the GPU (zero-copy) since 2026-10-19, by the user's decision
    (section 8).
- **M5 (2026-10-19):** app 3D works with the shim. The display has three `Box3D`s and a
  `Line3D` in a `Gwx3DControl`, with Diffuse+Specular and Emissive materials. Everything ran off
  screen, and frames were taken from the compositor.
  - **The shim against real D3D11 + D3D9** (Parallels hardware, `WPF_D3D11_SHIM=0`) differs in 120
    pixels, by at most one code. Before the fixes below it differed in about 30,000, every one of
    them antialiasing:
    - DXEngine's `DeviceCapabilities` probes 2 samples first and stops at the first refusal, so
      the shim's "1 and 4 only" gave it no MSAA at all. The shim now answers 2 and backs it with
      4 samples; `Desc` keeps 2. This is a deliberate approximation: an app that asks for 2 samples
      gets 4-sample antialiasing (app: within one code over 120 pixels of real D3D11). A
      Texture2DMS read's sample count is then 4.
    - `ResolveSubresource` into a texture that is not a render target goes through a texture of
      the shim's own, then a copy. Before, it was a wgpu validation abort.
    - On wgpu's GL backend, a multisampled texture made sampleable is a GL texture and keeps
      nothing drawn into it. It is now sampleable only when the app made it a shader resource.
      One that is still resolves empty on GL; the `D3DMultisampleTests` case is skipped there, and the
      gap and a possible emulation are in `Direct3D/Corpus/known-gaps.txt`.
    - A clear of a view the app releases at once (the clear still applies) left a freed view in
      the pending clears. The context now holds the view until the clear is encoded.
  - **`PreferredRendering=Compatible`** (WPF's own 3D) against real D3D11 differs in 2,650 pixels,
    and only 3 of them by more than 64 codes. All are edge pixels: the tube's highlight, and a grid
    line DXEngine draws through a box.
  - **No E_NOTIMPL and no refused shader** appear in the shim's log.
  - **Interaction:** the camera orbits every WPF frame for 8 s, and the main window's presents are
    counted, along with the frames that changed the 3D view.
    - With one device per window, the frame went as pixels inside an ElementHost: shim 62 fps,
      real D3D11 89, Compatible 91. The shim paid for reading each frame back.
    - With one device per UI thread (section 8), zero-copy inside the ElementHost: shim 77 and 73
      fps, real D3D11 77 (two runs, with the demo licence's countdown on screen).
    - Open: one run before the device fix froze the 3D view after 2.6 s. The demo licence then
      expired, so this is not rechecked.
  - **PBR:** app's XAML takes WPF materials only. DXEngine's PBR material is not reachable
    from a display.
- **M3 (in progress):**
  - DXEngine's vertex and pixel shaders compile, and all of them draw as WARP draws (5b48ce73e).
  - The general translator of section 9: the whole token format, every chunk, an opcode-coverage
    ratchet (bf5f1a69a, 98a31b0ee); compute, UAVs, atomics and every texture form on the device,
    executed against WARP word for word (1270e25ca).
  - Geometry shaders through the compute expansion of section 6: the vertex shader runs as compute
    and fetches its own vertices (every vertex format, unaligned elements, per-instance data), the
    GS runs as compute into fixed per-invocation slots (D3D's order, no atomics), strips stay strips
    through primitive restart, and a pass-through vertex shader feeds the pixel shader. All 47
    corpus geometry shaders (DXEngine's 42 and the own corpus) draw as WARP draws. Measured on
    WARP and followed: a triangle strip's odd triangles reach the GS as (n, n+2, n+1); every
    stream is rasterized when the GS has no stream output; SV_VertexID is the index as stored and
    SV_InstanceID counts from 0 (the native vertex stage now subtracts WebGPU's base vertex and
    start instance too).
  - Hull and domain shaders: the VS and both hull kernels (control points; fork and join phases)
    run as compute, the tess factors come back to the CPU, a port of Microsoft's reference
    tessellator (MIT, D3DTessellator) makes the points and primitives, and the DS draws them as
    the vertex shader. The tessellator matches WARP bit for bit over stream output (288 cases:
    every domain, partitioning and output primitive, fractional, clamped and culled factors), and
    tessellated draws match WARP's pixels. Cost: the factor read-back makes such a draw wait for
    the GPU (patterns are cached); a GPU tessellator is the later step.
  - Stream output, DrawAuto and the indirect draws. A GS made with
    CreateGeometryShaderWithStreamOutput writes its stream 0 through three kernels (count each
    invocation's primitives, scan for their places and for what fits, write): strips cut into lists,
    whole primitives only, appended at each buffer's filled size (a GPU counter that SOSetTargets
    sets). A vertex shader handed in a GS's place streams out the input assembler's primitives. Both
    match WARP word for word (gaps, partial components, two slots, appending, overflow) to an ulp
    or two of float (this GL driver over Metal reassociates adds). DrawAuto and the indirect draws
    through the compute paths read their counts back; a native indirect draw stays on the GPU, except
    on an adapter without indirect-first-instance (this one), which reads back too. Measured on
    WARP and followed: a non-indexed draw's SV_VertexID counts from 0, not from the start vertex.
  - Strip-cut indices into a GS (an ia_scan table of the strips' primitives); layered and
    viewport-array rendering from a GS (a pass per slice and viewport; a slice past the array is
    slice 0, a viewport past the viewports draws nothing, as WARP does); a GS after tessellation
    (the domain shader as compute over the tessellator's points, the GS over its primitives).
  - Texture uploads on GL: wgpu-hal's GL queue leaves a storage buffer bound to
    GL_PIXEL_UNPACK_BUFFER after a storage-to-copy barrier, and every writeTexture after it reads
    that buffer instead of its data. The context clears the binding after each submission that
    wrote storage (D3DContext.ResetGlUnpackBinding); D3DTextureUploadTests and the
    WriteTextureProbeTests repro hold it.
  - Texture atomics through an R32 mirror buffer (3D, 2D and 1D, returned values, compare-exchange,
    accesses outside the view, GetDimensions: the corpus matches WARP). 3D copies on GL: wgpu-hal's
    GL queue (v29) copies only slice 0 of a 3D texture to another texture, and a texture-to-buffer
    copy from z > 0 reads the wrong slices to the wrong offsets. Such copies go through a buffer
    read from z = 0 (D3DContext.GlSlicesToBuffer); before the fix a CopyResource of a 3D texture to
    another texture, and any readback of slices from z > 0, lost every slice but the first
    (D3DTextureUploadTests.Texture3D_EverySliceReadsBackAsWritten).
  - Doubles, exactly: every double instruction (SM5.0 and the 11.1 extensions) on IEEE binary64
    emulated in integer arithmetic after SoftFloat's algorithms -- round to nearest even,
    subnormals kept, a fused dfma -- so no lower-precision fallback is needed. SoftDoubleTests
    holds 16 operations to .NET's doubles bit for bit over 4096 operands each (subnormals, zeros,
    infinities, near-ties); the corpus's double shaders match WARP. WARP is no oracle for dtoi and
    dtou: it converts through a float (dtoi(16777217) is 16777216). movc and dmovc now select by
    bits, so a NaN pattern in a register survives them on any driver.
  - After tessellation, everything a GS path has:
    - a domain shader's own stream output (its bytecode handed to
      CreateGeometryShaderWithStreamOutput): the DS as compute, written by the tessellator's
      primitives;
    - a geometry shader's stream output;
    - layered and viewport-array passes.
    StreamOutputAfterTessellation_IsWarps matches WARP vertex for vertex for every domain and
    output primitive. A GS after tessellation sees the patch's SV_PrimitiveID, as the D3D11.3 spec
    says. WARP instead adds the primitive's place in its patch, so no test compares against WARP
    there.
  - Command lists keep and carry the whole state: every stage, both UAV sets and the
    stream-output targets, with ExecuteCommandList(restore) and FinishCommandList(restore)
    (D3DCommandListTests).
  - Geometry-shader streams above 0 (`emit_stream`, SM5's PointStream outputs): with stream output,
    each stream keeps its own index segments in the GS kernel (stream-major lanes) and writes its own
    slots with its own count, prefix and fit, so a stream that overflows its buffers stops only
    itself. The rasterized stream (D3D11_SO_RASTERIZED_STREAM, any of 0-3) is the one drawn: the
    pass-through reads that stream's signature, and the draw reads its lane. Without stream output
    every stream's vertices are still rasterized together, as WARP draws them.
    StreamOutput_WritesWhatWarpWrites matches WARP word for word (two streams with overlapping
    registers, stream 1 alone, stream 1 overflowing while appending) and pixel for pixel (stream 1
    rasterized).
  - Not yet: class linkage, DXIL. Managed D3DReflect is done (see Runtime
    compilation).
