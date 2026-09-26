using System.Text.Json;
using System.Threading.Channels;
using PbxVoice.Conversation;

namespace PbxVoice.Tests;

/// <summary>
/// A scripted Grok Voice session. A forced message plays back at once. Each response request from
/// the flow (and each function output followed by one) releases the next scripted batch of server
/// events, as the real server would after the callee speaks or a tool returns.
/// </summary>
internal sealed class FakeVoiceSession : IVoiceSession
{
    private readonly Channel<VoiceEvent> _events = Channel.CreateUnbounded<VoiceEvent>();
    private readonly Queue<List<VoiceEvent>> _batches = new();
    private int _responses;

    /// <summary>Connecting throws at once (for example, xAI refuses the connection).</summary>
    public bool FailConnect { get; set; }

    /// <summary>Connecting never completes; the flow's 3 s limit has to end it.</summary>
    public bool HangConnect { get; set; }
    public VoiceSessionConfig? Config { get; private set; }
    public List<string> Actions { get; } = new();
    public List<string> FunctionOutputs { get; } = new();
    public int AudioChunks { get; private set; }
    public bool Disposed { get; private set; }

    public ChannelReader<VoiceEvent> Events => _events.Reader;

    public FakeVoiceSession Then(params VoiceEvent[] batch)
    {
        _batches.Enqueue(batch.ToList());
        return this;
    }

    public Task ConnectAsync(VoiceSessionConfig config, CancellationToken ct)
    {
        Config = config;
        if (FailConnect)
            throw new InvalidOperationException("connection refused");
        if (HangConnect)
            return Task.Delay(Timeout.Infinite, ct);
        return Task.CompletedTask;
    }

    public ValueTask AppendAudioAsync(ReadOnlyMemory<byte> pcm)
    {
        AudioChunks++;
        return ValueTask.CompletedTask;
    }

    public ValueTask ForceMessageAsync(string text, bool interruptible)
    {
        Actions.Add($"force:{text}");
        string id = $"r_force_{++_responses}";
        Emit(new ResponseStarted(id), new AudioDelta(id, new byte[3200]), new AssistantTranscript(id, $"i_{id}", text), new ResponseDone(id, "completed"));
        return ValueTask.CompletedTask;
    }

    public ValueTask SendFunctionOutputAsync(string callId, string output)
    {
        Actions.Add($"output:{callId}");
        FunctionOutputs.Add(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask CreateResponseAsync(string? instructions = null)
    {
        Actions.Add("response" + (instructions is null ? "" : $":{instructions}"));
        if (_batches.TryDequeue(out var batch))
            Emit(batch.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CancelResponseAsync()
    {
        Actions.Add("cancel");
        return ValueTask.CompletedTask;
    }

    public void Emit(params VoiceEvent[] events)
    {
        foreach (var e in events)
            _events.Writer.TryWrite(e);
    }

    public void Close() => _events.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ---- helpers for building scripts ----------------------------------------------------------

    private static int _ids;

    /// <summary>The assistant says <paramref name="text"/> in a response of its own.</summary>
    public static VoiceEvent[] Says(string text)
    {
        string id = $"r{Interlocked.Increment(ref _ids)}";
        return new VoiceEvent[] { new ResponseStarted(id), new AudioDelta(id, new byte[1600]), new AssistantTranscript(id, $"i{id}", text), new ResponseDone(id, "completed") };
    }

    /// <summary>The callee says <paramref name="text"/> (speech start, then the final transcription).</summary>
    public static VoiceEvent[] Callee(string text)
    {
        string item = $"u{Interlocked.Increment(ref _ids)}";
        return new VoiceEvent[] { new CalleeSpeechStarted(item), new CalleeTranscript(item, text, Final: true) };
    }

    /// <summary>The model calls a tool in a response of its own.</summary>
    public static VoiceEvent[] Tool(string name, object args)
    {
        string id = $"r{Interlocked.Increment(ref _ids)}";
        return new VoiceEvent[] { new ResponseStarted(id), new FunctionCall($"c{id}", name, JsonSerializer.Serialize(args)), new ResponseDone(id, "completed") };
    }

    public static VoiceEvent[] Batch(params VoiceEvent[][] parts) => parts.SelectMany(p => p).ToArray();
}

internal sealed class FakeVoiceSessionFactory : IVoiceSessionFactory
{
    public Queue<FakeVoiceSession> Next { get; } = new();
    public List<FakeVoiceSession> Created { get; } = new();

    public IVoiceSession Create()
    {
        var session = Next.Count > 0 ? Next.Dequeue() : new FakeVoiceSession { FailConnect = true };
        Created.Add(session);
        return session;
    }
}
