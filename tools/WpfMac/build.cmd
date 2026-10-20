@echo off
rem Builds wpfmac into artifacts\wpfmac\<rid>\ (build.sh is the macOS/Linux twin):
rem
rem   launcher\                  wpfmac itself: dotnet launcher\wpfmac.dll run|compare <App.dll>
rem   overlay\wpfmac.payload\    the WebGPU stack an app runs on (the published WpfMac.Payload)
rem
rem That folder is all of wpfmac. Removing it plus `wpfmac clean --all` leaves nothing behind. On Windows
rem the app's P/Invokes reach the real DLLs, except calls on the stack's own windows, which the P/Invoke
rem rewriter routes to the stack (Documentation/pinvoke-rewriter.md).
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

rem Only this build's own two folders are cleaned: other folders beside them (an overlay-* or launcher-*
rem kept for comparison) are left alone. WPFMAC_LAUNCHER_NAME / WPFMAC_OVERLAY_NAME build into other
rem names under the RID folder (default launcher, overlay).
set "LNAME=%WPFMAC_LAUNCHER_NAME%"
if "%LNAME%"=="" set "LNAME=launcher"
set "ONAME=%WPFMAC_OVERLAY_NAME%"
if "%ONAME%"=="" set "ONAME=overlay"
if exist "%OUT%\%LNAME%" rmdir /s /q "%OUT%\%LNAME%"
if exist "%OUT%\%ONAME%" rmdir /s /q "%OUT%\%ONAME%"
echo ^>^> launcher -^> %OUT%\%LNAME%
dotnet publish "%HERE%WpfMac.Launcher" -c Release -o "%OUT%\%LNAME%" -nologo -v q || (popd & exit /b 1)
echo ^>^> overlay (%RID%) -^> %OUT%\%ONAME%\wpfmac.payload
dotnet publish "%HERE%WpfMac.Payload" -c Release -r %RID% --self-contained false -o "%OUT%\%ONAME%\wpfmac.payload" -nologo -v q -clp:NoSummary -nowarn:MSB3277 || (popd & exit /b 1)
echo ^>^> done: dotnet %OUT%\%LNAME%\wpfmac.dll compare ^<App.dll^>
popd
