using System.Security.Cryptography;
using PbxVoice.Audio;
using PbxVoice.Conversation;
using PbxVoice.Policy;
using Serilog;

namespace PbxVoice.Calls;

/// <summary>
/// Runs calls, one SIP call at a time (PR-SCHED-3). Each call is a record with attempts; after
/// each attempt the call is either finished or given a time for its next attempt (redial or
/// snooze). Due attempts run in time order, and an alarm goes ahead of other types when both are
/// due. Nothing here hangs up a call in progress for another one.
/// </summary>
internal sealed class Executor
{
    private readonly TimeProvider _time;
    private readonly IPhoneLine _phone;
    private readonly PolicyProvider _policy;
    private readonly CallStore _calls;
    private readonly ClipStore _clips;
    private readonly ReplyListener _listener;
    private readonly IVoiceSessionFactory? _sessions;
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource> _done = new();
    private CancellationTokenSource? _currentCts;
    private string? _currentCallId;

    public Executor(TimeProvider time, IPhoneLine phone, PolicyProvider policy, CallStore calls, ClipStore clips, ReplyListener listener,
        IVoiceSessionFactory? sessions = null)
    {
        _sessions = sessions;
        _time = time;
        _phone = phone;
        _policy = policy;
        _calls = calls;
        _clips = clips;
        _listener = listener;
    }

    /// <summary>How long after its fire time a call may still start (PR-SCHED-4).</summary>
    public static TimeSpan Grace(CallType type) => type == CallType.Alarm ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for a PR-REG-9 re-register to succeed before dialing anyway.</summary>
    public static readonly TimeSpan ReregisterWait = TimeSpan.FromSeconds(10);

    public string? CurrentCallId
    {
        get { lock (_lock) return _currentCallId; }
    }

    public CallRecord Create(string? scheduleId, CallType type, Target target, CallOptions options, ClipSet clips,
        string? text, DateTimeOffset fireTime, IEnumerable<string>? notes = null, Brief? brief = null)
    {
        var now = _time.GetUtcNow();
        var record = new CallRecord
        {
            CallId = NewId(now),
            ScheduleId = scheduleId,
            Type = type,
            Contact = target.Contact,
            MaskedNumber = target.MaskedNumber,
            Self = target.Self,
            TargetUri = target.Uri,
            Options = options,
            Clips = clips,
            Text = text,
            CreatedAt = now,
            FireTime = fireTime,
            Status = CallStatus.Pending,
            NextAttemptAt = fireTime,
            Conversation = type == CallType.Conversation ? new ConversationRecord { Brief = brief ?? new Brief() } : null,
        };
        if (notes is not null)
            record.Notes.AddRange(notes);
        _calls.Add(record);
        return record;
    }

    /// <summary>
    /// At startup: a call that was in progress when the daemon stopped gets its last attempt closed
    /// as a SIP failure ("interrupted") and continues under the normal redial rules.
    /// </summary>
    public void Recover()
    {
        foreach (var r in _calls.Open().Where(r => r.Status == CallStatus.InProgress))
        {
            _calls.Update(r, c =>
            {
                if (c.Attempts.LastOrDefault() is { Result: null } last)
                {
                    last.Result = last.Answered ? InterruptedAnsweredResult(c.Type) : AttemptResult.SipFailure;
                    last.Detail = "interrupted: the daemon stopped during this attempt";
                    last.EndedAt ??= _time.GetUtcNow();
                }
                c.Status = CallStatus.Pending;
            });
            Decide(r, r.Attempts.LastOrDefault()?.Result ?? AttemptResult.SipFailure);
        }
    }

