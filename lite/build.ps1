# Builds YtMiniLite into lite\dist\ without a .NET SDK: csc.exe from .NET Framework 4.x (ships with Windows)
# + the latest yt-dlp and Deno from GitHub (yt-dlp needs Deno for YouTube).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$cache = Join-Path $root '.cache'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $cache, "$dist\tools" | Out-Null

# Downloads the latest release asset of a GitHub repo into tools\<name>, plus the repo's license file.
function Get-Release($repo, $assetName, $name, $licensePath) {
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
    $dir = Join-Path $cache "$name-$($rel.tag_name)"
    if (-not (Test-Path $dir)) {
        $asset = $rel.assets | Where-Object { $_.name -eq $assetName }
        Invoke-WebRequest $asset.browser_download_url -OutFile "$dir.zip"
        Expand-Archive "$dir.zip" $dir
        Remove-Item "$dir.zip"
    }
    $license = Join-Path $dir (Split-Path $licensePath -Leaf)
    if (-not (Test-Path $license)) {
        Invoke-WebRequest "https://raw.githubusercontent.com/$repo/$($rel.tag_name)/$licensePath" -OutFile $license
    }
    if (Test-Path "$dist\tools\$name") { Remove-Item -Recurse -Force "$dist\tools\$name" }
    Copy-Item -Recurse $dir "$dist\tools\$name"
    Write-Host "$name $($rel.tag_name)"
}

Get-Release 'yt-dlp/yt-dlp' 'yt-dlp_win.zip' 'yt-dlp' 'LICENSE'
Get-Release 'denoland/deno' 'deno-x86_64-pc-windows-msvc.zip' 'deno' 'LICENSE.md'
Copy-Item -Recurse -Force "$root\lang" $dist

# Compile; WinRT (Windows.Media.Playback) is referenced from the system .winmd files
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$winmd = "$env:WINDIR\System32\WinMetadata"
& "$fx\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ "/out:$dist\YtMiniLite.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    "/r:$fx\System.Runtime.dll" "/r:$fx\System.Runtime.WindowsRuntime.dll" "/r:$fx\System.Runtime.InteropServices.WindowsRuntime.dll" `
    "/r:$winmd\Windows.Foundation.winmd" "/r:$winmd\Windows.Media.winmd" "/r:$winmd\Windows.Storage.winmd" `
    "$root\src\YtMiniLite.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Host "Done: $dist\YtMiniLite.exe"
