@echo off
rem Builds wpfmac into artifacts\wpfmac\<rid>\ (build.sh is the macOS/Linux twin):
rem
rem   launcher\                  wpfmac itself: dotnet launcher\wpfmac.dll run|compare <App.dll>
rem   overlay\wpfmac.payload\    the WebGPU stack an app runs on (the published WpfMac.Payload)
rem
rem That folder is all of wpfmac. Removing it plus `wpfmac clean --all` leaves nothing behind. There is
rem no libwin32shim on Windows: the app's P/Invokes reach the real DLLs.
rem
rem The overlay is built against the WpfWebGpu.Sdk package in artifacts\local-feed, so build and pack
rem the fork first (eng\build-sdk.cmd). Usage:
rem   tools\WpfMac\build.cmd                 for this machine's RID
rem   tools\WpfMac\build.cmd win-arm64       for another

setlocal
set "HERE=%~dp0"
for %%I in ("%HERE%..\..") do set "REPO=%%~fI"
pushd "%HERE%"

set "RID=%~1"
if "%RID%"=="" (
  if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" (set "RID=win-arm64") else (set "RID=win-x64")
)

set "OUT=%REPO%\artifacts\wpfmac\%RID%"
if not exist "%REPO%\artifacts\local-feed\WpfWebGpu.Sdk.0.1.1.nupkg" (
  echo error: artifacts\local-feed has no WpfWebGpu.Sdk; run eng\build-sdk.cmd first. 1>&2
  popd & exit /b 1
)

rem The SDK package version never changes between builds, so NuGet would keep serving the copy it
rem extracted the first time; take the one just packed.
set "NUGETROOT=%NUGET_PACKAGES%"
if "%NUGETROOT%"=="" set "NUGETROOT=%USERPROFILE%\.nuget\packages"
if exist "%NUGETROOT%\wpfwebgpu.sdk" rmdir /s /q "%NUGETROOT%\wpfwebgpu.sdk"
if exist "%NUGETROOT%\wpfwebgpu.fonts" rmdir /s /q "%NUGETROOT%\wpfwebgpu.fonts"

if exist "%OUT%" rmdir /s /q "%OUT%"
echo ^>^> launcher -^> %OUT%\launcher
dotnet publish "%HERE%WpfMac.Launcher" -c Release -o "%OUT%\launcher" -nologo -v q || (popd & exit /b 1)
echo ^>^> overlay (%RID%) -^> %OUT%\overlay\wpfmac.payload
dotnet publish "%HERE%WpfMac.Payload" -c Release -r %RID% --self-contained false -o "%OUT%\overlay\wpfmac.payload" -nologo -v q -clp:NoSummary -nowarn:MSB3277 || (popd & exit /b 1)
echo ^>^> done: dotnet %OUT%\launcher\wpfmac.dll compare ^<App.dll^>
popd
