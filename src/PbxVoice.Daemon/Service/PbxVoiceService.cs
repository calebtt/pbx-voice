using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Policy;
using PbxVoice.Scheduling;

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
}

/// <summary>
/// The operations behind <c>pbx-voice ctl</c> (and the MCP tools in a later version): schedule,
/// call now, wait, list and cancel schedules, cancel a call, list and read call records, list
/// contacts, and status (PR-API-1, PR-API-3).
/// </summary>
internal sealed partial class PbxVoiceService
{
    public const int MaxWaitSeconds = 900;
    public const int MaxTextLength = 2000;

    private readonly TimeProvider _time;
    private readonly PolicyProvider _policy;
    private readonly ScheduleStore _schedules;
    private readonly CallStore _calls;
    private readonly ClipStore _clips;
    private readonly Executor _executor;
    private readonly IPhoneLine _phone;
    private readonly HostStatus _host;

    public PbxVoiceService(TimeProvider time, PolicyProvider policy, ScheduleStore schedules, CallStore calls,
        ClipStore clips, Executor executor, IPhoneLine phone, HostStatus host)
    {
        _time = time;
        _policy = policy;
        _schedules = schedules;
        _calls = calls;
        _clips = clips;
        _executor = executor;
        _phone = phone;
        _host = host;
    }

    public static readonly string[] Operations =
    {
        "schedule_call", "call_now", "wait_for_call", "list_schedules", "cancel_schedule",
        "cancel_call", "list_calls", "get_call", "list_contacts", "status",
    };

    public Task<object> HandleAsync(string op, JsonElement args, CancellationToken ct) => op switch
    {
        "schedule_call" => ScheduleCallAsync(args, ct),
        "call_now" => CallNowAsync(args, ct),
        "wait_for_call" => WaitForCallAsync(args, ct),
        "list_schedules" => Task.FromResult(ListSchedules()),
        "cancel_schedule" => Task.FromResult(CancelSchedule(args)),
        "cancel_call" => Task.FromResult(CancelCall(args)),
        "list_calls" => Task.FromResult(ListCalls(args)),
        "get_call" => Task.FromResult(GetCall(args)),
        "list_contacts" => Task.FromResult(ListContacts()),
        "status" => Task.FromResult(Status()),
        _ => throw new ServiceError($"unknown operation '{op}'; known: {string.Join(", ", Operations)}"),
    };

    // ---- schedule_call / call_now ------------------------------------------------------------

    private sealed record Prepared(CallType Type, Target Target, CallOptions Options, string? Text);

