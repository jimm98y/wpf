#requires -Version 5.1
<#
.SYNOPSIS
  Downloads the pinned wgpu-native release for one or more RIDs and stages the
  native shared library + headers under native/<rid>/.

.DESCRIPTION
  wgpu-native is the Rust WebGPU implementation exposing the standard webgpu.h
  C ABI. This is the cross-platform GPU backend that replaces milcore's Direct3D
  renderer. Binaries are NOT committed (see .gitignore); run this script to
  populate them. The version is pinned to keep the hand-written binding in
  Wgpu.cs in lock-step with the header ABI it was transcribed from.

.PARAMETER Rid
  One or more .NET RIDs. Defaults to the current host RID.
#>
[CmdletBinding()]
param(
    [string[]] $Rid
)

$ErrorActionPreference = 'Stop'

# Pinned wgpu-native version. Bump together with Wgpu.cs (re-check the headers).
$Version = 'v29.0.1.1'

# RID -> release asset (release builds; *-debug.zip also exist).
$AssetMap = @{
    'win-x64'     = 'wgpu-windows-x86_64-msvc-release.zip'
    'win-x86'     = 'wgpu-windows-i686-msvc-release.zip'
    'win-arm64'   = 'wgpu-windows-aarch64-msvc-release.zip'
    'linux-x64'   = 'wgpu-linux-x86_64-release.zip'
    'linux-arm64' = 'wgpu-linux-aarch64-release.zip'
    'osx-x64'     = 'wgpu-macos-x86_64-release.zip'
    'osx-arm64'   = 'wgpu-macos-aarch64-release.zip'
}

if (-not $Rid -or $Rid.Count -eq 0) {
    $Rid = @([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier)
}

$root = Split-Path -Parent $PSScriptRoot          # .../WgpuInterop
$nativeRoot = Join-Path $root 'native'
New-Item -ItemType Directory -Force -Path $nativeRoot | Out-Null

foreach ($r in $Rid) {
    $asset = $AssetMap[$r]
    if (-not $asset) {
        Write-Error "No wgpu-native asset mapping for RID '$r'. Known: $($AssetMap.Keys -join ', ')"
        continue
    }

    $url = "https://github.com/gfx-rs/wgpu-native/releases/download/$Version/$asset"
    $destDir = Join-Path $nativeRoot $r
    $zip = Join-Path $nativeRoot "$asset"

    Write-Host "Fetching $Version for $r" -ForegroundColor Cyan
    Write-Host "  $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    if (Test-Path $destDir) { Remove-Item -Recurse -Force $destDir }
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Expand-Archive -Path $zip -DestinationPath $destDir -Force
    Remove-Item $zip -Force

    $lib = Get-ChildItem -Path (Join-Path $destDir 'lib') -Filter 'wgpu_native.*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.dll', '.so', '.dylib') } |
        Select-Object -First 1
    if ($lib) {
        Write-Host "  staged $($lib.Name) -> native/$r/lib/" -ForegroundColor Green
    } else {
        Write-Warning "  expected shared library not found under native/$r/lib/"
    }
}

Write-Host "Done. native/<rid>/lib/ holds wgpu_native.{dll,so,dylib}; include/ holds the pinned headers." -ForegroundColor Cyan
