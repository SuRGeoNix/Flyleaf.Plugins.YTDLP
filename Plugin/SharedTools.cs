using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.Plugins;

// One per-user copy of the external tools, shared by every Flyleaf application.
// yt-dlp and Deno retain their upstream self-updaters; never update a running binary.
internal static class SharedTools
{
    internal static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyleaf", "YTDLP");
    internal static readonly string Bin = Path.Combine(Root, "Bin");
    internal static readonly string YtDlp = Path.Combine(Bin, "yt-dlp.exe");
    internal static readonly string Deno = Path.Combine(Bin, "deno.exe");

    private const int MaxDownloadBytes = 256 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static int updateStarted;

    static SharedTools() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Flyleaf-YTDLP");

    internal static bool IsYouTube(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
        string host = uri.DnsSafeHost;
        return host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool EnsureYtDlp(string pluginFolder, CancellationToken cancellation, out string error)
        => Ensure("yt-dlp", YtDlp, "yt-dlp/yt-dlp", "yt-dlp.exe", false,
            pluginFolder, cancellation, out error);

    internal static bool EnsureDeno(string pluginFolder, CancellationToken cancellation, out string error)
        => Ensure("deno", Deno, "denoland/deno", "deno-x86_64-pc-windows-msvc.zip", true,
            pluginFolder, cancellation, out error);

    private static bool Ensure(string name, string path, string repository, string asset,
        bool zipped, string pluginFolder, CancellationToken cancellation, out string error)
    {
        error = null;
        if (File.Exists(path)) return true;
        string failureStamp = Path.Combine(Root, "." + name + "-failed-install");
        if (File.Exists(failureStamp) && File.GetLastWriteTimeUtc(failureStamp) > DateTime.UtcNow.AddMinutes(-5))
        {
            error = $"{name} unavailable; previous download failed recently";
            return false;
        }
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
        {
            error = $"{name} automatic installation requires Windows x64";
            return false;
        }
        try
        {
            using var mutex = new Mutex(false, @"Local\Flyleaf.YTDLP.SharedTools");
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { error = $"{name} installation is busy"; return false; }
            try
            {
                if (File.Exists(path)) return true;
                Directory.CreateDirectory(Bin);
                // Migrate previous application-local tools without network access.
                string filename = Path.GetFileName(path);
                string legacyFolder = Path.Combine(Path.GetDirectoryName(pluginFolder) ?? pluginFolder, "YoutubeDL");
                foreach (string source in new[] { Path.Combine(pluginFolder, filename), Path.Combine(legacyFolder, filename) })
                {
                    if (!File.Exists(source) || !Runs(source)) continue;
                    try { File.Copy(source, path, overwrite: false); return true; }
                    catch (IOException) when (File.Exists(path)) { return true; }
                }
                byte[] data = DownloadReleaseAssetAsync(repository, asset, cancellation).GetAwaiter().GetResult();
                string staged = Path.Combine(Bin, "." + name + "-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    if (zipped)
                    {
                        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
                        ZipArchiveEntry entry = zip.GetEntry("deno.exe") ??
                            throw new InvalidDataException("Deno archive does not contain deno.exe");
                        if (entry.Length is <= 0 or > MaxDownloadBytes)
                            throw new InvalidDataException("Invalid Deno executable size");
                        using var source = entry.Open();
                        using var dest = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        source.CopyTo(dest);
                        if (dest.Length != entry.Length) throw new InvalidDataException("Truncated Deno executable");
                    }
                    else File.WriteAllBytes(staged, data);
                    if (!Runs(staged)) throw new InvalidDataException($"Downloaded {name} failed its version check");
                    File.Move(staged, path); // Never overwrite a tool another application might be running.
                }
                finally { TryDelete(staged); }
                return true;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or
            OperationCanceledException or IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or System.Security.SecurityException or
            System.ComponentModel.Win32Exception)
        {
            error = $"{name} installation failed: {e.Message}";
            if (!cancellation.IsCancellationRequested)
            {
                try
                {
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(failureStamp, DateTimeOffset.UtcNow.ToString("O"));
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return false;
        }
    }

    // GitHub REST supplies the official asset URL and digest. Require both,
    // including on initial installs: never execute an unchecked download.
    internal static async Task<byte[]> DownloadReleaseAssetAsync(string repository, string assetName,
        CancellationToken cancellation)
    {
        string api = $"https://api.github.com/repos/{repository}/releases/latest";
        using var response = await Http.GetAsync(api, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        JsonElement asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == assetName);
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"Release asset {assetName} not found in {repository}");
        string digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("Release asset has no valid SHA-256 digest");
        string location = asset.GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected release download URL");

        using var download = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellation).ConfigureAwait(false);
        download.EnsureSuccessStatusCode();
        if (download.Content.Headers.ContentLength is > MaxDownloadBytes)
            throw new InvalidDataException("Release asset exceeds size limit");
        using var stream = await download.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > MaxDownloadBytes)
                throw new InvalidDataException("Release asset exceeds size limit");
            memory.Write(buffer, 0, read);
        }
        byte[] data = memory.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release asset SHA-256 does not match");
        return data;
    }

