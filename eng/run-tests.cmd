@echo off
rem Run the cross-platform test suite (Windows). The Unix twin is eng/run-tests.sh.
rem
rem Only adds DOTNET_ROOT over a plain `dotnet test`: xunit.v3 requires a native apphost, and the
rem apphost resolves the runtime through DOTNET_ROOT or a system install, neither of which knows
rem about this repo's private .dotnet. Where the SDK is installed normally, `dotnet test` is enough.
rem
rem Keep this list in step with the one in run-tests.sh. It listed two of the suites for a while
rem and the printing suite was one of the missing ones -- which matters most on THIS platform,
rem because the Windows print backend drives a real spooler and its tests are the only ones that
rem exercise it. A suite nobody runs is a suite that is always green.
setlocal
set "REPO=%~dp0.."
set "DOTNET=%REPO%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
for %%I in ("%DOTNET%") do set "DOTNET_ROOT=%%~dpI"
set RC=0

call :run "%REPO%\src\Microsoft.DotNet.Wpf\src\WgpuInterop\tests\WgpuInterop.Tests\WgpuInterop.Tests.csproj" %*
call :run "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Platform.Tests\Wpf.Platform.Tests.csproj" %*
call :run "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Accessibility.Tests\Wpf.Accessibility.Tests.csproj" %*
call :run "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Document.Tests\Wpf.Document.Tests.csproj" %*
call :run "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Geometry.Tests\Wpf.Geometry.Tests.csproj" %*
call :run "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Input.Tests\Wpf.Input.Tests.csproj" %*
call :run "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Printing.Tests\Wpf.Printing.Tests.csproj" %*
call :run "%REPO%\src\Microsoft.DotNet.Wpf\tests\CrossPlatform\Wpf.Text.Tests\Wpf.Text.Tests.csproj" %*

exit /b %RC%

:run
echo ^>^> %~n1
"%DOTNET%" test %1 -c Release --nologo %2 %3 %4 %5 %6 %7 %8 %9
if errorlevel 1 set RC=1
goto :eof
