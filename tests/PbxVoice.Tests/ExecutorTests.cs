using PbxVoice.Calls;
using Xunit;

namespace PbxVoice.Tests;

/// <summary>One call at a time, alarms first, grace windows, fire-time policy, and registration rules.</summary>
public class ExecutorTests
{
    [Fact]
    public async Task An_alarm_goes_ahead_of_a_message_due_at_the_same_time()
    {
        using var h = new Harness();
        await h.CallNow(new { type = "message", to = "mom", text = "Hello." });
        await h.CallNow(new { type = "alarm", to = "me" });
        h.Phone.Fail(DialKind.NoAnswer).Fail(DialKind.NoAnswer);

        await h.Executor.RunDueAsync(CancellationToken.None);

        Assert.Contains("15555550100", h.Phone.Dials[0].Uri);
    }

    [Fact]
    public async Task Between_alarm_attempts_another_due_call_runs()
    {
        using var h = new Harness();
        var alarm = await h.CallNow(new { type = "alarm", to = "me" });
        h.Phone.Fail(DialKind.NoAnswer).Fail(DialKind.NoAnswer).Fail(DialKind.NoAnswer);
        await h.Executor.RunDueAsync(CancellationToken.None);

        var message = await h.CallNow(new { type = "message", to = "mom", text = "Hello." });
        await h.Executor.RunDueAsync(CancellationToken.None);

        Assert.Contains("15555550101", h.Phone.Dials[1].Uri);
        Assert.Equal(CallStatus.Pending, h.Record(alarm).Status);
        Assert.Single(h.Record(message).Attempts);
    }

    [Theory]
    [InlineData("alarm", "me", 14, false)]
    [InlineData("alarm", "me", 16, true)]
    [InlineData("message", "me", 4, false)]
    [InlineData("message", "me", 6, true)]
    public async Task A_call_that_cannot_start_within_its_grace_window_is_missed(string type, string to, int minutesLate, bool missed)
    {
        // The daemon was stopped or busy between call_now and the first dial.
        using var h = new Harness();
        await h.Op("call_now", new { type, to, text = type == "message" ? "Hello." : null });
        h.Time.Advance(TimeSpan.FromMinutes(minutesLate));
        h.Phone.Fail(DialKind.NoAnswer);

        await h.Executor.RunDueAsync(CancellationToken.None);

        var r = h.Calls.SnapshotAll().Single();
        Assert.Equal(missed, r.Outcome == Outcome.Missed);
        Assert.Equal(missed ? 0 : 1, h.Phone.Dials.Count);
    }

    [Fact]
    public async Task Quiet_hours_are_checked_again_before_dialing()
    {
        using var h = new Harness(); // 10:00 in Chicago
        await h.Op("call_now", new { type = "message", to = "mom", text = "Hello." });
        var stricter = Policies.Standard();
        stricter.QuietHours!.Start = "09:00";
        h.Policy.Replace(stricter);

        await h.RunDue();

        var r = h.Calls.SnapshotAll().Single();
        Assert.Equal(Outcome.Missed, r.Outcome);
        Assert.Equal("quiet_hours", r.Reason);
        Assert.Empty(h.Phone.Dials);
    }

    [Fact]
    public async Task The_daily_cap_is_checked_again_before_dialing()
    {
        var policy = Policies.Standard();
        policy.Limits.CallsPerDay = 1;
        using var h = new Harness(policy);
        h.Phone.Fail(DialKind.NoAnswer);
        var alarm = await h.CallNow(new { type = "alarm", to = "me", options = new { redial = false } });
        var queued = await h.CallNow(new { type = "message", to = "me", text = "Hello." }); // nothing dialed yet today

        await h.RunToEnd(alarm); // alarms go first and use today's one call
        await h.RunDue();

        var message = h.Record(queued);
        Assert.Equal(Outcome.Missed, message.Outcome);
        Assert.Equal("daily_cap", message.Reason);
    }

    [Fact]
    public async Task After_a_403_the_daemon_re_registers_once_before_the_first_attempt()
    {
        using var h = new Harness();
        h.Phone.Registration = new RegistrationInfo("HardFailure", false, "403 Forbidden", null);
        h.Phone.RegistrationAfterReregister = new RegistrationInfo("Registered", true, null, null);
        h.Phone.Fail(DialKind.NoAnswer).Fail(DialKind.NoAnswer);

        var id = await h.CallNow(new { type = "alarm", to = "me", options = new { max_attempts = 2 } });
        var r = await h.RunToEnd(id);

        Assert.Equal(1, h.Phone.Reregisters);
        Assert.True(r.Reregister!.Attempted);
        Assert.True(r.Reregister.Registered);
        Assert.Equal("403 Forbidden", r.Reregister.Before);
    }

