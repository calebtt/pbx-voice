using PbxVoice.Audio;
using PbxVoice.Speech;

namespace PbxVoice.Calls;

internal enum DialKind { Answered, NoAnswer, Busy, SipFailure, Cancelled }

internal sealed record DialRequest(string Uri, TimeSpan RingTime, TimeSpan SetupLimit);

internal sealed record DialResult(
    DialKind Kind,
    int? FinalStatus,
    string Detail,
    IActiveCall? Call = null,
    int? RingingAfterMs = null,
    int? RingingStatus = null);

internal sealed record RegistrationInfo(
    string State,
    bool Registered,
    string? LastError,
    DateTimeOffset? LastSuccess)
{
    /// <summary>The SIP status of the last registration error, e.g. 403 from "403 Forbidden".</summary>
    public int? ErrorStatus =>
        LastError is { Length: >= 3 } e && int.TryParse(e.AsSpan(0, 3), out var code) ? code : null;

    public bool HardFailure => State == "HardFailure";
}

/// <summary>The SIP line: registration, outbound dialing, and a reachability probe.</summary>
internal interface IPhoneLine
{
    /// <summary>The PBX, used to normalize bare numbers and extensions into SIP URIs.</summary>
    string Server { get; }

    RegistrationInfo Registration { get; }

    /// <summary><c>StartRegistration()</c>: Stop, then Start, with no zero-expiry REGISTER (PR-REG-9).</summary>
    void Reregister();

    Task<bool> WaitForRegistrationAsync(TimeSpan max, CancellationToken ct);

    /// <summary>
    /// Places a call. The ring time runs from the first 180/183 (the SipBotLib ringing event), and
    /// the call is cancelled if neither ringing nor a final response arrives within the setup limit
    /// of the INVITE (PR-SCHED-6).
    /// </summary>
    Task<DialResult> DialAsync(DialRequest request, CancellationToken ct);

    /// <summary>SIP OPTIONS to the PBX. Returns the response status, or null when none arrived.</summary>
    Task<int?> PingServerAsync(TimeSpan timeout, CancellationToken ct);
}

internal enum PlayEnd { Completed, HungUp, Cancelled }

internal sealed record PlayResult(PlayEnd End, TimeSpan Played);

internal enum CaptureEnd { Silence, MaxSpeech, NoSpeech, Keypad, HungUp, Cancelled }

internal sealed record Capture(CaptureEnd End, bool SpeechStarted, TimeSpan Speech, byte[] MuLaw, char? Digit = null, float? VadPeak = null);

/// <summary>An answered call.</summary>
internal interface IActiveCall : IAsyncDisposable
{
    bool IsUp { get; }

    /// <summary>Plays a clip to the end, or until the callee hangs up or <paramref name="ct"/> cuts it.</summary>
    Task<PlayResult> PlayAsync(Clip clip, CancellationToken ct);

    /// <summary>
    /// Captures one reply after the prompt has finished (never during it, PR-SPEECH-2). Ends on
    /// silence after speech, the speech limit, no speech in time, a keypad digit, or a hang-up.
    /// A digit pressed while the prompt played ends the capture at once.
    /// </summary>
    Task<Capture> CaptureAsync(DetectorSettings settings, CancellationToken ct);

    /// <summary>Waits, returning false if the callee hung up meanwhile.</summary>
    Task<bool> PauseAsync(TimeSpan duration, CancellationToken ct);

    /// <summary>Completes when the call ends, whoever hangs up.</summary>
    Task Ended { get; }

    /// <summary>Streams the callee's audio (8 kHz 16-bit PCM) to <paramref name="onCalleeAudio"/> until stopped.</summary>
    void StartStreaming(Action<byte[]> onCalleeAudio);

    void StopStreaming();

    /// <summary>Queues 8 kHz 16-bit PCM for the callee to hear (a voice model's audio).</summary>
    void EnqueueAudio(ReadOnlySpan<byte> pcm);

    /// <summary>Drops queued audio at once (barge-in).</summary>
    void ClearPlayback();

    /// <summary>True while queued audio is still playing.</summary>
    bool PlaybackPending { get; }

    Task HangupAsync();
}

/// <summary>The call-result split of PR-ALARM-4, read from the final response actually received.</summary>
internal static class DialClassifier
{
    public static DialKind FromFinalStatus(int? status) => status switch
    {
        408 or 480 => DialKind.NoAnswer,
        486 or 600 or 603 => DialKind.Busy,
        _ => DialKind.SipFailure, // no final response, other 4xx, 5xx, or a local error
    };

    /// <summary>"486 Busy Here" -> 486. SipBotLib reports a failure as "status reason" when a response arrived.</summary>
    public static int? ParseStatus(string? failure) =>
        failure is { Length: >= 3 } f && int.TryParse(f.AsSpan(0, 3), out var code) && code is >= 300 and < 700 ? code : null;
}
