using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flyleaf.BrowserCookies;
using Microsoft.Win32;

namespace FlyleafLib.Plugins;

internal static class BrowserHostManager
{
    private const string HostExe = "Flyleaf.BrowserCookieHost.exe";
    private const string CompatibilityFile = "host-compat.json";
    private const string ReleaseAsset = "Flyleaf.BrowserCookieHost-win-x64.zip";
    private const string Repository = "SuRGeoNix/Flyleaf.Plugins.YTDLP";

    private static readonly string Root = Path.Combine(SharedTools.Root, "Host");
    private static readonly string ManifestPath = Path.Combine(Root, BrowserCookieProtocol.HostName + ".json");
    private static int updateStarted;

    private sealed record HostCandidate(string Exe, string Compatibility, string Directory);

    private static Version VersionOf(string exe)
    {
        try
        {
            string text = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            return Version.TryParse(text, out var version) ? version : new Version(0, 0);
        }
        catch { return new Version(0, 0); }
    }

    private static bool SupportsProtocol(string compatibilityPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(compatibilityPath));
            int min = doc.RootElement.GetProperty("minProtocol").GetInt32();
            int max = doc.RootElement.GetProperty("maxProtocol").GetInt32();
            return min > 0 && min <= BrowserCookieProtocol.Version &&
                   max >= BrowserCookieProtocol.Version && max >= min;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    private static bool IsCompatibleInstallation(string exe) =>
        File.Exists(exe) && VersionOf(exe).Major > 0 &&
        SupportsProtocol(Path.Combine(Path.GetDirectoryName(exe)!, CompatibilityFile));

    private static string ReadRegisteredExecutable()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            if (doc.RootElement.GetProperty("name").GetString() != BrowserCookieProtocol.HostName ||
                !doc.RootElement.GetProperty("allowed_origins").EnumerateArray()
                    .Any(x => x.GetString() == BrowserCookieProtocol.ExtensionOrigin)) return null;
            return doc.RootElement.GetProperty("path").GetString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static void Register()
    {
        foreach (string key in new[]
        {
            @"Software\Google\Chrome\NativeMessagingHosts\" + BrowserCookieProtocol.HostName,
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + BrowserCookieProtocol.HostName
        })
        {
            using RegistryKey subkey = Registry.CurrentUser.CreateSubKey(key);
            if (!string.Equals(subkey?.GetValue(null) as string, ManifestPath, StringComparison.OrdinalIgnoreCase))
                subkey?.SetValue(null, ManifestPath, RegistryValueKind.String);
        }
    }

