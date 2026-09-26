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
/// Energy-based speech detection over 8 kHz 16-bit PCM in 20 ms frames. A frame is speech when its
/// level is above both an absolute floor and the tracked noise floor plus a margin. Speech starts
/// after three speech frames in a row and ends after <see cref="DetectorSettings.EndSilence"/> of
/// non-speech. The capture keeps 200 ms before the start so the first syllable reaches STT.
/// </summary>
internal sealed class SpeechDetector
{
    public const int SampleRate = 8000;
    private const int FrameSamples = 160; // 20 ms
    private const int FrameMs = 20;
    private const int StartFrames = 3;
    private const int PreRollFrames = 10;
    private const double AbsoluteFloorDbfs = -42;
    private const double MarginDb = 12;

    private readonly DetectorSettings _settings;
    private readonly List<short> _pending = new();
    private readonly Queue<short[]> _preRoll = new();
    private readonly List<short> _captured = new();
    private double _noiseFloorDb = -60;
    private int _elapsedMs;
    private int _run;
    private int _silenceMs;
    private int? _speechStartMs;
    private int _lastSpeechEndMs;

    public SpeechDetector(DetectorSettings settings)
    {
        _settings = settings;
    }

    public DetectorEnd End { get; private set; }
    public bool SpeechStarted => _speechStartMs is not null;

    /// <summary>From the first speech frame to the end of the last one.</summary>
    public TimeSpan Speech => _speechStartMs is { } s ? TimeSpan.FromMilliseconds(_lastSpeechEndMs - s) : TimeSpan.Zero;

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
            if (_pending.Count == FrameSamples)
            {
                ProcessFrame(_pending.ToArray());
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

    private void ProcessFrame(short[] frame)
    {
        _elapsedMs += FrameMs;
        double db = Dbfs(frame);
        bool speech = db > Math.Max(AbsoluteFloorDbfs, _noiseFloorDb + MarginDb);

        if (_speechStartMs is null)
        {
            _preRoll.Enqueue(frame);
            if (_preRoll.Count > PreRollFrames + StartFrames)
                _preRoll.Dequeue();
            if (speech)
            {
                if (++_run >= StartFrames)
                {
                    _speechStartMs = _elapsedMs - StartFrames * FrameMs;
                    _lastSpeechEndMs = _elapsedMs;
                    foreach (var f in _preRoll)
                        _captured.AddRange(f);
                    _preRoll.Clear();
                }
            }
            else
            {
                _run = 0;
                // Track the background level only while nobody is talking.
                _noiseFloorDb = Math.Min(-30, 0.9 * _noiseFloorDb + 0.1 * db);
                if (_elapsedMs >= _settings.StartTimeout.TotalMilliseconds)
                    End = DetectorEnd.NoSpeech;
            }
            return;
        }

        _captured.AddRange(frame);
        if (speech)
        {
            _silenceMs = 0;
            _lastSpeechEndMs = _elapsedMs;
        }
        else
        {
            _silenceMs += FrameMs;
        }

        if (_silenceMs >= _settings.EndSilence.TotalMilliseconds)
            End = DetectorEnd.Silence;
        else if (_elapsedMs - _speechStartMs >= _settings.MaxSpeech.TotalMilliseconds)
            End = DetectorEnd.MaxSpeech;
    }

    private static double Dbfs(short[] frame)
    {
        double sum = 0;
        foreach (var s in frame)
            sum += (double)s * s;
        double rms = Math.Sqrt(sum / frame.Length);
        return rms < 1 ? -96 : 20 * Math.Log10(rms / 32768.0);
    }
}
