# Builds and launches the gallery against the from-source WPF runtime (testhost) with the
# managed WebGPU compositor enabled. Pass a number of seconds to auto-close (for CI), else
# it stays open. Requires the testhost staged at C:\Git\wpf-testhost (see the port memory).
param([int]$Seconds = 0, [switch]$Native)

$ErrorActionPreference = "Stop"
$th  = "C:\Git\wpf-testhost"
$dir = "C:\Git\wpf-webgpu-gallery"
$app = "$dir\bin\Release\net10.0-windows\WpfWebGpuGallery.dll"

# Rebuild the WebGPU backend + the gallery so the freshest bits deploy.
& "C:\Git\wpf\.dotnet\dotnet.exe" build "C:\Git\wpf\src\Microsoft.DotNet.Wpf\src\WgpuInterop\WgpuInterop.csproj" -c Release | Out-Null
& "C:\Git\wpf\.dotnet\dotnet.exe" build "$dir\WpfWebGpuGallery.csproj" -c Release | Out-Null

$env:DOTNET_ROOT = $th
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
if (-not $Native) { $env:WPF_USE_WEBGPU_COMPOSITION = "1" } else { Remove-Item Env:\WPF_USE_WEBGPU_COMPOSITION -ErrorAction SilentlyContinue }
$env:WPF_WEBGPU_SINK_LOG = "$dir\gallery.log"
if (Test-Path $env:WPF_WEBGPU_SINK_LOG) { Remove-Item $env:WPF_WEBGPU_SINK_LOG }

Write-Host ("Launching gallery ({0})..." -f $(if ($Native) { "native milcore" } else { "WebGPU backend" }))
if ($Seconds -gt 0) { & "$th\dotnet.exe" $app $Seconds } else { & "$th\dotnet.exe" $app }
Write-Host "exit = $LASTEXITCODE"
