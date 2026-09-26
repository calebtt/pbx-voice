using PbxVoice.Speech;
using SipBot;

namespace PbxVoice.Sip;

/// <summary>
/// One call's media. PCMU only, with SipBotLib's base keep-alive off: the endpoint owns its own
/// <see cref="RtpPacedSender"/>, whose idle silence keeps NAT and the RTP timeout alive and whose
/// queue plays the clips (tested as P-L2 to P-L4). Inbound audio feeds the reply detector only
/// while a capture is armed, so the call's own prompt is never heard as a reply.
/// </summary>
internal sealed class CallAudioEndPoint : BaseAudioEndPoint
{
    private static readonly byte[] SilenceFrame = Enumerable.Repeat((byte)0x7F, 160).ToArray();

    private readonly object _lock = new();
    private bool _pacerStarted;
    private int _realFramesSent;
    private SpeechDetector? _detector;
    private TaskCompletionSource<bool>? _captureDone;
    private Action<byte[]>? _stream;
    private readonly List<byte> _pendingOut = new();

    public CallAudioEndPoint() : base(enableContinuousKeepAlive: false, enableWidebandAudio: false)
    {
    }

    public RtpPacedSender Pacer { get; } = new();

    /// <summary>Clip frames sent so far (keep-alive silence not counted).</summary>
    public int RealFramesSent => Volatile.Read(ref _realFramesSent);

    public override async Task StartAudio()
    {
        await base.StartAudio().ConfigureAwait(false);
        lock (_lock)
        {
            if (_pacerStarted)
                return;
            _pacerStarted = true;
        }
        Pacer.SendAction = OnSend;
        Pacer.Start();
    }

    private void OnSend(uint durationRtpUnits, byte[] frame)
    {
        if (!frame.AsSpan().SequenceEqual(SilenceFrame))
            Interlocked.Increment(ref _realFramesSent);
        ExternalAudioSourceEncodedSample(durationRtpUnits, frame);
    }

    public void ArmCapture(SpeechDetector detector, TaskCompletionSource<bool> done)
    {
        lock (_lock)
        {
            _detector = detector;
            _captureDone = done;
        }
    }

    public void StartStreaming(Action<byte[]> sink)
    {
        lock (_lock)
            _stream = sink;
    }

    public void StopStreaming()
    {
        lock (_lock)
            _stream = null;
    }

    /// <summary>8 kHz PCM in, μ-law 20 ms frames to the pacer; a partial frame waits for the next chunk.</summary>
    public void EnqueuePcm(ReadOnlySpan<byte> pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
        byte[] frames;
        lock (_pendingOut)
        {
            _pendingOut.AddRange(Audio.G711.Encode(samples));
            int whole = _pendingOut.Count / 160 * 160;
            if (whole == 0)
                return;
            frames = _pendingOut.GetRange(0, whole).ToArray();
            _pendingOut.RemoveRange(0, whole);
        }
        Pacer.Enqueue(frames);
    }

    public void ClearPlayback()
    {
        lock (_pendingOut)
            _pendingOut.Clear();
        Pacer.ResetBuffer();
    }

    public void DisarmCapture()
    {
        lock (_lock)
        {
            _detector = null;
            _captureDone = null;
        }
    }

    protected override Task ProcessAudioAsync(byte[] pcm, int sampleRateHz)
    {
        SpeechDetector? detector;
        TaskCompletionSource<bool>? done;
        Action<byte[]>? stream;
        lock (_lock)
        {
            detector = _detector;
            done = _captureDone;
            stream = _stream;
        }
        if (stream is not null && sampleRateHz == SpeechDetector.SampleRate)
            stream(pcm);
        if (detector is null || sampleRateHz != SpeechDetector.SampleRate)
            return Task.CompletedTask;
        bool finished;
        lock (detector)
            finished = detector.PushPcm(pcm);
        if (finished)
            done?.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task StopPacerAsync() => Pacer.Stop();

    public override Task InitializeAsync() => Task.CompletedTask;
    public override Task ShutdownAsync() => Task.CompletedTask;
}