    private static bool InstallCandidate(string exe, string compatibility)
    {
        if (!File.Exists(exe) || !SupportsProtocol(compatibility)) return false;
        Version version = VersionOf(exe);
        if (version.Major <= 0) return false;

        string targetDir = Path.Combine(Root, "Versions", version.ToString());
        string targetExe = Path.Combine(targetDir, HostExe);
        string targetCompatibility = Path.Combine(targetDir, CompatibilityFile);
        Directory.CreateDirectory(targetDir);

        if (File.Exists(targetExe))
        {
            // The same host version must not silently contain two different binaries.
            using var existing = File.OpenRead(targetExe);
            using var incoming = File.OpenRead(exe);
            if (!SHA256.HashData(existing).SequenceEqual(SHA256.HashData(incoming)))
                throw new InvalidDataException($"Conflicting native host binaries for version {version}");
        }
        else
        {
            string stage = targetExe + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(exe, stage); File.Move(stage, targetExe); }
            finally { TryDelete(stage); }
        }
        if (!File.Exists(targetCompatibility) || !SupportsProtocol(targetCompatibility))
        {
            string stage = targetCompatibility + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(compatibility, stage); File.Move(stage, targetCompatibility, overwrite: true); }
            finally { TryDelete(stage); }
        }

        Directory.CreateDirectory(Root);
        string temporaryManifest = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryManifest, JsonSerializer.Serialize(new
            {
                name = BrowserCookieProtocol.HostName,
                description = "Flyleaf Browser Cookie Host",
                path = targetExe,
                type = "stdio",
                allowed_origins = new[] { BrowserCookieProtocol.ExtensionOrigin }
            }));
            File.Move(temporaryManifest, ManifestPath, overwrite: true);
            Register();
            return true;
        }
        finally { TryDelete(temporaryManifest); }
    }

    private static bool WithMutex(Func<bool> action)
    {
        using var mutex = new Mutex(false, @"Local\Flyleaf.YTDLP.HostSetup");
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return false;
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static bool EnsureInstalled(string pluginFolder, CancellationToken cancellation, out string error)
    {
        error = null;
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
        {
            error = "Native host requires Windows x64";
            return false;
        }
        try
        {
            if (WithMutex(() =>
            {
                string current = ReadRegisteredExecutable();
                if (current != null && IsCompatibleInstallation(current))
                {
                    Register();
                    return true;
                }
                string localFolder = Path.Combine(pluginFolder, "Host");
                return InstallCandidate(Path.Combine(localFolder, HostExe),
                    Path.Combine(localFolder, CompatibilityFile));
            })) return true;

            string failureStamp = Path.Combine(Root, ".failed-download");
            if (File.Exists(failureStamp) && File.GetLastWriteTimeUtc(failureStamp) > DateTime.UtcNow.AddMinutes(-5))
            {
                error = "Native host is unavailable; previous download failed recently";
                return false;
            }
            cancellation.ThrowIfCancellationRequested();
            HostCandidate candidate = DownloadAndStageAsync(cancellation).GetAwaiter().GetResult();
            try
            {
                if (WithMutex(() =>
                {
                    string installed = ReadRegisteredExecutable();
                    return installed != null && IsCompatibleInstallation(installed) &&
                           VersionOf(installed) >= VersionOf(candidate.Exe)
                        ? RegisterExisting()
                        : InstallCandidate(candidate.Exe, candidate.Compatibility);
                }))
                {
                    File.WriteAllText(Path.Combine(Root, "last-update-check.txt"), DateTimeOffset.UtcNow.ToString("O"));
                    TryDelete(failureStamp);
                    return true;
                }
                error = "Native host setup could not acquire its installation lock";
            }
            finally { TryDeleteDirectory(candidate.Directory); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            HttpRequestException or InvalidDataException or JsonException or
            System.Security.SecurityException or OperationCanceledException)
        {
            error = "Native host installation failed: " + e.Message;
        }
        if (!cancellation.IsCancellationRequested)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root, ".failed-download"), DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static bool RegisterExisting() { Register(); return true; }

    private static async Task<HostCandidate> DownloadAndStageAsync(CancellationToken cancellation)
    {
        byte[] archive = await SharedTools.DownloadReleaseAssetAsync(Repository, ReleaseAsset, cancellation)
            .ConfigureAwait(false);
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        ZipArchiveEntry compatibility = zip.GetEntry(CompatibilityFile) ??
            throw new InvalidDataException("Host release is missing protocol compatibility metadata");
        ZipArchiveEntry binary = zip.GetEntry(HostExe) ??
            throw new InvalidDataException("Host release does not include " + HostExe);
        if (compatibility.Length is <= 0 or > 4096 || binary.Length is <= 0 or > 50000000)
            throw new InvalidDataException("Invalid native host release archive");

        string directory = Path.Combine(Root, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string stagedExe = Path.Combine(directory, HostExe);
        string stagedCompatibility = Path.Combine(directory, CompatibilityFile);
        try
        {
            using (var source = compatibility.Open())
            using (var dest = new FileStream(stagedCompatibility, FileMode.CreateNew))
                await source.CopyToAsync(dest, cancellation).ConfigureAwait(false);
            if (!SupportsProtocol(stagedCompatibility))
                throw new InvalidDataException("Host release is incompatible with browser-cookie protocol " +
                    BrowserCookieProtocol.Version);
            using (var source = binary.Open())
            using (var dest = new FileStream(stagedExe, FileMode.CreateNew))
            {
                await source.CopyToAsync(dest, cancellation).ConfigureAwait(false);
                if (dest.Length != binary.Length) throw new InvalidDataException("Truncated host executable");
            }
            if (VersionOf(stagedExe).Major <= 0)
                throw new InvalidDataException("Native host has no valid file version");
            return new HostCandidate(stagedExe, stagedCompatibility, directory);
        }
        catch { TryDeleteDirectory(directory); throw; }
    }

    internal static void CheckForUpdatesInBackground()
    {
        if (Interlocked.Exchange(ref updateStarted, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Root);
                string stamp = Path.Combine(Root, "last-update-check.txt");
                if (File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) > DateTime.UtcNow.AddHours(-24))
                    return;
                HostCandidate candidate = await DownloadAndStageAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    bool activated = WithMutex(() =>
                    {
                        string existing = ReadRegisteredExecutable();
                        if (existing != null && IsCompatibleInstallation(existing) &&
                            VersionOf(existing) >= VersionOf(candidate.Exe))
                            return RegisterExisting();
                        return InstallCandidate(candidate.Exe, candidate.Compatibility);
                    });
                    if (activated)
                        File.WriteAllText(stamp, DateTimeOffset.UtcNow.ToString("O"));
                }
                finally { TryDeleteDirectory(candidate.Directory); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                HttpRequestException or InvalidDataException or JsonException or
                System.Security.SecurityException or OperationCanceledException) { }
        });
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
