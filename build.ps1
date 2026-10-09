# Builds YtMiniPlayer into dist\ without a .NET SDK: csc.exe from .NET Framework 4.x (ships with Windows)
# + the WebView2 SDK from NuGet + the latest uBlock Origin from GitHub.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$cache = Join-Path $root '.cache'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $cache, "$dist\licenses" | Out-Null

function Get-Zip($url, $dir) {
    if (Test-Path $dir) { return }
    $zip = "$dir.zip"
    Invoke-WebRequest $url -OutFile $zip
    Expand-Archive $zip $dir
    Remove-Item $zip
}

# WebView2 SDK (redistributable DLLs + their license)
$ver = (Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/index.json').versions |
    Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
$sdk = Join-Path $cache "webview2-$ver"
Get-Zip "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$ver/microsoft.web.webview2.$ver.nupkg" $sdk
Copy-Item "$sdk\lib\net462\Microsoft.Web.WebView2.Core.dll",
          "$sdk\lib\net462\Microsoft.Web.WebView2.WinForms.dll",
          "$sdk\runtimes\win-x64\native\WebView2Loader.dll" $dist
Copy-Item "$sdk\LICENSE.txt" "$dist\licenses\WebView2-LICENSE.txt"
Copy-Item "$sdk\NOTICE.txt" "$dist\licenses\WebView2-NOTICE.txt"
Write-Host "WebView2 SDK $ver"

# uBlock Origin (its GPLv3 LICENSE.txt ships inside the extension folder)
$rel = Invoke-RestMethod 'https://api.github.com/repos/gorhill/uBlock/releases/latest'
$asset = $rel.assets | Where-Object { $_.name -like '*.chromium.zip' } | Select-Object -First 1
$ubo = Join-Path $cache "ublock-$($rel.tag_name)"
Get-Zip $asset.browser_download_url $ubo
$manifest = Get-ChildItem $ubo -Recurse -Filter manifest.json | Select-Object -First 1
if (Test-Path "$dist\ublock") { Remove-Item -Recurse -Force "$dist\ublock" }
Copy-Item -Recurse $manifest.DirectoryName "$dist\ublock"
Write-Host "uBlock Origin $($rel.tag_name)"

# Compile
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
#   /win32icon: the icon Explorer, shortcuts and the taskbar show; /resource: the same icon for the window and tray
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$dist\YtMiniPlayer.exe" `
    "/win32icon:$root\src\YtMiniPlayer.ico" "/resource:$root\src\YtMiniPlayer.ico,app.ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/r:$dist\Microsoft.Web.WebView2.Core.dll" "/r:$dist\Microsoft.Web.WebView2.WinForms.dll" `
    "$root\src\YtMiniPlayer.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Host "Done: $dist\YtMiniPlayer.exe"
