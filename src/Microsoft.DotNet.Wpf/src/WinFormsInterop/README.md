# WinFormsInterop — legacy WinForms on the WebGPU stack

Runs legacy **System.Windows.Forms** apps cross-platform on this fork's WebGPU compositor, so a
WPF+WinForms legacy app can be ported with little/no change. It reuses Mono's fully-managed
`System.Windows.Forms` (every control is painted in managed code via `System.Drawing.Graphics` —
never native comctl32) and drives it through a custom `XplatUIDriver` that renders each window into
a bitmap and **presents through WebGPU** (the same `WgpuInterop` assembly WPF uses).

Status (macOS validated): compile → render → live message loop → child paint → interactive input
(mouse, keyboard, caret, text editing, ComboBox dropdowns) → **WebGPU present** (wgpu-native via a
CAMetalLayer surface). Windows and WebAssembly reuse the same present path (see below).

## Layout

| Path | What |
|------|------|
| `System.Windows.Forms.WebGpu.csproj` | Builds Mono's managed `System.Windows.Forms.dll` on net10 with our driver, from the vendored `swf/`. |
| `System.Design.WebGpu.csproj` | Builds Mono's managed `System.Design.dll` on net10 from the vendored `swfdesign/` — the WinForms **designer host** (`DesignSurface`, `DesignerHost`, CodeDom serializers, `UndoEngine`, `ControlDesigner`). |
| `swfdesign/` | **Vendored** Mono `System.Design` source (WinForms-relevant namespaces only). Pruned at vendoring time, so the csproj globs with no excludes. Edits vs upstream in `mono-patches/mono-system-design.patch`. |
| `swf/` | **Vendored** Mono `System.Windows.Forms` source (self-contained — no external mono checkout). Managed control/theme/layout subtrees + `swf/resources/` (embedded cursors/icons) + `swf/common/` (two Mono helper files). Our edits vs upstream are applied here (see `mono-patches/`). |
| `gen/XplatUIWebGpu.Core.cs` | Hand-written driver core: windows = `Hwnd` + a `System.Drawing.Bitmap` backing; managed message queue; `PaintEventStart` → `Graphics.FromImage(backing)`; mouse/keyboard/caret injection; screen metrics; `GetPresentWindows`/`GetWindowBackBuffer` present hooks. |
| `gen/XplatUIWebGpu.cs` | Auto-generated default overrides for the ~78 non-core `XplatUIDriver` members. Regenerate from `gen/gen-driver.txt` (the raw `XplatUIDriver` member dump) when the driver contract changes; a CORE set is implemented in `.Core.cs`. |
| `gen/Consts.cs`, `gen/Resources.targets` | Generated Mono `Consts.cs`; embeds the 58 Mono S.W.F resources (cursors/icons) by manifest name. |
| `shims/X11Stubs.cs` | `XEventQueue` stub (referenced by an X11 field in `Hwnd.cs`). |
| `host/` | On-screen host: `CocoaHost.cs` (NSWindow + event pump), `WgpuPresenter.cs` (platform-neutral WebGPU present), `Host.cs` (a demo form). |
| `web/WebBrowser.cs` | `System.Windows.Forms.WebBrowser` on the engine seam, including the ActiveX-shaped compatibility surface (`ActiveXInstance`, `CreateSink`/`DetachSink`) hosts still use. |
| `web/ManagedConnectionPoint.cs` | A managed stand-in for a COM connection point, so `AxHost.ConnectionPointCookie` works without ActiveX. |
| `swf/System.Resources/` | **Vendored** Mono `ResXResourceReader`/`Writer`/`ResXDataNode` etc. The originals were pruned with the rest of the non-managed tree, but they are pure managed XML handling and consumers do use them. |
| `mono-patches/mono-swf.patch` | The five Mono source edits needed (see below). |

## The vendored Mono System.Windows.Forms source

The Mono S.W.F source is **vendored** under `swf/` — the repo is self-contained and needs **no
external mono checkout**. Only the necessary managed subtrees were copied (control/theme/layout/
visual-styles/RTF/design/assembly), plus `swf/resources/` (embedded cursors/icons) and
`swf/common/` (two Mono helper files that lived outside the S.W.F tree). The native platform
drivers (X11/Carbon), Mono WebBrowser, and NUnit tests were **pruned** during vendoring, so the
csproj just globs `swf/**` with no excludes. `XplatUIWin32.cs` is kept compile-only (cross-platform
files call its static helpers; the DllImports never run because our driver is selected).

