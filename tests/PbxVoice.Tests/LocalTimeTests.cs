using PbxVoice.Policy;
using Xunit;

namespace PbxVoice.Tests;

/// <summary>Local wall-clock times across daylight-saving transitions (the policy day and quiet hours use them).</summary>
public class LocalTimeTests
{
    private static readonly TimeZoneInfo Chicago = PolicyLoader.Zone(Policies.Chicago);

    [Fact]
    public void A_skipped_local_time_becomes_the_first_valid_minute_after_it()
    {
        // 2027-03-14: Chicago jumps from 02:00 CST to 03:00 CDT.
        var t = PolicyGuard.ResolveLocal(new DateTime(2027, 3, 14, 2, 30, 0), Chicago);
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 3, 0, 0, TimeSpan.FromHours(-5)), t);
    }

    [Fact]
    public void A_repeated_local_time_is_its_first_occurrence()
    {
        // 2026-11-01: 01:00-02:00 happens twice (CDT, then CST).
        var t = PolicyGuard.ResolveLocal(new DateTime(2026, 11, 1, 1, 30, 0), Chicago);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5)), t);
    }

    [Fact]
    public void The_policy_day_is_25_hours_when_the_clocks_go_back()
    {
        var policy = Policies.Standard();
        var (start, end) = PolicyGuard.Day(policy, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(-6)));
        Assert.Equal(TimeSpan.FromHours(25), end - start);
    }
}
