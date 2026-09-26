using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Policy;

namespace PbxVoice.Service;

/// <summary>A request the agent or operator got wrong; the message goes back as the error.</summary>
internal sealed class ServiceError : Exception
{
    public ServiceError(string message) : base(message) { }
}

/// <summary>Daemon facts for <c>status</c> that the service does not own.</summary>
internal sealed class HostStatus
{
    public string Version { get; init; } = "";
    public string StateDirectory { get; init; } = "";
    public bool XaiKeyPresent { get; init; }
    public Func<(DateTimeOffset At, int? Status)?> LastPing { get; init; } = () => null;

    /// <summary>Stops the daemon (the <c>shutdown</c> operation behind <c>pbx-voice stop</c>).</summary>
    public Action? RequestStop { get; init; }
}

/// <summary>
/// The operations behind <c>pbx-voice ctl</c> and the MCP tools: call now, wait, cancel a call,
/// list and read call records, list contacts, and status (PR-API-1, PR-API-3). pbx-voice has no
/// scheduler: the agent's own scheduler decides when to call <c>call_now</c>.
/// </summary>
internal sealed partial class PbxVoiceService
{
    public const int MaxWaitSeconds = 900;
    public const int MaxTextLength = 2000;

    private readonly TimeProvider _time;
    private readonly PolicyProvider _policy;
    private readonly CallStore _calls;
    private readonly ClipStore _clips;
    private readonly Executor _executor;
    private readonly IPhoneLine _phone;
    private readonly HostStatus _host;

    public PbxVoiceService(TimeProvider time, PolicyProvider policy, CallStore calls,
        ClipStore clips, Executor executor, IPhoneLine phone, HostStatus host)
    {
        _time = time;
        _policy = policy;
        _calls = calls;
        _clips = clips;
        _executor = executor;
        _phone = phone;
        _host = host;
    }

    public static readonly string[] Operations =
    {
        "call_now", "wait_for_call", "cancel_call", "list_calls", "get_call", "list_contacts", "status", "shutdown",
    };

    public Task<object> HandleAsync(string op, JsonElement args, CancellationToken ct) => op switch
    {
        "call_now" => CallNowAsync(args, ct),
        "wait_for_call" => WaitForCallAsync(args, ct),
        "cancel_call" => Task.FromResult(CancelCall(args)),
        "list_calls" => Task.FromResult(ListCalls(args)),
        "get_call" => Task.FromResult(GetCall(args)),
        "list_contacts" => Task.FromResult(ListContacts()),
        "status" => Task.FromResult(Status()),
        "shutdown" => Task.FromResult(Shutdown(args)),
        _ => throw new ServiceError($"unknown operation '{op}'; known: {string.Join(", ", Operations)}"),
    };

    // ---- call_now ------------------------------------------------------------------------

    private sealed record Prepared(CallType Type, Target Target, CallOptions Options, string? Text, Brief? Brief);

    private Prepared Prepare(JsonElement args, PolicyFile policy)
    {
        var type = Str(args, "type") switch
        {
            "alarm" => CallType.Alarm,
            "message" => CallType.Message,
            "conversation" => CallType.Conversation,
            null => throw new ServiceError("'type' is required: alarm, message, or conversation"),
            var other => throw new ServiceError($"unknown type '{other}': use alarm, message, or conversation"),
        };
        var target = PolicyGuard.Resolve(policy, Str(args, "to") ?? "", _phone.Server, out var error)
                     ?? throw new ServiceError(error!);
        if (PolicyGuard.CheckType(type, target) is { } typeError)
            throw new ServiceError(typeError);

        string? text = Str(args, "text");
        if (text is { Length: > MaxTextLength })
            throw new ServiceError($"'text' is longer than {MaxTextLength} characters");
        if (type == CallType.Message && string.IsNullOrWhiteSpace(text))
            throw new ServiceError("a message needs 'text'");

        JsonElement? overrides = args.TryGetProperty("options", out var o) ? o : null;
        var options = CallOptions.Resolve(type, overrides, policy.Limits, out var optionsError);
        if (optionsError is not null)
            throw new ServiceError(optionsError);

        Brief? brief = null;
        if (type == CallType.Conversation)
        {
            JsonElement? briefElement = args.TryGetProperty("brief", out var b) ? b : null;
            brief = BriefValidator.Parse(briefElement, policy.Limits, out var briefError) ?? throw new ServiceError(briefError!);
            if (text is not null)
                throw new ServiceError("a conversation takes its words from 'brief' (message, facts, ask), not 'text'");
        }
        return new Prepared(type, target, options, string.IsNullOrWhiteSpace(text) ? null : text.Trim(), brief);
    }

