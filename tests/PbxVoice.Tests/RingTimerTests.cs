using Microsoft.Extensions.Time.Testing;
using PbxVoice.Calls;
using PbxVoice.Sip;
using Xunit;

namespace PbxVoice.Tests;

/// <summary>PR-SCHED-6: ring time from the first 180/183; 7 s setup limit from the INVITE.</summary>
public class RingTimerTests
{
    private readonly FakeTimeProvider _time = new();
    private int _expired;

    private RingTimer Start()
    {
        var t = new RingTimer(_time, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(7), () => _expired++);
        t.Start();
        return t;
    }

    [Fact]
    public void No_ringing_expires_at_the_setup_limit()
    {
        using var t = Start();
        _time.Advance(TimeSpan.FromSeconds(6.9));
        Assert.Equal(RingExpiry.None, t.Expired);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(RingExpiry.Setup, t.Expired);
        Assert.Equal(1, _expired);
    }

    [Fact]
    public void Ring_time_counts_from_ringing_not_from_the_invite()
    {
        using var t = Start();
        _time.Advance(TimeSpan.FromSeconds(3));
        t.OnRinging();
        _time.Advance(TimeSpan.FromSeconds(44.9)); // 47.9 s after the INVITE
        Assert.Equal(RingExpiry.None, t.Expired);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(RingExpiry.Ring, t.Expired);
        Assert.Equal(1, _expired);
    }

    [Fact]
    public void Later_ringing_does_not_restart_the_ring()
    {
        using var t = Start();
        t.OnRinging();
        _time.Advance(TimeSpan.FromSeconds(30));
        t.OnRinging();
        _time.Advance(TimeSpan.FromSeconds(15.1));
        Assert.Equal(RingExpiry.Ring, t.Expired);
    }

    [Fact]
    public void A_final_response_stops_both_timers()
    {
        using var t = Start();
        t.OnRinging();
        t.Stop();
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(RingExpiry.None, t.Expired);
        Assert.Equal(0, _expired);
    }

    [Theory]
    [InlineData(408, "NoAnswer")]
    [InlineData(480, "NoAnswer")]
    [InlineData(486, "Busy")]
    [InlineData(600, "Busy")]
    [InlineData(603, "Busy")]
    [InlineData(404, "SipFailure")]
    [InlineData(503, "SipFailure")]
    [InlineData(null, "SipFailure")]
    public void Final_status_split(int? status, string expected) =>
        Assert.Equal(Enum.Parse<DialKind>(expected), DialClassifier.FromFinalStatus(status));

    [Theory]
    [InlineData("486 Busy Here", 486)]
    [InlineData("603 Decline", 603)]
    [InlineData("Call cancelled by user.", null)]
    [InlineData(null, null)]
    public void Status_from_SipBotLib_failure_text(string? failure, int? expected) =>
        Assert.Equal(expected, DialClassifier.ParseStatus(failure));
}
