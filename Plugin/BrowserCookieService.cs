using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flyleaf.BrowserCookies;

namespace FlyleafLib.Plugins;

// An optional bridge. It does nothing until YoutubeDL requests cookies.
internal static class BrowserCookieService
{
    private const int MaxResultBytes = 4 * 1024 * 1024;
    private static readonly string CookieFolder = Path.Combine(
        SharedTools.Root, "Temp");

    private sealed class PipeMessage
    {
        public int protocol { get; set; }
        public string clientSecret { get; set; }
        public string type { get; set; }
        public string requestId { get; set; }
        public string url { get; set; }
        public bool ok { get; set; }
        public string error { get; set; }
        public List<BrowserCookie> cookies { get; set; }
    }

    internal sealed class BrowserCookie
    {
        public string name { get; set; }
        public string value { get; set; }
        public string domain { get; set; }
        public string path { get; set; }
        public bool hostOnly { get; set; }
        public bool secure { get; set; }
        public bool httpOnly { get; set; }
        public bool session { get; set; }
        public double? expirationDate { get; set; }
    }

    internal sealed class Snapshot
    {
        internal static readonly Snapshot Empty = new(Array.Empty<BrowserCookie>(), DateTimeOffset.MinValue, "none");
        private readonly BrowserCookie[] cookies;
        internal DateTimeOffset CreatedAt { get; }
        internal string Mode { get; }
        internal int Count => cookies.Length;
        internal string Error { get; }

        private Snapshot(BrowserCookie[] cookies, DateTimeOffset createdAt, string mode, string error = null)
        {
            Error = error;
            this.cookies = cookies;
            CreatedAt = createdAt;
            Mode = mode;
        }

        internal static Snapshot Rejected(string mode, string error)
            => new(Array.Empty<BrowserCookie>(), DateTimeOffset.MinValue, mode, error);

        internal static Snapshot Create(string url, List<BrowserCookie> raw, string mode = "normal")
        {
            var original = new Uri(url);
            // Defense in depth against malformed/foreign cookie records.
            BrowserCookie[] safe = (raw ?? new List<BrowserCookie>())
                .Where(c => IsSafe(c) && MatchesDomain(original.Host, c))
                .ToArray();
            return new Snapshot(safe, DateTimeOffset.UtcNow, mode);
        }

        private static bool IsSafe(BrowserCookie c)
        {
            if (c == null || string.IsNullOrEmpty(c.name) || c.value == null ||
                string.IsNullOrWhiteSpace(c.domain) || string.IsNullOrEmpty(c.path) ||
                !c.path.StartsWith("/", StringComparison.Ordinal) ||
                c.name.Any(ch => ch <= ' ' || ch >= 127 || ch == '\\' || ch == '"' || "()<>@,;:/[]?={}".Contains(ch)) ||
                c.value.IndexOfAny(new[] { '\r', '\n', '\t', '\0', ';' }) >= 0 ||
                c.domain.IndexOfAny(new[] { '\r', '\n', '\t', ';', ' ' }) >= 0 ||
                c.path.IndexOfAny(new[] { '\r', '\n', '\t', ';' }) >= 0)
                return false;
            string d = c.domain.TrimStart('.');
            return d.Length > 0 && !d.StartsWith(".", StringComparison.Ordinal) &&
                   Uri.CheckHostName(d) == UriHostNameType.Dns;
        }

