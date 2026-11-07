# Publishing Flyleaf YTDLP

The public repository is [SuRGeoNix/Flyleaf.Plugins.YTDLP](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP). GitHub Releases is the only distribution channel for the unpacked Chrome/Brave extension.

## One-time GitHub setup

1. Under **Settings → Actions → General**, enable workflows. The publish job requests `contents: write` for the built-in `GITHUB_TOKEN`; no personal token or variables are required.
2. Use `main` or `master` as the release branch, or adjust `.github/workflows/release.yml`.
3. Keep the repository **public** for anonymous host downloads through GitHub's `/releases/latest` API. The downloader needs a **published stable release**, not a draft or prerelease.

## Releasing

1. Change `Plugin/Plugin.csproj`'s `<Version>` for the new release. Change the host's `<Version>` only when its code changes, and the extension's `manifest.json` version only when its files change. Protocol changes require coordinated host, plugin and extension changes.
2. Commit and push. Confirm **Actions → Build and release** succeeds for the branch. Ordinary pushes build artifacts but **do not publish**.
3. Tag **the same committed version** (first release: `v2.0.0`), then **push the tag**. The tag must match the plugin DLL version.
4. Wait for the tag-triggered Actions run to publish the release. Inspect the GitHub Release page and download its assets to test installation.

A version tag that already has a published release must not be reused or overwritten; increment the plugin version for a subsequent release. If `v2.0.0` has already been published in a test repository, use a clean repository for the first public release or make a new version instead.

## Expected assets

```text
FlyleafLib.Plugins.YTDLP-win-x64.zip
Flyleaf.BrowserCookieHost-win-x64.zip
Flyleaf.BrowserExtension-v1.0.0.zip
component-versions.json
SHA256SUMS.txt
```

The host ZIP contains `Flyleaf.BrowserCookieHost.exe` and `host-compat.json`. The workflow verifies the compiled DLL/EXE file versions, protocol consistency, extension ID, and asset digests. Release notes show the independent component versions and whether they changed since the previous published release.

Only the **plugin DLL** is application-local. yt-dlp, Deno and the browser host are managed by the plugin in `%LOCALAPPDATA%\Flyleaf\YTDLP\`. Browsers update neither the host nor the unpacked extension; extension updates require replacing extracted files and clicking **Reload**.

## Local validation

```powershell
node Tests/extension.test.cjs
python -m unittest discover -s Tests -p test_release_info.py
dotnet build Plugin/Plugin.csproj -c Release
dotnet publish BrowserHost/BrowserHost.csproj -c Release -r win-x64 -p:PublishProfile=FolderProfile
```

On Windows, smoke-test first-run shared tool downloads, browser pairing, private/normal isolation, and host upgrades before marking a release ready for general use.
