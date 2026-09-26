using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PbxVoice.Conversation;
using Xunit;

namespace PbxVoice.Tests;

/// <summary>XaiVoiceSession against a local WebSocket server that plays xAI's part.</summary>
public class VoiceSessionProtocolTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private Uri _base = null!;
    private readonly Channel<JsonElement> _received = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<string> _toSend = Channel.CreateUnbounded<string>();
    private string? _authorization;
    private string? _model;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/v1/realtime", async (HttpContext context) =>
        {
            _authorization = context.Request.Headers.Authorization;
            _model = context.Request.Query["model"];
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var sender = Task.Run(async () =>
            {
                await foreach (var json in _toSend.Reader.ReadAllAsync())
                {
                    if (json == "CLOSE")
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    }
                    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            });
            var buffer = new byte[1 << 20];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                _received.Writer.TryWrite(JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone());
            }
            await sender;
        });
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _base = new Uri(address.Replace("http://", "ws://") + "/v1/realtime");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<JsonElement> Next(string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var msg = await _received.Reader.ReadAsync(timeout.Token);
            if (msg.GetProperty("type").GetString() == type)
                return msg;
        }
    }

    private static async Task<VoiceEvent> Event(IVoiceSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await session.Events.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task The_client_speaks_the_xai_protocol_and_maps_its_events()
    {
        var factory = new XaiVoiceSessionFactory("test-key", _base);
        await using var session = factory.Create();
        var config = new VoiceSessionConfig("Be brief.", "eve", "grok-voice-latest", "none", "en",
            Instructions.Tools(new Calls.Brief { Goal = "g", Ask = { new Calls.AskItem { Name = "when", Question = "When?" } } }));

        var connecting = session.ConnectAsync(config, CancellationToken.None);
        var update = (await Next("session.update")).GetProperty("session");
        Assert.False(connecting.IsCompleted); // waits for session.updated
        await _toSend.Writer.WriteAsync("""{"type":"session.updated"}""");
        await connecting;

        Assert.Equal("Bearer test-key", _authorization);
        Assert.Equal("grok-voice-latest", _model);
        Assert.Equal("Be brief.", update.GetProperty("instructions").GetString());
        Assert.Equal("eve", update.GetProperty("voice").GetString());
        Assert.Equal("none", update.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("audio/pcm", update.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(8000, update.GetProperty("audio").GetProperty("output").GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("en", update.GetProperty("audio").GetProperty("input").GetProperty("transcription").GetProperty("language_hint").GetString());
        Assert.Equal("server_vad", update.GetProperty("turn_detection").GetProperty("type").GetString());
        Assert.Equal(4, update.GetProperty("tools").GetArrayLength());

        await session.ForceMessageAsync("Hello there.", interruptible: false);
        var force = (await Next("conversation.item.create")).GetProperty("item");
        Assert.Equal("force_message", force.GetProperty("type").GetString());
        Assert.False(force.GetProperty("interruptible").GetBoolean());
        Assert.Equal("Hello there.", force.GetProperty("content")[0].GetProperty("text").GetString());

        await session.AppendAudioAsync(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), (await Next("input_audio_buffer.append")).GetProperty("audio").GetString());

        await session.SendFunctionOutputAsync("c1", """{"ok":true}""");
        var output = (await Next("conversation.item.create")).GetProperty("item");
        Assert.Equal("function_call_output", output.GetProperty("type").GetString());
        Assert.Equal("c1", output.GetProperty("call_id").GetString());

        await session.CreateResponseAsync("Ask the first question.");
        Assert.Equal("Ask the first question.", (await Next("response.create")).GetProperty("response").GetProperty("instructions").GetString());
        await session.CancelResponseAsync();
        await Next("response.cancel");

        string audio = Convert.ToBase64String(new byte[320]);
        foreach (var json in new[]
        {
            """{"type":"response.created","response":{"id":"r1"}}""",
            $$"""{"type":"response.output_audio.delta","response_id":"r1","delta":"{{audio}}"}""",
            """{"type":"response.output_audio_transcript.done","response_id":"r1","item_id":"i1","transcript":"Hi."}""",
            """{"type":"response.done","response":{"id":"r1","status":"completed"}}""",
            """{"type":"input_audio_buffer.speech_started","item_id":"u1"}""",
            """{"type":"conversation.item.input_audio_transcription.updated","item_id":"u1","transcript":"I'm"}""",
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"u1","transcript":"I'm up."}""",
            """{"type":"response.function_call_arguments.done","call_id":"c9","name":"record_answer","arguments":"{\"name\":\"when\"}"}""",
            """{"type":"ping"}""",
            """{"type":"error","error":{"message":"bad event"}}""",
            "CLOSE",
        })
            await _toSend.Writer.WriteAsync(json);

        Assert.Equal(new ResponseStarted("r1"), await Event(session));
        var delta = Assert.IsType<AudioDelta>(await Event(session));
        Assert.Equal(("r1", 320), (delta.ResponseId, delta.Pcm.Length));
        Assert.Equal(new AssistantTranscript("r1", "i1", "Hi."), await Event(session));
        Assert.Equal(new ResponseDone("r1", "completed"), await Event(session));
        Assert.Equal(new CalleeSpeechStarted("u1"), await Event(session));
        Assert.Equal(new CalleeTranscript("u1", "I'm", Final: false), await Event(session));
        Assert.Equal(new CalleeTranscript("u1", "I'm up.", Final: true), await Event(session));
        Assert.Equal(new FunctionCall("c9", "record_answer", """{"name":"when"}"""), await Event(session));
        Assert.Contains("bad event", Assert.IsType<SessionError>(await Event(session)).Message);
        Assert.IsType<SessionClosed>(await Event(session));
    }

    [Fact]
    public async Task A_refused_connection_fails_the_connect()
    {
        var factory = new XaiVoiceSessionFactory("k", new Uri(_base.ToString().Replace("/v1/realtime", "/nowhere")));
        await using var session = factory.Create();
        await Assert.ThrowsAnyAsync<Exception>(() => session.ConnectAsync(
            new VoiceSessionConfig("x", "eve", "m", "none", "en", Array.Empty<object>()), CancellationToken.None));
    }
}