        private static bool MatchesDomain(string host, BrowserCookie cookie)
        {
            string domain = cookie.domain.TrimStart('.');
            return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                   (!cookie.hostOnly && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesPath(string path, string cookiePath)
            => path.StartsWith(cookiePath, StringComparison.Ordinal) &&
               (path.Length == cookiePath.Length || cookiePath.EndsWith("/", StringComparison.Ordinal) ||
                path[cookiePath.Length] == '/');

        private static bool Matches(Uri url, BrowserCookie c)
            => MatchesDomain(url.Host, c) && MatchesPath(url.AbsolutePath, c.path) &&
               (!c.secure || url.Scheme == Uri.UriSchemeHttps) &&
               (c.session || !c.expirationDate.HasValue ||
                c.expirationDate.Value > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // FFmpeg accepts Set-Cookie-style records, one per line.
        internal string ToFFmpegCookies(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return string.Empty;

            var output = new StringBuilder();
            foreach (BrowserCookie c in cookies)
            {
                if (!Matches(uri, c)) continue;
                output.Append(c.name).Append('=').Append(c.value).Append("; path=").Append(c.path).Append(';');
                // No Domain attribute means host-only to FFmpeg (avoids leaking to subdomains).
                if (!c.hostOnly) output.Append(" domain=").Append(c.domain.TrimStart('.')).Append(';');
                if (c.secure) output.Append(" secure;");
                output.Append("\r\n");
            }
            return output.ToString();
        }

        // Netscape format (including #HttpOnly_ convention) consumed by yt-dlp.
        internal string ToNetscape()
        {
            var output = new StringBuilder("# Netscape HTTP Cookie File\n");
            foreach (BrowserCookie c in cookies)
            {
                if (!c.session && c.expirationDate is { } expiry &&
                    expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
                string domain = c.domain.TrimStart('.');
                if (!c.hostOnly) domain = "." + domain;
                if (c.httpOnly) domain = "#HttpOnly_" + domain;
                long expires = c.session ? 0L :
                    (long)Math.Clamp(Math.Floor(c.expirationDate ?? 0), 0, 253402300799L);
                output.Append(domain).Append('\t').Append(c.hostOnly ? "FALSE" : "TRUE")
                    .Append('\t').Append(c.path).Append('\t').Append(c.secure ? "TRUE" : "FALSE")
                    .Append('\t').Append(expires).Append('\t').Append(c.name).Append('\t')
                    .Append(c.value).Append('\n');
            }
            return output.ToString();
        }
    }

    private static readonly object SecretLock = new();
    private static readonly Dictionary<string, string> SecretCache = new(StringComparer.OrdinalIgnoreCase);

    // Persist an application key beside its portable plugin (fall back to the
    // user profile for read-only application installs). It is NEVER uploaded.
    internal static string GetClientSecret(string pluginFolder)
    {
        lock (SecretLock)
        {
            if (SecretCache.TryGetValue(pluginFolder, out string cached)) return cached;
            string path = Path.Combine(pluginFolder, ".flyleaf-browser-client-key");
            string secret = ReadOrCreateKey(path);
            if (secret == null)
            {
                string identity = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Flyleaf";
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                path = Path.Combine(SharedTools.Root, "Clients", key + ".key");
                string previous = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Flyleaf", "BrowserCookies", "Clients", key + ".key");
                if (!File.Exists(path) && File.Exists(previous))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.Copy(previous, path);
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                secret = ReadOrCreateKey(path);
            }
            if (secret != null) SecretCache[pluginFolder] = secret;
            return secret;
        }
    }

    private static string ReadOrCreateKey(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string value = File.ReadAllText(path).Trim();
                if (ValidSecret(value)) return value;
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                writer.Write(secret);
                return secret;
            }
            catch (IOException) when (File.Exists(path))
            {
                string value = File.ReadAllText(path).Trim();
                return ValidSecret(value) ? value : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private static bool ValidSecret(string value) => value is { Length: 43 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Browser choice and session mode are independent. Auto mode first tries
    // private contexts, then normal ones; explicit Private never falls back.
    internal static Snapshot Request(string url, string source, string mode, int timeoutMs,
        CancellationToken cancellation, string clientSecret)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Snapshot.Empty;

        string[] sources = source?.ToLowerInvariant() switch
        {
            "chrome" => new[] { "chrome" },
            "brave" => new[] { "brave" },
            _ => new[] { "brave", "chrome" }
        };
        string[] modes = mode?.ToLowerInvariant() switch
        {
            "normal" => new[] { "normal" },
            "private" => new[] { "private" },
            _ => new[] { "private", "normal" }
        };
        if (!ValidSecret(clientSecret)) return Snapshot.Empty;
        int attempts = sources.Length * modes.Length;
        Snapshot rejection = Snapshot.Empty;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(timeoutMs);
            foreach (string selectedMode in modes)
                foreach (string browser in sources)
                {
                    int remaining = timeoutMs - (int)watch.ElapsedMilliseconds;
                    if (remaining <= 0 || timeout.IsCancellationRequested) return Snapshot.Empty;
                    int budget = Math.Max(100, remaining / attempts--);
                    Snapshot snapshot = RequestOneAsync(uri, browser, selectedMode, budget, timeout.Token, clientSecret)
                        .GetAwaiter().GetResult();
                    if (snapshot.Count > 0) return snapshot;
                    if (snapshot.Error != null) rejection = snapshot;
                }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or
                                  JsonException or UnauthorizedAccessException or InvalidDataException) { }
        return rejection;
    }

    private static async Task<Snapshot> RequestOneAsync(Uri url, string browser, string mode, int timeoutMs,
        CancellationToken cancellation, string clientSecret)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            attempt.CancelAfter(Math.Max(100, timeoutMs));
            string pipeName = BrowserCookieProtocol.PipePrefix + browser + "." + mode;
            using var pipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(Math.Max(100, timeoutMs), attempt.Token).ConfigureAwait(false);
            string id = Guid.NewGuid().ToString("N");
            var request = new PipeMessage { protocol = BrowserCookieProtocol.Version, clientSecret = clientSecret, type = "getCookies",
                requestId = id, url = url.GetLeftPart(UriPartial.Path) };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await pipe.WriteAsync(header, attempt.Token).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, attempt.Token).ConfigureAwait(false);
            await pipe.FlushAsync(attempt.Token).ConfigureAwait(false);

            await pipe.ReadExactlyAsync(header, attempt.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaxResultBytes) throw new InvalidDataException("Cookie response too large");
            bytes = new byte[length];
            await pipe.ReadExactlyAsync(bytes, attempt.Token).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<PipeMessage>(bytes);
            if (result?.requestId != id || result.protocol != BrowserCookieProtocol.Version) return Snapshot.Empty;
            return result.ok ? Snapshot.Create(url.AbsoluteUri, result.cookies, mode)
                : Snapshot.Rejected(mode, result.error ?? "browser_failed");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or
                                  JsonException or UnauthorizedAccessException or InvalidDataException) { }
        return Snapshot.Empty;
    }

