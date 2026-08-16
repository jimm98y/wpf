#requires -Version 5.1
<#
.SYNOPSIS
  Stages the native GPU payload -- wgpu-native and ANGLE -- under native/<rid>/, which is
  exactly what WpfWebGpu.Sdk packs into its NuGet package.

.DESCRIPTION
  wgpu-native is the Rust WebGPU implementation exposing the standard webgpu.h C ABI: the
  cross-platform GPU backend that replaces milcore's Direct3D renderer. ANGLE (libEGL +
  libGLESv2) is the OpenGL ES implementation wgpu falls back to where the GPU is virtualized
  and there is no working D3D12 or Vulkan -- a VM, a remote session, an old driver. Both are
  prebuilt binary drops, neither is committed (see .gitignore), and both belong in the same
  native/<rid>/lib/ folder, so this script fetches both.

  The wgpu-native version is pinned to keep the hand-written binding in Wgpu.cs in lock-step
  with the header ABI it was transcribed from.

.PARAMETER Rid
  One or more .NET RIDs. Defaults to the current host RID.

.PARAMETER All
  Every RID the WpfWebGpu.Sdk package ships a backend for. This is what a machine that PACKS the
  SDK needs: the package carries all heads' natives, so that a package built anywhere is the same
  package (see WpfWebGpu.Sdk.csproj). Packing with any of them missing is an error.

.PARAMETER SkipAngle
  Fetch only wgpu-native.

.NOTES
  WHERE ANGLE COMES FROM. The ANGLE project (github.com/google/angle) publishes no releases and
  no tags -- it has never shipped a binary, and its own guidance is to copy the libraries out of
  an installed Chrome. So the source here is the official Electron release, which is Chromium's
  ANGLE built for every desktop platform and architecture we ship, at stable versioned URLs, and
  is the only GitHub-hosted build covering win-arm64. ANGLE is BSD-3-Clause (Copyright Google
  Inc.); Electron is MIT. The libraries taken are unmodified.

  Only the two files are taken, not the 150 MB archive around them: the download uses HTTP range
  requests and reads the zip's central directory, so fetching ANGLE for one platform moves about
  7 MB. It falls back to downloading the whole asset if the server will not serve ranges.
#>
[CmdletBinding()]
param(
    [string[]] $Rid,
    [switch] $All,
    [switch] $SkipAngle
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
    # iOS can only link the static library into the app (see Ios/WgpuInterop.Ios.csproj); the
    # directory names match the NativeReference paths the iOS heads use, not .NET RIDs.
    'ios-arm64'           = 'wgpu-ios-aarch64-release.zip'
    'ios-arm64-simulator' = 'wgpu-ios-aarch64-simulator-release.zip'
    # Android ships a real .so (unlike iOS, which can only link the .a statically), so the binding
    # keeps its normal DllImport("wgpu_native") and the head packs this with @(AndroidNativeLibrary).
    # The archives also carry a .a, which is ignored. arm64 is the only ABI worth shipping today --
    # it is every device made this decade and the emulator on an Apple-silicon Mac.
    'android-arm64' = 'wgpu-android-aarch64-release.zip'
    'android-x64'   = 'wgpu-android-x86_64-release.zip'
    'android-arm'   = 'wgpu-android-armv7-release.zip'
}

# The set the SDK package ships. Keep in lock-step with the native/ pack list and the
# WpfWebGpuValidateInputs check in sdk/WpfWebGpu.Sdk/WpfWebGpu.Sdk.csproj.
$ShippedRids = @(
    'win-x64', 'win-arm64',
    'linux-x64', 'linux-arm64',
    'osx-x64', 'osx-arm64',
    'android-arm64', 'android-x64',
    'ios-arm64', 'ios-arm64-simulator'
)

# ---------------------------------------------------------------------------------------------
# ANGLE
#
# Pinned Electron release; see the .NOTES block for why Electron is the source. Bump this and the
# two libraries move together, which is the only way they may be moved: libEGL is a thin front end
# over libGLESv2 built from the same tree, and a mismatched pair fails to initialise.
$AngleVersion = 'v43.4.0'

