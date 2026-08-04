@echo off
REM Build the WPF-on-WebGPU fork and pack WpfWebGpu.Sdk into artifacts\local-feed.
REM Windows wrapper around eng\build-sdk.proj (eng/build-sdk.sh is the macOS/Linux twin);
REM all of the actual logic lives in the .proj so the three platforms cannot drift apart.
REM
REM Usage:
REM   eng\build-sdk.cmd                       build everything, pack, evict the NuGet cache
REM   eng\build-sdk.cmd -p:SkipBrowser=true   skip the wasm flavor (needs the wasm-tools workload)
REM   eng\build-sdk.cmd -p:SkipPack=true      build only
REM
REM Any extra arguments are forwarded to msbuild verbatim.
REM
REM The lib\wpf-windows head is packed from a staged win-arm64 fork runtime, which this
REM script does not produce -- build it with build.cmd and point WpfWinRuntimeDir at it
REM (see sdk\WpfWebGpu.Sdk\WpfWebGpu.Sdk.csproj).
setlocal

set "REPO=%~dp0.."

REM Prefer the repo-local SDK: global.json pins 10.0.109 and build.cmd installs it into
REM .dotnet. A system dotnet on a lower feature band cannot load these projects at all.
if defined DOTNET if exist "%DOTNET%" goto :found
set "DOTNET=%REPO%\.dotnet\dotnet.exe"
if exist "%DOTNET%" goto :found
set "DOTNET=%UserProfile%\.dotnet\dotnet.exe"
if exist "%DOTNET%" goto :found
for %%I in (dotnet.exe) do set "DOTNET=%%~$PATH:I"
if not defined DOTNET (
  echo error: no dotnet found. Run build.cmd once to bootstrap .dotnet, or set DOTNET. 1>&2
  exit /b 1
)

:found
echo ^>^> dotnet: %DOTNET%
"%DOTNET%" msbuild "%REPO%\eng\build-sdk.proj" -nologo -v:m -p:DotNetTool="%DOTNET%" %*
exit /b %ERRORLEVEL%
