# WinForms on WebGPU — browser (WebAssembly)

The same WinForms demo as the desktop `winforms-webgpu-gallery`, running **in the browser** on
`navigator.gpu`. Mono's managed `System.Windows.Forms` + our vendored `System.Drawing` are compiled to
WebAssembly; control paint is recorded into a WebGPU scene and composited by WGSL onto a `<canvas>`.
DOM mouse/keyboard/wheel events feed the `XplatUIWebGpu` driver. It runs with **zero libgdiplus** — the
browser has none, so every `System.Drawing` object degrades to managed-only construction.

Interactive: button click, radio/checkbox toggle, keyboard typing, ComboBox dropdown (the canvas grows
to fit popups extending past the form), and wheel-scrolling the ListBox. Renders crisp on Retina
(devicePixelRatio). Reuses the browser WebGPU-interop backend from the WPF wasm gallery
(`src/Microsoft.DotNet.Wpf/src/WgpuInterop/Browser`).

## Prerequisites

- A .NET SDK with the `wasm-tools` (and, for AOT, `wasm-experimental`) workloads.
- The from-source WinForms/System.Drawing/Browser-WebGPU-interop assemblies built first (this sample
  references their `bin/Release` outputs):
  ```sh
  cd ../../src/Microsoft.DotNet.Wpf/src
  dotnet build WinFormsInterop/System.Windows.Forms.WebGpu.csproj -c Release
  dotnet build WinFormsInterop/sysdrawing/System.Drawing.WebGpu.csproj -c Release
  dotnet build WgpuInterop/Browser/WgpuInterop.Browser.csproj -c Release
  ```

## Run

```sh
dotnet publish WinFormsHostWasm.csproj -c Release -o out
cd out/wwwroot && python3 -m http.server 8080
# open http://localhost:8080 in a WebGPU-capable browser (Chrome/Edge; Safari TP)
```

Interpreter build by default (fast dev loop). Add `-p:WfAot=true` to AOT-compile the hot render path
(Mono.System.Drawing recorder + the WGSL renderer) for native-speed paint — `System.Windows.Forms`
stays interpreted so mono-aot-cross needn't resolve its legacy dependency closure.
