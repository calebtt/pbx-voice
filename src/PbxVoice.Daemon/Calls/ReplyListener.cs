using PbxVoice.Policy;
using PbxVoice.Speech;

namespace PbxVoice.Calls;

internal sealed record Reply(ReplyIntent Intent, AckSource? Source, ReplyRecord Record, bool HungUp, bool Cancelled);

/// <summary>
/// Listens for one spoken reply after a prompt (PR-SPEECH-2), transcribes it with xAI STT, and
/// matches it against the policy's phrase lists.
/// <list type="bullet">
/// <item>STT that fails or takes longer than 3 s: speech lasting 0.5–2.5 s counts as an
/// acknowledgment, recorded as <c>speech_detected</c>; anything longer, such as a voicemail
/// greeting, is unrecognized (PR-SPEECH-3).</item>
/// <item>A keypad digit counts as an acknowledgment, recorded as <c>keypad</c> (PR-SPEECH-4).</item>
/// <item>A hang-up after speech still transcribes what was said.</item>
/// </list>
/// </summary>
internal sealed class ReplyListener
{
    public static readonly TimeSpan DefaultSttTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FallbackMin = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan FallbackMax = TimeSpan.FromMilliseconds(2500);

    private readonly ISpeechToText _stt;
    private readonly TimeProvider _time;
    private readonly TimeSpan _sttTimeout;

    public ReplyListener(ISpeechToText stt, TimeProvider time, TimeSpan? sttTimeout = null)
    {
        _stt = stt;
        _time = time;
        _sttTimeout = sttTimeout ?? DefaultSttTimeout;
    }

    public async Task<Reply> ListenAsync(IActiveCall call, bool alarm, Phrases phrases, CancellationToken ct)
    {
        var capture = await call.CaptureAsync(DetectorSettings.Reply, ct).ConfigureAwait(false);
        var record = new ReplyRecord
        {
            At = _time.GetUtcNow(),
            SpeechMs = (int)capture.Speech.TotalMilliseconds,
            VadPeak = capture.VadPeak is { } peak ? MathF.Round(peak, 3) : null,
            EndedBy = Snake(capture.End.ToString()),
        };
        bool hungUp = capture.End == CaptureEnd.HungUp;
        var acknowledge = alarm ? ReplyIntent.Awake : ReplyIntent.Confirm;

        if (capture.End == CaptureEnd.Cancelled)
            return new Reply(ReplyIntent.None, null, record, hungUp, Cancelled: true);

        if (capture.End == CaptureEnd.Keypad)
        {
            record.Intent = IntentName(acknowledge);
            record.Source = AckSource.Keypad;
            record.Transcript = $"keypad {capture.Digit}";
            return new Reply(acknowledge, AckSource.Keypad, record, hungUp, false);
        }

        if (!capture.SpeechStarted)
        {
            record.Intent = hungUp ? "hung_up" : "none";
            return new Reply(ReplyIntent.None, null, record, hungUp, false);
        }

        var keyTerms = alarm ? phrases.Awake.Concat(phrases.Snooze).ToList() : phrases.Confirm.Concat(phrases.Repeat).ToList();
        SttResult stt;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            budget.CancelAfter(_sttTimeout);
            try
            {
                stt = await _stt.TranscribeMuLawAsync(capture.MuLaw, keyTerms, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                stt = SttResult.Failure("timed out");
            }
        }
        if (ct.IsCancellationRequested)
            return new Reply(ReplyIntent.None, null, record, hungUp, Cancelled: true);

        if (stt.Ok)
        {
            record.Transcript = stt.Text;
            var intent = alarm ? PhraseMatcher.MatchAlarm(stt.Text ?? "", phrases) : PhraseMatcher.MatchMessage(stt.Text ?? "", phrases);
            record.Intent = intent == ReplyIntent.None ? "unrecognized" : IntentName(intent);
            AckSource? source = intent == ReplyIntent.None ? null : AckSource.Stt;
            record.Source = source;
            return new Reply(intent, source, record, hungUp, false);
        }

        record.SttError = stt.Error;
        if (capture.Speech >= FallbackMin && capture.Speech <= FallbackMax)
        {
            record.Intent = IntentName(acknowledge);
            record.Source = AckSource.SpeechDetected;
            return new Reply(acknowledge, AckSource.SpeechDetected, record, hungUp, false);
        }
        record.Intent = "unrecognized";
        return new Reply(ReplyIntent.None, null, record, hungUp, false);
    }

    private static string IntentName(ReplyIntent intent) => intent.ToString().ToLowerInvariant();

    private static string Snake(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
