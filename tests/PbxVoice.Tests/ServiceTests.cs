using PbxVoice.Service;
using Xunit;

namespace PbxVoice.Tests;

/// <summary>Scheduling-time checks: times, targets, policy, and options.</summary>
public class ServiceTests
{
    private static async Task<string> Refused(Harness h, string op, object args) =>
        (await Assert.ThrowsAsync<ServiceError>(() => h.Op(op, args))).Message;

    [Fact]
    public async Task A_time_with_no_offset_and_no_zone_is_refused()
    {
        using var h = new Harness();
        Assert.Contains("no UTC offset", await Refused(h, "schedule_call", new { type = "alarm", to = "me", at = "2026-10-06T05:30:00" }));
    }

    [Fact]
    public async Task A_local_time_with_a_zone_is_accepted()
    {
        using var h = new Harness();
        var r = await h.Op("schedule_call", new { type = "alarm", to = "me", at = "2026-10-06T05:30:00", tz = Policies.Chicago });
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 10, 30, 0, TimeSpan.Zero), r.GetProperty("next_fire").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_weekly_alarm_fires_next_weekday_morning()
    {
        using var h = new Harness(); // Monday 10:00 Chicago
        var r = await h.Op("schedule_call", new { type = "alarm", to = "me", repeat = new { days = new[] { "mon", "wed" }, time = "05:30", tz = Policies.Chicago } });
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 10, 30, 0, TimeSpan.Zero), r.GetProperty("next_fire").GetDateTimeOffset());
        Assert.Single((await h.Op("list_schedules")).EnumerateArray());
    }

    [Fact]
    public async Task At_and_repeat_together_are_refused()
    {
        using var h = new Harness();
        Assert.Contains("exactly one", await Refused(h, "schedule_call",
            new { type = "alarm", to = "me", at = "2026-10-06T05:30:00Z", repeat = new { days = "daily", time = "05:30", tz = "UTC" } }));
    }

    [Fact]
    public async Task Repeat_needs_a_zone()
    {
        using var h = new Harness();
        Assert.Contains("repeat.tz", await Refused(h, "schedule_call", new { type = "alarm", to = "me", repeat = new { days = "daily", time = "05:30" } }));
    }

    [Fact]
    public async Task A_number_not_in_the_contacts_is_refused()
    {
        using var h = new Harness();
        Assert.Contains("not a contact", await Refused(h, "call_now", new { type = "message", to = "+15555559999", text = "Hi." }));
    }

    [Fact]
    public async Task A_contact_number_resolves_to_that_contact()
    {
        using var h = new Harness();
        var r = await h.Op("call_now", new { type = "message", to = "+1 (555) 555-0101", text = "Hi." });
        Assert.Equal("mom", r.GetProperty("contact").GetString());
    }

    [Fact]
    public async Task Unlisted_numbers_when_allowed_still_go_through_the_unsafe_target_check()
    {
        var policy = Policies.Standard();
        policy.AllowUnlistedNumbers = true;
        using var h = new Harness(policy);
        var ok = await h.Op("call_now", new { type = "message", to = "+15555559999", text = "Hi." });
        Assert.Equal("***9999", ok.GetProperty("masked_number").GetString());
        Assert.Contains("target rejected", await Refused(h, "call_now", new { type = "message", to = "sip:x@example.com%0d%0aVia: evil", text = "Hi." }));
    }

    [Fact]
    public async Task An_alarm_to_a_contact_that_is_not_self_is_refused()
    {
        using var h = new Harness();
        Assert.Contains("self", await Refused(h, "call_now", new { type = "alarm", to = "mom" }));
    }

    [Fact]
    public async Task Quiet_hours_refuse_non_self_calls_but_not_self_alarms()
    {
        using var h = new Harness();
        Assert.Contains("quiet hours", await Refused(h, "schedule_call", new { type = "message", to = "mom", text = "Hi.", at = "2026-10-05T22:00:00-05:00" }));
        await h.Op("schedule_call", new { type = "alarm", to = "me", at = "2026-10-06T05:30:00-05:00" });
    }

    [Theory]
    [InlineData("max_attempts", 11, "policy cap")]
    [InlineData("ring_seconds", 120, "policy cap")]
    [InlineData("retry_minutes", 0, "retry_minutes")]
    public async Task Options_outside_the_caps_are_refused(string option, int value, string expected)
    {
        using var h = new Harness();
        var options = new Dictionary<string, object> { [option] = value };
        Assert.Contains(expected, await Refused(h, "call_now", new { type = "alarm", to = "me", options }));
    }

    [Fact]
    public async Task Unknown_options_and_types_are_refused()
    {
        using var h = new Harness();
        Assert.Contains("not an option", await Refused(h, "call_now", new { type = "message", to = "me", text = "Hi.", options = new { snooze_minutes = 5 } }));
        Assert.Contains("not available", await Refused(h, "call_now", new { type = "conversation", to = "me" }));
        Assert.Contains("unknown operation", await Refused(h, "edit_policy", new { }));
    }

    [Fact]
    public async Task Clips_for_a_recurring_call_are_rendered_once()
    {
        using var h = new Harness();
        var weekly = new { type = "alarm", to = "me", repeat = new { days = "weekdays", time = "05:30", tz = Policies.Chicago } };
        await h.Op("schedule_call", weekly);
        int rendered = h.Tts.Rendered.Count;
        await h.Op("schedule_call", weekly);
        Assert.Equal(rendered, h.Tts.Rendered.Count);
    }

    [Fact]
    public async Task Contacts_are_listed_with_masked_numbers()
    {
        using var h = new Harness();
        var contacts = (await h.Op("list_contacts")).EnumerateArray().ToList();
        Assert.Contains(contacts, c => c.GetProperty("name").GetString() == "me" && c.GetProperty("self").GetBoolean()
                                       && c.GetProperty("masked_number").GetString() == "***0100");
        Assert.DoesNotContain("5555550100", (await h.Op("list_contacts")).GetRawText());
    }

    [Fact]
    public async Task Call_records_do_not_show_the_dial_target()
    {
        using var h = new Harness();
        var id = await h.CallNow(new { type = "alarm", to = "me" });
        var view = (await h.Op("get_call", new { call_id = id })).GetRawText();
        Assert.DoesNotContain("5555550100", view);
        Assert.Contains("***0100", view);
    }

    [Fact]
    public async Task Status_reports_registration_and_usage()
    {
        using var h = new Harness();
        var s = await h.Op("status");
        Assert.True(s.GetProperty("registration").GetProperty("registered").GetBoolean());
        Assert.Equal(20, s.GetProperty("usage_today").GetProperty("calls_per_day").GetInt32());
    }

    [Fact]
    public async Task Cancelling_a_schedule_stops_future_fires()
    {
        using var h = new Harness();
        var r = await h.Op("schedule_call", new { type = "alarm", to = "me", repeat = new { days = "daily", time = "05:30", tz = Policies.Chicago } });
        await h.Op("cancel_schedule", new { schedule_id = r.GetProperty("schedule_id").GetString() });
        Assert.Empty((await h.Op("list_schedules")).EnumerateArray());
        Assert.Empty(h.Schedules.CollectDue(h.Time.GetUtcNow().AddDays(8)));
    }
}
