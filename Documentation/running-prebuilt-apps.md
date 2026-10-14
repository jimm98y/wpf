# Running prebuilt apps with wpfmac

`wpfmac` runs an existing .NET 10 WPF and/or WinForms app on the WPF-on-WebGPU stack **without
rebuilding it and without changing a file in its folder**. Point it at the app's `.dll`, `.exe` or
folder:

```
dotnet artifacts/wpfmac/osx-arm64/launcher/wpfmac.dll run ~/Apps/Contoso/Contoso.dll
```

It is the counterpart of [porting-an-app.md](porting-an-app.md), which is about rebuilding an app
against `WpfWebGpu.Sdk`. Use wpfmac when you have only the binaries, or want to see how far an app
gets before porting it.

- **macOS** is the target. The app runs natively on arm64 (AnyCPU assemblies, which is nearly all of
  them), on the fork's WPF, Mono WinForms and the WebGPU renderer, with shims for the Windows
  services it calls.
- **Windows** is the comparison platform. The app's own `.exe` keeps running stock WPF, and
  `wpfmac compare` starts it next to the WebGPU instance, side by side.

Nothing in wpfmac is specific to an app.

## Build

wpfmac is built from this repo, on top of the SDK package:

```
eng/build-sdk.sh                  # once: builds the stack and packs WpfWebGpu.Sdk into artifacts/local-feed
tools/WpfMac/build.sh             # macOS / Linux   (tools\WpfMac\build.cmd on Windows)
```

The output is `artifacts/wpfmac/<rid>/`:

```
launcher/                 wpfmac itself (portable: dotnet launcher/wpfmac.dll ...)
overlay/wpfmac.payload/   the stack an app runs on: WPF, WinForms, System.Drawing, the renderer,
                          wgpu-native, fonts, the shims, the startup hook, and on macOS libwin32shim
```

After changing stack code, `tools/WpfMac/build.sh --repack` repacks the SDK from the existing build
outputs and rebuilds the overlay in seconds. Build the changed project first. Never `dotnet publish`
a single project into the overlay folder: an incremental clean of that folder deletes the native
libraries.

## Commands

| command | what it does |
|---|---|
| `wpfmac run [options] <app> [-- app args]` | run the app on the WebGPU stack |
| `wpfmac compare [options] <app>` | Windows: stock `.exe` and WebGPU instance side by side |
| `wpfmac audit [--out report.md] <app>` | what the app needs that the stack on this OS lacks |
| `wpfmac prepare <app>` | write the generated files and print the command, without running |
| `wpfmac reg import <file.reg>` / `reg export <key> <file.reg>` | macOS: move registry state in or out |
| `wpfmac clean [--all]` | delete generated files; `--all` also logs and shim state |
| `wpfmac where` | print every location wpfmac uses |

Options come **before** the app; everything after the app goes to the app.

`run` options:

