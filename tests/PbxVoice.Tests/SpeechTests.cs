using PbxVoice.Policy;
using PbxVoice.Speech;
using Xunit;

namespace PbxVoice.Tests;

public class PhraseMatcherTests
{
    private static readonly Phrases Defaults = new();

    [Theory]
    [InlineData("I'm up.", "Awake")]
    [InlineData("I am up", "Awake")]
    [InlineData("i’m awake!", "Awake")]
    [InlineData("Yeah, I'm up", "Awake")]
    [InlineData("okay I'm awake", "Awake")]
    [InlineData("I'm up, I'm up", "Awake")]
    [InlineData("Snooze.", "Snooze")]
    [InlineData("five more minutes", "Snooze")]
    [InlineData("yes", "None")]
    [InlineData("okay", "None")]
    [InlineData("I'm up but tired", "None")]
    [InlineData("I'm not up", "None")]
    [InlineData("", "None")]
    public void Alarm(string transcript, string expected) =>
        Assert.Equal(Enum.Parse<ReplyIntent>(expected), PhraseMatcher.MatchAlarm(transcript, Defaults));

    [Theory]
    [InlineData("Got it.", "Confirm")]
    [InlineData("okay", "Confirm")]
    [InlineData("Yes, yes.", "Confirm")]
    [InlineData("Thank you!", "Confirm")]
    [InlineData("Repeat", "Repeat")]
    [InlineData("say that again", "Repeat")]
    [InlineData("got it thanks", "None")]
    [InlineData("I'm up", "None")]
    public void Message(string transcript, string expected) =>
        Assert.Equal(Enum.Parse<ReplyIntent>(expected), PhraseMatcher.MatchMessage(transcript, Defaults));

    [Fact]
    public void Normalization() =>
        Assert.Equal("im up", PhraseMatcher.Normalize("I am UP... I am up!"));
}

/// <summary>
/// The detector's state machine, with a stand-in classifier that calls a window speech when it is
/// loud. Real speech against the Silero model is in <see cref="SileroTests"/>.
/// </summary>
public class SpeechDetectorTests
{
    private const int Rate = 8000;

    private sealed class LoudIsSpeech : ISpeechClassifier
    {
        public int WindowSamples => 256;
        public void Reset() { }
        public float Probability(ReadOnlySpan<short> window)
        {
            double sum = 0;
            foreach (var s in window)
                sum += (double)s * s;
            return Math.Sqrt(sum / window.Length) > 500 ? 0.9f : 0.05f;
        }
    }

    /// <summary>Returns scripted probabilities, one per window.</summary>
    private sealed class Scripted : ISpeechClassifier
    {
        private readonly Queue<float> _p;
        public Scripted(IEnumerable<float> p) => _p = new Queue<float>(p);
        public int WindowSamples => 256;
        public void Reset() { }
        public float Probability(ReadOnlySpan<short> window) => _p.Count > 0 ? _p.Dequeue() : 0f;
    }

