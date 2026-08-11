# Builds and launches the gallery with the managed WebGPU compositor enabled. Pass a number of
# seconds to auto-close (for CI), else it stays open.
#
# No testhost and no DOTNET_ROOT juggling: the gallery references this fork's WPF assemblies out of
# artifacts/bin and runs on the stock shared runtime. It deliberately does not use
# Microsoft.WindowsDesktop.App -- that framework's own System.Windows.Forms would shadow the Mono one
# the WinForms card hosts (see the csproj). Run build.cmd first so artifacts/bin is populated.
param([int]$Seconds = 0)

$ErrorActionPreference = "Stop"
$dotnet = "C:\Git\wpf\.dotnet\dotnet.exe"
$dir = $PSScriptRoot
$app = "$dir\bin\Release\net10.0\WpfWebGpuGallery.exe"

# Rebuild the WebGPU backend + the gallery so the freshest bits deploy.
& $dotnet build "$PSScriptRoot\..\..\src\Microsoft.DotNet.Wpf\src\WgpuInterop\WgpuInterop.csproj" -c Release | Out-Null
& $dotnet build "$dir\WpfWebGpuGallery.csproj" -c Release | Out-Null

$env:WPF_USE_WEBGPU_COMPOSITION = "1"
$env:WPF_WEBGPU_SINK_LOG = "$dir\gallery.log"
if (Test-Path $env:WPF_WEBGPU_SINK_LOG) { Remove-Item $env:WPF_WEBGPU_SINK_LOG }

Write-Host "Launching gallery (WebGPU backend)..."
if ($Seconds -gt 0) { & $app $Seconds } else { & $app }
Write-Host "exit = $LASTEXITCODE"