    /// <summary>Records missed calls, then runs the most urgent due attempt. Returns false if nothing was due.</summary>
    public async Task<bool> RunDueAsync(CancellationToken stop)
    {
        var now = _time.GetUtcNow();
        var open = _calls.Open();
        foreach (var r in open.Where(r => r.Status == CallStatus.Pending && r.Attempts.Count == 0 && now > r.FireTime + Grace(r.Type)))
            Finish(r, Outcome.Missed, "not placed within its grace window");

        var due = open
            .Where(r => r.Status == CallStatus.Pending && r.NextAttemptAt is { } t && t <= now)
            .OrderBy(r => r.Type == CallType.Alarm ? 0 : 1)
            .ThenBy(r => r.NextAttemptAt)
            .FirstOrDefault();
        if (due is null)
            return false;
        await RunAttemptAsync(due, stop).ConfigureAwait(false);
        return true;
    }

    /// <summary>Cancels a pending call, or cancels the ring or hangs up the call in progress.</summary>
    public bool Cancel(string callId)
    {
        lock (_lock)
        {
            if (_currentCallId == callId && _currentCts is { } cts)
            {
                cts.Cancel();
                return true;
            }
        }
        var record = _calls.Open().FirstOrDefault(r => r.CallId == callId && r.Status == CallStatus.Pending);
        if (record is null)
            return false;
        Finish(record, Outcome.Cancelled, "cancel_call");
        return true;
    }

