# Build loop

How to build this fork without waiting ten minutes for a no-op.

## There are two drivers, and they do not share incremental state

| driver | what it builds | when to use it |
|---|---|---|
| `build.cmd` / `build.sh` | `Microsoft.Dotnet.Wpf.sln` through Arcade | working on the WPF assemblies |
| `eng/build-sdk.cmd` / `.sh` | the assemblies **and** the `WpfWebGpu.Sdk` + `WpfWebGpu.Fonts` NuGet packages, each project via a separate `dotnet build` | producing something an app can consume (see [porting-an-app.md](porting-an-app.md)) |

Arcade invokes MSBuild with its own property set — the `artifacts/` layout, generated version
properties, `Platform`. A bare `dotnet build Foo.csproj` uses a different one. Neither is wrong, but
each project's `obj` state records the properties it was last built with, so **switching drivers
makes every project look out of date** and the next build recompiles the world. In either direction.

Measured on this repo, every row a **no-change** build:

| | wall clock | `Csc` invocations |
|---|---|---|
| `build.cmd` following bare `dotnet build` | 646s | **185** |
| `build.cmd` following `build.cmd` | **80s** | **0** |
| `build.cmd -projects <one.csproj>` | 100s | 0 |
| bare `dotnet build` following bare `dotnet build` | 7s (WindowsBase), 23s (PresentationFramework) | 0 |

Both drivers are individually incremental. The 646s row is not a bug in either of them — it is the
cost of alternating. **Pick one driver and stay on it for the session.** If you must switch, expect
to pay for one full rebuild.

## The inner loop

Build only what you changed, through Arcade so the property set stays consistent:

```cmd
build.cmd -configuration Release -platform AnyCPU -warnAsError 0 ^
          -projects src\Microsoft.DotNet.Wpf\src\PresentationCore\PresentationCore.csproj
```

`-projects` takes a semicolon-delimited list and accepts globs. Project references are still built,
so naming the leaf project you touched is enough.

`-platform AnyCPU` is not optional here: the default is x86 and this port is AnyCPU. `-warnAsError 0`
keeps an unrelated analyzer warning from failing the build.

## Running the tests

The `tests/CrossPlatform` suites bind to the built assemblies **by path**, so they need whatever you
just built in `artifacts/bin/*/Release/net10.0` and are then run with plain `dotnet test`:

```cmd
cd src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Document.Tests
dotnet test
```

That is not a driver switch — those projects are outside the solution and have no Arcade state to
invalidate.

The `tests/UnitTests` suites are in the solution and are built by `build.cmd`. They are the only
projects with `InternalsVisibleTo` on the product assemblies, so internal types can only be tested
there.

## When a build still feels wrong

Ask MSBuild rather than guessing:

```cmd
build.cmd -configuration Release -platform AnyCPU -warnAsError 0 -verbosity normal /clp:PerformanceSummary
```

The task summary at the end names the cost. On a no-change build `Csc` should be absent; if it shows
up with a three-digit call count, something is rewriting a compile input on every build — see
`eng/WpfArcadeSdk/tools/ExtendedAssemblyInfo.targets`, where exactly that once happened.

There is **no C++ in this build**. The solution contains no `.vcxproj`, and the ones still on disk
(wpfgfx, PenImc, DirectWriteForwarder, System.Printing) are unreferenced leftovers of the native
stack this port replaced. If a build is slow, it is not native code.
