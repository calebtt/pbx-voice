using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PbxVoice.Audio;
using PbxVoice.Speech;

namespace PbxVoice.Xai;

/// <summary>
/// xAI REST speech: <c>POST /v1/tts</c> renders clips as 8 kHz μ-law, and <c>POST /v1/stt</c>
/// transcribes short replies sent as raw μ-law (multipart form). The key is sent only in the
/// Authorization header and never logged.
/// </summary>
internal sealed class XaiSpeech : ISpeechToText, ITextToSpeech, IDisposable
{
    private readonly HttpClient _http;

    public XaiSpeech(string apiKey, Uri? baseAddress = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseAddress ?? new Uri("https://api.x.ai/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<byte[]> SynthesizeMuLawAsync(string text, string voice, string language, double speed, CancellationToken ct)
    {
        var body = new
        {
            text,
            voice_id = voice,
            language,
            speed,
            output_format = new { codec = "mulaw", sample_rate = 8000 },
        };
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("v1/tts", content, ct).ConfigureAwait(false);
        byte[] payload = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"xAI TTS returned {(int)response.StatusCode}: {Snippet(payload)}");
        return ExtractAudio(payload, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The TTS response is either JSON with base64 <c>audio</c> or the audio itself.</summary>
    internal static byte[] ExtractAudio(byte[] payload, string? mediaType)
    {
        byte[] audio = payload;
        if (mediaType == "application/json" || (payload.Length > 0 && payload[0] == (byte)'{'))
        {
            using var doc = JsonDocument.Parse(payload);
            string b64 = doc.RootElement.GetProperty("audio").GetString()
                         ?? throw new InvalidDataException("xAI TTS response has no audio");
            audio = Convert.FromBase64String(b64);
        }
        return audio.Length >= 12 && audio[0] == (byte)'R' && audio[1] == (byte)'I' && audio[2] == (byte)'F' && audio[3] == (byte)'F'
            ? MuLawWav.Read(audio)
            : audio;
    }

    public async Task<SttResult> TranscribeMuLawAsync(byte[] muLaw, IReadOnlyList<string> keyTerms, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("mulaw"), "audio_format");
            form.Add(new StringContent("8000"), "sample_rate");
            form.Add(new StringContent("en"), "language");
            // Key terms bias recognition toward the phrases the prompt asked for.
            foreach (var term in keyTerms.Where(t => t.Length is > 0 and <= 50).Distinct().Take(100))
                form.Add(new StringContent(term), "keyterm");
            var file = new ByteArrayContent(muLaw);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", "reply.ulaw"); // the file part must be last

            using var response = await _http.PostAsync("v1/stt", form, ct).ConfigureAwait(false);
            byte[] payload = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SttResult.Failure($"xAI STT returned {(int)response.StatusCode}: {Snippet(payload)}");
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("text", out var text)
                ? SttResult.Success(text.GetString() ?? "")
                : SttResult.Failure("xAI STT response has no text");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SttResult.Failure("timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return SttResult.Failure(ex.Message);
        }
    }

    private static string Snippet(byte[] payload)
    {
        string s = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 200));
        return s.ReplaceLineEndings(" ");
    }

    public void Dispose() => _http.Dispose();
}
