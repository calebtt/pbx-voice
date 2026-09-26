namespace PbxVoice.Speech;

internal enum DetectorEnd { None, NoSpeech, Silence, MaxSpeech }

internal sealed record DetectorSettings(
    TimeSpan StartTimeout,
    TimeSpan EndSilence,
    TimeSpan MaxSpeech)
{
    /// <summary>PR-ALARM-3: up to 8 s for speech to start; a reply ends at ~0.6 s of silence or 5 s of speech.</summary>
    public static DetectorSettings Reply { get; } = new(TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(5));
}

/// <summary>
/// Finds one spoken reply in 8 kHz 16-bit PCM, window by window, using a speech classifier
/// (Silero VAD in the daemon). Speech starts after three speech windows in a row and ends after
/// <see cref="DetectorSettings.EndSilence"/> without speech. A window starts speech at a
/// probability of <see cref="StartThreshold"/> and keeps it going at
/// <see cref="ContinueThreshold"/> (Silero's usual hysteresis). The capture keeps about 200 ms
/// before the start so the first syllable reaches speech-to-text.
/// </summary>
internal sealed class SpeechDetector
{
    public const int SampleRate = 8000;
    public const float StartThreshold = 0.5f;
    public const float ContinueThreshold = 0.35f;
    private const int StartWindows = 3;
    private const int PreRollMs = 200;

    /// <summary>
    /// Silero under-scores the soft start and end of an utterance, so its reference segmenter
    /// pads each segment by 30 ms on both sides (<c>speech_pad_ms</c>); the speech length here
    /// does the same.
    /// </summary>
    private const int SpeechPadMs = 30;

    private readonly DetectorSettings _settings;
    private readonly ISpeechClassifier _classifier;
    private readonly int _windowSamples;
    private readonly int _windowMs;
    private readonly int _preRollWindows;
    private readonly List<short> _pending = new();
    private readonly Queue<short[]> _preRoll = new();
    private readonly List<short> _captured = new();
    private int _elapsedMs;
    private int _run;
    private int _silenceMs;
    private int? _speechStartMs;
    private int _lastSpeechEndMs;

    public SpeechDetector(DetectorSettings settings, ISpeechClassifier classifier)
    {
        _settings = settings;
        _classifier = classifier;
        _classifier.Reset();
        _windowSamples = classifier.WindowSamples;
        _windowMs = _windowSamples * 1000 / SampleRate;
        _preRollWindows = (PreRollMs + _windowMs - 1) / _windowMs;
    }

    public DetectorEnd End { get; private set; }
    public bool SpeechStarted => _speechStartMs is not null;

    /// <summary>The highest speech probability seen, for tuning.</summary>
    public float PeakProbability { get; private set; }

    /// <summary>From the start of the first speech window to the end of the last one, padded as Silero does.</summary>
    public TimeSpan Speech => _speechStartMs is { } s ? TimeSpan.FromMilliseconds(_lastSpeechEndMs - s + 2 * SpeechPadMs) : TimeSpan.Zero;

    /// <summary>The captured reply (pre-roll included), as 16-bit PCM samples.</summary>
    public short[] Audio => _captured.ToArray();

    /// <summary>Feeds samples in any chunk size. Returns true once the detector has finished.</summary>
    public bool Push(ReadOnlySpan<short> samples)
    {
        foreach (var s in samples)
        {
            if (End != DetectorEnd.None)
                return true;
            _pending.Add(s);
            if (_pending.Count == _windowSamples)
            {
                ProcessWindow(_pending.ToArray());
                _pending.Clear();
            }
        }
        return End != DetectorEnd.None;
    }

    /// <summary>Feeds 16-bit little-endian PCM bytes.</summary>
    public bool PushPcm(ReadOnlySpan<byte> pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
        return Push(samples);
    }

    private void ProcessWindow(short[] window)
    {
        _elapsedMs += _windowMs;
        float p = _classifier.Probability(window);
        PeakProbability = Math.Max(PeakProbability, p);

        if (_speechStartMs is null)
        {
            _preRoll.Enqueue(window);
            if (_preRoll.Count > _preRollWindows + StartWindows)
                _preRoll.Dequeue();
            if (p >= StartThreshold)
            {
                if (++_run >= StartWindows)
                {
                    _speechStartMs = _elapsedMs - StartWindows * _windowMs;
                    _lastSpeechEndMs = _elapsedMs;
                    foreach (var w in _preRoll)
                        _captured.AddRange(w);
                    _preRoll.Clear();
                }
            }
            else
            {
                _run = 0;
                if (_elapsedMs >= _settings.StartTimeout.TotalMilliseconds)
                    End = DetectorEnd.NoSpeech;
            }
            return;
        }

        _captured.AddRange(window);
        if (p >= ContinueThreshold)
        {
            _silenceMs = 0;
            _lastSpeechEndMs = _elapsedMs;
        }
        else
        {
            _silenceMs += _windowMs;
        }

        if (_silenceMs >= _settings.EndSilence.TotalMilliseconds)
            End = DetectorEnd.Silence;
        else if (_elapsedMs - _speechStartMs >= _settings.MaxSpeech.TotalMilliseconds)
            End = DetectorEnd.MaxSpeech;
    }
}
