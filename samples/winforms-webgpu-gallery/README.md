# WinForms on WebGPU — desktop gallery

A Windows Forms demo (GroupBox + radios, CheckBox, ComboBox, ListBox, TextBox, ProgressBar, Button,
Labels) whose controls are painted through **WebGPU/WGSL instead of GDI+/libgdiplus**. It runs Mono's
managed `System.Windows.Forms` on a custom `XplatUIWebGpu` driver: control paint is intercepted by a
GPU-raster recorder inside our vendored `System.Drawing`, turned into a WebGPU scene, and composited by
`WgpuSceneRenderer` onto a native surface (CAMetalLayer on macOS, HWND on Windows).

The from-source WinForms/System.Drawing/WebGPU-interop projects live under
`src/Microsoft.DotNet.Wpf/src/WinFormsInterop/` and `.../WgpuInterop/`; this sample references them and
provides the demo form plus the per-OS windowing host (`CocoaHost` / `Win32Host`).

## Run (macOS, Apple Silicon)

```sh
dotnet build WinFormsHost.csproj -c Release
WF_WEBGPU=1 WF_GPU_RASTER=1 DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib \
  dotnet bin/Release/net10.0/WinFormsHost.dll 60
```

An interactive window opens for 60 seconds — click the button, toggle the radios/checkbox, open the
combo dropdown, type in the text box. Append `selftest` to inject clicks headlessly and exit non-zero on
failure. `WF_GPU_RASTER=1` selects the GPU-raster paint path (the point of the sample); without it the
driver falls back to libgdiplus-rasterized control bitmaps.

Windows needs `WgpuInterop/native/win-x64/lib/wgpu_native.dll`; the macOS `libwgpu_native.dylib` is
already fetched in this fork. The **browser/WebAssembly** variant is the sibling
`winforms-webgpu-gallery-wasm` sample.
