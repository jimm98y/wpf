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
| `swf/` | **Vendored** Mono `System.Windows.Forms` source (self-contained — no external mono checkout). Managed control/theme/layout subtrees + `swf/resources/` (embedded cursors/icons) + `swf/common/` (two Mono helper files). Our edits vs upstream are applied here (see `mono-patches/`). |
| `gen/XplatUIWebGpu.Core.cs` | Hand-written driver core: windows = `Hwnd` + a `System.Drawing.Bitmap` backing; managed message queue; `PaintEventStart` → `Graphics.FromImage(backing)`; mouse/keyboard/caret injection; screen metrics; `GetPresentWindows`/`GetWindowBackBuffer` present hooks. |
| `gen/XplatUIWebGpu.cs` | Auto-generated default overrides for the ~78 non-core `XplatUIDriver` members. Regenerate from `gen/gen-driver.txt` (the raw `XplatUIDriver` member dump) when the driver contract changes; a CORE set is implemented in `.Core.cs`. |
| `gen/Consts.cs`, `gen/Resources.targets` | Generated Mono `Consts.cs`; embeds the 58 Mono S.W.F resources (cursors/icons) by manifest name. |
| `shims/DrawingDesignShim.cs` | `UITypeEditor` et al. (Mono type-forwards these; the shim avoids dragging in the real WinForms ref). |
| `shims/X11Stubs.cs` | `XEventQueue` stub (referenced by an X11 field in `Hwnd.cs`). |
| `host/` | On-screen host: `CocoaHost.cs` (NSWindow + event pump), `WgpuPresenter.cs` (platform-neutral WebGPU present), `Host.cs` (a demo form). |
| `mono-patches/mono-swf.patch` | The four Mono source edits needed (see below). |

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

These are already applied in `swf/`; the patch is kept only to document the delta from upstream Mono
(e.g. to re-apply if `swf/` is ever refreshed from a newer mono checkout).

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

| Platform | Surface (WebGPU struct in `Wgpu.Surface.cs`) | Windowing host | Status |
|----------|----------------------------------------------|----------------|--------|
| macOS | `WGPUSurfaceSourceMetalLayer` (`MacInterop.CreateSurface`) | `CocoaHost` | ✅ validated |
| Windows | `WGPUSurfaceSourceWindowsHWND` | Win32 host (TODO) | surface struct ready |
| WebAssembly | canvas (`Browser/Wgpu.Browser.cs`) | JS/DOM host + WASM AOT (TODO) | browser interop ready |

> **WebGPU, not Metal.** wgpu-native presents on macOS only through a CAMetalLayer — that is a
> *WebGPU* surface source, not the Metal API. Metal is the backend wgpu lowers WGSL to, exactly like
> Vulkan on Linux / D3D12 on Windows. No Metal API code is written here.

`WgpuInterop` internals are exposed to the host via `InternalsVisibleTo("WinFormsHost")`.

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
