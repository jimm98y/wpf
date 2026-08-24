# Porting a Windows WPF app

What an existing `.csproj` has to change to build against the WPF-on-WebGPU stack, and what it gets
in return: the same app, unmodified, running on Windows, macOS, Linux, Android, iOS and in a browser.

## What we distribute

Two NuGet packages, both produced by `eng/build-sdk.cmd` (or `.sh`) into `artifacts/local-feed`.
They are not published anywhere yet.

| package | what it is |
|---|---|
| `WpfWebGpu.Sdk` | an MSBuild SDK. The fork's WPF assemblies, the XAML markup compiler, the WebGPU backend for every RID, and the targets that wire a head together. |
| `WpfWebGpu.Fonts` | the font bundle for the heads that have no system copy of Segoe UI, the Fluent icon face, Cascadia, CJK or a drawable emoji font. Referenced automatically; a Windows app never restores it. |

Inside `WpfWebGpu.Sdk`:

```
Sdk/            Sdk.props + Sdk.targets — head selection and all the wiring
tools/          PresentationBuildTasks.dll, the fork's XAML markup compiler
targets/        Microsoft.WinFX.targets
lib/wpf/                    the managed WPF payload — ONE set, used by every head
lib/wpf-desktop/            the P/Invoke WebGPU renderer + the SystemEvents/Registry shims
lib/wpf-ios/                the Metal renderer
lib/wpf-browser/            the JS-interop renderer + the browser shims
lib/wpf-winforms/           Mono WinForms, for UseWindowsForms apps, on every head
lib/wpf-webview2*/          WebView2 assemblies carrying the real package's identity
native/<rid>/               the GPU payload for all ten RIDs — wgpu-native, ANGLE on every
                            desktop RID, and WebView2Loader on Windows
web/                        the browser host page and the JS interop modules
```

There is no separate Windows payload and no separate wasm SDK id. Both existed once; see the header
comment in `sdk/WpfWebGpu.Sdk/WpfWebGpu.Sdk.csproj` for what went wrong with the first and
`Sdk/Sdk.props` for the second.

## The conversion

Four steps, and only two of them are edits to your project.

**1. Point restore at the feed.** A `nuget.config` beside the project (see
`samples/wpf-linux-sdk-check/nuget.config`):

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="local-feed" value="path/to/wpf/artifacts/local-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

**2. Switch the Sdk attribute.**

```diff
-<Project Sdk="Microsoft.NET.Sdk">
+<Project Sdk="WpfWebGpu.Sdk/0.1.1">
```

**3. Drop `-windows` from the target framework.**

```diff
-    <TargetFramework>net8.0-windows</TargetFramework>
+    <TargetFramework>net10.0</TargetFramework>
```

The head follows the platform being built for, not the moniker: the same project file produces the
Windows head here, the macOS head on a Mac and the Linux head on Linux.

**4. Delete `<UseWPF>true</UseWPF>`.** The SDK turns the WPF build pipeline on itself and then turns
the property back off before the base SDK sees it, so that no head can fall back to the stock
`Microsoft.WindowsDesktop.App` (milcore) WPF. Leaving the property in is harmless; relying on it is
not.

Everything else stays: `PackageReference`s, `Resource`/`Content`/`EmbeddedResource` items, `Page` and
`ApplicationDefinition` overrides, `ApplicationIcon`, `StartupObject`, `UseWindowsForms`. XAML is
compiled by the fork's own markup compiler, so `.xaml` files need no changes at all.

`WPF-Samples`' WPFGallery — a real app with themes, behaviours, JSON data files and a hundred XAML
pages — is converted by exactly those four steps and nothing else.

## Do not turn off `deps.json`

The one build setting that is not optional: the app must generate a `deps.json`
(`GenerateDependencyFile`, which is on by default — just do not set it to `false`).

`Microsoft.NETCore.App` ships its **own `WindowsBase`** — a 16KB type-forwarding facade,
`Version=4.0.0.0`, `PublicKeyToken=31bf3856ad364e35`. It is not WPF; it exists to forward a handful
of types that moved. The fork's real `WindowsBase` is 1.4MB and `Version=10.0.0.0`, and carries
`DispatcherObject` and the rest of the WPF core.

Both are called `WindowsBase`, and **a framework assembly beats an app-local one whenever the app
has no `deps.json` to say otherwise**. So without one, the facade wins and the app dies at the first
touch of WPF:

```
System.IO.FileNotFoundException: Could not load file or assembly
'WindowsBase, Version=10.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'
```

