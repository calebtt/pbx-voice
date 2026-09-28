using PbxVoice.Calls;
using PbxVoice.Speech;

namespace PbxVoice.Conversation;

/// <summary>
/// The daemon's own checks on what the model reported (PR-OUT-1, PR-CONV-5, PR-CONV-7). The
/// callee-side transcript comes from the voice session's transcription of the callee's audio.
/// </summary>
internal static class Evidence
{
    private static readonly HashSet<string> StopWords = new()
    {
        "a", "an", "the", "is", "are", "to", "of", "and", "or", "do", "does", "did", "you", "your", "i", "im",
        "it", "its", "be", "will", "for", "in", "on", "at", "when", "what", "who", "how", "can", "could", "would",
        "there", "that", "this", "someone", "anyone", "need", "needs",
    };

    private static readonly string[][] Affirmations =
    {
        new[] { "yes" }, new[] { "yeah" }, new[] { "yep" }, new[] { "yup" }, new[] { "correct" }, new[] { "right" },
        new[] { "thats", "right" }, new[] { "sure" }, new[] { "ok" }, new[] { "okay" }, new[] { "got", "it" },
        new[] { "sounds", "good" }, new[] { "thanks" }, new[] { "thank", "you" }, new[] { "perfect" }, new[] { "exactly" },
        new[] { "uh", "huh" }, new[] { "mhm" }, new[] { "will", "do" }, new[] { "understood" }, new[] { "alright" }, new[] { "all", "right" },
    };

    private static readonly Dictionary<string, string> NumberWords = new()
    {
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6",
        ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12",
        ["thirteen"] = "13", ["fourteen"] = "14", ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17",
        ["eighteen"] = "18", ["nineteen"] = "19", ["twenty"] = "20", ["thirty"] = "30", ["forty"] = "40",
        ["fifty"] = "50", ["noon"] = "12",
    };

