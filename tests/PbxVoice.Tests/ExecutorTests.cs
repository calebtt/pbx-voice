using PbxVoice.Calls;
using PbxVoice.Hosting;
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
    public async Task A_late_fire_runs_only_inside_its_grace_window(string type, string to, int minutesLate, bool missed)
    {
        using var h = new Harness();
        await h.Op("schedule_call", new { type, to, text = type == "message" ? "Hello." : null, at = "2026-10-05T10:05:00-05:00" });
        h.Time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 15, 5, 0, TimeSpan.Zero).AddMinutes(minutesLate));
        DaemonHost.FireDueSchedules(h.Schedules, h.Executor, h.Time.GetUtcNow());
        h.Phone.Fail(DialKind.NoAnswer);

        await h.Executor.RunDueAsync(CancellationToken.None);

        var r = h.Calls.SnapshotAll().Single();
        Assert.Equal(missed, r.Outcome == Outcome.Missed);
        Assert.Equal(missed ? 0 : 1, h.Phone.Dials.Count);
    }

    [Fact]
    public async Task A_call_due_inside_quiet_hours_is_missed()
    {
        using var h = new Harness();
        await h.Op("schedule_call", new { type = "message", to = "mom", text = "Hello.", at = "2026-10-05T20:30:00-05:00" });
        var stricter = Policies.Standard();
        stricter.QuietHours!.Start = "20:00";
        h.Policy.Replace(stricter);

        h.Time.SetUtcNow(new DateTimeOffset(2026, 10, 6, 1, 30, 0, TimeSpan.Zero));
        DaemonHost.FireDueSchedules(h.Schedules, h.Executor, h.Time.GetUtcNow());
        await h.RunDue();

        var r = h.Calls.SnapshotAll().Single();
        Assert.Equal(Outcome.Missed, r.Outcome);
        Assert.Equal("quiet_hours", r.Reason);
        Assert.Empty(h.Phone.Dials);
    }

    [Fact]
    public async Task The_daily_cap_is_checked_again_at_fire_time()
    {
        var policy = Policies.Standard();
        policy.Limits.CallsPerDay = 1;
        using var h = new Harness(policy);
        await h.Op("schedule_call", new { type = "message", to = "me", text = "Hello.", at = "2026-10-05T12:00:00-05:00" });
        h.Phone.Fail(DialKind.NoAnswer);
        await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { redial = false } }));

        h.Time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 17, 0, 1, TimeSpan.Zero));
        DaemonHost.FireDueSchedules(h.Schedules, h.Executor, h.Time.GetUtcNow());
        await h.RunDue();

        var message = h.Calls.SnapshotAll().Single(c => c.Type == CallType.Message);
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

    [Fact]
    public async Task Preflight_checks_reachability_and_re_registers_after_a_404()
    {
        using var h = new Harness();
        await h.Op("schedule_call", new { type = "alarm", to = "me", at = "2026-10-05T10:30:00-05:00" });
        h.Phone.Registration = new RegistrationInfo("HardFailure", false, "404 Not Found", null);
        h.Phone.RegistrationAfterReregister = new RegistrationInfo("Registered", true, null, null);

        h.Time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 15, 21, 0, TimeSpan.Zero)); // 9 min before
        await DaemonHost.PreflightAsync(h.Schedules, h.Phone, h.Time, CancellationToken.None);
        await DaemonHost.PreflightAsync(h.Schedules, h.Phone, h.Time, CancellationToken.None);

        var p = h.Schedules.All.Single().Preflight!;
        Assert.True(p.PbxReachable);
        Assert.True(p.Reregister!.Attempted);
        Assert.Equal("Registered", p.RegistrationState);
        Assert.Equal(1, h.Phone.Reregisters); // once per fire
    }
}