    private static bool Runs(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path, Arguments = "--version", UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null) return false;
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or
            InvalidOperationException or UnauthorizedAccessException) { return false; }
    }

    internal static void CheckUpdatesInBackground(Action<string> diagnostics)
    {
        if (Interlocked.Exchange(ref updateStarted, 1) != 0) return;
        _ = Task.Run(() =>
        {
            UpdateTool("yt-dlp", YtDlp, "-U", diagnostics);
            if (File.Exists(Deno)) UpdateTool("deno", Deno, "upgrade", diagnostics);
        });
    }

    private static void UpdateTool(string name, string path, string arguments, Action<string> diagnostics)
    {
        if (!File.Exists(path)) return;
        string stamp = Path.Combine(Bin, "." + name + "-last-check");
        try
        {
            if (File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) > DateTime.UtcNow.AddHours(-24)) return;
            // Serialize updaters separately from first-use installation.
            // An active self-update must not block an unrelated player's startup.
            using var mutex = new Mutex(false, @"Local\Flyleaf.YTDLP.ToolUpdates");
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            try
            {
                if (!File.Exists(path)) return;
                if (File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) > DateTime.UtcNow.AddHours(-24)) return;
                string stagingDir = Path.Combine(Bin, ".update-" + name + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingDir);
                try
                {
                    string staged = Path.Combine(stagingDir, Path.GetFileName(path));
                    File.Copy(path, staged);
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = staged, Arguments = arguments, WorkingDirectory = stagingDir,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = false, RedirectStandardError = false
                    });
                    if (process == null) return;
                    if (!process.WaitForExit(120000))
                    {
                        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                        return;
                    }
                    if (process.ExitCode != 0 || !File.Exists(staged) || !Runs(staged)) return;
                    // The source binary is never modified by its self-updater.
                    // Windows may lock the active binary: in that case defer activation.
                    if (!SHA256.HashData(File.ReadAllBytes(staged)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
                    {
                        // Only the short activation step shares the installation lock.
                        using var installMutex = new Mutex(false, @"Local\Flyleaf.YTDLP.SharedTools");
                        bool installAcquired;
                        try { installAcquired = installMutex.WaitOne(2000); }
                        catch (AbandonedMutexException) { installAcquired = true; }
                        if (!installAcquired) return;
                        try
                        {
                            try { File.Move(staged, path, overwrite: true); }
                            catch (IOException) { return; }
                        }
                        finally { installMutex.ReleaseMutex(); }
                    }
                    File.WriteAllText(stamp, DateTimeOffset.UtcNow.ToString("O"));
                }
                finally
                {
                    try { Directory.Delete(stagingDir, recursive: true); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            diagnostics?.Invoke($"{name} background update skipped: {e.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