    /// <summary>
    /// Words for comparing what was said with what was recorded: the phrase-matching tokens, with
    /// number words as digits and "3pm" split into "3 pm", since the model and the transcriber
    /// write numbers either way.
    /// </summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        foreach (var token in PhraseMatcher.Tokens(text))
        {
            int split = 0;
            while (split < token.Length && char.IsDigit(token[split]))
                split++;
            if (split > 0 && split < token.Length)
            {
                words.Add(token[..split]);
                words.Add(token[split..]);
            }
            else
            {
                words.Add(NumberWords.TryGetValue(token, out var digits) ? digits : token);
            }
        }
        return words;
    }

    /// <summary>A quote counts when it is at least two words or the callee's whole turn (PR-CONV-5).</summary>
    public static bool QuoteFound(string quote, TurnRecord turn)
    {
        var q = Words(quote);
        var t = Words(turn.Text);
        if (q.Count == 0 || t.Count == 0)
            return false;
        if (q.SequenceEqual(t))
            return true;
        if (q.Count < 2)
            return false;
        for (int i = 0; i + q.Count <= t.Count; i++)
        {
            if (t.Skip(i).Take(q.Count).SequenceEqual(q))
                return true;
        }
        return false;
    }

    /// <summary>
    /// When the question was asked: the first assistant turn that contains at least half of the
    /// question's content words. If the model paraphrased beyond that, the first assistant turn
    /// after the message (or the first assistant turn) stands in. Only turns that existed when the
    /// answer was recorded (<paramref name="recordedSeq"/>, 0 for unknown) count, so a later
    /// read-back, which repeats the question's words, can't be taken for the question.
    /// </summary>
    public static int QuestionSeq(AskItem ask, IReadOnlyList<TurnRecord> transcript, int recordedSeq = 0)
    {
        var content = Words(ask.Question).Where(w => !StopWords.Contains(w)).Distinct().ToList();
        var assistant = transcript.Where(t => t.Role == "assistant" && !t.Scripted).OrderBy(t => t.Seq).ToList();
        foreach (var turn in assistant.Where(t => recordedSeq <= 0 || t.Seq <= recordedSeq))
        {
            var words = Words(turn.Text).ToHashSet();
            if (content.Count > 0 && content.Count(words.Contains) * 2 >= content.Count)
                return turn.Seq;
        }
        return assistant.FirstOrDefault()?.Seq ?? int.MaxValue;
    }

    public static bool Affirmative(string text)
    {
        var t = PhraseMatcher.Tokens(text);
        return Affirmations.Any(a => t.Count >= a.Length && t.Take(a.Length).SequenceEqual(a));
    }

    /// <summary>Checks one recorded answer against the callee's turns after its question.</summary>
    public static string Match(AskItem ask, AnswerRecord answer, IReadOnlyList<TurnRecord> transcript)
    {
        int askedAt = QuestionSeq(ask, transcript, answer.RecordedSeq);
        return transcript.Any(t => t.Role == "callee" && t.Seq > askedAt && QuoteFound(answer.Quote, t)) ? "matched" : "unmatched";
    }

    /// <summary>
    /// The callee affirmed the read-back: an assistant turn containing most of the answer's words,
    /// followed by an affirmative callee turn before the next assistant turn.
    /// </summary>
    public static bool ReadbackConfirmed(AnswerRecord answer, IReadOnlyList<TurnRecord> transcript)
    {
        var value = Words(answer.Value).Where(w => !StopWords.Contains(w)).ToList();
        if (value.Count == 0)
            return false;
        var ordered = transcript.OrderBy(t => t.Seq).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Role != "assistant")
                continue;
            var words = Words(ordered[i].Text).ToHashSet();
            if (value.Count(words.Contains) * 10 < value.Count * 6)
                continue;
            var reply = ordered.Skip(i + 1).TakeWhile(t => t.Role == "callee").FirstOrDefault();
            if (reply is not null && Affirmative(reply.Text))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Fills in evidence, read-back, and message confirmation, then computes the outcome
    /// (PR-CONV-5, PR-CONV-7). Model claims (confirm_received, end_call) are inputs, never the answer.
    /// </summary>
    public static (Outcome Outcome, string? Reason) Evaluate(ConversationRecord c)
    {
        var transcript = c.Transcript;
        foreach (var (name, answer) in c.Answers)
        {
            var ask = c.Brief.Ask.FirstOrDefault(a => a.Name == name);
            answer.Evidence = ask is null ? "unmatched" : Match(ask, answer, transcript);
            answer.ReadbackConfirmed = ReadbackConfirmed(answer, transcript);
        }

        int messageSeq = transcript.FirstOrDefault(t => t.Scripted)?.Seq ?? -1;
        if (c.Brief.Message is not null)
            c.MessageConfirmed = c.MessageDelivered && transcript.Any(t => t.Role == "callee" && t.Seq > messageSeq && Affirmative(t.Text));

        var claim = c.EndClaim?.Reason;
        if (claim == "voicemail" && c.Brief.Message is not null && c.MessageDelivered
            && !transcript.Any(t => t.Role == "callee" && t.Seq > messageSeq))
            return (Outcome.VoicemailLeft, null);

        if (claim is "declined" or "wrong_person" && c.EndClaim!.Quote is { Length: > 0 } refusal
            && transcript.Any(t => t.Role == "callee" && QuoteFound(refusal, t)))
            return (Outcome.Declined, claim);

        bool requiredMatched = c.Brief.Ask.Where(a => a.Required)
            .All(a => c.Answers.TryGetValue(a.Name, out var ans) && ans.Evidence == "matched");
        bool messageOk = c.Brief.Message is null || c.MessageConfirmed == true;
        if (requiredMatched && messageOk && (c.Brief.Ask.Count > 0 || c.MessageDelivered))
            return (Outcome.Completed, null);

        if (c.Answers.Count > 0)
            return (Outcome.Partial, null);

        return (Outcome.Failed, claim switch
        {
            "voicemail" when c.Brief.Message is not null && c.MessageDelivered => "voicemail claimed, but the callee spoke after the message",
            "voicemail" => "voicemail (no message could be left)",
            "automated_system" => "automated_system",
            "declined" or "wrong_person" => $"{claim} claimed without a matching quote",
            _ when c.Brief.Message is not null && c.MessageDelivered => "message delivered but not confirmed",
            _ => "no answers captured",
        });
    }
}
