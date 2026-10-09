# Builds both apps and packs ready-to-run zips into release\ (attach them to a GitHub release).
# Usage: powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.0.0
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'release'
Add-Type -AssemblyName System.IO.Compression.FileSystem

& "$root\build.ps1"
& "$root\lite\build.ps1"

function Pack($name, $dist) {
    $stage = Join-Path $out "stage\$name"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    Copy-Item -Recurse $dist $stage
    Copy-Item "$root\README.md", "$root\THIRD_PARTY_NOTICES.md" $stage
    if (Test-Path "$root\LICENSE") { Copy-Item "$root\LICENSE" $stage }
    $zip = Join-Path $out "$name-v$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, 'Optimal', $true)
    Remove-Item -Recurse -Force $stage
    Write-Host ("{0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}

New-Item -ItemType Directory -Force $out | Out-Null
Pack 'YtMiniPlayer' "$root\dist"
Pack 'YtMiniLite' "$root\lite\dist"
Remove-Item -Recurse -Force (Join-Path $out 'stage')
