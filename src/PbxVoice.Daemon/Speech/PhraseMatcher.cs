using System.Text;
using PbxVoice.Policy;

namespace PbxVoice.Speech;

internal enum ReplyIntent { None, Awake, Snooze, Confirm, Repeat }

/// <summary>
/// Whole-utterance phrase matching (PR-SPEECH-2). The reply and every listed phrase are lowercased,
/// stripped of punctuation and apostrophes, "i am" becomes "im", and an immediate repetition of the
/// same words is collapsed. A phrase must then equal the entire reply, not a word inside a longer
/// one. For alarm phrases only, one leading "yeah", "yes", "okay", or "ok" is ignored.
/// </summary>
internal static class PhraseMatcher
{
    private static readonly HashSet<string> AlarmFillers = new() { "yeah", "yes", "okay", "ok" };

    public static ReplyIntent MatchAlarm(string transcript, Phrases phrases)
    {
        var words = Tokens(transcript);
        foreach (var candidate in WithOptionalFiller(words))
        {
            if (AnyEquals(candidate, phrases.Awake)) return ReplyIntent.Awake;
            if (AnyEquals(candidate, phrases.Snooze)) return ReplyIntent.Snooze;
        }
        return ReplyIntent.None;
    }

    public static ReplyIntent MatchMessage(string transcript, Phrases phrases)
    {
        var words = Tokens(transcript);
        if (AnyEquals(words, phrases.Confirm)) return ReplyIntent.Confirm;
        if (AnyEquals(words, phrases.Repeat)) return ReplyIntent.Repeat;
        return ReplyIntent.None;
    }

    public static string Normalize(string text) => string.Join(' ', Tokens(text));

    private static IEnumerable<List<string>> WithOptionalFiller(List<string> words)
    {
        yield return words;
        if (words.Count > 1 && AlarmFillers.Contains(words[0]))
            yield return words.Skip(1).ToList();
    }

    private static bool AnyEquals(List<string> words, IEnumerable<string> phrases) =>
        words.Count > 0 && phrases.Any(p => Tokens(p).SequenceEqual(words));

    internal static List<string> Tokens(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char raw in text.ToLowerInvariant())
        {
            char c = raw;
            if (c is '\'' or '’' or '‘')
                continue; // "i'm" -> "im"
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        for (int i = 0; i + 1 < words.Count; i++)
        {
            if (words[i] == "i" && words[i + 1] == "am")
            {
                words[i] = "im";
                words.RemoveAt(i + 1);
            }
        }
        return CollapseRepeats(words);
    }

    /// <summary>"im up im up" -> "im up"; "yes yes" -> "yes".</summary>
    private static List<string> CollapseRepeats(List<string> words)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int n = 1; n <= words.Count / 2 && !changed; n++)
            {
                for (int i = 0; i + 2 * n <= words.Count; i++)
                {
                    if (words.Skip(i).Take(n).SequenceEqual(words.Skip(i + n).Take(n)))
                    {
                        words.RemoveRange(i + n, n);
                        changed = true;
                        break;
                    }
                }
            }
        }
        return words;
    }
}
