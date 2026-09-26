using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PbxVoice.Hosting;
using PbxVoice.Service;
using Serilog;

namespace PbxVoice.Control;

/// <summary>
/// The operator's control socket (<c>control.sock</c> in the state directory, mode 0600). One
/// request per connection: a JSON line <c>{"op": "...", "args": {...}}</c>, answered by
/// <c>{"ok": true, "result": ...}</c> or <c>{"ok": false, "error": "..."}</c>.
/// </summary>
internal sealed class ControlServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 64 * 1024;

    private readonly string _path;
    private readonly Func<string, JsonElement, CancellationToken, Task<object>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private Task? _loop;

    public ControlServer(string path, Func<string, JsonElement, CancellationToken, Task<object>> handler)
    {
        _path = path;
        _handler = handler;
    }

    public void Start()
    {
        string? dir = Path.GetDirectoryName(_path);
        if (dir is not null && !Directory.Exists(dir))
            StatePaths.CreatePrivateDirectory(dir);
        if (File.Exists(_path))
        {
            if (IsListening(_path))
                throw new InvalidOperationException($"another daemon is already serving {_path}");
            File.Delete(_path);
        }
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, StatePaths.FileMode);
        _listener.Listen(16);
        _loop = Task.Run(AcceptLoopAsync);
    }

    private static bool IsListening(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                Log.Warning(ex, "Control socket accept failed");
                continue;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(Socket client)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            string response;
            try
            {
                string? line = await ReadLineAsync(stream, _cts.Token).ConfigureAwait(false);
                response = await HandleLineAsync(line).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = Error(ex.Message);
            }
            catch (OperationCanceledException)
            {
                response = Error("the daemon is stopping");
            }
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\n")).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client went away (for example ctl was interrupted during wait_for_call).
            }
        }
    }

    internal async Task<string> HandleLineAsync(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Error("empty request");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return Error("request is not JSON: " + ex.Message);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
                return Error("request needs an 'op' string");
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            try
            {
                var result = await _handler(op.GetString()!, args, _cts.Token).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true, result }, Json.Compact);
            }
            catch (ServiceError ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Control operation {Op} failed", op.GetString());
                return Error("internal error: " + ex.Message);
            }
        }
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message }, Json.Compact);

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[4096];
        while (buffer.Length < MaxRequestBytes)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
                break;
            int newline = Array.IndexOf(one, (byte)'\n', 0, n);
            buffer.Write(one, 0, newline >= 0 ? newline : n);
            if (newline >= 0)
                break;
        }
        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Dispose();
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
        _cts.Dispose();
    }
}

/// <summary>
/// Nothing answered on the control socket, so the daemon isn't running. The request was never
/// sent, so it is safe to start the daemon and try again.
/// </summary>
internal sealed class DaemonUnavailableException : IOException
{
    public DaemonUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary><c>pbx-voice ctl</c>: sends one request to the running daemon.</summary>
internal static class ControlClient
{
    public static async Task<string> SendAsync(string socketPath, string op, JsonElement args, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new DaemonUnavailableException($"the pbx-voice daemon is not running ({ex.SocketErrorCode})", ex);
        }
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        string request = JsonSerializer.Serialize(new { op, args }, Json.Compact) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new IOException("the daemon closed the connection");
    }
}