    private async Task<object> CallNowAsync(JsonElement args, CancellationToken ct)
    {
        var policy = _policy.Current;
        var p = Prepare(args, policy);
        var now = _time.GetUtcNow();
        CheckTimeRules(policy, p, now, now);
        var zone = PolicyLoader.Zone(policy.Timezone);
        var (clips, notes) = await RenderClipsAsync(p, policy, TimeZoneInfo.ConvertTime(now, zone), ct).ConfigureAwait(false);
        var record = _executor.Create(p.Type, p.Target, p.Options, clips, p.Text, now, notes, p.Brief);
        return new { call_id = record.CallId, contact = record.Contact, masked_number = record.MaskedNumber, notes };
    }

    /// <summary>Quiet hours (PR-SAFE-3) and the daily caps (PR-SAFE-4) when the call is requested.</summary>
    private void CheckTimeRules(PolicyFile policy, Prepared p, DateTimeOffset fire, DateTimeOffset now)
    {
        if (PolicyGuard.InQuietHours(policy, p.Target, fire))
            throw new ServiceError($"{fire:O} is inside quiet hours for '{p.Target.Contact}'");
        var (start, end) = PolicyGuard.Day(policy, now);
        if (fire < end && _calls.AttemptsBetween(start, end) >= policy.Limits.CallsPerDay)
            throw new ServiceError($"the daily call cap ({policy.Limits.CallsPerDay}) is already reached for today");
        if (p.Brief is { } brief && fire < end
            && _calls.ConversationSecondsBetween(start, end) / 60 + brief.MaxMinutes > policy.Limits.ConversationMinutesPerDay)
            throw new ServiceError($"this conversation could exceed today's conversation minutes ({policy.Limits.ConversationMinutesPerDay})");
    }

    /// <summary>
    // ---- clip rendering ----------------------------------------------------------------------

    internal const string DefaultAlarmPrompt = "Good morning, it's {time}. Say 'I'm up' when you're awake.";
    internal const string DefaultAlarmMessage = "Good morning, it's {time}. Time to wake up.";
    internal const string AlarmClosing = "Great. Have a good day.";
    internal const string SnoozeAck = "Okay. I'll call back in {minutes} minutes.";
    internal const string ConfirmPrompt = "Please say 'got it' to confirm, or 'repeat' to hear it again.";
    internal const string MessageClosing = "Thanks. Goodbye.";
    internal const string Disclosure = "Hello. This is an automated assistant calling on behalf of {name}. A short spoken reply may be transcribed.";
    internal const string ConversationDisclosure = "Hello. This is an automated assistant calling on behalf of {name}. This call is transcribed.";
    internal const string Apology = "I'm sorry, I can't continue this call right now. {name} will call you back. Goodbye.";
    internal const string ConversationClosing = "I have to go now. {name} will follow up. Goodbye.";

