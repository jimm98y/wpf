@echo off
rem Pack WpfWebGpu.Sdk + WpfWebGpuWasm.Sdk into the local feed (artifacts\local-feed).
rem Windows equivalent of assemble.sh; all layout logic lives in the pack csprojs.
setlocal
set HERE=%~dp0
set FEED=%HERE%..\..\artifacts\local-feed

dotnet pack "%HERE%WpfWebGpu.Sdk.csproj" -c Release -o "%FEED%" ^
  -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false ^
  --nologo -v:m || exit /b 1
dotnet pack "%HERE%..\WpfWebGpuWasm.Sdk\WpfWebGpuWasm.Sdk.csproj" -c Release -o "%FEED%" ^
  -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false ^
  --nologo -v:m || exit /b 1
echo PACKED -^> %FEED%
