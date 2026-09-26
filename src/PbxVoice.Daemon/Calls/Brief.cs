using System.Text.Json;
using System.Text.RegularExpressions;
using PbxVoice.Policy;

namespace PbxVoice.Calls;

/// <summary>
/// A conversation brief (PR-CONV-1): why the call is made, what to deliver, what the assistant
/// may share, and what to find out.
/// </summary>
internal sealed class Brief
{
    /// <summary>One sentence: why the call is being made.</summary>
    public string Goal { get; set; } = "";

    /// <summary>Delivered word for word at the start, not interruptible (PR-CONV-2).</summary>
    public string? Message { get; set; }

    /// <summary>What the assistant may share when asked.</summary>
    public List<string> Facts { get; set; } = new();

    /// <summary>Questions to get answered.</summary>
    public List<AskItem> Ask { get; set; } = new();

    /// <summary>Hard limit on the answered call; default 3, capped by policy.</summary>
    public int MaxMinutes { get; set; } = 3;
}

internal sealed class AskItem
{
    /// <summary>A short key for the answer, e.g. <c>visit_time</c>.</summary>
    public string Name { get; set; } = "";
    public string Question { get; set; } = "";
    public bool Required { get; set; } = true;

    /// <summary>The expected form, e.g. "day and time window".</summary>
    public string? Hint { get; set; }
}

internal static partial class BriefValidator
{
    public const int MaxTextChars = 2000;
    public const int MaxFacts = 20;
    public const int MaxAsks = 8;

    public static Brief? Parse(JsonElement? element, Limits limits, out string? error)
    {
        error = null;
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            error = "a conversation needs a 'brief' object: {goal, message and/or ask, facts, max_minutes}";
            return null;
        }
        Brief? brief;
        try
        {
            brief = JsonSerializer.Deserialize<Brief>(obj.GetRawText(), Json.Options);
        }
        catch (JsonException ex)
        {
            error = "brief is not valid: " + ex.Message;
            return null;
        }
        if (brief is null)
        {
            error = "brief is empty";
            return null;
        }
        if (!obj.TryGetProperty("max_minutes", out _))
            brief.MaxMinutes = Math.Min(3, limits.MaxConversationMinutes);
        error = Validate(brief, limits);
        return error is null ? brief : null;
    }

    public static string? Validate(Brief b, Limits limits)
    {
        b.Goal = b.Goal?.Trim() ?? "";
        b.Message = string.IsNullOrWhiteSpace(b.Message) ? null : b.Message.Trim();
        b.Facts = b.Facts?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList() ?? new();
        b.Ask ??= new();

        if (b.Goal.Length == 0)
            return "brief.goal is required: one sentence on why the call is made";
        if (b.Message is null && b.Ask.Count == 0)
            return "a brief needs a message to deliver, questions to ask, or both (PR-CONV-1)";
        if (b.Goal.Length > MaxTextChars || (b.Message?.Length ?? 0) > MaxTextChars || b.Facts.Any(f => f.Length > MaxTextChars))
            return $"brief text fields are limited to {MaxTextChars} characters";
        if (b.Facts.Count > MaxFacts)
            return $"at most {MaxFacts} facts";
        if (b.Ask.Count > MaxAsks)
            return $"at most {MaxAsks} questions";
        var names = new HashSet<string>();
        foreach (var a in b.Ask)
        {
            a.Name = a.Name?.Trim() ?? "";
            a.Question = a.Question?.Trim() ?? "";
            if (!NamePattern().IsMatch(a.Name))
                return $"ask name '{a.Name}' must be lowercase letters, digits, or underscores (e.g. visit_time)";
            if (!names.Add(a.Name))
                return $"ask name '{a.Name}' is used twice";
            if (a.Question.Length is 0 or > 500)
                return $"ask '{a.Name}' needs a question of at most 500 characters";
        }
        if (b.MaxMinutes < 1 || b.MaxMinutes > limits.MaxConversationMinutes)
            return $"brief.max_minutes must be 1-{limits.MaxConversationMinutes} (policy cap)";
        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex NamePattern();
}