    internal sealed class CookieFile : IDisposable
    {
        internal string Path { get; }
        internal CookieFile(string path) => Path = path;

        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException) { ScheduleDelete(Path); }
            catch (UnauthorizedAccessException) { ScheduleDelete(Path); }
        }

        private static void ScheduleDelete(string path)
        {
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    try { File.Delete(path); return; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            });
        }
    }

    private static int cleaned;
    internal static CookieFile CreateCookieFile(Snapshot snapshot)
    {
        if (snapshot == null || snapshot.Count == 0) return null;
        string path = null;
        try
        {
            Directory.CreateDirectory(CookieFolder);
            if (Interlocked.Exchange(ref cleaned, 1) == 0)
                foreach (string file in Directory.EnumerateFiles(CookieFolder, "flyleaf-*.cookies"))
                {
                    try {
                        bool privateFile = System.IO.Path.GetFileName(file).StartsWith("flyleaf-private-", StringComparison.Ordinal);
                        if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(privateFile ? -10 : -1440))
                            File.Delete(file);
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }

            path = System.IO.Path.Combine(CookieFolder, "flyleaf-" + (snapshot.Mode == "private" ? "private-" : "") + Guid.NewGuid().ToString("N") + ".cookies");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                writer.Write(snapshot.ToNetscape());
            return new CookieFile(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (path != null)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
