using PbxVoice.Calls;
using PbxVoice.Policy;
using PbxVoice.Scheduling;
using Xunit;

namespace PbxVoice.Tests;

public class RecurrenceTests
{
    private static readonly TimeZoneInfo Chicago = PolicyLoader.Zone(Policies.Chicago);
    private static readonly HashSet<DayOfWeek> Daily = Enum.GetValues<DayOfWeek>().ToHashSet();

    [Fact]
    public void A_skipped_local_time_fires_at_the_first_valid_minute_after_it()
    {
        // 2027-03-14: Chicago jumps from 02:00 CST to 03:00 CDT.
        var fire = Recurrence.ResolveLocal(new DateTime(2027, 3, 14, 2, 30, 0), Chicago);
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 3, 0, 0, TimeSpan.FromHours(-5)), fire);
    }

    [Fact]
    public void A_repeated_local_time_fires_once_at_the_first_occurrence()
    {
        // 2026-11-01: 01:00-02:00 happens twice (CDT, then CST).
        var first = Recurrence.ResolveLocal(new DateTime(2026, 11, 1, 1, 30, 0), Chicago);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5)), first);

        var next = Recurrence.NextWeekly(first, Daily, new TimeOnly(1, 30), Chicago);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 1, 30, 0, TimeSpan.FromHours(-6)), next);
    }

    [Fact]
    public void Weekly_keeps_wall_clock_time_across_a_transition()
    {
        var weekdays = Recurrence.ParseDays(new[] { "weekdays" }, out _)!;
        var friday = new DateTimeOffset(2026, 10, 30, 10, 30, 0, TimeSpan.Zero); // 05:30 CDT
        var next = Recurrence.NextWeekly(friday, weekdays, new TimeOnly(5, 30), Chicago);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 11, 30, 0, TimeSpan.Zero), next); // Monday 05:30 CST
    }

    [Fact]
    public void Weekdays_skip_the_weekend()
    {
        var weekdays = Recurrence.ParseDays(new[] { "weekdays" }, out _)!;
        var saturdayNoon = new DateTimeOffset(2026, 10, 10, 17, 0, 0, TimeSpan.Zero);
        var next = Recurrence.NextWeekly(saturdayNoon, weekdays, new TimeOnly(5, 30), Chicago);
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTime(next, Chicago).DayOfWeek);
    }

    [Theory]
    [InlineData("monday", true)]
    [InlineData("Tue", true)]
    [InlineData("daily", true)]
    [InlineData("someday", false)]
    public void Day_names(string name, bool ok)
    {
        var days = Recurrence.ParseDays(new[] { name }, out var error);
        Assert.Equal(ok, days is not null);
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void Missed_fires_while_down_each_produce_a_call_and_a_one_off_finishes()
    {
        using var h = new Harness();
        var now = h.Time.GetUtcNow();
        h.Schedules.Add(new Schedule
        {
            Id = "daily", Type = CallType.Alarm, Contact = "me", Self = true, TargetUri = "sip:1@x",
            Repeat = new RepeatSpec { Days = { "daily" }, Time = "05:30", Tz = Policies.Chicago },
            NextFire = new DateTimeOffset(2026, 10, 3, 10, 30, 0, TimeSpan.Zero), // Saturday 05:30 CDT; the harness clock is Monday 10:00
        });
        h.Schedules.Add(new Schedule { Id = "once", Type = CallType.Alarm, Contact = "me", Self = true, TargetUri = "sip:1@x", At = now.AddMinutes(-1), NextFire = now.AddMinutes(-1) });

        var due = h.Schedules.CollectDue(now);

        Assert.Equal(3, due.Count(d => d.Schedule.Id == "daily"));
        Assert.True(h.Schedules.Find("daily")!.NextFire > now);
        Assert.Equal("done", h.Schedules.Find("once")!.Status);
        Assert.Null(h.Schedules.Find("once")!.NextFire);
    }
}
