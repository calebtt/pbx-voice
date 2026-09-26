using System.Diagnostics;
using MinimalSileroVAD.Core;

namespace PbxVoice.Speech;

/// <summary>Speech probability for fixed-size windows of 8 kHz 16-bit PCM.</summary>
internal interface ISpeechClassifier
{
    /// <summary>Samples per window.</summary>
    int WindowSamples { get; }

    /// <summary>Clears recurrent state before a new reply.</summary>
    void Reset();

    /// <summary>The probability (0..1) that the window contains speech.</summary>
    float Probability(ReadOnlySpan<short> window);
}

/// <summary>
/// Silero VAD V5 at 8 kHz (256-sample, 32 ms windows) through MinimalSileroVad and the CPU build
/// of ONNX Runtime. The model is embedded in MinimalSileroVad's assembly, so nothing is downloaded.
/// One instance serves the daemon; the executor runs one call at a time.
/// </summary>
internal sealed class SileroClassifier : ISpeechClassifier, IDisposable
{
    private const string ModelResource = "MinimalSileroVAD.Core.models.silero_vad_v5.onnx";

    private readonly SileroModelV5 _model;
    private readonly byte[] _buffer = new byte[SileroModelV5.Samples8k * 2];

    private SileroClassifier(SileroModelV5 model)
    {
        _model = model;
    }

    public int WindowSamples => SileroModelV5.Samples8k;

    /// <summary>How long loading the model took, for <c>selftest</c> and the startup log.</summary>
    public TimeSpan LoadTime { get; private init; }

    public static SileroClassifier Load()
    {
        var watch = Stopwatch.StartNew();
        using var stream = typeof(SileroModelV5).Assembly.GetManifestResourceStream(ModelResource)
                           ?? throw new InvalidOperationException($"Silero model resource '{ModelResource}' not found");
        // The threshold here is unused: the detector reads the probability and applies its own.
        var model = new SileroModelV5(stream, threshold: 0.5f);
        return new SileroClassifier(model) { LoadTime = watch.Elapsed };
    }

    public void Reset() => _model.ResetState();

    public float Probability(ReadOnlySpan<short> window)
    {
        if (window.Length != WindowSamples)
            throw new ArgumentException($"Silero V5 at 8 kHz needs {WindowSamples} samples per window", nameof(window));
        lock (_buffer)
        {
            for (int i = 0; i < window.Length; i++)
            {
                _buffer[2 * i] = (byte)window[i];
                _buffer[2 * i + 1] = (byte)(window[i] >> 8);
            }
            _model.IsSpeech(_buffer, SpeechDetector.SampleRate);
            return _model.LastProbability;
        }
    }

    public void Dispose() => _model.Dispose();
}
