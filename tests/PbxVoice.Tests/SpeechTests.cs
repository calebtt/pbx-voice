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

public class SpeechDetectorTests
{
    private const int Rate = 8000;

    private static short[] Tone(int ms, double dbfs, int hz = 300)
    {
        double amp = 32768 * Math.Pow(10, dbfs / 20) * Math.Sqrt(2);
        return Enumerable.Range(0, Rate * ms / 1000).Select(i => (short)(amp * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();
    }

    private static short[] Noise(int ms, double dbfs, int seed = 1)
    {
        var rng = new Random(seed);
        double amp = 32768 * Math.Pow(10, dbfs / 20) * Math.Sqrt(3);
        return Enumerable.Range(0, Rate * ms / 1000).Select(_ => (short)(amp * (2 * rng.NextDouble() - 1))).ToArray();
    }

    private static short[] Silence(int ms) => new short[Rate * ms / 1000];

    private static SpeechDetector Feed(params short[][] parts)
    {
        var d = new SpeechDetector(DetectorSettings.Reply);
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
        var d = Feed(Silence(9000));
        Assert.Equal(DetectorEnd.NoSpeech, d.End);
        Assert.False(d.SpeechStarted);
    }

    [Fact]
    public void A_one_second_reply_ends_on_silence_with_its_length()
    {
        var d = Feed(Silence(1000), Tone(1000, -20), Silence(1000));
        Assert.Equal(DetectorEnd.Silence, d.End);
        Assert.InRange(d.Speech.TotalMilliseconds, 960, 1040);
        // The capture keeps 200 ms of pre-roll before the first syllable.
        Assert.True(d.Audio.Length >= Rate * 1.15);
    }

    [Fact]
    public void Long_speech_is_cut_at_five_seconds()
    {
        var d = Feed(Tone(7000, -20));
        Assert.Equal(DetectorEnd.MaxSpeech, d.End);
        Assert.InRange(d.Speech.TotalMilliseconds, 4900, 5100);
    }

    [Fact]
    public void Steady_background_noise_is_not_speech()
    {
        var d = Feed(Noise(9000, -45));
        Assert.Equal(DetectorEnd.NoSpeech, d.End);
    }

    [Fact]
    public void A_short_click_is_not_speech()
    {
        var d = Feed(Silence(500), Tone(40, -10), Silence(8000));
        Assert.Equal(DetectorEnd.NoSpeech, d.End);
    }

    [Fact]
    public void Speech_over_noise_is_found()
    {
        var noise = Noise(3000, -45);
        var speech = Tone(1200, -18).Zip(Noise(1200, -45, 2), (a, b) => (short)Math.Clamp(a + b, short.MinValue, short.MaxValue)).ToArray();
        var d = Feed(noise, speech, Noise(1000, -45, 3));
        Assert.True(d.SpeechStarted);
        Assert.Equal(DetectorEnd.Silence, d.End);
        Assert.InRange(d.Speech.TotalMilliseconds, 1100, 1300);
    }
}
