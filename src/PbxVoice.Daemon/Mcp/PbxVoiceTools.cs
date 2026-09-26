using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PbxVoice.Mcp;

/// <summary>Weekly repeat for <c>schedule_call</c>.</summary>
internal sealed class RepeatInput
{
    [Description("Days: mon..sun, or weekdays, weekends, daily. Example: [\"weekdays\"] or [\"mon\",\"wed\"].")]
    public List<string> Days { get; set; } = new();

    [Description("Local wall-clock time, HH:mm (24-hour). Example: \"05:30\".")]
    public string Time { get; set; } = "";

    [Description("IANA time zone, e.g. \"America/Chicago\". Required.")]
    public string Tz { get; set; } = "";
}

/// <summary>One question in a conversation brief.</summary>
internal sealed class AskInput
{
    [Description("Short key for the answer: lowercase letters, digits, underscores. Example: \"visit_time\".")]
    public string Name { get; set; } = "";

    [Description("The question, e.g. \"When is the plumber coming?\".")]
    public string Question { get; set; } = "";

    [Description("Whether the call only counts as completed with this answered. Default true.")]
    public bool? Required { get; set; }

    [Description("The expected form of the answer, e.g. \"day and time window\".")]
    public string? Hint { get; set; }
}

/// <summary>What a conversation call is for.</summary>
internal sealed class BriefInput
{
    [Description("One sentence: why the call is being made.")]
    public string Goal { get; set; } = "";

    [Description("Spoken word for word at the start and not interruptible. Give a message, questions (ask), or both.")]
    public string? Message { get; set; }

    [Description("What the assistant may share if asked. Anything else it will say it doesn't know. Only include what the user would tell this person directly.")]
    public List<string>? Facts { get; set; }

    [Description("Questions to get answered, asked one at a time.")]
    public List<AskInput>? Ask { get; set; }

    [Description("Hard time limit for the answered call, in minutes. Default 3; capped by policy.")]
    public int? MaxMinutes { get; set; }
}

/// <summary>Per-call options; anything left out uses the defaults, and the operator's policy caps apply.</summary>
internal sealed class OptionsInput
{
    [Description("\"voice\" (default): the callee must answer out loud (alarm: \"I'm up\"; message: \"got it\"). \"none\": just play it (twice).")]
    public string? Ack { get; set; }

    [Description("Call again after no answer, busy, or (alarm) no acknowledgment. Default true; false means one attempt.")]
    public bool? Redial { get; set; }

    [Description("Attempts in total. Defaults: alarm 5, message 2. Capped by policy.")]
    public int? MaxAttempts { get; set; }

    [Description("Minutes between attempts. Defaults: alarm 3, message 10.")]
    public int? RetryMinutes { get; set; }

    [Description("Seconds to let it ring, counted from when it starts ringing. Defaults: alarm 45, message 30.")]
    public int? RingSeconds { get; set; }

    [Description("Alarm only: minutes before calling back after the callee says \"snooze\". Default 10.")]
    public int? SnoozeMinutes { get; set; }

    [Description("Alarm only: how many snoozes are allowed. Default 3.")]
    public int? MaxSnoozes { get; set; }
}

/// <summary>
/// The agent's tools (PR-API-1). Each one forwards to the daemon's service, in process or over the
/// control socket, and returns JSON text. Refusals from the operator's policy come back as tool
/// errors with the reason. Results that carry the callee's words are marked as untrusted speech.
/// </summary>
[McpServerToolType]
internal sealed class PbxVoiceTools
{
    private readonly IToolBackend _backend;
    private readonly PlacementRateLimiter _rate;

    public PbxVoiceTools(IToolBackend backend, PlacementRateLimiter rate)
    {
        _backend = backend;
        _rate = rate;
    }