    /// <summary>Completes when the call has a final outcome, or false after <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitAsync(string callId, TimeSpan timeout, CancellationToken ct)
    {
        Task done;
        lock (_lock)
        {
            if (_calls.Snapshot(callId) is not { } snapshot)
                return false;
            if (snapshot.Status == CallStatus.Done)
                return true;
            if (!_done.TryGetValue(callId, out var tcs))
                _done[callId] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done = tcs.Task;
        }
        try
        {
            await done.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task RunAttemptAsync(CallRecord r, CancellationToken stop)
    {
        var policy = _policy.Current;
        var now = _time.GetUtcNow();
        bool first = r.Attempts.Count == 0;

        // Fire-time policy checks (PR-ALARM-5, PR-SAFE-1, PR-SAFE-3, PR-SAFE-4).
        if (!CheckTargetStillAllowed(r, policy, out var targetProblem))
        {
            Finish(r, first ? Outcome.Failed : Tally(r), targetProblem);
            return;
        }
        var target = new Target(r.Contact, r.TargetUri, r.MaskedNumber, r.Self);
        if (PolicyGuard.InQuietHours(policy, target, now))
        {
            Finish(r, first ? Outcome.Missed : Tally(r), "quiet_hours");
            return;
        }
        var (dayStart, dayEnd) = PolicyGuard.Day(policy, now);
        if (_calls.AttemptsBetween(dayStart, dayEnd) >= policy.Limits.CallsPerDay)
        {
            Finish(r, first ? Outcome.Missed : Tally(r), "daily_cap");
            return;
        }
        if (r.Type == CallType.Conversation && r.Conversation is { } conv
            && _calls.ConversationSecondsBetween(dayStart, dayEnd) / 60 + conv.Brief.MaxMinutes > policy.Limits.ConversationMinutesPerDay)
        {
            Finish(r, first ? Outcome.Missed : Tally(r), "daily_cap (conversation minutes)");
            return;
        }
        if (MissingClip(r) is { } missing)
        {
            Finish(r, first ? Outcome.Failed : Tally(r), $"clip missing: {missing}");
            return;
        }

        if (first)
            await ReregisterIfRejectedAsync(r, stop).ConfigureAwait(false);

        var ring = TimeSpan.FromSeconds(r.Options.RingSeconds);
        var attempt = new AttemptRecord { Number = r.Attempts.Count + 1, StartedAt = now };
        _calls.Update(r, c =>
        {
            c.Status = CallStatus.InProgress;
            c.NextAttemptAt = null;
            c.Attempts.Add(attempt);
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_lock)
        {
            _currentCts = cts;
            _currentCallId = r.CallId;
        }

        AttemptResult result;
        try
        {
            if (policy.Register && !policy.DialWhileUnregistered && !_phone.Registration.Registered
                && !await _phone.WaitForRegistrationAsync(ring, cts.Token).ConfigureAwait(false))
            {
                // PR-REG-8: this PBX needs registration to dial, and it did not come back in time.
                _calls.Update(r, _ => attempt.Detail = "registration down; not dialed");
                result = cts.IsCancellationRequested && !stop.IsCancellationRequested ? AttemptResult.Cancelled : AttemptResult.SipFailure;
            }
            else
            {
                result = await DialAndRunAsync(r, attempt, policy, ring, cts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _currentCts = null;
                _currentCallId = null;
            }
        }

        // A daemon shutdown is not the agent's cancel: close the attempt and keep the call.
        if (result == AttemptResult.Cancelled && stop.IsCancellationRequested)
        {
            _calls.Update(r, _ => attempt.Detail = "interrupted: the daemon stopped during this attempt");
            result = attempt.Answered ? InterruptedAnsweredResult(r.Type) : AttemptResult.SipFailure;
        }

        _calls.Update(r, c =>
        {
            attempt.Result = result;
            attempt.EndedAt ??= _time.GetUtcNow();
            attempt.Snooze = result == AttemptResult.Snoozed;
            if (attempt.AnsweredAt is { } answered)
                c.BilledSeconds += (int)Math.Ceiling((attempt.EndedAt.Value - answered).TotalSeconds);
        });
        Log.Information("Call {CallId} attempt {N}: {Result}", r.CallId, attempt.Number, result);
        Decide(r, result);
    }

    private async Task<AttemptResult> DialAndRunAsync(CallRecord r, AttemptRecord attempt, PolicyFile policy, TimeSpan ring, CancellationToken ct)
    {
        DialResult dial;
        try
        {
            dial = await _phone.DialAsync(new DialRequest(r.TargetUri, ring, TimeSpan.FromSeconds(policy.SetupLimitSeconds)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dial = new DialResult(DialKind.SipFailure, null, "local error: " + ex.Message);
        }

        _calls.Update(r, _ =>
        {
            attempt.RingingAfterMs = dial.RingingAfterMs;
            attempt.RingingStatus = dial.RingingStatus;
            attempt.FinalStatus = dial.FinalStatus;
            attempt.Detail = dial.Detail;
            if (dial.Kind == DialKind.Answered)
                attempt.AnsweredAt = _time.GetUtcNow();
        });

        switch (dial.Kind)
        {
            case DialKind.NoAnswer: return AttemptResult.NoAnswer;
            case DialKind.Busy: return AttemptResult.Busy;
            case DialKind.SipFailure: return AttemptResult.SipFailure;
            case DialKind.Cancelled: return AttemptResult.Cancelled;
        }

        await using var line = dial.Call!;
        var ctx = new AttemptContext
        {
            Call = r,
            Attempt = attempt,
            Line = line,
            Policy = policy,
            Store = _calls,
            Clips = _clips,
            Listener = _listener,
            Ct = ct,
        };
        try
        {
            return r.Type switch
            {
                CallType.Alarm => await AlarmFlow.RunAsync(ctx).ConfigureAwait(false),
                CallType.Message => await MessageFlow.RunAsync(ctx).ConfigureAwait(false),
                CallType.Conversation => await ConversationFlow.RunAsync(ctx, new ConversationSettings(
                    _sessions, _time, policy.Speech.RealtimeModel, policy.Speech.RealtimeReasoning, policy.Speech.Voice,
                    policy.Speech.Language, policy.DisplayName)).ConfigureAwait(false),
                _ => throw new NotSupportedException($"{r.Type} calls are not available in this version"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AttemptResult.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Call {CallId}: error during the answered call", r.CallId);
            _calls.Update(r, _ => attempt.Detail = "error during the call: " + ex.Message);
            return InterruptedAnsweredResult(r.Type);
        }
        finally
        {
            if (line.IsUp)
                await line.HangupAsync().ConfigureAwait(false);
            _calls.Update(r, _ => attempt.EndedAt = _time.GetUtcNow());
        }
    }

    /// <summary>An answered attempt that ended abnormally: an alarm redials; a message does not.</summary>
    private static AttemptResult InterruptedAnsweredResult(CallType type) => type switch
    {
        CallType.Alarm => AttemptResult.NotAcknowledged,
        CallType.Conversation => AttemptResult.Conversed,
        _ => AttemptResult.PlayedUnconfirmed,
    };

    /// <summary>The redial and snooze rules (PR-ALARM-3, PR-ALARM-4, PR-MSG-2, PR-MSG-3).</summary>
    private void Decide(CallRecord r, AttemptResult last)
    {
        switch (last)
        {
            case AttemptResult.Cancelled:
                Finish(r, Outcome.Cancelled, "cancel_call");
                return;
            case AttemptResult.Awake:
                Finish(r, Outcome.Awake, null);
                return;
            case AttemptResult.Played:
                Finish(r, Outcome.Played, null);
                return;
            case AttemptResult.Confirmed:
                Finish(r, Outcome.Confirmed, null);
                return;
            case AttemptResult.PlayedUnconfirmed:
                Finish(r, Outcome.PlayedUnconfirmed, r.Attempts.LastOrDefault()?.CutOff == true ? "cut_off" : null);
                return;
            case AttemptResult.HungUpEarly:
                Finish(r, Outcome.NotAnswered, "hung_up_early");
                return;
            case AttemptResult.Snoozed:
                ScheduleNext(r, TimeSpan.FromMinutes(r.Options.SnoozeMinutes));
                return;
            case AttemptResult.Conversed:
                // Answered: never redialed (PR-CONV-8). The outcome comes from the evidence.
                var conv = r.Conversation!;
                if (conv.Outcome is null)
                {
                    var (evaluated, why) = Evidence.Evaluate(conv);
                    _calls.Update(r, _ => { conv.Outcome = evaluated; conv.OutcomeReason = why ?? "interrupted"; });
                }
                Finish(r, conv.Outcome!.Value, conv.OutcomeReason);
                return;
        }

        // No answer, busy, SIP failure, or (alarm) answered without acknowledgment.
        if (r.Options.Redial && r.CountedAttempts < r.Options.MaxAttempts)
            ScheduleNext(r, TimeSpan.FromMinutes(r.Options.RetryMinutes));
        else
            Finish(r, Tally(r), null);
    }

    /// <summary>
    /// After the last attempt (PR-ALARM-4): <c>failed</c> when every attempt was a SIP failure and
    /// none was answered; <c>not_acknowledged</c> when an alarm was answered but never acknowledged;
    /// <c>not_answered</c> otherwise.
    /// </summary>
    internal static Outcome Tally(CallRecord r)
    {
        bool anyAnswered = r.Attempts.Any(a => a.Answered);
        var counted = r.Attempts.Where(a => a.Result is not null && a.Result != AttemptResult.Snoozed).ToList();
        if (!anyAnswered && counted.Count > 0 && counted.All(a => a.Result == AttemptResult.SipFailure))
            return Outcome.Failed;
        if (r.Type == CallType.Alarm && anyAnswered)
            return Outcome.NotAcknowledged;
        return Outcome.NotAnswered;
    }

    private void ScheduleNext(CallRecord r, TimeSpan delay)
    {
        _calls.Update(r, c =>
        {
            c.Status = CallStatus.Pending;
            c.NextAttemptAt = _time.GetUtcNow() + delay;
        });
    }

    private void Finish(CallRecord r, Outcome outcome, string? reason)
    {
        _calls.Update(r, c =>
        {
            c.Status = CallStatus.Done;
            c.Outcome = outcome;
            c.Reason = reason;
            c.NextAttemptAt = null;
            c.CompletedAt = _time.GetUtcNow();
        });
        Log.Information("Call {CallId} ({Type}) finished: {Outcome}{Reason}", r.CallId, r.Type, outcome, reason is null ? "" : $" ({reason})");
        lock (_lock)
        {
            if (_done.Remove(r.CallId, out var tcs))
                tcs.TrySetResult();
        }
    }

    /// <summary>The contact must still be in the policy, and an alarm's contact must still be <c>self</c>.</summary>
    private bool CheckTargetStillAllowed(CallRecord r, PolicyFile policy, out string problem)
    {
        problem = "";
        if (r.Contact == "(unlisted)")
        {
            if (!policy.AllowUnlistedNumbers)
                problem = "unlisted numbers are no longer allowed by policy.json";
            return problem.Length == 0;
        }
        var match = policy.Contacts.FirstOrDefault(kv => string.Equals(kv.Key, r.Contact, StringComparison.OrdinalIgnoreCase));
        if (match.Value is null)
        {
            problem = $"contact '{r.Contact}' is no longer in policy.json";
            return false;
        }
        if (r.Type == CallType.Alarm && !match.Value.Self)
        {
            problem = $"contact '{r.Contact}' is no longer marked self; alarms only call self (PR-ALARM-5)";
            return false;
        }
        // The operator may have changed the number since the call was scheduled.
        var fresh = PolicyGuard.Resolve(policy, match.Key, _phone.Server, out var error);
        if (fresh is null)
        {
            problem = error ?? "target rejected";
            return false;
        }
        if (fresh.Uri != r.TargetUri || fresh.Self != r.Self)
        {
            _calls.Update(r, c =>
            {
                c.TargetUri = fresh.Uri;
                c.MaskedNumber = fresh.MaskedNumber;
                c.Self = fresh.Self;
            });
        }
        return true;
    }

    private string? MissingClip(CallRecord r)
    {
        var c = r.Clips;
        var required = r.Type switch
        {
            // An alarm falls back to the built-in wake tone, so only a message needs its clips.
            CallType.Message => new[] { ("message", c.Message), ("confirm_prompt", r.Options.VoiceAck ? c.ConfirmPrompt : "-"), ("disclosure", r.Self ? "-" : c.Disclosure) },
            CallType.Conversation => r.Conversation?.Brief.Message is not null
                ? new[] { ("disclosure", r.Self ? "-" : c.Disclosure), ("message", c.Message), ("confirm_prompt", c.ConfirmPrompt) }
                : new[] { ("disclosure", r.Self ? "-" : c.Disclosure), ("apology", c.Apology) },
            _ => Array.Empty<(string, string?)>(),
        };
        foreach (var (name, id) in required)
        {
            if (id == "-")
                continue;
            if (id is null || _clips.Load(id) is null)
                return name;
        }
        return null;
    }

    /// <summary>
    /// PR-REG-9: when a call is due and registration is down after a 402, 403, or 404, re-register
    /// once before the first attempt and record the result. A rejected password (401/407) is
    /// recorded and not retried.
    /// </summary>
    private async Task ReregisterIfRejectedAsync(CallRecord r, CancellationToken stop)
    {
        var reg = _phone.Registration;
        if (!reg.HardFailure)
            return;
        var record = new ReregisterRecord { At = _time.GetUtcNow(), Before = reg.LastError ?? reg.State };
        if (reg.ErrorStatus is 402 or 403 or 404)
        {
            record.Attempted = true;
            _phone.Reregister();
            record.Registered = await _phone.WaitForRegistrationAsync(ReregisterWait, stop).ConfigureAwait(false);
        }
        _calls.Update(r, c => c.Reregister = record);
    }

    private static string NewId(DateTimeOffset now) =>
        $"c_{now.UtcDateTime:yyyyMMddHHmmss}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
}
