using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;

namespace PbxVoice.Conversation;

internal abstract record VoiceEvent;
internal sealed record ResponseStarted(string ResponseId) : VoiceEvent;
internal sealed record AudioDelta(string ResponseId, byte[] Pcm) : VoiceEvent;
internal sealed record AssistantTranscript(string ResponseId, string ItemId, string Text) : VoiceEvent;
internal sealed record ResponseDone(string ResponseId, string Status) : VoiceEvent;
internal sealed record CalleeSpeechStarted(string ItemId) : VoiceEvent;
internal sealed record CalleeTranscript(string ItemId, string Text, bool Final) : VoiceEvent;
internal sealed record FunctionCall(string CallId, string Name, string ArgumentsJson) : VoiceEvent;
internal sealed record SessionError(string Message) : VoiceEvent;
internal sealed record SessionClosed(string Reason) : VoiceEvent;

internal sealed record VoiceSessionConfig(
    string Instructions,
    string Voice,
    string Model,
    string ReasoningEffort,
    string Language,
    IReadOnlyList<object> Tools);

/// <summary>A Grok Voice realtime session: the callee's audio in, the model's audio and events out.</summary>
internal interface IVoiceSession : IAsyncDisposable
{
    ChannelReader<VoiceEvent> Events { get; }

    /// <summary>Connects and applies the configuration; completes when the session is ready.</summary>
    Task ConnectAsync(VoiceSessionConfig config, CancellationToken ct);

    /// <summary>The callee's audio, 8 kHz 16-bit PCM.</summary>
    ValueTask AppendAudioAsync(ReadOnlyMemory<byte> pcm);

    /// <summary>Speaks exact text (xAI <c>force_message</c>); the model cannot change it.</summary>
    ValueTask ForceMessageAsync(string text, bool interruptible);

    ValueTask SendFunctionOutputAsync(string callId, string output);

    ValueTask CreateResponseAsync(string? instructions = null);

    ValueTask CancelResponseAsync();
}

internal interface IVoiceSessionFactory
{
    IVoiceSession Create();
}

internal sealed class XaiVoiceSessionFactory : IVoiceSessionFactory
{
    private readonly string _apiKey;
    private readonly Uri _baseUri;

    public XaiVoiceSessionFactory(string apiKey, Uri? baseUri = null)
    {
        _apiKey = apiKey;
        _baseUri = baseUri ?? new Uri("wss://api.x.ai/v1/realtime");
    }

    public IVoiceSession Create() => new XaiVoiceSession(_apiKey, _baseUri);
}

/// <summary>
/// xAI's realtime voice API (<c>wss://api.x.ai/v1/realtime?model=…</c>), 8 kHz PCM both ways,
/// server-side turn detection, and transcription of the callee's speech. Ported from homeline's
/// GrokRealtimeSession without its HomeLine tools. The key is sent only in the Authorization header.
/// </summary>
internal sealed class XaiVoiceSession : IVoiceSession
{
    public const int SampleRate = 8000;

    private readonly string _apiKey;
    private readonly Uri _baseUri;
    private readonly Channel<VoiceEvent> _events = Channel.CreateUnbounded<VoiceEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _ws;
    private Task? _receive;
    private Task? _send;

    public XaiVoiceSession(string apiKey, Uri baseUri)
    {
        _apiKey = apiKey;
        _baseUri = baseUri;
    }

    public ChannelReader<VoiceEvent> Events => _events.Reader;

