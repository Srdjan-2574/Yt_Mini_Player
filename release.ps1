# Builds the apps and packs ready-to-run zips into release\ (attach them to a GitHub release).
# Usage: powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.0.0
#        powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.0.1 -Apps YtMiniLite
param(
    [string]$Version = '1.0.0',
    [ValidateSet('YtMiniPlayer', 'YtMiniLite')][string[]]$Apps = @('YtMiniPlayer', 'YtMiniLite')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'release'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function Pack($name, $dist) {
    $stage = Join-Path $out "stage\$name"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    Copy-Item -Recurse $dist $stage
    Copy-Item "$root\README.md", "$root\THIRD_PARTY_NOTICES.md" $stage
    if (Test-Path "$root\LICENSE") { Copy-Item "$root\LICENSE" $stage }
    $zip = Join-Path $out "$name-v$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    # Entries are added one by one because ZipFile.CreateFromDirectory on .NET Framework writes "\" in entry
    # names, which the zip format doesn't allow (some unzip tools then create files named "folder\file").
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $entry = "$name/" + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, 'Optimal') | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
    Remove-Item -Recurse -Force $stage
    Write-Host ("{0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}

New-Item -ItemType Directory -Force $out | Out-Null
if ($Apps -contains 'YtMiniPlayer') {
    & "$root\build.ps1"
    Pack 'YtMiniPlayer' "$root\dist"
}
if ($Apps -contains 'YtMiniLite') {
    & "$root\lite\build.ps1"
    Pack 'YtMiniLite' "$root\lite\dist"
}
Remove-Item -Recurse -Force (Join-Path $out 'stage')
