@echo off
rem Run the cross-platform test suite (Windows). The Unix twin is eng/run-tests.sh.
rem
rem Only adds DOTNET_ROOT over a plain `dotnet test`: xunit.v3 requires a native apphost, and the
rem apphost resolves the runtime through DOTNET_ROOT or a system install, neither of which knows
rem about this repo's private .dotnet. Where the SDK is installed normally, `dotnet test` is enough.
setlocal
set "REPO=%~dp0.."
set "DOTNET=%REPO%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
for %%I in ("%DOTNET%") do set "DOTNET_ROOT=%%~dpI"
"%DOTNET%" test "%REPO%\src\Microsoft.DotNet.Wpf\src\WgpuInterop\tests\WgpuInterop.Tests\WgpuInterop.Tests.csproj" -c Release --nologo %*
if errorlevel 1 set RC=1
"%DOTNET%" test "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Platform.Tests\Wpf.Platform.Tests.csproj" -c Release --nologo %*
if errorlevel 1 set RC=1
exit /b %RC%