    /// <summary>
    /// Renders every clip when the call is requested (PR-SPEECH-1). An alarm whose prompt cannot
    /// be rendered is still placed with the built-in wake tone, and the record says so
    /// (PR-ALARM-6). A message needs its clips, so a rendering failure refuses the request.
    /// </summary>
    private async Task<(ClipSet Clips, List<string> Notes)> RenderClipsAsync(Prepared p, PolicyFile policy, DateTimeOffset localFire, CancellationToken ct)
    {
        var clips = new ClipSet();
        var notes = new List<string>();
        string time = localFire.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"));

        async Task<string?> Render(string text, string what, bool required)
        {
            try
            {
                return await _clips.RenderAsync(text, policy.Speech, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (required)
                    throw new ServiceError($"could not render the {what} clip: {ex.Message}");
                notes.Add($"{what} clip could not be rendered ({ex.Message})");
                return null;
            }
        }

        if (p.Type == CallType.Alarm)
        {
            string text = (p.Text ?? (p.Options.VoiceAck ? DefaultAlarmPrompt : DefaultAlarmMessage)).Replace("{time}", time);
            clips.Prompt = await Render(text, "wake-up", required: false).ConfigureAwait(false);
            if (clips.Prompt is null)
            {
                clips.Prompt = BuiltinClips.WakeToneId;
                notes.Add("the built-in wake tone plays instead of the wake-up message");
            }
            if (p.Options.VoiceAck)
            {
                clips.Closing = await Render(AlarmClosing, "closing", required: false).ConfigureAwait(false);
                if (p.Options.MaxSnoozes > 0)
                    clips.SnoozeAck = await Render(SnoozeAck.Replace("{minutes}", p.Options.SnoozeMinutes.ToString(CultureInfo.InvariantCulture)), "snooze", required: false).ConfigureAwait(false);
            }
        }
        else if (p.Type == CallType.Conversation)
        {
            var brief = p.Brief!;
            if (!p.Target.Self)
                clips.Disclosure = await Render(ConversationDisclosure.Replace("{name}", policy.DisplayName), "disclosure", required: true).ConfigureAwait(false);
            if (brief.Message is { } message)
            {
                // For the fallback to the message flow if the voice session cannot open (PR-CONV-3).
                clips.Message = await Render(message, "message", required: true).ConfigureAwait(false);
                clips.ConfirmPrompt = await Render(ConfirmPrompt, "confirmation prompt", required: true).ConfigureAwait(false);
            }
            else
            {
                clips.Apology = await Render(Apology.Replace("{name}", policy.DisplayName), "apology", required: true).ConfigureAwait(false);
            }
            clips.Closing = await Render(ConversationClosing.Replace("{name}", policy.DisplayName), "closing", required: false).ConfigureAwait(false);
        }
        else
        {
            clips.Message = await Render(p.Text!, "message", required: true).ConfigureAwait(false);
            if (p.Options.VoiceAck)
            {
                clips.ConfirmPrompt = await Render(ConfirmPrompt, "confirmation prompt", required: true).ConfigureAwait(false);
                clips.Closing = await Render(MessageClosing, "closing", required: false).ConfigureAwait(false);
            }
            if (!p.Target.Self)
                clips.Disclosure = await Render(Disclosure.Replace("{name}", policy.DisplayName), "disclosure", required: true).ConfigureAwait(false);
        }
        return (clips, notes);
    }

    private static object ClipSummary(ClipSet c)
    {
        static string? Kind(string? id) => id is null ? null : id.StartsWith("builtin:", StringComparison.Ordinal) ? id : "rendered";
        return new
        {
            prompt = Kind(c.Prompt),
            closing = Kind(c.Closing),
            snooze_ack = Kind(c.SnoozeAck),
            message = Kind(c.Message),
            confirm_prompt = Kind(c.ConfirmPrompt),
            disclosure = Kind(c.Disclosure),
            apology = Kind(c.Apology),
        };
    }

    // ---- reading and cancelling ----------------------------------------------------------------

    private async Task<object> WaitForCallAsync(JsonElement args, CancellationToken ct)
    {
        string id = Str(args, "call_id") ?? throw new ServiceError("'call_id' is required");
        int timeout = args.TryGetProperty("timeout_sec", out var t) && t.TryGetInt32(out var v) ? v : MaxWaitSeconds;
        if (timeout < 1 || timeout > MaxWaitSeconds)
            throw new ServiceError($"timeout_sec must be 1-{MaxWaitSeconds}");
        if (_calls.Snapshot(id) is null)
            throw new ServiceError($"no call '{id}'");

        bool done = await _executor.WaitAsync(id, TimeSpan.FromSeconds(timeout), ct).ConfigureAwait(false);
        var record = _calls.Snapshot(id)!;
        if (done)
            return CallView(record);
        // Never a guessed outcome (PR-OUT-3).
        return new { status = "in_progress", call_id = id, latest_attempt = record.Attempts.LastOrDefault(), next_attempt_at = record.NextAttemptAt };
    }

    private object CancelCall(JsonElement args)
    {
        string id = Str(args, "call_id") ?? throw new ServiceError("'call_id' is required");
        if (!_executor.Cancel(id))
            throw new ServiceError($"call '{id}' is not pending or in progress");
        return new { ok = true, call_id = id };
    }

    private object ListCalls(JsonElement args)
    {
        DateTimeOffset since = DateTimeOffset.MinValue;
        if (Str(args, "since") is { } s && !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out since))
            throw new ServiceError("'since' must be an ISO 8601 time with an offset");
        int limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? Math.Clamp(n, 1, 200) : 20;
        return _calls.SnapshotAll()
            .Where(r => r.CreatedAt >= since)
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(r => new
            {
                call_id = r.CallId,
                type = r.Type,
                contact = r.Contact,
                status = r.Status,
                outcome = r.Outcome,
                reason = r.Reason,
                created_at = r.CreatedAt,
                completed_at = r.CompletedAt,
                attempts = r.Attempts.Count,
                ack_source = r.AckSource,
            })
            .ToList();
    }

