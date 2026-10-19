# Compiles the DXBC translator's own corpus (manifest.txt) with fxc into bin\, one blob per entry
# point and profile: <file>.<entry>.<profile>.dxbc. The blobs are checked in: the tests read them
# and need no compiler. Run on Windows with the Windows SDK installed:  pwsh build.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $fxc) { throw 'fxc.exe not found (install the Windows SDK)' }
$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$failed = 0
foreach ($line in Get-Content (Join-Path $here 'manifest.txt')) {
    if ($line -match '^\s*(#|$)') { continue }
    $parts = $line -split '\s+' | Where-Object { $_ }
    $file, $entry = $parts[0], $parts[1]
    foreach ($spec in $parts[2..($parts.Count - 1)]) {
        $pieces = $spec -split ':'
        $prof = $pieces[0]
        $extra = if ($pieces.Count -gt 1) { $pieces[1] } else { $null }
        $defines = @()
        if ($prof -match '_4_1$') { $defines += '/DSM41' }
        if ($prof -match '_5_') { $defines += '/DSM5' }
        if ($extra) { $defines += "/D$extra" }
        $name = '{0}.{1}.{2}.dxbc' -f [IO.Path]::GetFileNameWithoutExtension($file), $entry, $prof
        & $fxc.FullName /nologo /T $prof /E $entry @defines /Fo (Join-Path $out $name) (Join-Path $here "hlsl\$file") | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "FAILED $name"; $failed++ } else { Write-Host "ok     $name" }
    }
}
if ($failed) { throw "$failed shader(s) did not compile" }
