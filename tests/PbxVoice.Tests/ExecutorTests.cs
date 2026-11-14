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

    // ---- #22: a cancel while the executor holds the call before dialing -------------------

    private static async Task<(Harness H, string Id, Task Running, TaskCompletionSource Release)> HoldInReregisterWait()
    {
        var h = new Harness();
        h.Phone.Registration = new RegistrationInfo("HardFailure", false, "403 Forbidden", null);
        var release = new TaskCompletionSource();
        h.Phone.HoldRegistrationWait = release.Task;
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        var running = h.RunDue();
        await h.Phone.RegistrationWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (h, id, running, release);
    }

    [Fact]
    public async Task A_cancel_during_the_re_register_wait_is_not_dialed()
    {
        var (h, id, running, release) = await HoldInReregisterWait();
        using var _ = h;

        Assert.Equal(id, h.Executor.CurrentCallId);
        Assert.True(h.Executor.Cancel(id));
        // Back from the wait: the cancel must win, with or without registration.
        h.Phone.Registration = new RegistrationInfo("Registered", true, null, null);
        release.SetResult();
        await running;

        var r = h.Record(id);
        Assert.Empty(h.Phone.Dials);
        Assert.Equal(CallStatus.Done, r.Status);
        Assert.Equal(Outcome.Cancelled, r.Outcome);
        Assert.Empty(r.Attempts);
    }

    [Fact]
    public async Task A_shutdown_during_the_re_register_wait_leaves_the_call_pending()
    {
        using var h = new Harness();
        h.Phone.Registration = new RegistrationInfo("HardFailure", false, "403 Forbidden", null);
        var release = new TaskCompletionSource();
        h.Phone.HoldRegistrationWait = release.Task;
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        using var stop = new CancellationTokenSource();
        var running = h.Executor.RunDueAsync(stop.Token);
        await h.Phone.RegistrationWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        release.SetResult();
        await running;

        var r = h.Record(id);
        Assert.Empty(h.Phone.Dials);
        Assert.Equal(CallStatus.Pending, r.Status);
        Assert.Empty(r.Attempts);
    }

    [Fact]
    public async Task A_finished_call_is_never_reopened()
    {
        using var h = new Harness();
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        var record = h.Calls.Open().Single();
        Assert.True(h.Executor.Cancel(id));

        // The executor held a reference from an earlier due query.
        Assert.False(h.Calls.TryUpdate(record, c => c.Status == CallStatus.Pending, c => c.Status = CallStatus.InProgress));
        await h.RunDue();

        Assert.Equal(CallStatus.Done, h.Record(id).Status);
        Assert.Equal(Outcome.Cancelled, h.Record(id).Outcome);
        Assert.Empty(h.Phone.Dials);
    }

    // ---- #23: redials and snooze call-backs have a grace window too -----------------------

    [Fact]
    public async Task A_redial_long_overdue_after_a_restart_is_not_dialed()
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.NoAnswer);
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        await h.RunDue();
        Assert.Equal(CallStatus.Pending, h.Record(id).Status);

        // The daemon was stopped between attempts and starts again days later.
        h.Time.Advance(TimeSpan.FromDays(3) + TimeSpan.FromHours(7));
        var calls = new CallStore(h.Paths.Calls);
        var executor = new Executor(h.Time, h.Phone, h.Policy, calls, h.Clips, new ReplyListener(h.Stt, h.Time, TimeSpan.FromSeconds(3)));
        executor.Recover();
        while (await executor.RunDueAsync(CancellationToken.None)) { }

        var r = calls.Snapshot(id)!;
        Assert.Single(h.Phone.Dials);
        Assert.Equal(CallStatus.Done, r.Status);
        Assert.Equal(Outcome.NotAnswered, r.Outcome);
        Assert.Equal("redial_window_passed", r.Reason);
    }

    [Theory]
    [InlineData("alarm", 3 + 14, 2)]   // 14 minutes late: within the alarm's 15
    [InlineData("alarm", 3 + 16, 1)]
    [InlineData("message", 10 + 4, 2)] // within the message's 5
    [InlineData("message", 10 + 6, 1)]
    public async Task A_slightly_late_redial_is_still_placed(string type, int minutesAfterFirst, int dials)
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.NoAnswer).Fail(DialKind.NoAnswer);
        var id = await h.CallNow(new { type, to = "me", text = type == "message" ? "Hello." : null, options = new { max_attempts = 2 } });
        await h.RunDue();

        h.Time.Advance(TimeSpan.FromMinutes(minutesAfterFirst));
        await h.RunDue();

        Assert.Equal(dials, h.Phone.Dials.Count);
        if (dials == 1)
            Assert.Equal("redial_window_passed", h.Record(id).Reason);
    }

    [Fact]
    public async Task A_snooze_call_back_long_overdue_is_not_dialed()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(new[] { Replies.Speech(700) }));
        h.Stt.Says("snooze");
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        await h.RunDue();
        Assert.Equal(1, h.Record(id).Snoozes);

        h.Time.Advance(TimeSpan.FromHours(5));
        await h.RunDue();

        var r = h.Record(id);
        Assert.Single(h.Phone.Dials);
        Assert.Equal(Outcome.NotAcknowledged, r.Outcome);
        Assert.Equal("snooze_window_passed", r.Reason);
    }

    [Fact]
    public async Task A_call_interrupted_long_ago_is_finished_at_restart_not_redialed()
    {
        using var h = new Harness();
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        var r = h.Calls.Open().Single();
        h.Calls.Update(r, c =>
        {
            c.Status = CallStatus.InProgress;
            c.Attempts.Add(new AttemptRecord { Number = 1, StartedAt = h.Time.GetUtcNow() });
        });
        h.Time.Advance(TimeSpan.FromHours(8));

        h.Executor.Recover();
        await h.RunDue();

        var done = h.Record(id);
        Assert.Empty(h.Phone.Dials);
        Assert.Equal(CallStatus.Done, done.Status);
        Assert.Equal(Outcome.Failed, done.Outcome); // the only attempt is a SIP failure ("interrupted")
        Assert.Equal("redial_window_passed", done.Reason);
    }
}
