@echo off
rem Pack WpfWebGpu.Sdk + WpfWebGpu.Fonts into the local feed (artifacts\local-feed).
rem Windows equivalent of assemble.sh; all layout logic lives in the pack csprojs.
rem
rem This packs whatever is already in the build outputs -- it builds nothing. Use
rem eng\build-sdk.cmd to build the fork and pack it in one go.
setlocal
set HERE=%~dp0
set FEED=%HERE%..\..\artifacts\local-feed

rem `dotnet pack` compares the .nupkg against the PROJECT, not against the outputs the nuspec
rem pulls in from artifacts\bin, so a repack after rebuilding the fork is silently a no-op.
del /q "%FEED%\WpfWebGpu.Sdk.*.nupkg" 2>nul
del /q "%FEED%\WpfWebGpu.Fonts.*.nupkg" 2>nul

dotnet pack "%HERE%WpfWebGpu.Sdk.csproj" -c Release -o "%FEED%" ^
  -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false ^
  --nologo -v:m || exit /b 1
dotnet pack "%HERE%..\WpfWebGpu.Fonts\WpfWebGpu.Fonts.csproj" -c Release -o "%FEED%" ^
  -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false ^
  --nologo -v:m || exit /b 1
echo PACKED -^> %FEED%
