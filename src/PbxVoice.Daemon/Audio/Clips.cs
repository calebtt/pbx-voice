using System.Security.Cryptography;
using System.Text;
using NAudio.Codecs;
using PbxVoice.Hosting;
using PbxVoice.Speech;

namespace PbxVoice.Audio;

/// <summary>8 kHz μ-law audio ready for the RTP pacer.</summary>
internal sealed class Clip
{
    public Clip(string id, byte[] muLaw)
    {
        Id = id;
        MuLaw = Sanitize(muLaw);
    }

    public string Id { get; }
    public byte[] MuLaw { get; }
    public TimeSpan Duration => TimeSpan.FromMilliseconds(MuLaw.Length / 8.0);

    /// <summary>
    /// SipBotLib's pacer treats a frame of all 0x7F bytes as keep-alive silence and does not count
    /// it as playing. 0x7F and 0xFF both decode to zero, so clip data uses 0xFF and every clip
    /// frame counts toward "played to the end".
    /// </summary>
    private static byte[] Sanitize(byte[] data)
    {
        var copy = (byte[])data.Clone();
        for (int i = 0; i < copy.Length; i++)
        {
            if (copy[i] == 0x7F)
                copy[i] = 0xFF;
        }
        return copy;
    }
}

internal static class G711
{
    public static byte[] Encode(ReadOnlySpan<short> pcm)
    {
        var mu = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            mu[i] = MuLawEncoder.LinearToMuLawSample(pcm[i]);
        return mu;
    }

    public static short[] Decode(ReadOnlySpan<byte> muLaw)
    {
        var pcm = new short[muLaw.Length];
        for (int i = 0; i < muLaw.Length; i++)
            pcm[i] = MuLawDecoder.MuLawToLinearSample(muLaw[i]);
        return pcm;
    }
}

/// <summary>8 kHz mono μ-law WAV files (format tag 7).</summary>
internal static class MuLawWav
{
    public static byte[] Build(byte[] muLaw)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(4 + 26 + 12 + 8 + muLaw.Length + (muLaw.Length & 1)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(18); w.Write((short)7); w.Write((short)1); w.Write(8000); w.Write(8000);
        w.Write((short)1); w.Write((short)8); w.Write((short)0);
        w.Write("fact"u8); w.Write(4); w.Write(muLaw.Length);
        w.Write("data"u8); w.Write(muLaw.Length); w.Write(muLaw);
        if ((muLaw.Length & 1) == 1)
            w.Write((byte)0);
        return ms.ToArray();
    }

    /// <summary>The data chunk of an 8 kHz mono μ-law WAV.</summary>
    public static byte[] Read(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("not a WAV file");
        int pos = 12;
        short format = 0;
        int rate = 0, channels = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(wav, pos + 8);
                channels = BitConverter.ToInt16(wav, pos + 10);
                rate = BitConverter.ToInt32(wav, pos + 12);
            }
            else if (id == "data")
            {
                if (format != 7 || rate != 8000 || channels != 1)
                    throw new InvalidDataException($"need 8 kHz mono μ-law, got format {format}, {rate} Hz, {channels} channels");
                return wav.AsSpan(pos + 8, Math.Min(size, wav.Length - pos - 8)).ToArray();
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("WAV has no data chunk");
    }
}

/// <summary>Clips the daemon generates itself, so an alarm can always ring (PR-ALARM-6).</summary>
internal static class BuiltinClips
{
    public const string WakeToneId = "builtin:wake";

    public static Clip? Get(string id) => id == WakeToneId ? WakeTone() : null;

    /// <summary>Three groups of three 880 Hz beeps, about 4 s.</summary>
    public static Clip WakeTone()
    {
        var pcm = new List<short>();
        void Tone(int ms)
        {
            int n = 8 * ms;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - i) / 80.0); // 10 ms ramps, no clicks
                pcm.Add((short)(9000 * env * Math.Sin(2 * Math.PI * 880 * i / 8000)));
            }
        }
        void Gap(int ms) => pcm.AddRange(new short[8 * ms]);

        for (int group = 0; group < 3; group++)
        {
            for (int beep = 0; beep < 3; beep++)
            {
                Tone(200);
                Gap(120);
            }
            Gap(500);
        }
        return new Clip(WakeToneId, G711.Encode(pcm.ToArray()));
    }
}

/// <summary>
/// Rendered clips in <c>clips/{sha256}.wav</c>, keyed by text, voice, language, and speed, so a
/// recurring call's clips are rendered once.
/// </summary>
internal sealed class ClipStore
{
    private readonly string _dir;
    private readonly ITextToSpeech _tts;

    public ClipStore(string dir, ITextToSpeech tts)
    {
        _dir = dir;
        _tts = tts;
    }

    public static string KeyFor(string text, string voice, string language, double speed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{voice}\n{language}\n{speed:0.###}\n{text}"))).ToLowerInvariant();

    public Clip? Load(string id)
    {
        if (BuiltinClips.Get(id) is { } builtin)
            return builtin;
        string path = PathFor(id);
        if (!File.Exists(path))
            return null;
        try
        {
            return new Clip(id, MuLawWav.Read(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Renders <paramref name="text"/> unless it is already cached. Returns the clip id.</summary>
    public async Task<string> RenderAsync(string text, Policy.SpeechSettings speech, CancellationToken ct)
    {
        string id = KeyFor(text, speech.Voice, speech.Language, speech.Speed);
        if (File.Exists(PathFor(id)))
            return id;
        byte[] muLaw = await _tts.SynthesizeMuLawAsync(text, speech.Voice, speech.Language, speech.Speed, ct).ConfigureAwait(false);
        if (muLaw.Length < 800)
            throw new InvalidDataException($"text-to-speech returned {muLaw.Length} bytes");
        SecureFile.WriteAllBytes(PathFor(id), MuLawWav.Build(muLaw));
        return id;
    }

    private string PathFor(string id) => Path.Combine(_dir, id + ".wav");
}