    [McpServerTool(Name = "schedule_call", OpenWorld = true)]
    [Description(
        "Schedule a phone call that the pbx-voice daemon places later, through the user's own phone extension. " +
        "type \"alarm\": a wake-up call to a contact marked self; it redials until the callee says \"I'm up\". " +
        "type \"message\": calls a contact, speaks `text` word for word, and asks them to say \"got it\". " +
        "type \"conversation\": a live AI voice call that follows `brief`: it can deliver a message, answer from the facts, and " +
        "ask questions, then returns the answers with the callee's words as evidence. " +
        "Give exactly one of `at` (one-off) or `repeat` (weekly). Only schedule calls the user asked for, " +
        "and put in `text` only what the user would say to that person directly. " +
        "Returns schedule_id and next_fire, or an error from the operator's policy (unlisted contact, quiet hours, caps).")]
    public Task<CallToolResult> ScheduleCall(
        [Description("\"alarm\", \"message\", or \"conversation\".")] string type,
        [Description("Contact name from list_contacts (or a number, only if the operator allows unlisted numbers).")] string to,
        [Description("One-off time, ISO 8601 with a UTC offset, e.g. \"2026-10-06T05:30:00-05:00\". A time without an offset needs `tz`.")] string? at = null,
        [Description("IANA time zone for an `at` without an offset.")] string? tz = null,
        [Description("Weekly repeat instead of `at`.")] RepeatInput? repeat = null,
        [Description("Message: the words to speak (required). Alarm: an optional wake-up prompt; {time} becomes the call time.")] string? text = null,
        [Description("Conversation only: what the call is for (required for conversation).")] BriefInput? brief = null,
        [Description("Optional per-call settings.")] OptionsInput? options = null,
        CancellationToken ct = default)
    {
        if (_rate.TryPlace() is { } limited)
            return Task.FromResult(Error(limited));
        return Call("schedule_call", new { type, to, at, tz, repeat, text, brief, options }, ct);
    }

    [McpServerTool(Name = "call_now", OpenWorld = true)]
    [Description(
        "Place a call right away (same types and rules as schedule_call). Returns call_id at once; the call runs in the " +
        "background. To get the result in this session, call wait_for_call with the call_id.")]
    public Task<CallToolResult> CallNow(
        [Description("\"alarm\", \"message\", or \"conversation\".")] string type,
        [Description("Contact name from list_contacts.")] string to,
        [Description("Message: the words to speak (required). Alarm: optional wake-up prompt.")] string? text = null,
        [Description("Conversation only: what the call is for (required for conversation).")] BriefInput? brief = null,
        [Description("Optional per-call settings.")] OptionsInput? options = null,
        CancellationToken ct = default)
    {
        if (_rate.TryPlace() is { } limited)
            return Task.FromResult(Error(limited));
        return Call("call_now", new { type, to, text, brief, options }, ct);
    }

    [McpServerTool(Name = "wait_for_call", ReadOnly = true)]
    [Description(
        "Wait until a call has its final outcome, up to timeout_sec (at most 900), and return the call record. " +
        "If it is still running (for example an alarm waiting to redial), the result is status \"in_progress\" with the " +
        "latest attempt. Never guess an outcome; check later with get_call. Report the outcome exactly as recorded.")]
    public Task<CallToolResult> WaitForCall(
        [Description("The call_id from call_now or list_calls.")] string call_id,
        [Description("Seconds to wait, 1-900. Default 300.")] int timeout_sec = 300,
        CancellationToken ct = default) =>
        Call("wait_for_call", new { call_id, timeout_sec }, ct);

    [McpServerTool(Name = "list_schedules", ReadOnly = true)]
    [Description("List active scheduled calls with their next fire time and the last pre-flight check.")]
    public Task<CallToolResult> ListSchedules(CancellationToken ct = default) => Call("list_schedules", new { }, ct);

    [McpServerTool(Name = "cancel_schedule", Destructive = true, Idempotent = true)]
    [Description("Cancel a scheduled call so it does not fire again. Calls already running keep going; use cancel_call for those.")]
    public Task<CallToolResult> CancelSchedule(
        [Description("The schedule_id.")] string schedule_id,
        CancellationToken ct = default) =>
        Call("cancel_schedule", new { schedule_id }, ct);

