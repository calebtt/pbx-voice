using System.Text.Json;
using PbxVoice.Control;
using PbxVoice.Hosting;
using PbxVoice.Service;

namespace PbxVoice.Mcp;

internal sealed record BackendResult(bool Ok, JsonElement Result, string? Error)
{
    public static BackendResult Success(JsonElement result) => new(true, result, null);
    public static BackendResult Failure(string error) => new(false, default, error);
}

/// <summary>Where MCP tool calls go: the daemon's service in process, or the daemon's control socket.</summary>
internal interface IToolBackend
{
    Task<BackendResult> CallAsync(string op, JsonElement args, CancellationToken ct);
}

/// <summary>The HTTP front inside the daemon calls the service directly.</summary>
internal sealed class ServiceBackend : IToolBackend
{
    private readonly PbxVoiceService _service;

    public ServiceBackend(PbxVoiceService service) => _service = service;

    public async Task<BackendResult> CallAsync(string op, JsonElement args, CancellationToken ct)
    {
        try
        {
            var result = await _service.HandleAsync(op, args, ct).ConfigureAwait(false);
            return BackendResult.Success(JsonSerializer.SerializeToElement(result, Json.Compact));
        }
        catch (ServiceError ex)
        {
            return BackendResult.Failure(ex.Message);
        }
    }
}

/// <summary>
/// <c>pbx-voice mcp-stdio</c> runs as a separate process started by the agent, so it forwards to
/// the daemon's 0600 control socket instead. No token is needed: only the same user can connect.
/// </summary>
internal sealed class SocketBackend : IToolBackend
{
    private readonly string _socketPath;
    private readonly Func<CancellationToken, Task<LaunchResult>>? _ensureRunning;

    /// <param name="ensureRunning">
    /// Starts the daemon when nothing answers. The request is retried only then: a request that
    /// reached the daemon is never sent twice, so a call can't be placed twice.
    /// </param>
    public SocketBackend(string socketPath, Func<CancellationToken, Task<LaunchResult>>? ensureRunning = null)
    {
        _socketPath = socketPath;
        _ensureRunning = ensureRunning;
    }

    public async Task<BackendResult> CallAsync(string op, JsonElement args, CancellationToken ct)
    {
        string line;
        try
        {
            try
            {
                line = await ControlClient.SendAsync(_socketPath, op, args, ct).ConfigureAwait(false);
            }
            catch (DaemonUnavailableException) when (_ensureRunning is not null)
            {
                var started = await _ensureRunning(ct).ConfigureAwait(false);
                if (!started.Ok)
                    return BackendResult.Failure($"the pbx-voice daemon is not running, and starting it failed: {started.Message}");
                line = await ControlClient.SendAsync(_socketPath, op, args, ct).ConfigureAwait(false);
            }
        }
        catch (DaemonUnavailableException ex)
        {
            return BackendResult.Failure($"{ex.Message}; start it with `pbx-voice start`");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            return BackendResult.Failure($"the connection to the pbx-voice daemon failed ({ex.Message}); check the call with get_call before trying again");
        }
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        return root.GetProperty("ok").GetBoolean()
            ? BackendResult.Success(root.GetProperty("result").Clone())
            : BackendResult.Failure(root.GetProperty("error").GetString() ?? "error");
    }
}