    private Prepared Prepare(JsonElement args, PolicyFile policy)
    {
        var type = Str(args, "type") switch
        {
            "alarm" => CallType.Alarm,
            "message" => CallType.Message,
            "conversation" => CallType.Conversation,
            null => throw new ServiceError("'type' is required: alarm or message"),
            var other => throw new ServiceError($"unknown type '{other}': use alarm or message"),
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
        return new Prepared(type, target, options, string.IsNullOrWhiteSpace(text) ? null : text.Trim());
    }

    private async Task<object> ScheduleCallAsync(JsonElement args, CancellationToken ct)
    {
        var policy = _policy.Current;
        var p = Prepare(args, policy);
        var now = _time.GetUtcNow();

        bool hasAt = args.TryGetProperty("at", out var atElement);
        bool hasRepeat = args.TryGetProperty("repeat", out var repeatElement);
        if (hasAt == hasRepeat)
            throw new ServiceError("give exactly one of 'at' (one-off) or 'repeat' (weekly)");

        DateTimeOffset firstFire;
        RepeatSpec? repeat = null;
        TimeZoneInfo zone;
        if (hasAt)
        {
            (firstFire, zone) = ParseAt(atElement.GetString(), Str(args, "tz"));
            if (firstFire < now - Executor.Grace(p.Type))
                throw new ServiceError($"'at' is in the past ({firstFire:O})");
        }
        else
        {
            repeat = ParseRepeat(repeatElement);
            zone = PolicyLoader.Zone(repeat.Tz);
            var days = Recurrence.ParseDays(repeat.Days, out _)!;
            Recurrence.TryParseTime(repeat.Time, out var time);
            firstFire = Recurrence.NextWeekly(now, days, time, zone);
        }

        CheckTimeRules(policy, p.Target, firstFire, now);
        var (clips, notes) = await RenderClipsAsync(p, policy, TimeZoneInfo.ConvertTime(firstFire, zone), ct).ConfigureAwait(false);

        var schedule = new Schedule
        {
            Id = NewId("s", now),
            Type = p.Type,
            To = Str(args, "to")!,
            Contact = p.Target.Contact,
            Self = p.Target.Self,
            TargetUri = p.Target.Uri,
            MaskedNumber = p.Target.MaskedNumber,
            At = hasAt ? firstFire : null,
            Repeat = repeat,
            Text = p.Text,
            Options = p.Options,
            Clips = clips,
            Notes = notes,
            CreatedAt = now,
            NextFire = firstFire,
        };
        lock (_schedules.Sync)
            _schedules.Add(schedule);

        return new
        {
            schedule_id = schedule.Id,
            next_fire = firstFire,
            next_fire_local = TimeZoneInfo.ConvertTime(firstFire, zone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
            contact = schedule.Contact,
            masked_number = schedule.MaskedNumber,
            options = schedule.Options,
            clips = ClipSummary(clips),
            notes,
        };
    }

    private async Task<object> CallNowAsync(JsonElement args, CancellationToken ct)
    {
        var policy = _policy.Current;
        var p = Prepare(args, policy);
        var now = _time.GetUtcNow();
        CheckTimeRules(policy, p.Target, now, now);
        var zone = PolicyLoader.Zone(policy.Timezone);
        var (clips, notes) = await RenderClipsAsync(p, policy, TimeZoneInfo.ConvertTime(now, zone), ct).ConfigureAwait(false);
        var record = _executor.Create(null, p.Type, p.Target, p.Options, clips, p.Text, now, notes);
        return new { call_id = record.CallId, contact = record.Contact, masked_number = record.MaskedNumber, notes };
    }

    /// <summary>Quiet hours (PR-SAFE-3) and the daily cap (PR-SAFE-4) at scheduling time.</summary>
    private void CheckTimeRules(PolicyFile policy, Target target, DateTimeOffset fire, DateTimeOffset now)
    {
        if (PolicyGuard.InQuietHours(policy, target, fire))
            throw new ServiceError($"{fire:O} is inside quiet hours for '{target.Contact}'");
        var (start, end) = PolicyGuard.Day(policy, now);
        if (fire < end && _calls.AttemptsBetween(start, end) >= policy.Limits.CallsPerDay)
            throw new ServiceError($"the daily call cap ({policy.Limits.CallsPerDay}) is already reached for today");
    }

    /// <summary>
    /// Parses 'at'. A time with an offset (or Z) is exact. A time without one needs 'tz'; with
    /// neither it is refused (PR-SCHED-1).
    /// </summary>
    internal static (DateTimeOffset Fire, TimeZoneInfo Zone) ParseAt(string? at, string? tz)
    {
        if (string.IsNullOrWhiteSpace(at))
            throw new ServiceError("'at' must be an ISO 8601 time");
        bool hasOffset = OffsetPattern().IsMatch(at.Trim());
        if (hasOffset)
        {
            if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                throw new ServiceError($"'at' is not an ISO 8601 time: {at}");
            var zone = tz is null ? TimeZoneInfo.CreateCustomTimeZone("offset", exact.Offset, "offset", "offset") : ZoneOrError(tz);
            return (exact, zone);
        }
        if (tz is null)
            throw new ServiceError("'at' has no UTC offset and no 'tz' was given; add an offset (e.g. -05:00) or an IANA 'tz'");
        if (!DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new ServiceError($"'at' is not an ISO 8601 time: {at}");
        var z = ZoneOrError(tz);
        return (Recurrence.ResolveLocal(local, z), z);
    }

    private static RepeatSpec ParseRepeat(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new ServiceError("'repeat' must be an object: {\"days\": [...], \"time\": \"HH:mm\", \"tz\": \"Area/City\"}");
        var days = e.TryGetProperty("days", out var d)
            ? d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string> { d.GetString() ?? "" }
            : new List<string>();
        var spec = new RepeatSpec
        {
            Days = days,
            Time = e.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "",
            Tz = e.TryGetProperty("tz", out var z) ? z.GetString() ?? "" : "",
        };
        if (Recurrence.ParseDays(spec.Days, out var dayError) is null)
            throw new ServiceError(dayError!);
        if (!Recurrence.TryParseTime(spec.Time, out _))
            throw new ServiceError("repeat.time must be HH:mm");
        if (spec.Tz.Length == 0)
            throw new ServiceError("repeat.tz is required (an IANA zone such as America/Chicago)");
        ZoneOrError(spec.Tz);
        return spec;
    }

    private static TimeZoneInfo ZoneOrError(string id) =>
        PolicyLoader.TryZone(id, out var zone) ? zone : throw new ServiceError($"unknown time zone '{id}'");

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();

    // ---- clip rendering ----------------------------------------------------------------------

    internal const string DefaultAlarmPrompt = "Good morning, it's {time}. Say 'I'm up' when you're awake.";
    internal const string DefaultAlarmMessage = "Good morning, it's {time}. Time to wake up.";
    internal const string AlarmClosing = "Great. Have a good day.";
    internal const string SnoozeAck = "Okay. I'll call back in {minutes} minutes.";
    internal const string ConfirmPrompt = "Please say 'got it' to confirm, or 'repeat' to hear it again.";
    internal const string MessageClosing = "Thanks. Goodbye.";
    internal const string Disclosure = "Hello. This is an automated assistant calling on behalf of {name}. A short spoken reply may be transcribed.";

    /// <summary>
    /// Renders every clip when the call is scheduled (PR-SPEECH-1). An alarm whose prompt cannot
    /// be rendered is still scheduled with the built-in wake tone, and the record says so
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

    private object ListSchedules()
    {
        lock (_schedules.Sync)
        {
            return _schedules.All.Where(s => s.Status == "active").Select(s => new
            {
                schedule_id = s.Id,
                type = s.Type,
                contact = s.Contact,
                masked_number = s.MaskedNumber,
                at = s.At,
                repeat = s.Repeat,
                next_fire = s.NextFire,
                options = s.Options,
                notes = s.Notes,
                preflight = s.Preflight,
            }).ToList();
        }
    }

    private object CancelSchedule(JsonElement args)
    {
        string id = Str(args, "schedule_id") ?? throw new ServiceError("'schedule_id' is required");
        lock (_schedules.Sync)
        {
            var s = _schedules.Find(id) ?? throw new ServiceError($"no schedule '{id}'");
            if (s.Status != "active")
                throw new ServiceError($"schedule '{id}' is already {s.Status}");
            s.Status = "cancelled";
            s.NextFire = null;
            _schedules.Save();
        }
        return new { ok = true, schedule_id = id };
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
            .OrderByDescending(r => r.FireTime)
            .Take(limit)
            .Select(r => new
            {
                call_id = r.CallId,
                type = r.Type,
                contact = r.Contact,
                status = r.Status,
                outcome = r.Outcome,
                reason = r.Reason,
                fire_time = r.FireTime,
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
        List<object> nextFires;
        lock (_schedules.Sync)
        {
            nextFires = _schedules.All.Where(s => s.Status == "active" && s.NextFire is not null)
                .OrderBy(s => s.NextFire).Take(5)
                .Select(s => (object)new { schedule_id = s.Id, type = s.Type, contact = s.Contact, next_fire = s.NextFire, preflight = s.Preflight })
                .ToList();
        }
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
            next_fires = nextFires,
            usage_today = new { attempts = _calls.AttemptsBetween(start, end), calls_per_day = policy.Limits.CallsPerDay },
            xai_key_present = _host.XaiKeyPresent,
            policy_error = _policy.LastError,
            state_directory = _host.StateDirectory,
        };
    }

    /// <summary>A call record as tools show it: the dial target is masked and clip ids are left out.</summary>
    internal static object CallView(CallRecord r) => new
    {
        call_id = r.CallId,
        schedule_id = r.ScheduleId,
        type = r.Type,
        contact = r.Contact,
        masked_number = r.MaskedNumber,
        status = r.Status,
        outcome = r.Outcome,
        reason = r.Reason,
        ack_source = r.AckSource,
        fire_time = r.FireTime,
        created_at = r.CreatedAt,
        completed_at = r.CompletedAt,
        next_attempt_at = r.NextAttemptAt,
        options = r.Options,
        snoozes = r.Snoozes,
        billed_seconds = r.BilledSeconds,
        reregister = r.Reregister,
        attempts = r.Attempts,
        notes = r.Notes,
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string NewId(string prefix, DateTimeOffset now) =>
        $"{prefix}_{now.UtcDateTime:yyyyMMddHHmmss}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
}