    [McpServerTool(Name = "cancel_call", Destructive = true)]
    [Description("Cancel a pending call, stop one that is ringing, or hang up one in progress. Its outcome becomes \"cancelled\".")]
    public Task<CallToolResult> CancelCall(
        [Description("The call_id.")] string call_id,
        CancellationToken ct = default) =>
        Call("cancel_call", new { call_id }, ct);

    [McpServerTool(Name = "list_calls", ReadOnly = true)]
    [Description("Recent calls, newest first: type, contact, status, outcome, and times. Use get_call for the details of one.")]
    public Task<CallToolResult> ListCalls(
        [Description("Only calls created at or after this ISO 8601 time (with offset).")] string? since = null,
        [Description("How many, 1-200. Default 20.")] int limit = 20,
        CancellationToken ct = default) =>
        Call("list_calls", new { since, limit }, ct);

    [McpServerTool(Name = "get_call", ReadOnly = true)]
    [Description(
        "The full record of one call: every attempt with its SIP result and ringing time, the callee's transcribed replies " +
        "and how each was recognized (stt, speech_detected, keypad), and the outcome. For a conversation: the answers " +
        "(value, the callee's quote, and evidence: matched or unmatched), questions the callee asked, and the transcript. " +
        "Report the outcome exactly as recorded, and pass answers on as what the callee said; mention any answer with " +
        "evidence unmatched.")]
    public Task<CallToolResult> GetCall(
        [Description("The call_id.")] string call_id,
        CancellationToken ct = default) =>
        Call("get_call", new { call_id }, ct);

    [McpServerTool(Name = "list_contacts", ReadOnly = true)]
    [Description("The contacts the operator allows calling, with masked numbers and whether each is the user (self). Alarms only call self. Contacts cannot be added from here.")]
    public Task<CallToolResult> ListContacts(CancellationToken ct = default) => Call("list_contacts", new { }, ct);

    [McpServerTool(Name = "status", ReadOnly = true)]
    [Description("Daemon health: SIP registration and last error, PBX reachability, the call in progress, next scheduled calls, today's usage against the cap, and whether speech services are configured.")]
    public Task<CallToolResult> Status(CancellationToken ct = default) => Call("status", new { }, ct);

    private async Task<CallToolResult> Call(string op, object args, CancellationToken ct)
    {
        var element = JsonSerializer.SerializeToElement(args, Json.Compact);
        var result = await _backend.CallAsync(op, element, ct).ConfigureAwait(false);
        return result.Ok
            ? new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = ResultShaper.Shape(result.Result) } } }
            : Error(result.Error!);
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = message } } };
}

/// <summary>
/// Limits how fast the agent can place or schedule calls (plan: request-rate limiting on
/// schedule_call and call_now), on top of the policy's daily cap.
/// </summary>
internal sealed class PlacementRateLimiter
{
    private readonly TimeProvider _time;
    private readonly int _perMinute;
    private readonly int _perHour;
    private readonly Queue<DateTimeOffset> _recent = new();

    public PlacementRateLimiter(TimeProvider time, int perMinute = 10, int perHour = 60)
    {
        _time = time;
        _perMinute = perMinute;
        _perHour = perHour;
    }

    /// <summary>Records a placement, or returns why it is refused.</summary>
    public string? TryPlace()
    {
        var now = _time.GetUtcNow();
        lock (_recent)
        {
            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromHours(1))
                _recent.Dequeue();
            if (_recent.Count >= _perHour)
                return $"rate limit: at most {_perHour} calls scheduled or placed per hour";
            if (_recent.Count(t => now - t < TimeSpan.FromMinutes(1)) >= _perMinute)
                return $"rate limit: at most {_perMinute} calls scheduled or placed per minute";
            _recent.Enqueue(now);
            return null;
        }
    }
}
