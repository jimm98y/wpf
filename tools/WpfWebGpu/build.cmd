@echo off
rem Builds wpfrun into artifacts\wpfrun\<rid>\ (build.sh is the macOS/Linux twin):
rem
rem   launcher\                  wpfrun itself: dotnet launcher\wpfrun.dll run|compare <App.dll>
rem   overlay\wpfrun.payload\    the WebGPU stack an app runs on (the published WpfWebGpu.Payload)
rem
rem That folder is all of wpfrun. Removing it plus `wpfrun clean --all` leaves nothing behind. On Windows
rem the app's P/Invokes reach the real DLLs, except calls on the stack's own windows, which the P/Invoke
rem rewriter routes to the stack (Documentation/pinvoke-rewriter.md).
rem
rem The overlay is built against the WpfWebGpu.Sdk package in artifacts\local-feed, so build and pack
rem the fork first (eng\build-sdk.cmd). Usage:
rem   tools\WpfWebGpu\build.cmd                 for this machine's RID
rem   tools\WpfWebGpu\build.cmd win-arm64       for another

setlocal
set "HERE=%~dp0"
for %%I in ("%HERE%..\..") do set "REPO=%%~fI"
pushd "%HERE%"

set "RID=%~1"
if "%RID%"=="" (
  if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" (set "RID=win-arm64") else (set "RID=win-x64")
)

set "OUT=%REPO%\artifacts\wpfrun\%RID%"
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
rem kept for comparison) are left alone. WPFRUN_LAUNCHER_NAME / WPFRUN_OVERLAY_NAME build into other
rem names under the RID folder (default launcher, overlay).
set "LNAME=%WPFRUN_LAUNCHER_NAME%"
if "%LNAME%"=="" set "LNAME=launcher"
set "ONAME=%WPFRUN_OVERLAY_NAME%"
if "%ONAME%"=="" set "ONAME=overlay"
if exist "%OUT%\%LNAME%" rmdir /s /q "%OUT%\%LNAME%"
if exist "%OUT%\%ONAME%" rmdir /s /q "%OUT%\%ONAME%"
echo ^>^> launcher -^> %OUT%\%LNAME%
dotnet publish "%HERE%WpfWebGpu.Launcher" -c Release -o "%OUT%\%LNAME%" -nologo -v q || (popd & exit /b 1)
echo ^>^> overlay (%RID%) -^> %OUT%\%ONAME%\wpfrun.payload
dotnet publish "%HERE%WpfWebGpu.Payload" -c Release -r %RID% --self-contained false -o "%OUT%\%ONAME%\wpfrun.payload" -nologo -v q -clp:NoSummary -nowarn:MSB3277 || (popd & exit /b 1)
rem The same implementations, and the build-time rewriter, for the heads that bundle the app
rem (WpfWebGpu.Sdk's browser head): the WpfWebGpu.Win32 package, from the overlay just built.
echo ^>^> WpfWebGpu.Win32 package -^> artifacts\local-feed
set "REWRITE=%OUT%\rewrite"
if exist "%REWRITE%" rmdir /s /q "%REWRITE%"
dotnet publish "%HERE%WpfWebGpu.Rewrite" -c Release -o "%REWRITE%" -nologo -v q || (popd & exit /b 1)
if exist "%REPO%\artifacts\local-feed\WpfWebGpu.Win32.*.nupkg" del /q "%REPO%\artifacts\local-feed\WpfWebGpu.Win32.*.nupkg"
dotnet pack "%HERE%WpfWebGpu.Win32.Package\WpfWebGpu.Win32.Package.csproj" -c Release -o "%REPO%\artifacts\local-feed" -p:WpfWebGpuPayloadDir="%OUT%\%ONAME%\wpfrun.payload" -p:WpfWebGpuRewriteDir="%REWRITE%" -nologo -v q || (popd & exit /b 1)
if exist "%NUGETROOT%\wpfwebgpu.win32" rmdir /s /q "%NUGETROOT%\wpfwebgpu.win32"
echo ^>^> done: dotnet %OUT%\%LNAME%\wpfrun.dll compare ^<App.dll^>
popd
