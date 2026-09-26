using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Policy;
using PbxVoice.Scheduling;
using PbxVoice.Service;
using PbxVoice.Speech;

namespace PbxVoice.Tests;

internal static class Policies
{
    public const string Chicago = "America/Chicago";

    public static PolicyFile Standard() => new()
    {
        Timezone = Chicago,
        DisplayName = "Alex",
        Contacts =
        {
            ["me"] = new Contact { Number = "+15555550100", Self = true },
            ["mom"] = new Contact { Number = "+15555550101" },
        },
        QuietHours = new QuietHours { Start = "21:00", End = "08:00", Tz = Chicago },
    };
}

/// <summary>One scripted answered call.</summary>
internal sealed class FakeCall : IActiveCall
{
    private readonly Queue<Capture> _captures;
    private readonly int? _hangUpDuringPlay;
    private readonly TimeSpan _playedBeforeHangUp;

    /// <param name="captures">Replies, in order. When they run out, every capture is "no speech".</param>
    /// <param name="hangUpDuringPlay">1-based play number during which the callee hangs up.</param>
    public FakeCall(IEnumerable<Capture>? captures = null, int? hangUpDuringPlay = null, TimeSpan? playedBeforeHangUp = null,
        bool hangUpDuringPause = false)
    {
        _captures = new Queue<Capture>(captures ?? Array.Empty<Capture>());
        _hangUpDuringPlay = hangUpDuringPlay;
        _playedBeforeHangUp = playedBeforeHangUp ?? TimeSpan.FromSeconds(1);
        HangUpDuringPause = hangUpDuringPause;
    }

    public bool HangUpDuringPause { get; }
    public List<string> Played { get; } = new();
    public int Captures { get; private set; }
    public bool HungUpByUs { get; private set; }
    public bool IsUp { get; private set; } = true;

    public Task<PlayResult> PlayAsync(Clip clip, CancellationToken ct)
    {
        if (!IsUp)
            return Task.FromResult(new PlayResult(PlayEnd.HungUp, TimeSpan.Zero));
        if (ct.IsCancellationRequested)
            return Task.FromResult(new PlayResult(PlayEnd.Cancelled, TimeSpan.Zero));
        Played.Add(clip.Id);
        if (_hangUpDuringPlay == Played.Count)
        {
            IsUp = false;
            return Task.FromResult(new PlayResult(PlayEnd.HungUp, _playedBeforeHangUp));
        }
        return Task.FromResult(new PlayResult(PlayEnd.Completed, clip.Duration));
    }

    public Task<Capture> CaptureAsync(DetectorSettings settings, CancellationToken ct)
    {
        Captures++;
        if (!IsUp)
            return Task.FromResult(Replies.HungUp());
        var capture = _captures.Count > 0 ? _captures.Dequeue() : Replies.Silence();
        if (capture.End == CaptureEnd.HungUp)
            IsUp = false;
        return Task.FromResult(capture);
    }

    public Task<bool> PauseAsync(TimeSpan duration, CancellationToken ct)
    {
        if (HangUpDuringPause)
            IsUp = false;
        return Task.FromResult(IsUp);
    }

    public Task HangupAsync()
    {
        if (IsUp)
            HungUpByUs = true;
        IsUp = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class Replies
{
    public static Capture Speech(int ms) => new(CaptureEnd.Silence, true, TimeSpan.FromMilliseconds(ms), new byte[ms * 8]);
    public static Capture Silence() => new(CaptureEnd.NoSpeech, false, TimeSpan.Zero, Array.Empty<byte>());
    public static Capture Keypad(char digit) => new(CaptureEnd.Keypad, false, TimeSpan.Zero, Array.Empty<byte>(), digit);
    public static Capture HungUp() => new(CaptureEnd.HungUp, false, TimeSpan.Zero, Array.Empty<byte>());
}

/// <summary>A scripted phone line. Each dial takes the next scripted result.</summary>
internal sealed class FakePhone : IPhoneLine
{
    private readonly Queue<Func<DialRequest, DialResult>> _script = new();

    public string Server => "pbx.example.test";
    public RegistrationInfo Registration { get; set; } = new("Registered", true, null, null);
    public int Reregisters { get; private set; }
    public RegistrationInfo? RegistrationAfterReregister { get; set; }
    public bool RegistersWhileWaiting { get; set; } = true;
    public List<DialRequest> Dials { get; } = new();
    public int? PingStatus { get; set; } = 200;

    public FakePhone Answer(FakeCall call) { _script.Enqueue(_ => new DialResult(DialKind.Answered, 200, "answered", call, 300, 180)); return this; }
    public FakePhone Fail(DialKind kind, int? status = null, string detail = "scripted") { _script.Enqueue(_ => new DialResult(kind, status, detail)); return this; }

    public void Reregister()
    {
        Reregisters++;
        if (RegistrationAfterReregister is { } after)
            Registration = after;
    }

    public Task<bool> WaitForRegistrationAsync(TimeSpan max, CancellationToken ct)
    {
        if (!Registration.Registered && RegistersWhileWaiting && !Registration.HardFailure)
            Registration = new RegistrationInfo("Registered", true, null, null);
        return Task.FromResult(Registration.Registered);
    }

    public Task<DialResult> DialAsync(DialRequest request, CancellationToken ct)
    {
        Dials.Add(request);
        if (_script.Count == 0)
            return Task.FromResult(new DialResult(DialKind.NoAnswer, null, "unscripted dial"));
        return Task.FromResult(_script.Dequeue()(request));
    }

    public Task<int?> PingServerAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(PingStatus);
}

internal sealed class FakeStt : ISpeechToText
{
    private readonly Queue<SttResult> _results = new();

    public List<IReadOnlyList<string>> KeyTerms { get; } = new();
    public TimeSpan Delay { get; set; }

    public FakeStt Says(params string[] texts) { foreach (var t in texts) _results.Enqueue(SttResult.Success(t)); return this; }
    public FakeStt Fails(string error = "503") { _results.Enqueue(SttResult.Failure(error)); return this; }

    public async Task<SttResult> TranscribeMuLawAsync(byte[] muLaw, IReadOnlyList<string> keyTerms, CancellationToken ct)
    {
        KeyTerms.Add(keyTerms);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct);
        return _results.Count > 0 ? _results.Dequeue() : SttResult.Success("");
    }
}

internal sealed class FakeTts : ITextToSpeech
{
    public List<string> Rendered { get; } = new();
    public Func<string, bool> FailWhen { get; set; } = _ => false;

