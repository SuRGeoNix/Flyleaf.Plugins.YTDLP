# Flyleaf YTDLP

**yt-dlp playback for [FlyleafLib](https://github.com/SuRGeoNix/Flyleaf)** — with optional Chrome/Brave cookie access, including private windows. No Python installation, browser database extraction, or per-application native-host setup.

**Windows x64 · .NET 10 · FlyleafLib 3.11.16+**  
[GitHub Releases](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP/releases) · [Source](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP)

## For application developers

1. Download **`FlyleafLib.Plugins.YTDLP-win-x64.zip`** from [Releases](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP/releases).
2. Extract its `Plugins/YTDLP/` directory into your application folder:

   ```text
   MyPlayer/
   └── Plugins/
       └── YTDLP/
           ├── FlyleafLib.Plugins.YTDLP.dll
           ├── FlyleafLib.Plugins.YTDLP.deps.json  (if included)
           └── FlyleafLib.Plugins.YTDLP.pdb        (optional)
   ```

3. That's it. Flyleaf loads the plugin using its existing plugin discovery. **Your application manages updates to the DLL**; no plugin self-update or FlyleafLib changes are required.

The DLL references [FlyleafLib NuGet](https://www.nuget.org/packages/FlyleafLib) **`[3.11.16,)`** (minimum inclusive). Build against the oldest supported version; newer FlyleafLib versions do not require a new plugin build unless an API changes incompatibly.

## What installs automatically?

The plugin manages shared tools under `%LOCALAPPDATA%\Flyleaf\YTDLP\`, reused by all Flyleaf applications running as the same Windows user:

| Component | When installed | Updates |
|---|---|---|
| **yt-dlp** | First media extraction | Upstream `yt-dlp -U` |
| **Deno** | When needed for YouTube | Upstream `deno upgrade` |
| **Browser host** (.NET Native AOT) | Only with `BrowserCookies=true` | Compatible releases from this repository |
| **Plugin DLL** | Bundled by your application | Your application's update process |

The host is the local **Native Messaging bridge** between the browser extension and Flyleaf. Chrome/Brave launches it automatically; it is shared, versioned, and registered per Windows user. There is no service or separate installer. The plugin checks for tool/host updates periodically (at most once every 24 hours) without requiring a new application release.

## Optional browser cookies

1. Download **`Flyleaf.BrowserExtension-vX.Y.Z.zip`** from [Releases](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP/releases) and extract it into a **permanent folder**.
2. Visit `brave://extensions` or `chrome://extensions`, enable **Developer mode**, then choose **Load unpacked** and select that folder.
3. Set `BrowserCookies=true` in YTDLP's plugin options and open a URL. The plugin will install/register the shared native host automatically.
4. Open the extension's **Options** page, approve your application once, then retry playback. If the browser was already running during host setup, use **Reconnect browser bridge** in Options.

For private/incognito cookies, also enable **Allow in Private/Incognito** in the browser's extension settings and select `BrowserCookiesMode=Private` (or `Auto`). Browser cookies are requested only for playback and aren't saved by the extension; yt-dlp temporary cookie files are removed after extraction, with stale-file cleanup after abnormal termination.

Useful plugin options:

```ini
BrowserCookies=false             # Enable the bridge
BrowserCookiesSource=Auto        # Auto, Chrome, Brave
BrowserCookiesMode=Normal        # Normal, Private, Auto
ForwardCookies=true             # Also forward relevant cookies to FFmpeg
```

**Extension updates are manual:** replace the files in the same extracted folder and click **Reload** on the browser's extensions page. GitHub Releases is the only extension distribution channel; the browser does not update unpacked extensions automatically. The extension and host use an independent, validated browser-cookie protocol (currently v3).

## Build & releases

The solution contains the plugin, .NET 10 Native AOT host, and Manifest V3 extension. Build on Windows with Visual Studio, the .NET 10 SDK, and Native AOT C++ tools. The CI workflow tests the extension, builds both .NET projects, checks component/protocol versions, and packages the GitHub release assets.

```powershell
dotnet build Plugin/Plugin.csproj -c Release
dotnet publish BrowserHost/BrowserHost.csproj -c Release -r win-x64 -p:PublishProfile=FolderProfile
```

For repository setup and tagging, see [Release guide](docs/RELEASE.md). **Plugin v2.0.0, host v1.0.0 and extension v1.0.0** are the initial public component versions; their later versions are listed separately in each GitHub Release.