With a `deps.json` the app's list is authoritative, the usual version comparison applies, 10.0.0.0
beats 4.0.0.0, and the fork's `WindowsBase` loads from the app directory.

Two things worth knowing about the shape of this bug:

- **`WindowsBase` is the only name that collides.** `PresentationCore`, `PresentationFramework`,
  `System.Xaml` and the rest load from the app directory quite happily either way, because the base
  framework has no copy of them. That makes the failure look arbitrary — most of WPF loads, one
  assembly does not.
- **Lowering our version to 4.0.0.0 would make it worse, not better.** Ties go to the framework, so
  matching the facade's version hands it the win. The identity is already correct; the manifest is
  what was missing.

This is the same rule that keeps `Microsoft.WindowsDesktop.App` out of the picture entirely (see the
WinForms note in `src/Microsoft.DotNet.Wpf/src/WinFormsInterop/README.md`) — a framework assembly
always wins. It just bites one layer further down than expected.

## One output for Windows, Linux and macOS

```
dotnet publish -p:WpfWebGpuPortable=true
dotnet YourApp.dll          # on any of them
```

The managed half was always portable — `lib/wpf` is AnyCPU and BAML has no platform in it. What
this switch changes is everything that was previously decided at build time:

- **The GPU backend** goes to `runtimes/<rid>/native/` instead of one flat copy beside the app,
  because win-x64, win-x86 and win-arm64 are all called `wgpu_native.dll` and a flat folder holds
  one of them. The interop picks by the RID the process is actually running as, and preloads ANGLE
  out of the same folder (wgpu asks the OS loader for it by name, and the OS looks beside the app,
  not there).
- **The fonts** always ship, since the output may land on Linux or macOS.
- **The platform shims** ship instead of the Windows-only runtime packages. `Microsoft.Win32.Registry`
  and `Microsoft.Win32.SystemEvents` exist in two flavours that share one assembly identity, so a
  single output can carry only one — and the shims are now the ones that work everywhere: the real
  registry through advapi32 on Windows, an in-memory tree elsewhere; a hidden top-level window
  pumping `WM_SETTINGCHANGE` and friends on Windows, accepted-but-never-raised elsewhere.

What you do not get is a launcher for other platforms. The `.exe` beside the app only runs where it
was built; an executable apphost is per-RID by definition, so other platforms start the app with
`dotnet YourApp.dll`.

## The other heads, from the same project

```
dotnet build                                  # desktop head for the machine you are on
dotnet build -r linux-x64                     # or any other desktop RID
dotnet publish -f net10.0 -p:WpfPlatform=browser   # WebAssembly
```

`WpfPlatform=browser` must be a **global** property — on the command line — because it selects the
base SDK (`Microsoft.NET.Sdk.WebAssembly`) before the project body or a `Directory.Build.props` has
been read. Setting it in the project body is an error that says so.

Android and iOS are the same app assembly hosted by a small platform project (`net10.0-android` /
`net10.0-ios` with an Activity or an AppDelegate); see `samples/wpf-gallery-android` and
`samples/wpf-gallery-ios`. Those heads reference `lib/wpf` and get their native backend the way each
platform requires it — packed into the APK, statically linked into the iOS executable.

## What you get in the output

A Windows build drops the WPF assemblies, `wgpu_native.dll` for the RID being built, the ANGLE GL
runtime, `WebView2Loader.dll`, and a `runtimeconfig.json` naming only `Microsoft.NETCore.App`. The
app is self-contained with respect to WPF: no WindowsDesktop framework, no milcore, whatever is
installed on the machine.

A macOS or Linux build additionally gets a `fonts/` folder beside the app. A Windows build does not,
and does not restore `WpfWebGpu.Fonts` at all — Windows has those families already.

## Checking a change to the packaging

```cmd
pwsh src\Microsoft.DotNet.Wpf\src\WgpuInterop\eng\fetch-wgpu.ps1 -All   :: once per clone
eng\build-sdk.cmd                             :: build the fork, pack both packages, evict the cache
dotnet run --project samples\wpf-linux-sdk-check -- --seconds 6
```

`native/` is gitignored, so a fresh clone has no GPU backend at all. `fetch-wgpu.ps1` stages both
halves of it — wgpu-native from the gfx-rs release, and ANGLE (the OpenGL ES implementation wgpu
falls back to where the GPU is virtualized) out of the Electron release, because the ANGLE project
has never shipped a binary of its own. Packing with any RID missing either one is an error rather
than a package that quietly does not work on somebody else's machine.

That sample is a consumer like any other — one `Sdk=` attribute and nothing else — and it has the
only compiled XAML among the samples, so if it builds and paints, the package is intact.