    public Task<byte[]> SynthesizeMuLawAsync(string text, string voice, string language, double speed, CancellationToken ct)
    {
        if (FailWhen(text))
            throw new HttpRequestException("xAI TTS returned 503: unavailable");
        Rendered.Add(text);
        return Task.FromResult(Enumerable.Repeat((byte)0x55, 8000).ToArray()); // 1 s
    }
}

/// <summary>The real stores, executor, and service on a temp directory, a fake clock, and fakes for SIP and xAI.</summary>
internal sealed class Harness : IDisposable
{
    public Harness(PolicyFile? policy = null, DateTimeOffset? start = null)
    {
        Dir = Directory.CreateTempSubdirectory("pbx-voice-test-").FullName;
        Paths = new Hosting.StatePaths(Dir);
        Paths.EnsureCreated();
        // Monday 2026-10-05 10:00 in Chicago (CDT, UTC-5).
        Time = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));
        Policy = new PolicyProvider(policy ?? Policies.Standard());
        Clips = new ClipStore(Paths.Clips, Tts);
        Calls = new CallStore(Paths.Calls);
        Schedules = new ScheduleStore(Paths.Schedules);
        Executor = new Executor(Time, Phone, Policy, Calls, Clips, new ReplyListener(Stt, Time, TimeSpan.FromSeconds(3)));
        Service = new PbxVoiceService(Time, Policy, Schedules, Calls, Clips, Executor, Phone,
            new HostStatus { Version = "test", StateDirectory = Dir, XaiKeyPresent = true });
    }

    public string Dir { get; }
    public Hosting.StatePaths Paths { get; }
    public FakeTimeProvider Time { get; }
    public FakePhone Phone { get; } = new();
    public FakeStt Stt { get; } = new();
    public FakeTts Tts { get; } = new();
    public PolicyProvider Policy { get; }
    public ClipStore Clips { get; }
    public CallStore Calls { get; }
    public ScheduleStore Schedules { get; }
    public Executor Executor { get; }
    public PbxVoiceService Service { get; }

    public async Task<JsonElement> Op(string op, object? args = null)
    {
        var element = JsonSerializer.SerializeToElement(args ?? new { }, Json.Compact);
        var result = await Service.HandleAsync(op, element, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, Json.Compact);
    }

    public async Task<string> CallNow(object args) => (await Op("call_now", args)).GetProperty("call_id").GetString()!;

    public CallRecord Record(string callId) => Calls.Snapshot(callId)!;

    /// <summary>Runs every attempt that is due now; returns how many ran.</summary>
    public async Task<int> RunDue()
    {
        int n = 0;
        while (await Executor.RunDueAsync(CancellationToken.None))
            n++;
        return n;
    }

    /// <summary>Advances the clock to each next attempt and runs it, until the call is done.</summary>
    public async Task<CallRecord> RunToEnd(string callId, int maxSteps = 50)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            await RunDue();
            var r = Record(callId);
            if (r.Status == CallStatus.Done)
                return r;
            if (r.NextAttemptAt is { } next && next > Time.GetUtcNow())
                Time.SetUtcNow(next);
        }
        throw new InvalidOperationException($"call {callId} did not finish");
    }

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}
