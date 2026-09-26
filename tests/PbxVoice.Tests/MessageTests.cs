using PbxVoice.Calls;
using PbxVoice.Service;
using Xunit;
using static PbxVoice.Tests.AlarmTests;

namespace PbxVoice.Tests;

/// <summary>PR-MSG-1 to PR-MSG-3 and the disclosure (PR-SAFE-6).</summary>
public class MessageTests
{
    private const string Text = "Your flight lands at 3:40.";
    private static readonly string Disclosure = PbxVoiceService.Disclosure.Replace("{name}", "Alex");

    [Fact]
    public async Task Got_it_confirms_and_a_non_self_call_starts_with_the_disclosure()
    {
        using var h = new Harness();
        var call = new FakeCall(new[] { Replies.Speech(600) });
        h.Phone.Answer(call);
        h.Stt.Says("Got it.");

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "mom", text = Text }));

        Assert.Equal(Outcome.Confirmed, r.Outcome);
        Assert.Equal(new[] { Id(Disclosure), Id(Text), Id(PbxVoiceService.ConfirmPrompt), Id(PbxVoiceService.MessageClosing) }, call.Played);
        Assert.Contains(Disclosure, h.Tts.Rendered);
    }

    [Fact]
    public async Task A_call_to_self_has_no_disclosure()
    {
        using var h = new Harness();
        var call = new FakeCall(new[] { Replies.Speech(600) });
        h.Phone.Answer(call);
        h.Stt.Says("thanks");

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "me", text = Text }));

        Assert.Equal(Outcome.Confirmed, r.Outcome);
        Assert.Equal(Id(Text), call.Played[0]);
        Assert.DoesNotContain(Id(Disclosure), call.Played);
    }

    [Fact]
    public async Task Repeat_plays_the_message_again()
    {
        using var h = new Harness();
        var call = new FakeCall(new[] { Replies.Speech(600), Replies.Speech(600) });
        h.Phone.Answer(call);
        h.Stt.Says("say that again", "okay");

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "me", text = Text }));

        Assert.Equal(Outcome.Confirmed, r.Outcome);
        Assert.Equal(2, r.Attempts[0].MessagePlays);
        Assert.Equal(new[] { Id(Text), Id(PbxVoiceService.ConfirmPrompt), Id(Text), Id(PbxVoiceService.ConfirmPrompt), Id(PbxVoiceService.MessageClosing) }, call.Played);
    }

    [Fact]
    public async Task No_reply_after_asking_twice_is_played_unconfirmed_and_not_redialed()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall()).Answer(new FakeCall());

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "mom", text = Text }));

        Assert.Equal(Outcome.PlayedUnconfirmed, r.Outcome);
        Assert.Equal(2, r.Attempts[0].PromptPlays);
        Assert.Single(h.Phone.Dials);
    }

    [Fact]
    public async Task A_hang_up_during_the_disclosure_is_hung_up_early()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(hangUpDuringPlay: 1)).Answer(new FakeCall());

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "mom", text = Text }));

        Assert.Equal(Outcome.NotAnswered, r.Outcome);
        Assert.Equal("hung_up_early", r.Reason);
        Assert.Single(h.Phone.Dials);
    }

    [Fact]
    public async Task A_hang_up_in_the_pause_after_answer_is_hung_up_early()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(hangUpDuringPause: true));

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "me", text = Text }));

        Assert.Equal(Outcome.NotAnswered, r.Outcome);
        Assert.Equal("hung_up_early", r.Reason);
    }

    [Fact]
    public async Task A_hang_up_before_the_message_starts_is_hung_up_early()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(hangUpDuringPlay: 1, playedBeforeHangUp: TimeSpan.Zero));

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "me", text = Text }));

        Assert.Equal("hung_up_early", r.Reason);
    }

    [Fact]
    public async Task A_hang_up_during_the_message_is_a_cut_off_played_unconfirmed()
    {
        using var h = new Harness();
        h.Phone.Answer(new FakeCall(hangUpDuringPlay: 2)).Answer(new FakeCall());

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "mom", text = Text }));

        Assert.Equal(Outcome.PlayedUnconfirmed, r.Outcome);
        Assert.Equal("cut_off", r.Reason);
        Assert.True(r.Attempts[0].CutOff);
        Assert.Single(h.Phone.Dials);
    }

    [Fact]
    public async Task No_answer_then_answer_redials_once_ten_minutes_later()
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.NoAnswer, 480).Answer(new FakeCall(new[] { Replies.Speech(500) }));
        h.Stt.Says("yes");

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "me", text = Text }));

        Assert.Equal(Outcome.Confirmed, r.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(10), r.Attempts[1].StartedAt - r.Attempts[0].StartedAt);
    }

    [Fact]
    public async Task Two_unanswered_attempts_is_not_answered()
    {
        using var h = new Harness();
        h.Phone.Fail(DialKind.NoAnswer).Fail(DialKind.Busy, 486);

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "me", text = Text }));

        Assert.Equal(Outcome.NotAnswered, r.Outcome);
        Assert.Equal(2, h.Phone.Dials.Count);
    }

    [Fact]
    public async Task Ack_none_plays_the_message_twice()
    {
        using var h = new Harness();
        var call = new FakeCall();
        h.Phone.Answer(call);

        var r = await h.RunToEnd(await h.CallNow(new { type = "message", to = "me", text = Text, options = new { ack = "none" } }));

        Assert.Equal(Outcome.Played, r.Outcome);
        Assert.Equal(new[] { Id(Text), Id(Text) }, call.Played);
    }

    [Fact]
    public async Task A_message_that_cannot_be_rendered_is_refused()
    {
        using var h = new Harness();
        h.Tts.FailWhen = t => t == Text;

        var ex = await Assert.ThrowsAsync<ServiceError>(() => h.Op("call_now", new { type = "message", to = "me", text = Text }));
        Assert.Contains("could not render the message clip", ex.Message);
    }
}
