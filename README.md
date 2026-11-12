# Flyleaf YTDLP

**yt-dlp playback for [FlyleafLib](https://github.com/SuRGeoNix/Flyleaf)** — with optional Chrome/Brave cookie access, including private windows. No Python installation, browser database extraction, or per-application native-host setup.

**Windows x64 · .NET 10 · FlyleafLib 3.11.16+**  
[NuGet](https://www.nuget.org/packages/FlyleafLib.Plugins.YTDLP) · [GitHub Releases](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP/releases) · [Source](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP)

## For application developers

**Recommended: NuGet**

```powershell
dotnet add package FlyleafLib.Plugins.YTDLP
```

The package references [FlyleafLib](https://www.nuget.org/packages/FlyleafLib) **`[3.11.16,)`** (minimum inclusive). NuGet's `buildTransitive` target copies the plugin DLL when your application builds or publishes; you don't need to edit your `.csproj` to copy it.

**Alternative: manual installation**

Download `FlyleafLib.Plugins.YTDLP-win-x64.zip` from [GitHub Releases](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP/releases) and extract its `Plugins/YTDLP/` directory into your application's output folder.

In either case, the required application-local layout is:

```text
MyPlayer/
└── Plugins/
    └── YTDLP/
        └── FlyleafLib.Plugins.YTDLP.dll
```

Flyleaf discovers the plugin through its existing plugin loading mechanism. The NuGet package is intentionally *not* a normal compile-time library reference: the plugin DLL lives in `tools/`, with an empty framework marker in `lib/` and a build target that deploys it to `Plugins/YTDLP/`. It does not ship a plugin `.deps.json`, Python, Deno, the extension, or the native host. An optional `.pdb` can be included in the manual ZIP; `dotnet pack` also produces an optional NuGet symbol package (`.snupkg`) for manual publication.

Build against the oldest supported FlyleafLib version; newer FlyleafLib versions do not require a new plugin build unless an API changes incompatibly. **Your application controls plugin DLL updates** (through its NuGet dependency or manual deployment); the plugin does not update its own DLL.

## What installs automatically?

The plugin manages shared tools under `%LOCALAPPDATA%\Flyleaf\YTDLP\`, reused by all Flyleaf applications running as the same Windows user:

| Component | When installed | Updates |
|---|---|---|
| **yt-dlp** | First media extraction | Upstream `yt-dlp -U` |
| **Deno** | When needed for YouTube | Upstream `deno upgrade` |
| **Browser host** (.NET Native AOT) | Only with `BrowserCookies=true` | Compatible releases from this repository |
| **Plugin DLL** | Bundled by your application | Through your application's NuGet or manual update process |

The host is the local **Native Messaging bridge** between the browser extension and Flyleaf. Chrome/Brave launches it automatically; it is shared, versioned, and registered per Windows user. There is no service or separate installer. The plugin checks for tool/host updates periodically (at most once every 24 hours) without requiring a new application release.

## Optional browser cookies

1. Download `Flyleaf.BrowserExtension-vX.Y.Z.zip` from [Releases](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP/releases) and extract it into a **permanent folder**.
2. Visit `brave://extensions` or `chrome://extensions`, enable **Developer mode**, then choose **Load unpacked** and select that folder.
3. Set `BrowserCookies=true` in YTDLP's plugin options and open a URL. The plugin will install/register the shared native host automatically.
4. Open the extension's **Options** page, approve your application once, then retry playback. If the browser was already running during host setup, use **Reconnect browser bridge** in Options.

For private/incognito cookies, also enable **Allow in Private/Incognito** in the browser's extension settings and select `BrowserCookiesMode=Private` (or `Auto`). Browser cookies are requested only for playback and aren't saved by the extension; yt-dlp temporary cookie files are removed after extraction, with stale-file cleanup after abnormal termination.

Useful plugin options:

```ini
BrowserCookies=false             # Enable the bridge by changing this to true
BrowserCookiesSource=Auto        # Auto, Chrome, Brave
BrowserCookiesMode=Normal        # Normal, Private, Auto
ForwardCookies=true             # Also forward relevant cookies to FFmpeg
```

**Extension updates are manual:** replace the files in the same extracted folder and click **Reload** on the browser's extensions page. GitHub Releases is the only extension distribution channel; the browser does not update unpacked extensions automatically. The extension and host use an independent, validated browser-cookie protocol (currently v3).

## Build & releases

The repository contains the plugin, .NET 10 Native AOT host, and Manifest V3 extension. Build on Windows with Visual Studio, the .NET 10 SDK, and Native AOT C++ tools. GitHub Actions tests the extension, builds both .NET projects, verifies component/protocol versions, and creates the GitHub ZIP release assets. NuGet packages are built and published manually.

```powershell
dotnet build Plugin/Plugin.csproj -c Release
dotnet pack Plugin/Plugin.csproj -c Release --no-build -o dist/nuget
dotnet publish BrowserHost/BrowserHost.csproj -c Release -r win-x64 -p:PublishProfile=FolderProfile
```

On plugin-version tag pushes (for example, `v2.0.0`), GitHub Actions publishes the GitHub ZIP assets **only**. It does not pack or publish NuGet packages. Build the NuGet package locally with `dotnet pack` (as shown above), then publish it manually to NuGet.org when ready.

For manual NuGet publishing, versioning, tagging, and validation, see the [release guide](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP/blob/master/docs/RELEASE.md). **Plugin v2.0.0, host v1.0.0 and extension v1.0.0** are the initial public component versions; later versions are listed separately in each GitHub Release.
