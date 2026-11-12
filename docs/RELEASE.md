# Publishing Flyleaf YTDLP

The public repository is [SuRGeoNix/Flyleaf.Plugins.YTDLP](https://github.com/SuRGeoNix/Flyleaf.Plugins.YTDLP). Application developers can install the plugin from NuGet (`FlyleafLib.Plugins.YTDLP`) or from GitHub ZIP releases. The unpacked Chrome/Brave extension is distributed **only** through GitHub Releases.

**Distribution is deliberately separate:** GitHub Actions builds and publishes the GitHub ZIP assets on version tags. **NuGet is packed and published manually** on the developer's machine; there is no NuGet job, CI artifact, or `NUGET_API_KEY` GitHub secret.

## One-time setup

1. Under **Settings → Actions → General**, enable workflows. The GitHub release job requests `contents: write` for the built-in `GITHUB_TOKEN`; no personal GitHub token is required.
2. Create a NuGet.org account and reserve or verify ownership of the package ID `FlyleafLib.Plugins.YTDLP`. Create an API key with permission to push new package versions, scoped to this ID. **Keep the key on your own machine**; do not add it to GitHub Actions or commit it.
3. Use `main` or `master` as the GitHub release branch, or adjust `.github/workflows/release.yml`.
4. Keep the repository **public** for anonymous host downloads through GitHub's `/releases/latest` API. The downloader requires a published stable release, not a draft or prerelease.

## Releasing GitHub ZIP assets (automatic on tags)

1. Update `<Version>` in `Plugin/Plugin.csproj` for a new plugin release. Change the Native AOT host version only if its code changed, and update the extension's `manifest.json` version only when its files change. Protocol changes require coordinated host, plugin, and extension changes.
2. Commit and push. Confirm **Actions → Build and release** passes for the branch. Ordinary pushes validate/build the ZIP release assets but do not publish a GitHub release or a NuGet package.
3. Tag the same commit, e.g. `v2.0.0` (if that version has not already been released), and push the tag. The tag must match the plugin version.
4. The tag workflow creates the GitHub release with the plugin, host, and extension ZIP assets, component metadata, and checksums. **It does not build or publish NuGet packages.**
5. Verify the GitHub release assets and their integrity before distributing them.

A published GitHub release must not be overwritten. Use a new plugin version and tag for a later release.

## Building and publishing NuGet (manual)

From the repository root on a development machine with the .NET 10 SDK:

```powershell
dotnet restore Plugin/Plugin.csproj
dotnet build Plugin/Plugin.csproj -c Release
dotnet pack Plugin/Plugin.csproj -c Release --no-build -o dist/nuget
```

The version comes from `Plugin/Plugin.csproj`, and the resulting files are:

```text
dist/nuget/FlyleafLib.Plugins.YTDLP.2.0.0.nupkg
dist/nuget/FlyleafLib.Plugins.YTDLP.2.0.0.snupkg
```

Replace `2.0.0` with the current project version. Before publication, inspect the `.nupkg` as a ZIP and check that it contains:

```text
tools/net10.0-windows7.0/FlyleafLib.Plugins.YTDLP.dll
buildTransitive/FlyleafLib.Plugins.YTDLP.targets
lib/net10.0-windows7.0/_._
README.md
```

It should **not** contain `.deps.json`, third-party executables, or host/extension binaries. The consuming-project target places the plugin DLL in `Plugins/YTDLP/` on build and publish, rather than copying it to the application's root.

When ready to publish, supply your NuGet API key through a locally configured `NUGET_API_KEY` environment variable and run (substitute your actual package version):

```powershell
dotnet nuget push 'dist/nuget/FlyleafLib.Plugins.YTDLP.2.0.0.nupkg' --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
```

If the matching `.snupkg` is beside the `.nupkg`, `dotnet nuget push` also pushes the symbol package to the symbol server. Do not store the API key in tracked scripts or project files, and do not commit local environment settings containing the key.

**NuGet package versions are immutable.** Do not republish an existing version. Confirm the package and symbols are visible on NuGet.org after publication.

## Expected GitHub release assets

```text
FlyleafLib.Plugins.YTDLP-win-x64.zip
Flyleaf.BrowserCookieHost-win-x64.zip
Flyleaf.BrowserExtension-v1.0.0.zip
component-versions.json
SHA256SUMS.txt
```

The host ZIP contains `Flyleaf.BrowserCookieHost.exe` and `host-compat.json`. The workflow verifies compiled DLL/EXE versions, protocol compatibility, extension metadata, and GitHub asset hashes. Release notes show independent component versions and changes since the previous release.

Only the **plugin DLL** is required application-local. yt-dlp, Deno, and the native browser host are managed by the plugin under `%LOCALAPPDATA%\Flyleaf\YTDLP\`. The browser extension requires manual installation and updating.

## Local validation

```powershell
node Tests/extension.test.cjs
python -m unittest discover -s Tests -p test_release_info.py
dotnet build Plugin/Plugin.csproj -c Release
dotnet pack Plugin/Plugin.csproj -c Release --no-build -o dist/nuget
dotnet publish BrowserHost/BrowserHost.csproj -c Release -r win-x64 -p:PublishProfile=FolderProfile
```

Install the locally produced `.nupkg` into a small Windows .NET 10 consumer project and test **both** `dotnet build` and `dotnet publish`. They should produce:

```text
Plugins/YTDLP/FlyleafLib.Plugins.YTDLP.dll
```

Ensure the DLL is not duplicated in the application root. Also test alongside another Flyleaf plugin package (such as BitSwarm) to confirm their MSBuild targets don't interfere with one another. On Windows x64, smoke-test shared-tool downloads, browser pairing (normal/private), and host upgrades.