    [Fact]
    public async Task A_rejected_password_is_recorded_and_not_retried()
    {
        using var h = new Harness();
        h.Phone.Registration = new RegistrationInfo("HardFailure", false, "401 Unauthorized", null);
        h.Phone.Fail(DialKind.SipFailure, 403);

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { redial = false } }));

        Assert.Equal(0, h.Phone.Reregisters);
        Assert.False(r.Reregister!.Attempted);
    }

    [Fact]
    public async Task Dial_while_unregistered_off_waits_then_counts_a_sip_failure()
    {
        var policy = Policies.Standard();
        policy.DialWhileUnregistered = false;
        using var h = new Harness(policy);
        h.Phone.Registration = new RegistrationInfo("TemporaryFailure", false, "timeout", null);
        h.Phone.RegistersWhileWaiting = false;

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { redial = false } }));

        Assert.Equal(Outcome.Failed, r.Outcome);
        Assert.Equal("registration down; not dialed", r.Attempts[0].Detail);
        Assert.Empty(h.Phone.Dials);
    }

    [Fact]
    public async Task The_ring_time_and_setup_limit_reach_the_dialer()
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.NoAnswer);

        await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { redial = false, ring_seconds = 20 } }));

        Assert.Equal(TimeSpan.FromSeconds(20), h.Phone.Dials[0].RingTime);
        Assert.Equal(TimeSpan.FromSeconds(7), h.Phone.Dials[0].SetupLimit);
    }

    [Fact]
    public async Task Cancelling_a_pending_call()
    {
        using var h = new Harness();
        var id = await h.CallNow(new { type = "alarm", to = "me" });

        await h.Op("cancel_call", new { call_id = id });
        await h.RunDue();

        Assert.Equal(Outcome.Cancelled, h.Record(id).Outcome);
        Assert.Empty(h.Phone.Dials);
    }

    [Fact]
    public async Task Wait_for_call_reports_in_progress_on_timeout_and_the_outcome_when_done()
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.NoAnswer).Fail(DialKind.NoAnswer);
        var id = await h.CallNow(new { type = "alarm", to = "me", options = new { max_attempts = 2 } });
        await h.RunDue();

        var waiting = h.Op("wait_for_call", new { call_id = id, timeout_sec = 5 });
        h.Time.Advance(TimeSpan.FromSeconds(6));
        var pending = await waiting;
        Assert.Equal("in_progress", pending.GetProperty("status").GetString());

        var final = h.Op("wait_for_call", new { call_id = id, timeout_sec = 900 });
        await h.RunToEnd(id);
        Assert.Equal("not_answered", (await final).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_call_interrupted_by_a_restart_continues_under_the_redial_rules()
    {
        using var h = new Harness();
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        var r = h.Calls.Open().Single();
        h.Calls.Update(r, c =>
        {
            c.Status = CallStatus.InProgress;
            c.Attempts.Add(new AttemptRecord { Number = 1, StartedAt = h.Time.GetUtcNow() });
        });

        h.Executor.Recover();

        var recovered = h.Record(id);
        Assert.Equal(CallStatus.Pending, recovered.Status);
        Assert.Equal(AttemptResult.SipFailure, recovered.Attempts[0].Result);
        Assert.NotNull(recovered.NextAttemptAt);
    }

    // ---- #28: rendered clips follow the records' retention -------------------------------

    [Fact]
    public async Task Retention_deletes_a_pruned_messages_clip_and_keeps_clips_still_in_use()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(new[] { Replies.Speech(600) }));
        h.Stt.Says("got it");
        var old = await h.CallNow(new { type = "message", to = "me", text = "The old message." });
        await h.RunToEnd(old);
        var oldMessage = h.Record(old).Clips.Message!;

        // A month and a day later, another message: it shares the fixed prompts and is still open.
        h.Time.Advance(TimeSpan.FromDays(31));
        foreach (var file in Directory.EnumerateFiles(h.Paths.Clips))
            File.SetLastWriteTimeUtc(file, h.Time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(31));
        var recent = await h.CallNow(new { type = "message", to = "me", text = "The new message." });
        var recentClips = h.Record(recent).Clips;
        // An orphan from long ago that no record uses, and a fresh one (being rendered for a request).
        var orphan = Path.Combine(h.Paths.Clips, new string('a', 64) + ".wav");
        var fresh = Path.Combine(h.Paths.Clips, new string('b', 64) + ".wav");
        File.WriteAllBytes(orphan, new byte[100]);
        File.WriteAllBytes(fresh, new byte[100]);
        File.SetLastWriteTimeUtc(orphan, h.Time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(40));
        File.SetLastWriteTimeUtc(fresh, h.Time.GetUtcNow().UtcDateTime);

        var (records, clips) = Retention.Run(h.Calls, h.Clips, h.Time.GetUtcNow() - TimeSpan.FromDays(30));

        Assert.Equal(1, records);
        Assert.Null(h.Calls.Snapshot(old));
        Assert.Equal(2, clips); // the old message and the orphan
        Assert.Null(h.Clips.Load(oldMessage));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(fresh));
        Assert.NotNull(h.Clips.Load(recentClips.Message!));
        Assert.NotNull(h.Clips.Load(recentClips.ConfirmPrompt!)); // shared with the pruned call, still used
    }

    [Fact]
    public async Task A_reused_clip_is_marked_fresh_so_retention_keeps_it()
    {
        using var h = new Harness();
        var first = await h.CallNow(new { type = "message", to = "me", text = "Same words." });
        var path = Path.Combine(h.Paths.Clips, h.Record(first).Clips.Message + ".wav");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(90));

        await h.CallNow(new { type = "message", to = "me", text = "Same words." }); // cache hit

        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - TimeSpan.FromMinutes(5));
    }
}

