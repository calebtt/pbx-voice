using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Service;
using Xunit;

namespace PbxVoice.Tests;

/// <summary>PR-ALARM-1 to PR-ALARM-6 through the executor, with a scripted phone and STT.</summary>
public class AlarmTests
{
    internal static string Id(string text) => ClipStore.KeyFor(text, "eve", "en", 1.0);

    [Fact]
    public async Task Im_up_on_the_first_reply_is_awake()
    {
        using var h = new Harness();
        var call = new FakeCall(new[] { Replies.Speech(800) });
        h.Phone.Answer(call);
        h.Stt.Says("I'm up.");

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me" }));

        Assert.Equal(Outcome.Awake, r.Outcome);
        Assert.Equal(AckSource.Stt, r.AckSource);
        Assert.Single(r.Attempts);
        Assert.Equal(2, call.Played.Count);
        Assert.Equal(Id(PbxVoiceService.AlarmClosing), call.Played[1]);
        Assert.True(call.HungUpByUs);
        Assert.Equal("I'm up.", r.Attempts[0].Replies.Single().Transcript);
        Assert.Contains(h.Stt.KeyTerms[0], t => t == "i'm up");
    }

    [Fact]
    public async Task Yes_alone_is_not_awake_and_the_prompt_plays_three_times()
    {
        using var h = new Harness();
        var call = new FakeCall(new[] { Replies.Speech(500), Replies.Speech(500), Replies.Speech(500) });
        h.Phone.Answer(call).Fail(DialKind.NoAnswer);
        h.Stt.Says("yes", "okay", "yes");

        var id = await h.CallNow(new { type = "alarm", to = "me", options = new { max_attempts = 2 } });
        await h.RunDue();
        var afterFirst = h.Record(id);
        Assert.Equal(3, afterFirst.Attempts[0].PromptPlays);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(3), afterFirst.NextAttemptAt);

