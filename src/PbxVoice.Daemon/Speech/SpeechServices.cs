namespace PbxVoice.Speech;

internal sealed record SttResult(bool Ok, string? Text, string? Error)
{
    public static SttResult Success(string text) => new(true, text, null);
    public static SttResult Failure(string error) => new(false, null, error);
}

/// <summary>Speech-to-text for short replies (8 kHz μ-law).</summary>
internal interface ISpeechToText
{
    Task<SttResult> TranscribeMuLawAsync(byte[] muLaw, IReadOnlyList<string> keyTerms, CancellationToken ct);
}

/// <summary>Text-to-speech rendered to 8 kHz μ-law when a call is requested.</summary>
internal interface ITextToSpeech
{
    Task<byte[]> SynthesizeMuLawAsync(string text, string voice, string language, double speed, CancellationToken ct);
}

/// <summary>Used when no xAI key is configured: every request fails, and the daemon falls back.</summary>
internal sealed class UnavailableSpeech : ISpeechToText, ITextToSpeech
{
    public Task<SttResult> TranscribeMuLawAsync(byte[] muLaw, IReadOnlyList<string> keyTerms, CancellationToken ct) =>
        Task.FromResult(SttResult.Failure("no XAI_API_KEY configured"));

    public Task<byte[]> SynthesizeMuLawAsync(string text, string voice, string language, double speed, CancellationToken ct) =>
        throw new InvalidOperationException("no XAI_API_KEY configured");
}