    internal static short[] Tone(int ms, double dbfs, int hz = 300)
    {
        double amp = 32768 * Math.Pow(10, dbfs / 20) * Math.Sqrt(2);
        return Enumerable.Range(0, Rate * ms / 1000).Select(i => (short)(amp * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();
    }

    internal static short[] Silence(int ms) => new short[Rate * ms / 1000];

    internal static SpeechDetector Feed(ISpeechClassifier classifier, params short[][] parts)
    {
        var d = new SpeechDetector(DetectorSettings.Reply, classifier);
        foreach (var p in parts)
        {
            // In 20 ms RTP-sized chunks, as the endpoint delivers them.
            for (int i = 0; i < p.Length && d.End == DetectorEnd.None; i += 160)
                d.Push(p.AsSpan(i, Math.Min(160, p.Length - i)));
        }
        return d;
    }

    [Fact]
    public void Nothing_said_ends_after_eight_seconds()
    {
        var d = Feed(new LoudIsSpeech(), Silence(9000));
        Assert.Equal(DetectorEnd.NoSpeech, d.End);
        Assert.False(d.SpeechStarted);
    }

    [Fact]
    public void A_one_second_reply_ends_on_silence_with_its_length()
    {
        var d = Feed(new LoudIsSpeech(), Silence(1000), Tone(1000, -20), Silence(1000));
        Assert.Equal(DetectorEnd.Silence, d.End);
        Assert.InRange(d.Speech.TotalMilliseconds, 1020, 1130); // 1 s, window rounding, and 2 x 30 ms padding
        // The capture keeps about 200 ms of pre-roll before the first syllable.
        Assert.True(d.Audio.Length >= Rate * 1.15);
    }

    [Fact]
    public void Long_speech_is_cut_at_five_seconds()
    {
        var d = Feed(new LoudIsSpeech(), Tone(7000, -20));
        Assert.Equal(DetectorEnd.MaxSpeech, d.End);
        Assert.InRange(d.Speech.TotalMilliseconds, 5010, 5110);
    }

    [Fact]
    public void Two_speech_windows_do_not_start_a_reply()
    {
        var d = Feed(new LoudIsSpeech(), Silence(500), Tone(40, -10), Silence(8000));
        Assert.Equal(DetectorEnd.NoSpeech, d.End);
    }

    [Fact]
    public void Speech_continues_above_the_lower_threshold_and_ends_below_it()
    {
        // Start at 0.5 (three windows), continue at 0.35: the 0.4 windows keep the reply going.
        var p = new List<float> { 0.1f, 0.6f, 0.6f, 0.6f, 0.4f, 0.4f, 0.4f, 0.6f };
        p.AddRange(Enumerable.Repeat(0.3f, 30));
        var d = Feed(new Scripted(p), Silence(2000));
        Assert.Equal(DetectorEnd.Silence, d.End);
        Assert.Equal(7 * 32 + 60, (int)d.Speech.TotalMilliseconds);
        Assert.Equal(0.6f, d.PeakProbability);
    }
}

/// <summary>The Silero model itself, on recorded speech (Fixtures/) and on synthetic noise.</summary>
public class SileroTests : IClassFixture<SileroTests.Model>
{
    public sealed class Model : IDisposable
    {
        internal SileroClassifier Classifier { get; } = SileroClassifier.Load();
        public void Dispose() => Classifier.Dispose();
    }

    private readonly SileroClassifier _vad;

    public SileroTests(Model model) => _vad = model.Classifier;

    private static short[] Fixture(string name) =>
        Audio.G711.Decode(Audio.MuLawWav.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".wav"))));

    private static short[] Scale(short[] pcm, double db) =>
        pcm.Select(s => (short)Math.Clamp(s * Math.Pow(10, db / 20), short.MinValue, short.MaxValue)).ToArray();

    private static short[] Noise(int ms, double dbfs, int seed = 1)
    {
        var rng = new Random(seed);
        double amp = 32768 * Math.Pow(10, dbfs / 20) * Math.Sqrt(3);
        return Enumerable.Range(0, 8 * ms).Select(_ => (short)(amp * (2 * rng.NextDouble() - 1))).ToArray();
    }

    private static short[] Mix(short[] a, short[] b) =>
        a.Select((s, i) => (short)Math.Clamp(s + (i < b.Length ? b[i] : 0), short.MinValue, short.MaxValue)).ToArray();

    private SpeechDetector Feed(params short[][] parts) => SpeechDetectorTests.Feed(_vad, parts);

    /// <summary>
    /// A short reply is found, its measured length is within 100 ms of how long it is audible
    /// (the span above -40 to -55 dBFS: 574-677 ms and 760-957 ms for these clips), and it falls
    /// inside PR-SPEECH-3's 0.5-2.5 s window, which counts a reply when speech-to-text is down.
    /// </summary>
    [Theory]
    [InlineData("im_up", 574, 677)]
    [InlineData("got_it", 760, 957)]
    public void A_short_reply_is_found_with_about_its_spoken_length(string name, int audibleMinMs, int audibleMaxMs)
    {
        var d = Feed(SpeechDetectorTests.Silence(1000), Fixture(name), SpeechDetectorTests.Silence(1500));
        Assert.Equal(DetectorEnd.Silence, d.End);
        Assert.True(d.PeakProbability > 0.8f, $"peak {d.PeakProbability}");
        Assert.InRange(d.Speech.TotalMilliseconds, audibleMinMs - 100, audibleMaxMs + 100);
        Assert.InRange(d.Speech.TotalMilliseconds, 500, 2500);
    }

    [Fact]
    public void A_voicemail_greeting_is_longer_than_the_fallback_window()
    {
        var d = Feed(SpeechDetectorTests.Silence(500), Fixture("greeting"), SpeechDetectorTests.Silence(1500));
        Assert.True(d.SpeechStarted);
        Assert.True(d.Speech.TotalMilliseconds > 2500, $"speech {d.Speech.TotalMilliseconds} ms");
    }

    [Fact]
    public void Noise_and_a_tone_are_not_speech()
    {
        Assert.Equal(DetectorEnd.NoSpeech, Feed(Noise(9000, -30)).End);
        Assert.Equal(DetectorEnd.NoSpeech, Feed(SpeechDetectorTests.Tone(9000, -15, 1000)).End);
    }

    [Fact]
    public void A_reply_over_background_noise_is_found()
    {
        var speech = Mix(Fixture("im_up"), Noise(950, -40, 2));
        var d = Feed(Noise(1000, -40, 3), speech, Noise(1500, -40, 4));
        Assert.True(d.SpeechStarted);
        Assert.Equal(DetectorEnd.Silence, d.End);
    }

    [Fact]
    public void A_quiet_reply_is_found()
    {
        // 20 dB down, as from a groggy speaker or a far-away phone.
        var d = Feed(SpeechDetectorTests.Silence(1000), Scale(Fixture("im_up"), -20), SpeechDetectorTests.Silence(1500));
        Assert.True(d.SpeechStarted, $"peak {d.PeakProbability}");
    }
}
