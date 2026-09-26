using System.Collections.Concurrent;
using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Speech;
using SipBot;

namespace PbxVoice.Sip;

/// <summary>An answered SipBotLib call: clip playback, reply capture, keypad digits, and hang-up.</summary>
internal sealed class SipActiveCall : IActiveCall
{
    private const int FrameBytes = 160;

    private readonly SipClient _client;
    private readonly CallAudioEndPoint _endpoint;
    private readonly ISpeechClassifier _vad;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<char> _digits = new();
    private TaskCompletionSource<char>? _digitWaiter;

    public SipActiveCall(SipClient client, CallAudioEndPoint endpoint, ISpeechClassifier vad)
    {
        _client = client;
        _endpoint = endpoint;
        _vad = vad;
        _client.CallEnded += OnCallEnded;
        _client.DtmfDigitReceived += OnDigit;
        if (!_client.IsCallActive)
            _ended.TrySetResult();
    }

    public bool IsUp => !_ended.Task.IsCompleted;

    public Task Ended => _ended.Task;

    public void StartStreaming(Action<byte[]> onCalleeAudio) => _endpoint.StartStreaming(onCalleeAudio);

    public void StopStreaming() => _endpoint.StopStreaming();

    public void EnqueueAudio(ReadOnlySpan<byte> pcm) => _endpoint.EnqueuePcm(pcm);

    public void ClearPlayback() => _endpoint.ClearPlayback();

    public bool PlaybackPending => _endpoint.Pacer.IsPlaying;

    private void OnCallEnded(SipClient _) => _ended.TrySetResult();

    private void OnDigit(SipClient _, char digit)
    {
        _digits.Enqueue(digit);
        Volatile.Read(ref _digitWaiter)?.TrySetResult(digit);
    }

    public async Task<PlayResult> PlayAsync(Clip clip, CancellationToken ct)
    {
        if (!IsUp)
            return new PlayResult(PlayEnd.HungUp, TimeSpan.Zero);
        if (ct.IsCancellationRequested)
            return new PlayResult(PlayEnd.Cancelled, TimeSpan.Zero);

        int before = _endpoint.RealFramesSent;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnComplete() => complete.TrySetResult();
        _endpoint.Pacer.SendingComplete += OnComplete;
        try
        {
            _endpoint.Pacer.Enqueue(PadToFrames(clip.MuLaw));
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => cancelled.TrySetResult());
            var first = await Task.WhenAny(complete.Task, _ended.Task, cancelled.Task).ConfigureAwait(false);
            var end = first == complete.Task ? PlayEnd.Completed : first == _ended.Task ? PlayEnd.HungUp : PlayEnd.Cancelled;
            if (end != PlayEnd.Completed)
                _endpoint.Pacer.ResetBuffer(); // stop sending the rest; this also raises SendingComplete
            return new PlayResult(end, TimeSpan.FromMilliseconds(20.0 * (_endpoint.RealFramesSent - before)));
        }
        finally
        {
            _endpoint.Pacer.SendingComplete -= OnComplete;
        }
    }

    public async Task<Capture> CaptureAsync(DetectorSettings settings, CancellationToken ct)
    {
        if (_digits.TryDequeue(out var early))
            return new Capture(CaptureEnd.Keypad, false, TimeSpan.Zero, Array.Empty<byte>(), early);
        if (!IsUp)
            return new Capture(CaptureEnd.HungUp, false, TimeSpan.Zero, Array.Empty<byte>());

        var detector = new SpeechDetector(settings, _vad);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var digit = new TaskCompletionSource<char>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _digitWaiter, digit);
        if (_digits.TryDequeue(out var raced))
            digit.TrySetResult(raced);
        _endpoint.ArmCapture(detector, done);
        using var reg = ct.Register(() => cancelled.TrySetResult());
        // The detector counts time by received audio; this backstop ends the capture if RTP stops.
        var backstop = Task.Delay(settings.StartTimeout + settings.MaxSpeech + TimeSpan.FromSeconds(2), CancellationToken.None);
        Task first;
        try
        {
            first = await Task.WhenAny(done.Task, digit.Task, _ended.Task, cancelled.Task, backstop).ConfigureAwait(false);
        }
        finally
        {
            _endpoint.DisarmCapture();
            Volatile.Write(ref _digitWaiter, null);
        }

        bool started;
        TimeSpan speech;
        byte[] audio;
        DetectorEnd detectorEnd;
        float peak;
        lock (detector)
        {
            started = detector.SpeechStarted;
            speech = detector.Speech;
            audio = G711.Encode(detector.Audio);
            detectorEnd = detector.End;
            peak = detector.PeakProbability;
        }

        if (first == digit.Task)
        {
            _digits.TryDequeue(out _);
            return new Capture(CaptureEnd.Keypad, started, speech, audio, digit.Task.Result, peak);
        }
        if (first == cancelled.Task)
            return new Capture(CaptureEnd.Cancelled, started, speech, audio, VadPeak: peak);
        if (first == _ended.Task)
            return new Capture(CaptureEnd.HungUp, started, speech, audio, VadPeak: peak);
        var end = detectorEnd switch
        {
            DetectorEnd.Silence => CaptureEnd.Silence,
            DetectorEnd.MaxSpeech => CaptureEnd.MaxSpeech,
            _ => started ? CaptureEnd.MaxSpeech : CaptureEnd.NoSpeech,
        };
        return new Capture(end, started, speech, audio, VadPeak: peak);
    }

    public async Task<bool> PauseAsync(TimeSpan duration, CancellationToken ct)
    {
        var delay = Task.Delay(duration, ct);
        await Task.WhenAny(delay, _ended.Task).ConfigureAwait(false);
        return IsUp && !ct.IsCancellationRequested;
    }

    public async Task HangupAsync()
    {
        if (!IsUp)
            return;
        _client.Hangup();
        await Task.WhenAny(_ended.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _client.CallEnded -= OnCallEnded;
        _client.DtmfDigitReceived -= OnDigit;
        await _endpoint.StopPacerAsync().ConfigureAwait(false);
    }

    private static byte[] PadToFrames(byte[] muLaw)
    {
        int frames = (muLaw.Length + FrameBytes - 1) / FrameBytes;
        if (frames * FrameBytes == muLaw.Length)
            return muLaw;
        var padded = Enumerable.Repeat((byte)0xFF, frames * FrameBytes).ToArray();
        Buffer.BlockCopy(muLaw, 0, padded, 0, muLaw.Length);
        return padded;
    }
}
