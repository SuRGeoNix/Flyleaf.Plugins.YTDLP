using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Flyleaf.BrowserCookies;

namespace Flyleaf.BrowserCookieHost;

internal sealed class ClientIdentity
{
    public string? id { get; set; }
    public string? name { get; set; }
    public string? path { get; set; }
}

internal sealed class ProtocolMessage
{
    public int protocol { get; set; }
    public string? type { get; set; }
    public string? browser { get; set; }
    public string? mode { get; set; }
    public string? requestId { get; set; }
    public string? url { get; set; }
    public string? clientSecret { get; set; }
    public ClientIdentity? client { get; set; }
    public bool ok { get; set; }
    public string? error { get; set; }
    public BrowserCookie[]? cookies { get; set; }
}

internal sealed class BrowserCookie
{
    public string? name { get; set; }
    public string? value { get; set; }
    public string? domain { get; set; }
    public string? path { get; set; }
    public bool hostOnly { get; set; }
    public bool secure { get; set; }
    public bool httpOnly { get; set; }
    public bool session { get; set; }
    public double? expirationDate { get; set; }
}

[JsonSerializable(typeof(ProtocolMessage))]
internal partial class ProtocolJsonContext : JsonSerializerContext { }

internal static class Program
{
    private const int MaxBrowserFrame = 8 * 1024 * 1024;
    private const int MaxPipeFrame = 32 * 1024;
    private const int MaxCookieResult = 4 * 1024 * 1024;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint size, usage, processId;
        public IntPtr defaultHeapId;
        public uint moduleId, threads, parentId;
        public int basePriority;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    private static readonly ConcurrentDictionary<string, TaskCompletionSource<ProtocolMessage>> Pending = new();
    private static readonly TaskCompletionSource<string> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly string? LaunchingBrowser = DetectBrowser();

    private static string? DetectBrowser()
    {
        try
        {
            using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
            if (snapshot.IsInvalid) return null;
            uint parentPid = 0;
            var names = new Dictionary<uint, string>();
            var entry = new ProcessEntry32 { size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return null;
            do
            {
                names[entry.processId] = entry.executable ?? "";
                if (entry.processId == (uint)Environment.ProcessId)
                    parentPid = entry.parentId;
            } while (Process32Next(snapshot, ref entry));
            return names.GetValueOrDefault(parentPid)?.ToLowerInvariant() switch
            {
                "brave.exe" => "brave",
                "chrome.exe" => "chrome",
                _ => null
            };
        }
        catch { return null; }
    }

    // A random per-application key is a convenience identity, not a security boundary
    // against a malicious process running as the same Windows user.
    private static ClientIdentity? IdentifyClient(NamedPipeServerStream pipe, string? secret)
    {
        if (secret is not { Length: 43 } || secret.Any(c =>
            !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
              (c >= '0' && c <= '9') || c is '-' or '_')))
            return null;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0)
                return null;
            using var process = Process.GetProcessById(checked((int)pid));
            string path = Path.GetFullPath(process.MainModule?.FileName ?? "");
            if (!File.Exists(path)) return null;
            string keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))
                .ToLowerInvariant();
            return new ClientIdentity { id = keyHash, name = Path.GetFileName(path), path = path };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<int> Main(string[] args)
    {
        // Chrome supplies the caller's extension origin in argv[0].
        if (args.Length == 0 || !StringComparer.Ordinal.Equals(args[0], BrowserCookieProtocol.ExtensionOrigin))
            return 2;

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var stopped = new CancellationTokenSource();
        Task pump = ReadBrowserAsync(input, stopped);
        try
        {
            string pipeName = await Ready.Task.WaitAsync(TimeSpan.FromSeconds(10), stopped.Token);
            while (!stopped.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopped.Token);
                await HandleClientAsync(pipe, output, stopped.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or
                                   EndOfStreamException or IOException) { }
        finally
        {
            await stopped.CancelAsync();
            try { await pump; } catch (Exception) { /* Browser disconnected. */ }
        }
        return 0;
    }

    private static async Task ReadBrowserAsync(Stream input, CancellationTokenSource stopped)
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                ProtocolMessage message = await ReadFrameAsync(input, MaxBrowserFrame, stopped.Token);
                if (message.type == "hello" && message.protocol == BrowserCookieProtocol.Version &&
                    (message.browser == "chrome" || message.browser == "brave") &&
                    (message.mode == "normal" || message.mode == "private"))
                {
                    Ready.TrySetResult(BrowserCookieProtocol.PipePrefix +
                        (LaunchingBrowser ?? message.browser) + "." + message.mode);
                }
                else if (message.type == "cookies" && message.protocol == BrowserCookieProtocol.Version &&
                         message.requestId is { Length: > 0 } id &&
                         Pending.TryRemove(id, out var completion))
                    completion.TrySetResult(message);
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or
                                   OperationCanceledException or JsonException or InvalidDataException) { }
        finally
        {
            Ready.TrySetCanceled();
            foreach (var pair in Pending)
                if (Pending.TryRemove(pair.Key, out var completion)) completion.TrySetCanceled();
            await stopped.CancelAsync();
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, Stream output, CancellationToken stopped)
    {
        ProtocolMessage reply;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            ProtocolMessage request = await ReadFrameAsync(pipe, MaxPipeFrame, timeout.Token);
            ClientIdentity? client = IdentifyClient(pipe, request.clientSecret);
            if (request.protocol != BrowserCookieProtocol.Version)
                reply = new ProtocolMessage { type = "result", error = "protocol_mismatch" };
            else if (client == null)
                reply = new ProtocolMessage { type = "result", requestId = request.requestId, error = "invalid_client" };
            else if (request.type != "getCookies" || request.requestId is not { Length: >= 8 and <= 80 } id ||
                !Uri.TryCreate(request.url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                reply = new ProtocolMessage { type = "result", requestId = request.requestId, error = "invalid_request" };
            else
            {
                var completion = new TaskCompletionSource<ProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!Pending.TryAdd(id, completion))
                    reply = new ProtocolMessage { type = "result", requestId = id, error = "duplicate_id" };
                else
                {
                    try
                    {
                        // Only the extension decides which applications are trusted.
                        await WriteFrameAsync(output, new ProtocolMessage {
                            protocol = BrowserCookieProtocol.Version, type = "getCookies", requestId = id,
                            url = uri.GetLeftPart(UriPartial.Path), client = client
                        }, 1024 * 1024, timeout.Token);
                        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(7), timeout.Token);
                        reply = new ProtocolMessage {
                            protocol = BrowserCookieProtocol.Version, type = "result", requestId = id, ok = result.ok,
                            cookies = result.ok ? result.cookies : null,
                            error = result.ok ? null : result.error ?? "browser_failed"
                        };
                    }
                    finally { Pending.TryRemove(id, out _); }
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or OperationCanceledException or
                                   TimeoutException or JsonException or InvalidDataException)
        {
            reply = new ProtocolMessage { type = "result", error = "unavailable" };
        }
        reply.protocol = BrowserCookieProtocol.Version;
        try { await WriteFrameAsync(pipe, reply, MaxCookieResult, stopped); }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException) { }
    }

    private static async Task<ProtocolMessage> ReadFrameAsync(Stream stream, int maxLength, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > maxLength) throw new InvalidDataException("Invalid frame length");
        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, ct);
        return JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.ProtocolMessage)
               ?? throw new InvalidDataException("Null message");
    }

    private static async Task WriteFrameAsync(Stream stream, ProtocolMessage message, int maxLength, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.ProtocolMessage);
        if (bytes.Length > maxLength) throw new InvalidDataException("Frame too large");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}
