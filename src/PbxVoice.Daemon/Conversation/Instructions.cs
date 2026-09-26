using System.Text;
using System.Text.RegularExpressions;
using PbxVoice.Calls;

namespace PbxVoice.Conversation;

/// <summary>
/// Builds the session instructions: the conversation prompt (<see cref="PromptTemplate"/>) with the
/// call and the brief filled in as data blocks. The daemon plays the disclosure and the verbatim
/// message itself; the model asks the questions, answers from the facts, reads the answers back,
/// and ends the call.
/// </summary>
internal static partial class Instructions
{
    /// <summary>
    /// Fills the prompt. Brief and policy text goes in one field per line, with line breaks,
    /// control characters, and call/brief tags removed, so it cannot start a heading of its own or
    /// close its block.
    /// </summary>
    public static string Build(PromptTemplate prompt, Brief brief, string calleeName, bool self, string displayName, int maxMinutes)
    {
        string name = Clean(displayName);
        return PromptTemplate.Placeholder().Replace(prompt.Text, m => m.Groups[1].Value switch
        {
            "display_name" => name,
            "call" => CallBlock(calleeName, self, name, maxMinutes),
            "brief" => BriefBlock(brief),
            _ => m.Value,
        });
    }

    private static string CallBlock(string calleeName, bool self, string displayName, int maxMinutes)
    {
        var sb = new StringBuilder("<call>\n");
        sb.Append("You are calling: ").Append(self ? $"{displayName}, the person you work for" : Clean(Display(calleeName))).Append(".\n");
        if (!self)
            sb.Append("A recorded notice that you are an automated assistant has already played. Don't repeat it unless asked.\n");
        sb.Append("Time limit: ").Append(maxMinutes).Append(maxMinutes == 1 ? " minute" : " minutes").Append(".\n");
        return sb.Append("</call>").ToString();
    }

    private static string BriefBlock(Brief brief)
    {
        var sb = new StringBuilder("<brief>\n");
        sb.Append("Goal: ").Append(Clean(brief.Goal)).Append('\n');
        sb.Append(brief.Message is { } message
            ? $"Message, already spoken to them word for word: \"{Clean(message)}\"\n"
            : "Message: none.\n");
        if (brief.Facts.Count == 0)
            sb.Append("Facts you may share: none.\n");
        else
        {
            sb.Append("Facts you may share:\n");
            foreach (var fact in brief.Facts)
                sb.Append("- ").Append(Clean(fact)).Append('\n');
        }
        if (brief.Ask.Count == 0)
            sb.Append("Questions to ask: none.\n");
        else
        {
            sb.Append("Questions to ask, in order:\n");
            int i = 1;
            foreach (var a in brief.Ask)
            {
                sb.Append(i++).Append(". ").Append(a.Name).Append(a.Required ? " (required): " : " (optional): ").Append(Clean(a.Question));
                if (!string.IsNullOrWhiteSpace(a.Hint))
                    sb.Append(" Expected form: ").Append(Clean(a.Hint)).Append('.');
                sb.Append('\n');
            }
        }
        return sb.Append("</brief>").ToString();
    }

    [GeneratedRegex(@"</?\s*(call|brief)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>One line of data: no control characters, line breaks, or call/brief tags.</summary>
    internal static string Clean(string text)
    {
        var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        string line = BlockTag().Replace(new string(chars), " ");
        return Whitespace().Replace(line, " ").Trim();
    }

    /// <summary>The response instruction after the verbatim message has played.</summary>
    public static string AfterMessage(Brief brief) => brief.Ask.Count > 0
        ? "The message has been delivered. Briefly check they got it, then start on the questions."
        : "The message has been delivered. Briefly check they got it and answer any questions from the facts, then say goodbye and end the call.";

    /// <summary>The opening response instruction when there is no message.</summary>
    public const string Opening = "Greet them briefly, say in one sentence why you're calling, then ask the first question.";

    private static string Display(string contact) => contact == "(unlisted)" ? "the person who answers" : contact;

    /// <summary>The function tools the model may call (PR-CONV-4). They record; they cannot dial, transfer, or read data.</summary>
    public static IReadOnlyList<object> Tools(Brief brief) => new object[]
    {
        new
        {
            type = "function",
            name = "record_answer",
            description = "Record the person's answer to one of the questions.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["name"] = new { type = "string", description = "The question's name.", @enum = brief.Ask.Select(a => a.Name).ToArray() },
                    ["value"] = new { type = "string", description = "The answer in short normalized form, or \"unknown\"." },
                    ["quote"] = new { type = "string", description = "The person's own words, exactly as they said them." },
                },
                required = new[] { "name", "value", "quote" },
            },
        },
        new
        {
            type = "function",
            name = "record_question",
            description = "Record a question the person asked you.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["text"] = new { type = "string", description = "Their question." },
                    ["answered"] = new { type = "boolean", description = "True if you answered it from the facts." },
                },
                required = new[] { "text", "answered" },
            },
        },
        new
        {
            type = "function",
            name = "confirm_received",
            description = "Call when the person confirms they got the message.",
            parameters = new { type = "object", properties = new Dictionary<string, object>() },
        },
        new
        {
            type = "function",
            name = "end_call",
            description = "End the call, after saying goodbye.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["reason"] = new { type = "string", @enum = new[] { "done", "voicemail", "declined", "wrong_person", "automated_system" } },
                    ["quote"] = new { type = "string", description = "For declined or wrong_person: the person's words." },
                },
                required = new[] { "reason" },
            },
        },
    };
}