`mono-patches/mono-swf.patch` records the four Mono source edits we made vs upstream — **already
applied** to the vendored `swf/` copy; it's kept only as provenance/documentation:
- **XplatUI.cs** — select our driver (`driver = XplatUIWebGpu.GetInstance()`), add `GetHwndGraphics`.
- **XplatUIDriver.cs** — add the `GetHwndGraphics` virtual.
- **Control.cs** — `CreateGraphics()` → `XplatUI.GetHwndGraphics` (libgdiplus `Graphics.FromHwnd`
  throws on modern macOS — it uses dead Carbon/QuickDraw; our driver draws over the backing bitmap).
- **Win32DnD.cs** — `AppDomain.DefineDynamicAssembly` → `AssemblyBuilder.DefineDynamicAssembly`
  (API moved in .NET Core).
- **AxHost.cs** — `ConnectionPointCookie` delegates to a managed connection point rather than
  throwing, and its finalizer no longer throws (it did unconditionally, which aborts the
  process on GC).

These are already applied in `swf/`; the patch is kept only to document the delta from upstream Mono
(e.g. to re-apply if `swf/` is ever refreshed from a newer mono checkout).

## The designer host (`swfdesign/`)

Mono's WinForms was written expecting a companion `System.Design`: its controls carry **570
`[Designer]` attributes, 55 of them naming `System.Windows.Forms.Design`**. Vendoring the WinForms
half alone left every one of those pointing at nothing, so no design-time host could run on this
stack.

Microsoft's `System.Design` cannot stand in. It is a type-forwarding facade over
`System.Windows.Forms.Design.dll`, which references the **private** WinForms substrate —
`System.Private.Windows.Core`, `System.Windows.Forms.Primitives`, `System.Private.Windows.GdiPlus` —
plus `System.Drawing.Common`. Those are implementation assemblies, not contracts; shimming them
would mean reimplementing Microsoft's WinForms internals.

Only the WinForms-relevant namespaces are vendored (`System.ComponentModel.Design`,
`.Design.Serialization`, `System.Windows.Forms.Design`, `.Design.Behavior`). The ASP.NET, Data,
Messaging, ServiceProcess and `System.Resources.Tools` trees are not.

**Pruned at vendoring time** (so the csproj needs no excludes):

- Nothing, any more. The 21 `UITypeEditor`-derived property editors were pruned for one release
  while `UITypeEditor` had two providers — `shims/DrawingDesignShim.cs` declared it inside
  System.Windows.Forms and `Mono.System.Drawing` declared it too, so anything deriving from one
  was ambiguous (CS0433), which bit WpfDesigner's `DropDownEditor` and SharpDevelop's
  `TypeResolutionService` (the latter needs `System.Windows.Forms.Design.AnchorEditor` by name).
  Deleting that redundant shim collapsed the two providers into one, and all 21 are back.
- `AxImporter.cs` — the ActiveX importer, on `TYPELIBATTR`/`UCOMITypeLib` (COM interop types that
  modern .NET dropped).

**What works and what does not.** The load/serialize/undo core is complete — `DesignSurface`,
`DesignerHost`, `CodeDomSerializer`, `CodeDomDesignerLoader`, `UndoEngine` and `DocumentDesigner`
carry no `NotImplementedException` at all. The gaps are concentrated in the *interactive* layer:
`ControlDesigner` (25), `BehaviorService`/`Behavior`/`Adorner` (53), `ComponentDocumentDesigner`
(17). In practice that means **loading a form, rendering it and editing through the property grid
is implemented; drag-drop, resize grips and selection adorners are where Mono stopped.**

`CodeDomComponentSerializationService` uses `BinaryFormatter` for the component-state snapshot that
backs undo. `SYSLIB0011` is silenced so the assembly compiles; that does **not** make it work — .NET
9+ removed `BinaryFormatter`, so that path throws at runtime and needs a real replacement serializer.