| option | |
|---|---|
| `-v` | print the merge report (which assemblies were replaced, fetched, kept) |
| `--offline` | do not fetch missing NuGet assets |
| `--isolate` | suffix the names of the app's named mutexes, events, semaphores and shared memory, so a single-instance app can run beside another copy of itself |
| `--title-prefix <text>` | prefix every top-level window title |
| `--screenshots <dir> [--every <s>]` | save a PNG of every window every few seconds (from the stack's own rendering, so no screen-recording permission is needed) |
| `--crash-reports` | macOS: keep the system's crash reports and dialogs (see [When the app crashes](#when-the-app-crashes)) |

`compare` options: `--isolate`, `--screenshot <dir>` (captures both windows and a diff image),
`--seconds <n>`, `--close`.

## What happens to the app

The app's folder is only ever read. wpfmac writes a generated `runtimeconfig.json` and `deps.json`
into its cache and runs:

```
dotnet exec --runtimeconfig <cache>/App.runtimeconfig.json --depsfile <cache>/App.deps.json
            --additionalprobingpath <overlay> ... <app folder>/App.dll
```

- **runtimeconfig**: `Microsoft.WindowsDesktop.App` is dropped on every OS, Windows included. It is
  where stock WPF and WinForms come from, and a framework assembly always wins over an app-local one.
  `Microsoft.NETCore.App` and anything else the app asked for stay. The startup hook and the
  overlay's settings are added as `configProperties`, not environment variables, so the app's own
  child processes do not inherit them.
- **deps.json**: the app's libraries, merged with the overlay as one extra library. An assembly the
  stack owns (WPF, WinForms, the shims) replaces the app's copy whatever its version. Between two
  ordinary packages the newer one wins. Package assets the app lists for this platform but did not
  deploy (a `SkiaSharp.NativeAssets.macOS`, say) are fetched from the NuGet cache or nuget.org into
  wpfmac's cache.
- **startup hook** (`WpfMac.Bootstrap`), before `Main`:
  - puts `AppContext.BaseDirectory` back to the app's folder;
  - maps the app's .NET 10 WinForms references to the stack's;
  - starts the compatibility log;
  - on macOS, loads `libwin32shim` and installs the runtime detours.

Because the app's own `.dll` runs from its own folder, `GetEntryAssembly`, `App.dll.config` and
relative content paths behave as they do on Windows.

## What is covered on macOS

**Managed shims.** Each one has the identity and the exact public API of the assembly it replaces
(checked by `shims/Surface/check-surface.sh`). Each reports the true state of a Mac rather than
pretending to be a particular Windows machine, so an app takes its own "not installed" paths.

| assembly | on macOS |
|---|---|
| `Microsoft.Win32.Registry` | a persistent per-user store in wpfmac's state, shared by every app run through wpfmac, like the real registry. `wpfmac reg import` seeds it from a regedit export. |
| `Microsoft.Win32.SystemEvents` | handlers are accepted and never raised (macOS has no setting-change broadcast to translate) |
| `System.Management` (WMI) | WQL `SELECT ... WHERE` over `root\cimv2` with this Mac's data: `Win32_ComputerSystem[Product]`, `_BaseBoard`, `_BIOS`, `_Processor`, `_OperatingSystem`, `_LogicalDisk`, `_NetworkAdapter[Configuration]`, `_VideoController`, `_PhysicalMemory`, `_Process`. `Win32_Service` is empty. Other built-in namespaces return nothing, and a product's namespace (SQL Server's, say) is `InvalidNamespace`. Values have WMI's types (`uint32`, `uint64`, ...). |
| `System.ServiceProcess.ServiceController` | a service manager with no services: Windows' "service not found" errors |
| `System.Security.Cryptography.ProtectedData` | DPAPI's contract with AES-GCM under per-user and per-machine keys in wpfmac's state. Blobs protected on Windows cannot be read here, just as between two Windows machines. |
| `System.Diagnostics.PerformanceCounter` | counters the app creates work in-process; Processor, Memory, Process and System report live values |
| `System.Diagnostics.EventLog` | Application, System and Security exist; sources are registered in the registry where Windows keeps them; entries are kept for the process and copied to the compatibility log |

**Native Win32 (`libwin32shim`).** Every function in the Windows API metadata, about 20,000 exports
across 364 DLLs, has an entry point, so a P/Invoke always resolves. Each entry point is one of two
things:

- a managed implementation, in `tools/WpfMac/WpfMac.Win32`: window, DC and GDI functions over the
  stack's own windows and `System.Drawing`, plus system metrics, `kernel32` basics, `dwmapi`,
  `uxtheme` and others;
- otherwise, a stub that logs the call once and returns the function's failure value (`FALSE`,
  `E_NOTIMPL`, an invalid handle ...), so the app sees an ordinary failure.

**Runtime detours.** Named `EventWaitHandle`, `Semaphore` and `MemoryMappedFile` objects, which .NET
refuses on Unix, work. `CommonApplicationData` and the other machine-wide folders are mapped into
wpfmac's state.

## What does not work (yet)

- **Direct3D / DXGI** and libraries built on them (SharpDX views, D3DImage content) degrade:
  their factories return failure HRESULTs.
- **C++/CLI assemblies** cannot load outside Windows.
- **COM activation** is unavailable (`PlatformNotSupportedException: COM Interop`).
- **Unimplemented Win32 functions** fail quietly. The compatibility log names each one; that list
  is what decides what to implement next.
- **Fidelity**: WinForms is Mono's WinForms rendered by the stack, so owner-drawn third-party
  controls may differ in detail. `compare` on Windows is the place to judge that.

## Logs and diagnostics

Every run writes a compatibility log, `~/Library/Logs/WpfMac/<App>.log` (Windows:
`%LOCALAPPDATA%\WpfMac\logs`). It records:

- the first occurrence of each exception that means "this platform or stack lacks something"
  (`PlatformNotSupportedException`, `DllNotFoundException`, `EntryPointNotFoundException`,
  `MissingMethodException`, `TypeLoadException`, ...), with the stack that reached it;
- every unimplemented Win32 call (`win32 shim: user32!GetSystemMenu is not implemented`);
- every WMI class the shim has no data for;
- event log entries;
- unhandled exceptions;
- repeat counts, at exit.

`wpfmac audit` gives the same picture statically before the app runs: missing assemblies, types and
members, P/Invokes (marked ✓/✗ by whether libwin32shim exports them), non-AnyCPU and C++/CLI
assemblies, named kernel objects, hard-coded paths, and Windows-only APIs such as WMI and DPAPI.
A referenced assembly that stock cannot load either (it is neither in the app's folder nor in the
frameworks the app's own runtimeconfig.json names) is listed separately under "Absent for stock too":
the app ships without it, so it is not a gap in the stack. `Microsoft.WindowsDesktop.App` is never
read for this, on any OS: the overlay, which carries every member of it, stands in for it. A framework
the app names that is not installed on this machine makes the split unknown, and every miss then still
counts as a gap.

On macOS, `WPFMAC_DUMP_WINDOWS=1` with `--screenshots` also logs which WinForms windows the host
composites for each form.

## When the app crashes

On macOS every `abort()` normally leaves a crash report and queues a "dotnet quit unexpectedly"
dialog, one per crash. Under wpfmac, an app that dies of an unhandled exception, an uncaught
Objective-C exception or a native panic instead:

- notes it in the compatibility log and on stderr;
- exits with status 134, the status an abort would give.

Pass `--crash-reports` to keep the system's behaviour, for the native backtraces in the reports
(`~/Library/Logs/DiagnosticReports`).

## Windows: side by side

```
dotnet artifacts\wpfmac\win-arm64\launcher\wpfmac.dll compare --screenshot shots --size 1900x1150 C:\Apps\Contoso\Contoso.exe
```

This starts `Contoso.exe` as usual (stock WPF and WinForms) and the same folder through wpfmac
(WebGPU), and places the two main windows side by side at the same size.

- `--screenshot <dir>` waits `--seconds`, brings each window to the front, makes both inactive and
  parks the cursor off them, and captures each from the screen (window and client area), then writes
  `diff.png` and `diff-client.png` and reports how many pixels differ in any channel.
  `--tolerance <n>` also reports how many differ by more than n.
- `--size <w>x<h>` sets the windows' size; otherwise each gets half the work area.
- `--title-prefix <text>` prefixes the WebGPU window's caption. There is none by default, because the
  captions are compared too.
- `--isolate` lets a single-instance app start twice. An app that searches for its own window by
  title can still refuse the second instance.

On Windows the WebGPU instance uses the real registry, event log, WMI and Win32. The two differ only
in the UI stack.

### The app's own Win32 calls on Windows

On Windows the stack's WinForms controls are not Windows windows: `Control.Handle` is a handle the
stack made up. An app that passes one to user32 (`GetWindowDC(control.Handle)`, `SendMessage`,
`BeginPaint`, `SetWindowPos`, ...) would reach a Windows that has never heard of it. So wpfmac gives
each of the app's assemblies that P/Invokes user32, gdi32, uxtheme or msimg32 its own copy of that DLL
(`NativeLibrary.SetDllImportResolver`, from the startup hook):

- the copy is generated at run time from the real DLL's export table (`WpfMac.Bootstrap/Win32/
  ShimImage.cs`, no native toolchain), with every export at the same name and ordinal, and cached in
  the app's cache folder;
- an export without a managed implementation jumps straight to the real function;
- an export with one (`WpfMac.Win32/Windows*.cs`) runs it when a handle argument is one of the
  stack's windows, and the real function for every other handle: WPF windows, the windows forms are
  shown in, windows the app created itself;
- a device context for one of the stack's windows is a real memory DC; what the app draws into it
  reaches the window when it is released (`ReleaseDC`, `EndPaint`);
- GDI calls on a DC that a `Graphics` is drawing into first write that `Graphics`' drawing to the DC,
  as GDI+ does by drawing as it goes.

The stack's own assemblies and the runtime's are not given the copy. An assembly that installs its
own resolver is left alone (the log says so).
## Removing wpfmac

wpfmac installs nothing: no shared framework, no persisted environment variables, no file
associations, and it never writes to an app's folder. To remove it:

```
dotnet artifacts/wpfmac/<rid>/launcher/wpfmac.dll clean --all
rm -rf artifacts/wpfmac
```

`clean --all` deletes everything wpfmac created. `wpfmac where` lists the locations:

| | macOS | Windows |
|---|---|---|
| cache (generated files, fetched packages) | `~/Library/Caches/WpfMac` | `%LOCALAPPDATA%\WpfMac\cache` |
| logs | `~/Library/Logs/WpfMac` | `%LOCALAPPDATA%\WpfMac\logs` |
| state (registry, DPAPI keys, mapped machine folders) | `~/Library/Application Support/WpfMac` | not used: the real registry and folders are |

The app is then exactly as before: its folder was never written to, and on Windows its `.exe` never
stopped running on stock WPF.

What the app itself wrote while it ran under wpfmac stays where it wrote it: settings in its own
`~/Library/...` folders, files it saved. Writes to machine-wide folders and the registry went into
wpfmac's state and are gone.

`WPFMAC_CACHE`, `WPFMAC_LOGS`, `WPFMAC_STATE` and `WPFMAC_OVERLAY` move these locations, for example
to try an app in a throwaway sandbox.

## Testing wpfmac

```
dotnet test tools/WpfMac/tests/WpfMac.Tests                # unit tests: merge, rewrite, .reg, shims, ...
tools/WpfMac/tests/fixtures/build-fixtures.sh              # stock net10.0-windows test apps
dotnet artifacts/wpfmac/osx-arm64/launcher/wpfmac.dll run artifacts/wpfmac-fixtures/MixedFixture/MixedFixture.dll -- --seconds 3
```

Each fixture prints `fixture: PASS` and exits 0:

| fixture | what it checks |
|---|---|
| `WpfFixture` | a plain WPF app |
| `WinFormsFixture` | the .NET WinForms template, plus `System.Drawing` type identities |
| `MixedFixture` | WinForms hosting WPF and WPF hosting WinForms, plus the registry, event log, WMI and user32 |

Run on Windows, each fixture must behave the same both as its own `.exe` and through wpfmac.

A changed shim must keep its surface:

```
shims/Surface/check-surface.sh shims/System.Management System.Management 10.0.10
```

To regenerate a shim's `Surface.g.cs` after `implemented.txt` changes, use
`shims/Surface/gen-surface.sh` with the same arguments.