    public async Task ConnectAsync(VoiceSessionConfig config, CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", "Bearer " + _apiKey);
        var uri = new Uri($"{_baseUri}?model={Uri.EscapeDataString(config.Model)}");
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _send = Task.Run(() => SendLoopAsync(_cts.Token));

        await EnqueueAsync(new
        {
            type = "session.update",
            session = new
            {
                instructions = config.Instructions,
                voice = config.Voice,
                reasoning = new { effort = config.ReasoningEffort },
                turn_detection = new { type = "server_vad", threshold = 0.75, silence_duration_ms = 700, prefix_padding_ms = 300 },
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = SampleRate },
                        transcription = new { language_hint = config.Language },
                    },
                    output = new { format = new { type = "audio/pcm", rate = SampleRate } },
                },
                tools = config.Tools,
            },
        }).ConfigureAwait(false);

        using var reg = ct.Register(() => _ready.TrySetCanceled(ct));
        await _ready.Task.ConfigureAwait(false);
    }

    public ValueTask AppendAudioAsync(ReadOnlyMemory<byte> pcm) =>
        EnqueueAsync(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(pcm.Span) });

    public ValueTask ForceMessageAsync(string text, bool interruptible) =>
        EnqueueAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "force_message",
                role = "assistant",
                interruptible,
                content = new[] { new { type = "output_text", text } },
            },
        });

    public ValueTask SendFunctionOutputAsync(string callId, string output) =>
        EnqueueAsync(new { type = "conversation.item.create", item = new { type = "function_call_output", call_id = callId, output } });

    public ValueTask CreateResponseAsync(string? instructions = null) =>
        instructions is null
            ? EnqueueAsync(new { type = "response.create" })
            : EnqueueAsync(new { type = "response.create", response = new { instructions } });

    public ValueTask CancelResponseAsync() => EnqueueAsync(new { type = "response.cancel" });

    private ValueTask EnqueueAsync(object payload) => _outbound.Writer.WriteAsync(JsonSerializer.Serialize(payload));

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_ws?.State != WebSocketState.Open)
                    break;
                await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        string reason = "closed";
        try
        {
            while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        reason = $"closed by server ({result.CloseStatus} {result.CloseStatusDescription})";
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            reason = ex is OperationCanceledException ? "closed" : "connection lost: " + ex.Message;
        }
        finally
        {
            _ready.TrySetException(new InvalidOperationException("voice session " + reason));
            _events.Writer.TryWrite(new SessionClosed(reason));
            _events.Writer.TryComplete();
        }
    }

    /// <summary>Maps one server event. Unknown events are ignored.</summary>
    internal void Handle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        string type = Str(e, "type");
        switch (type)
        {
            case "session.updated":
                _ready.TrySetResult();
                break;
            case "response.created":
                _events.Writer.TryWrite(new ResponseStarted(e.TryGetProperty("response", out var r) ? Str(r, "id") : ""));
                break;
            case "response.output_audio.delta":
            case "response.audio.delta":
                if (Str(e, "delta") is { Length: > 0 } b64)
                    _events.Writer.TryWrite(new AudioDelta(Str(e, "response_id"), Convert.FromBase64String(b64)));
                break;
            case "response.output_audio_transcript.done":
            case "response.audio_transcript.done":
                _events.Writer.TryWrite(new AssistantTranscript(Str(e, "response_id"), Str(e, "item_id"), Str(e, "transcript")));
                break;
            case "response.done":
                var resp = e.TryGetProperty("response", out var rd) ? rd : default;
                _events.Writer.TryWrite(new ResponseDone(resp.ValueKind == JsonValueKind.Object ? Str(resp, "id") : "",
                    resp.ValueKind == JsonValueKind.Object ? Str(resp, "status") : ""));
                break;
            case "input_audio_buffer.speech_started":
                _events.Writer.TryWrite(new CalleeSpeechStarted(Str(e, "item_id")));
                break;
            // xAI documents the cumulative "updated" event; sessions also send "completed" with the final text.
            case "conversation.item.input_audio_transcription.updated":
            case "conversation.item.input_audio_transcription.delta":
                if (Str(e, "transcript") is { Length: > 0 } partial)
                    _events.Writer.TryWrite(new CalleeTranscript(Str(e, "item_id"), partial, Final: false));
                break;
            case "conversation.item.input_audio_transcription.completed":
                _events.Writer.TryWrite(new CalleeTranscript(Str(e, "item_id"), Str(e, "transcript"), Final: true));
                break;
            case "response.function_call_arguments.done":
                _events.Writer.TryWrite(new FunctionCall(Str(e, "call_id"), Str(e, "name"), Str(e, "arguments") is { Length: > 0 } a ? a : "{}"));
                break;
            case "error":
                string detail = e.TryGetProperty("error", out var err) ? err.GetRawText() : json;
                Log.Warning("Voice session error: {Detail}", detail.Length > 300 ? detail[..300] : detail);
                _events.Writer.TryWrite(new SessionError(detail));
                break;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public async ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or OperationCanceledException)
        {
        }
        _cts.Cancel();
        if (_receive is not null) await _receive.ConfigureAwait(false);
        if (_send is not null) await _send.ConfigureAwait(false);
        _ws?.Dispose();
        _cts.Dispose();
    }
}