# RID -> the Electron asset carrying that platform's ANGLE.
#
# DESKTOP ONLY, and deliberately so:
#   Windows  wgpu picks D3D12 first and falls back to GL here. On a virtualized GPU (a VM, a
#            remote session) GL is frequently the ONLY backend that works, so this is the one
#            platform where ANGLE is load-bearing rather than insurance.
#   Linux    the GL backend loads the system EGL (Mesa), which every desktop install has. ANGLE
#            ships for parity and for machines whose driver stack is broken.
#   macOS    Metal only -- wgpu-native has no GL backend there at all.
#   Android  the system GLES driver is always present; ANGLE would duplicate it.
#   iOS      Metal only, and an app may not load a third-party shared library anyway.
$AngleAssetMap = @{
    'win-x64'     = @{ Asset = "electron-$AngleVersion-win32-x64.zip";     Files = @('libEGL.dll', 'libGLESv2.dll') }
    'win-arm64'   = @{ Asset = "electron-$AngleVersion-win32-arm64.zip";   Files = @('libEGL.dll', 'libGLESv2.dll') }
    'win-x86'     = @{ Asset = "electron-$AngleVersion-win32-ia32.zip";    Files = @('libEGL.dll', 'libGLESv2.dll') }
    'linux-x64'   = @{ Asset = "electron-$AngleVersion-linux-x64.zip";     Files = @('libEGL.so', 'libGLESv2.so') }
    'linux-arm64' = @{ Asset = "electron-$AngleVersion-linux-arm64.zip";   Files = @('libEGL.so', 'libGLESv2.so') }
    # Nested inside Electron.app/Contents/Frameworks/...; matched on the leaf name, so the path
    # does not have to be spelled out here (and does not break when Electron reshuffles it).
    'osx-x64'     = @{ Asset = "electron-$AngleVersion-darwin-x64.zip";    Files = @('libEGL.dylib', 'libGLESv2.dylib') }
    'osx-arm64'   = @{ Asset = "electron-$AngleVersion-darwin-arm64.zip";  Files = @('libEGL.dylib', 'libGLESv2.dylib') }
}

# Reads a remote zip through HTTP range requests, so that pulling two files out of a 150 MB asset
# transfers about 7 MB instead of 150. ZipArchive does all the parsing; this only has to make the
# stream seekable. BufferedStream keeps the central-directory scan from turning into one request
# per read.
Add-Type -ReferencedAssemblies System.Net.Http @'
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

public sealed class WpfHttpRangeStream : Stream
{
    private readonly HttpClient _client;
    private readonly string _url;
    private readonly long _length;
    private long _position;

    public WpfHttpRangeStream(string url)
    {
        _url = url;
        _client = new HttpClient();
        _client.Timeout = TimeSpan.FromMinutes(10);

        HttpResponseMessage head = _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url)).Result;
        head.EnsureSuccessStatusCode();
        if (!head.Content.Headers.ContentLength.HasValue)
            throw new IOException("no Content-Length; cannot range-read " + url);
        // A server that ignores Range would hand back the whole asset for every read, silently and
        // very slowly. Insist it says it accepts them.
        bool ranges = false;
        foreach (string v in head.Headers.AcceptRanges) { if (v == "bytes") ranges = true; }
        if (!ranges) throw new IOException("server does not accept byte ranges: " + url);
        _length = head.Content.Headers.ContentLength.Value;
    }

    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return true; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { return _length; } }
    public override long Position { get { return _position; } set { _position = value; } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0 || _position >= _length) return 0;
        long last = Math.Min(_position + count, _length) - 1;
        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.Range = new RangeHeaderValue(_position, last);
        HttpResponseMessage resp = _client.SendAsync(req).Result;
        resp.EnsureSuccessStatusCode();
        byte[] data = resp.Content.ReadAsByteArrayAsync().Result;
        Array.Copy(data, 0, buffer, offset, data.Length);
        _position += data.Length;
        return data.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin == SeekOrigin.Begin) _position = offset;
        else if (origin == SeekOrigin.Current) _position += offset;
        else _position = _length + offset;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _client.Dispose();
        base.Dispose(disposing);
    }
}
'@

