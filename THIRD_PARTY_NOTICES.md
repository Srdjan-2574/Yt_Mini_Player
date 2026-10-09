# Third-party notices

The release zips bundle the following third-party components, **unmodified**, exactly as published by their
authors. The build scripts download them at build time; they are not stored in this repository.

| Component | Used by | License | Where the license is in the release | Source |
|---|---|---|---|---|
| uBlock Origin | YtMiniPlayer | GPL-3.0 | `ublock/LICENSE.txt` | https://github.com/gorhill/uBlock |
| Microsoft Edge WebView2 SDK (`Microsoft.Web.WebView2.*.dll`, `WebView2Loader.dll`) | YtMiniPlayer | Microsoft WebView2 SDK license | `licenses/WebView2-LICENSE.txt`, `licenses/WebView2-NOTICE.txt` | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| yt-dlp (Windows build, incl. its bundled Python libraries) | YtMiniLite | Unlicense (bundled libraries: see file) | `tools/yt-dlp/LICENSE`, `tools/yt-dlp/_internal/THIRD_PARTY_LICENSES.txt` | https://github.com/yt-dlp/yt-dlp |
| Deno | YtMiniLite | MIT | `tools/deno/LICENSE.md` | https://github.com/denoland/deno |

uBlock Origin is loaded as a separate browser extension and is not linked into YtMiniPlayer. Its complete source
code for every bundled version is available from the uBlock Origin repository linked above (see the release tag
matching the version in `ublock/manifest.json`).

The apps also use components that ship with Windows and are not redistributed: the Microsoft Edge WebView2
Runtime, .NET Framework 4.x and the Windows Media Playback (WinRT) APIs.

YouTube and YouTube Music are trademarks of Google LLC. This project is not affiliated with, endorsed by or
sponsored by Google or YouTube.