        var r = await h.RunToEnd(id);
        Assert.Equal(Outcome.NotAcknowledged, r.Outcome);
        Assert.Equal(2, h.Phone.Dials.Count);
    }

    [Fact]
    public async Task Silence_on_every_answer_redials_to_the_limit()
    {
        using var h = new Harness();
        for (int i = 0; i < 5; i++)
            h.Phone.Answer(new FakeCall());

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me" }));

        Assert.Equal(Outcome.NotAcknowledged, r.Outcome);
        Assert.Equal(5, r.Attempts.Count);
        Assert.All(r.Attempts, a => Assert.Equal(3, a.PromptPlays));
        Assert.Equal(TimeSpan.FromMinutes(3), r.Attempts[1].StartedAt - r.Attempts[0].StartedAt);
    }

    [Fact]
    public async Task Redial_off_means_one_attempt()
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.NoAnswer).Fail(DialKind.NoAnswer);

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { redial = false } }));

        Assert.Equal(Outcome.NotAnswered, r.Outcome);
        Assert.Single(h.Phone.Dials);
    }

    [Fact]
    public async Task Only_sip_failures_is_failed()
    {
        using var h = new Harness();
        for (int i = 0; i < 5; i++)
            h.Phone.Fail(DialKind.SipFailure, 503);

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me" }));

        Assert.Equal(Outcome.Failed, r.Outcome);
        Assert.Equal(5, r.Attempts.Count);
    }

    [Fact]
    public async Task Busy_and_no_answer_is_not_answered()
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.Busy, 486).Fail(DialKind.SipFailure).Fail(DialKind.NoAnswer);

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { max_attempts = 3 } }));

        Assert.Equal(Outcome.NotAnswered, r.Outcome);
        Assert.Equal(486, r.Attempts[0].FinalStatus);
    }

    [Fact]
    public async Task Snooze_calls_back_later_and_does_not_use_an_attempt()
    {
        using var h = new Harness();
        var first = new FakeCall(new[] { Replies.Speech(700) });
        var second = new FakeCall(new[] { Replies.Speech(700) });
        h.Phone.Answer(first).Answer(second);
        h.Stt.Says("snooze", "I'm awake");

        var id = await h.CallNow(new { type = "alarm", to = "me", options = new { max_attempts = 1 } });
        await h.RunDue();
        var snoozed = h.Record(id);
        Assert.Equal(1, snoozed.Snoozes);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(10), snoozed.NextAttemptAt);
        Assert.Contains(Id("Okay. I'll call back in 10 minutes."), first.Played);

        var r = await h.RunToEnd(id);
        Assert.Equal(Outcome.Awake, r.Outcome);
        Assert.True(r.Attempts[0].Snooze);
        Assert.Equal(2, h.Phone.Dials.Count);
    }

    [Fact]
    public async Task A_snooze_past_the_limit_is_unrecognized()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(new[] { Replies.Speech(700), Replies.Speech(700), Replies.Speech(700) }));
        h.Stt.Says("snooze", "snooze", "snooze");

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { max_attempts = 1, max_snoozes = 0 } }));

        Assert.Equal(Outcome.NotAcknowledged, r.Outcome);
        Assert.All(r.Attempts[0].Replies, reply => Assert.Equal("unrecognized", reply.Intent));
    }

    [Fact]
    public async Task Stt_down_and_a_short_reply_acknowledges_by_speech_detection()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(new[] { Replies.Speech(1200) }));
        h.Stt.Fails();

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me" }));

        Assert.Equal(Outcome.Awake, r.Outcome);
        Assert.Equal(AckSource.SpeechDetected, r.AckSource);
        Assert.Equal("503", r.Attempts[0].Replies[0].SttError);
    }

    [Fact]
    public async Task Stt_down_and_a_long_capture_like_a_greeting_does_not()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(new[] { Replies.Speech(4000), Replies.Speech(4000), Replies.Speech(4000) }));
        h.Stt.Fails().Fails().Fails();

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { max_attempts = 1 } }));

        Assert.Equal(Outcome.NotAcknowledged, r.Outcome);
    }

    [Fact]
    public async Task Slow_stt_counts_as_down()
    {
        using var h = new Harness();
        var listener = new ReplyListener(h.Stt, h.Time, TimeSpan.FromMilliseconds(50));
        h.Stt.Delay = TimeSpan.FromSeconds(5);
        h.Stt.Says("I'm up");
        var call = new FakeCall(new[] { Replies.Speech(1000) });

        var reply = await listener.ListenAsync(call, alarm: true, new Policy.Phrases(), CancellationToken.None);

        Assert.Equal(AckSource.SpeechDetected, reply.Source);
        Assert.Equal("timed out", reply.Record.SttError);
    }

    [Fact]
    public async Task A_keypad_press_acknowledges()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(new[] { Replies.Keypad('5') }));

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me" }));

        Assert.Equal(Outcome.Awake, r.Outcome);
        Assert.Equal(AckSource.Keypad, r.AckSource);
    }

    [Fact]
    public async Task Saying_im_up_and_hanging_up_still_counts()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(new[] { new Capture(CaptureEnd.HungUp, true, TimeSpan.FromMilliseconds(900), new byte[7200]) }));
        h.Stt.Says("I'm up");

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me" }));

        Assert.Equal(Outcome.Awake, r.Outcome);
    }

    [Fact]
    public async Task Ack_none_plays_the_message_twice_and_never_listens()
    {
        using var h = new Harness();
        var call = new FakeCall();
        h.Phone.Answer(call);

        var r = await h.RunToEnd(await h.CallNow(new { type = "alarm", to = "me", options = new { ack = "none" } }));

        Assert.Equal(Outcome.Played, r.Outcome);
        Assert.Equal(2, call.Played.Count);
        Assert.Equal(call.Played[0], call.Played[1]);
        Assert.Equal(0, call.Captures);
    }

    [Fact]
    public async Task A_failed_render_still_places_the_call_with_the_wake_tone()
    {
        using var h = new Harness();
        h.Tts.FailWhen = t => t.StartsWith("Good morning", StringComparison.Ordinal);
        var call = new FakeCall(new[] { Replies.Speech(700) });
        h.Phone.Answer(call);
        h.Stt.Says("I'm up");

        var result = await h.Op("call_now", new { type = "alarm", to = "me" });
        var r = await h.RunToEnd(result.GetProperty("call_id").GetString()!);

        Assert.Equal(BuiltinClips.WakeToneId, call.Played[0]);
        Assert.Contains(r.Notes, n => n.Contains("built-in wake tone", StringComparison.Ordinal));
        Assert.Equal(Outcome.Awake, r.Outcome);
    }

    [Fact]
    public async Task The_prompt_says_the_time_of_the_call()
    {
        using var h = new Harness(start: new DateTimeOffset(2026, 10, 5, 10, 30, 0, TimeSpan.Zero)); // 5:30 in Chicago
        await h.Op("call_now", new { type = "alarm", to = "me" });
        Assert.Contains("Good morning, it's 5:30 AM. Say 'I'm up' when you're awake.", h.Tts.Rendered);
    }

    [Fact]
    public async Task A_contact_no_longer_self_before_dialing_is_not_called()
    {
        using var h = new Harness();
        await h.Op("call_now", new { type = "alarm", to = "me" });
        var changed = Policies.Standard();
        changed.Contacts["me"].Self = false;
        h.Policy.Replace(changed);

        await h.RunDue();

        var r = h.Calls.SnapshotAll().Single();
        Assert.Equal(Outcome.Failed, r.Outcome);
        Assert.Contains("PR-ALARM-5", r.Reason);
        Assert.Empty(h.Phone.Dials);
    }
}