    private object GetCall(JsonElement args)
    {
        string id = Str(args, "call_id") ?? throw new ServiceError("'call_id' is required");
        return CallView(_calls.Snapshot(id) ?? throw new ServiceError($"no call '{id}'"));
    }

    /// <summary>
    /// Operator only: the MCP tools don't offer it. Refused while a call is in progress unless
    /// forced; queued calls wait for the next start (and are missed after their grace window).
    /// </summary>
    private object Shutdown(JsonElement args)
    {
        if (_host.RequestStop is null)
            throw new ServiceError("this daemon can't be stopped through the control socket");
        bool force = args.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
        if (_executor.CurrentCallId is { } current && !force)
            throw new ServiceError($"a call is in progress ({current}); wait for it to finish, or stop with --force");
        int pending = _calls.Open().Count(r => r.Status == CallStatus.Pending);
        _host.RequestStop();
        return new { stopping = true, pending_calls = pending };
    }

    private object ListContacts()
    {
        var policy = _policy.Current;
        return policy.Contacts.Select(kv => new { name = kv.Key, self = kv.Value.Self, masked_number = PolicyGuard.Mask(kv.Value.Number) }).ToList();
    }

    private object Status()
    {
        var policy = _policy.Current;
        var now = _time.GetUtcNow();
        var (start, end) = PolicyGuard.Day(policy, now);
        var reg = _phone.Registration;
        var ping = _host.LastPing();
        var open = _calls.Open();
        return new
        {
            version = _host.Version,
            registration = new { state = reg.State, registered = reg.Registered, last_error = reg.LastError, last_success = reg.LastSuccess },
            pbx = ping is { } p ? new { reachable = p.Status is not null, options_status = p.Status, checked_at = p.At } : null,
            register = policy.Register,
            dial_while_unregistered = policy.DialWhileUnregistered,
            current_call = _executor.CurrentCallId,
            pending_calls = open.Count(r => r.Status == CallStatus.Pending),
            usage_today = new { attempts = _calls.AttemptsBetween(start, end), calls_per_day = policy.Limits.CallsPerDay },
            xai_key_present = _host.XaiKeyPresent,
            policy_error = _policy.LastError,
            conversation_prompt = ConversationPromptStatus(),
            state_directory = _host.StateDirectory,
        };
    }

    private object ConversationPromptStatus()
    {
        var prompts = _executor.Prompts;
        var current = prompts.Current;
        return new { source = current.Source, sha256 = current.Sha256, error = prompts.LastError };
    }

    /// <summary>A call record as tools show it: the dial target is masked and clip ids are left out.</summary>
    internal static object CallView(CallRecord r) => new
    {
        call_id = r.CallId,
        type = r.Type,
        contact = r.Contact,
        masked_number = r.MaskedNumber,
        status = r.Status,
        outcome = r.Outcome,
        reason = r.Reason,
        ack_source = r.AckSource,
        created_at = r.CreatedAt,
        completed_at = r.CompletedAt,
        next_attempt_at = r.NextAttemptAt,
        options = r.Options,
        snoozes = r.Snoozes,
        billed_seconds = r.BilledSeconds,
        reregister = r.Reregister,
        attempts = r.Attempts,
        conversation = r.Conversation is { } c ? new
        {
            brief = c.Brief,
            answers = c.Answers,
            callee_questions = c.CalleeQuestions,
            message_delivered = c.MessageDelivered,
            message_confirmed = c.MessageConfirmed,
            model_reported_confirmation = c.ModelReportedConfirmation,
            end_claim = c.EndClaim,
            ended_by = c.EndedBy,
            session_open_ms = c.SessionOpenMs,
            prompt = c.PromptSha256 is null ? null : new { source = c.PromptSource, sha256 = c.PromptSha256 },
            transcript = c.Transcript,
        } : null,
        notes = r.Notes,
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string NewId(string prefix, DateTimeOffset now) =>
        $"{prefix}_{now.UtcDateTime:yyyyMMddHHmmss}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
}