function Get-AngleForRid {
    param([string] $Rid, [string] $DestLibDir)

    $spec = $AngleAssetMap[$Rid]
    if (-not $spec) { return }        # a RID with no GL backend to fetch; see the map's comment

    $url = "https://github.com/electron/electron/releases/download/$AngleVersion/$($spec.Asset)"
    Write-Host "Fetching ANGLE $AngleVersion for $Rid" -ForegroundColor Cyan
    Write-Host "  $url"

    $wanted = @{}
    foreach ($f in $spec.Files) { $wanted[$f] = $true }

    $extracted = @()
    $stream = $null; $buffered = $null; $zip = $null; $whole = $null
    try {
        try {
            $stream = New-Object WpfHttpRangeStream $url
            $buffered = New-Object System.IO.BufferedStream $stream, 1048576
            $zip = New-Object System.IO.Compression.ZipArchive $buffered, ([System.IO.Compression.ZipArchiveMode]::Read)
        }
        catch {
            # Range requests refused or no Content-Length: fall back to the whole asset. Correct,
            # just twenty times the bytes -- and saying so beats appearing to hang.
            Write-Warning "  range read unavailable ($($_.Exception.Message)); downloading the full asset"
            $whole = Join-Path ([System.IO.Path]::GetTempPath()) $spec.Asset
            Invoke-WebRequest -Uri $url -OutFile $whole -UseBasicParsing
            $zip = [System.IO.Compression.ZipFile]::OpenRead($whole)
        }

        foreach ($entry in $zip.Entries) {
            $leaf = [System.IO.Path]::GetFileName($entry.FullName)
            if (-not $wanted.ContainsKey($leaf)) { continue }
            $target = Join-Path $DestLibDir $leaf
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            $extracted += $leaf
        }
    }
    finally {
        if ($zip) { $zip.Dispose() }
        if ($buffered) { $buffered.Dispose() }
        if ($stream) { $stream.Dispose() }
        if ($whole -and (Test-Path $whole)) { Remove-Item $whole -Force }
    }

    $missing = @($spec.Files | Where-Object { $extracted -notcontains $_ })
    if ($missing.Count -gt 0) {
        # The pair must move together, so a partial extract is a failure rather than a warning:
        # libEGL without a matching libGLESv2 initialises and then cannot create a context.
        throw "ANGLE for $Rid is incomplete -- $($missing -join ', ') not found in $($spec.Asset). Has the Electron layout changed?"
    }
    Write-Host "  staged $($extracted -join ', ') -> native/$Rid/lib/" -ForegroundColor Green
}

if ($All) {
    $Rid = $ShippedRids
} elseif (-not $Rid -or $Rid.Count -eq 0) {
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

    # Expand into a staging dir and copy over the top, rather than wiping native/<rid> first.
    # That directory is not ours alone: the Windows RIDs also hold the ANGLE drop (libEGL.dll,
    # libGLESv2.dll) beside wgpu_native.dll, and those are what give the head an OpenGL backend on a
    # machine whose GPU is virtualized. Deleting the folder took them with it, silently, and the next
    # pack shipped a Windows head with no GL fallback.
    $staging = Join-Path $nativeRoot "$r.tmp"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    Expand-Archive -Path $zip -DestinationPath $staging -Force
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Copy-Item -Path (Join-Path $staging '*') -Destination $destDir -Recurse -Force
    Remove-Item -Recurse -Force $staging
    Remove-Item $zip -Force

    # The shared library is libwgpu_native.so/.dylib everywhere but Windows, and the static archive
    # (libwgpu_native.a) is the ONLY payload in the iOS drops -- an iOS app cannot load a third-party
    # shared library. Matching only 'wgpu_native.*' reported every non-Windows RID as a failure while
    # it had staged perfectly well.
    # Shared library FIRST, static archive only as a fallback: the non-Windows drops carry both, and
    # reporting the .a for a Linux/macOS/Android RID names the file that head does not use. iOS is
    # the only one where the .a is genuinely the payload.
    $staged = Get-ChildItem -Path (Join-Path $destDir 'lib') -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(lib)?wgpu_native\.(dll|so|dylib|a)$' }
    $lib = $staged | Where-Object { $_.Extension -ne '.a' } | Select-Object -First 1
    if (-not $lib) { $lib = $staged | Select-Object -First 1 }
    if ($lib) {
        Write-Host "  staged $($lib.Name) -> native/$r/lib/" -ForegroundColor Green
    } else {
        Write-Warning "  expected wgpu_native library not found under native/$r/lib/"
    }

    # ANGLE into the SAME lib/ folder, because native/<rid>/lib is the unit the SDK packs: whatever
    # the GPU payload for a RID consists of lands in one directory and ships as one directory.
    if (-not $SkipAngle) {
        Get-AngleForRid -Rid $r -DestLibDir (Join-Path $destDir 'lib')
    }
}

Write-Host "Done. native/<rid>/lib/ holds the GPU payload the SDK packs (wgpu_native plus ANGLE where a head has a GL backend); include/ holds the pinned headers." -ForegroundColor Cyan
