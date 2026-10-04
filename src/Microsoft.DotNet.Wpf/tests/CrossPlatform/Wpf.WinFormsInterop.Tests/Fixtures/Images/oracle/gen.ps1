# Regenerates the image fixtures and their oracle from REAL GDI+: run under Windows PowerShell 5.1,
# whose System.Drawing is the .NET Framework's over gdiplus.dll.
#   powershell -ExecutionPolicy Bypass -File gen.ps1 <outdir>
# writes <outdir>\files\* (the samples) and <outdir>\oracle.json (what GDI+ reads from each, plus
# its API behaviours). Copy both up into Fixtures\Images.
param([string]$Out = "$PSScriptRoot\out")
$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition (Get-Content -Raw "$PSScriptRoot\Oracle.cs.txt") -ReferencedAssemblies System.Drawing
[Oracle]::Run($Out)
"ok"