### System.Drawing off Windows
Modern **System.Drawing.Common 10 is Windows-only**. This uses **6.0.0** (the last version with Unix
support) + a runtimeconfig `System.Drawing.EnableUnixSupport=true` + **libgdiplus**
(`brew install mono-libgdiplus`). Run with `DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib`. Control
pixels are rasterized by libgdiplus (Cairo/CPU) today; GPU rasterization (drawing controls with WGSL
to drop libgdiplus) is the next tier.

## WebGPU present path (cross-platform by design)

`host/WgpuPresenter.cs` is **platform-neutral**: given a `WgpuContext` + a presentable surface +
the composite bitmap, it does `RGBA → ImageBrush → full-window quad → WgpuSceneRenderer.RenderSceneToView
→ wgpuSurfacePresent`. The API, WGSL, renderer and loop are **identical** on all targets; only two
things differ per platform, and `WgpuInterop` already has all three surface types:

Surface creation is already cross-platform: `Platform.NativePlatform.CreateWindowSurface(instance,
nativeWindow)` dispatches to the Metal/HWND/Xlib/canvas source by detected OS. The host picks its
windowing shell (`IWinFormsHost`) by OS in `Host.Main`; the `WgpuPresenter` path below is identical.

| Platform | Surface (via `NativePlatform`) | Windowing host | Status |
|----------|--------------------------------|----------------|--------|
| macOS | `WGPUSurfaceSourceMetalLayer` | `host/CocoaHost.cs` | ✅ validated |
| Windows | `WGPUSurfaceSourceWindowsHWND` | `host/Win32Host.cs` | ✅ builds; ⚠ not yet run on Windows (needs `native/win-x64/wgpu_native.dll`) |
| WebAssembly | canvas (`Browser/Wgpu.Browser.cs`) | JS/DOM host + WASM AOT (TODO) | browser interop ready |

> **WebGPU, not Metal.** wgpu-native presents on macOS only through a CAMetalLayer — that is a
> *WebGPU* surface source, not the Metal API. Metal is the backend wgpu lowers WGSL to, exactly like
> Vulkan on Linux / D3D12 on Windows. No Metal API code is written here.

`WgpuInterop` internals are exposed to the host via `InternalsVisibleTo("WinFormsHost")`.

## GPU rasterization (`gpuraster/`)

Today the control PIXELS come from libgdiplus (CPU) and are uploaded to WebGPU as textures. The next
tier draws the controls *themselves* with WGSL. `gpuraster/` proves that vocabulary end to end:
`SceneGraphics.cs` is a `System.Drawing.Graphics`-shaped translator that records into a
`WgpuSceneRenderer` scene (FillRectangle → `GeometryFill`, DrawLine/edges → thin fills, FillEllipse →
`EllipseGeometry`, FillPolygon → `PolygonGeometry`, DrawString → `GlyphRunDraw`, gradients →
`LinearGradientBrush`). `Program.cs` draws a whole classic dialog (GroupBox, radios, checkbox,
combo, listbox with selection, textbox, progress bar, button) through it, renders offscreen
(`RenderToRgba`), and writes a PNG with a built-in encoder — **no libgdiplus, no System.Drawing at
all**. Run: `dotnet run --project gpuraster -c Release -- out.png` (uses an Arial TTF for glyphs).

The full tier drives *every* control's `Graphics` through `SceneGraphics` by replacing the
System.Drawing backend (Mono's `System.Drawing` has one seam, `gdipFunctions.cs`) — `SceneGraphics`
is the reusable translation layer that backend calls. `System.Drawing.Common`'s `Graphics` is
`sealed`, so a backend swap (not interception) is the only seam.

## Build & run (macOS)

```sh
# brew install mono-libgdiplus   (the only external prerequisite; source is vendored under swf/)
dotnet build host/WinFormsHost.csproj -c Release
WF_WEBGPU=1 DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib \
  dotnet host/bin/Release/net10.0/WinFormsHost.dll 30      # 30 = seconds to run
```

Env vars: `WF_WEBGPU=1` uses the WebGPU present path (else CoreGraphics fallback);
`WF_WEBGPU_SAVE=<png>` dumps the GPU's readback of one frame; `WF_TRACE=1` prints per-frame surface
status.

## Isolation

`Directory.Build.props`/`.targets` here are intentionally empty to stop MSBuild inheriting the WPF
repo-wide build customization (SR-resource generation, artifacts layout) — these are standalone
SDK-style projects.
