using PbxVoice.Audio;
using PbxVoice.Policy;
using PbxVoice.Speech;

namespace PbxVoice.Calls;

/// <summary>What an answered-call flow needs. Record changes go through the call store's lock.</summary>
internal sealed class AttemptContext
{
    public required CallRecord Call { get; init; }
    public required AttemptRecord Attempt { get; init; }
    public required IActiveCall Line { get; init; }
    public required PolicyFile Policy { get; init; }
    public required CallStore Store { get; init; }
    public required ClipStore Clips { get; init; }
    public required ReplyListener Listener { get; init; }
    public required CancellationToken Ct { get; init; }

    public void Update(Action<CallRecord, AttemptRecord> change) => Store.Update(Call, c => change(c, Attempt));

    public Clip? Clip(string? id) => id is null ? null : Clips.Load(id);

    public Task<PlayResult> PlayAsync(Clip clip) => Line.PlayAsync(clip, Ct);

    public async Task HangupAsync()
    {
        if (Line.IsUp)
            await Line.HangupAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// An answered alarm (PR-ALARM-1 to PR-ALARM-3). With <c>ack: voice</c> the prompt plays at most
/// three times; "I'm up" ends it as awake, a snooze phrase ends it for a call-back, and anything
/// else is not acknowledged. With <c>ack: none</c> the wake-up message plays twice and the result
/// is <c>played</c>, never awake.
/// </summary>
internal static class AlarmFlow
{
    public const int MaxPromptPlays = 3;

    public static async Task<AttemptResult> RunAsync(AttemptContext ctx)
    {
        var options = ctx.Call.Options;
        var prompt = ctx.Clip(ctx.Call.Clips.Prompt);
        if (prompt is null)
        {
            // PR-ALARM-6: an unreadable stored clip still wakes the callee, and the record says so.
            prompt = BuiltinClips.WakeTone();
            ctx.Update((c, _) => c.Notes.Add("the stored wake-up clip could not be read; the built-in wake tone played"));
        }

        if (!options.VoiceAck)
        {
            var first = await ctx.PlayAsync(prompt).ConfigureAwait(false);
            ctx.Update((_, a) => a.PromptPlays++);
            if (first.End == PlayEnd.Cancelled)
                return AttemptResult.Cancelled;
            // Hung up before the message finished once: answered, but nothing was delivered.
            if (first.End == PlayEnd.HungUp)
                return AttemptResult.NotAcknowledged;
            var second = await ctx.PlayAsync(prompt).ConfigureAwait(false);
            if (second.End == PlayEnd.Completed)
                ctx.Update((_, a) => a.PromptPlays++);
            await ctx.HangupAsync().ConfigureAwait(false);
            return AttemptResult.Played;
        }

        for (int plays = 0; plays < MaxPromptPlays; plays++)
        {
            var played = await ctx.PlayAsync(prompt).ConfigureAwait(false);
            ctx.Update((_, a) => a.PromptPlays++);
            if (played.End == PlayEnd.Cancelled)
                return AttemptResult.Cancelled;
            if (played.End == PlayEnd.HungUp)
                return AttemptResult.NotAcknowledged;

            var reply = await ctx.Listener.ListenAsync(ctx.Line, alarm: true, ctx.Policy.Phrases, ctx.Ct).ConfigureAwait(false);
            if (reply.Cancelled)
                return AttemptResult.Cancelled;

            if (reply.Intent == ReplyIntent.Awake)
            {
                ctx.Update((c, a) => { a.Replies.Add(reply.Record); c.AckSource = reply.Source; });
                if (ctx.Clip(ctx.Call.Clips.Closing) is { } closing && ctx.Line.IsUp)
                    await ctx.PlayAsync(closing).ConfigureAwait(false);
                await ctx.HangupAsync().ConfigureAwait(false);
                return AttemptResult.Awake;
            }

            if (reply.Intent == ReplyIntent.Snooze)
            {
                if (ctx.Call.Snoozes < options.MaxSnoozes)
                {
                    ctx.Update((c, a) => { a.Replies.Add(reply.Record); c.Snoozes++; });
                    if (ctx.Clip(ctx.Call.Clips.SnoozeAck) is { } ack && ctx.Line.IsUp)
                        await ctx.PlayAsync(ack).ConfigureAwait(false);
                    await ctx.HangupAsync().ConfigureAwait(false);
                    return AttemptResult.Snoozed;
                }
                // Past the snooze limit a snooze phrase is an unrecognized reply (PR-ALARM-3).
                reply.Record.Intent = "unrecognized";
                reply.Record.Transcript += " (snooze limit reached)";
            }

            ctx.Update((_, a) => a.Replies.Add(reply.Record));
            if (reply.HungUp || !ctx.Line.IsUp)
                return AttemptResult.NotAcknowledged;
        }

        await ctx.HangupAsync().ConfigureAwait(false);
        return AttemptResult.NotAcknowledged;
    }
}

/// <summary>
/// An answered message (PR-MSG-1 to PR-MSG-3): a pause, the disclosure for contacts that are not
/// <c>self</c> (PR-SAFE-6), then the message. A hang-up before the message starts is
/// <c>hung_up_early</c>; a hang-up during it is a cut-off <c>played_unconfirmed</c>. Neither is
/// redialed.
/// </summary>
internal static class MessageFlow
{
    public const int MaxReplays = 3;
    public static readonly TimeSpan AnswerPause = TimeSpan.FromSeconds(1);

    /// <summary>Played less than this before a hang-up: the message had not really started.</summary>
    private static readonly TimeSpan StartedThreshold = TimeSpan.FromMilliseconds(100);

    public static async Task<AttemptResult> RunAsync(AttemptContext ctx)
    {
        var options = ctx.Call.Options;
        var message = ctx.Clip(ctx.Call.Clips.Message)!;

        if (!await ctx.Line.PauseAsync(AnswerPause, ctx.Ct).ConfigureAwait(false))
            return ctx.Ct.IsCancellationRequested ? AttemptResult.Cancelled : AttemptResult.HungUpEarly;

        if (!ctx.Call.Self)
        {
            var disclosure = await ctx.PlayAsync(ctx.Clip(ctx.Call.Clips.Disclosure)!).ConfigureAwait(false);
            if (disclosure.End == PlayEnd.Cancelled)
                return AttemptResult.Cancelled;
            if (disclosure.End == PlayEnd.HungUp)
                return AttemptResult.HungUpEarly;
        }

        var first = await ctx.PlayAsync(message).ConfigureAwait(false);
        switch (first.End)
        {
            case PlayEnd.Cancelled:
                return AttemptResult.Cancelled;
            case PlayEnd.HungUp when first.Played < StartedThreshold:
                return AttemptResult.HungUpEarly;
            case PlayEnd.HungUp:
                ctx.Update((_, a) => a.CutOff = true);
                return AttemptResult.PlayedUnconfirmed;
        }
        ctx.Update((_, a) => a.MessagePlays++);

        if (!options.VoiceAck)
        {
            var again = await ctx.PlayAsync(message).ConfigureAwait(false);
            if (again.End == PlayEnd.Completed)
                ctx.Update((_, a) => a.MessagePlays++);
            await ctx.HangupAsync().ConfigureAwait(false);
            return AttemptResult.Played;
        }

        var confirmPrompt = ctx.Clip(ctx.Call.Clips.ConfirmPrompt)!;
        int replays = 0, unanswered = 0;
        while (true)
        {
            var asked = await ctx.PlayAsync(confirmPrompt).ConfigureAwait(false);
            ctx.Update((_, a) => a.PromptPlays++);
            if (asked.End == PlayEnd.Cancelled)
                return AttemptResult.Cancelled;
            if (asked.End == PlayEnd.HungUp)
                return AttemptResult.PlayedUnconfirmed;

            var reply = await ctx.Listener.ListenAsync(ctx.Line, alarm: false, ctx.Policy.Phrases, ctx.Ct).ConfigureAwait(false);
            if (reply.Cancelled)
                return AttemptResult.Cancelled;

            if (reply.Intent == ReplyIntent.Confirm)
            {
                ctx.Update((c, a) => { a.Replies.Add(reply.Record); c.AckSource = reply.Source; });
                if (ctx.Clip(ctx.Call.Clips.Closing) is { } closing && ctx.Line.IsUp)
                    await ctx.PlayAsync(closing).ConfigureAwait(false);
                await ctx.HangupAsync().ConfigureAwait(false);
                return AttemptResult.Confirmed;
            }

            if (reply.Intent == ReplyIntent.Repeat && replays < MaxReplays)
            {
                ctx.Update((_, a) => a.Replies.Add(reply.Record));
                replays++;
                unanswered = 0;
                var replay = await ctx.PlayAsync(message).ConfigureAwait(false);
                if (replay.End == PlayEnd.Cancelled)
                    return AttemptResult.Cancelled;
                if (replay.End == PlayEnd.HungUp)
                    return AttemptResult.PlayedUnconfirmed;
                ctx.Update((_, a) => a.MessagePlays++);
                continue;
            }

            if (reply.Intent == ReplyIntent.Repeat)
            {
                reply.Record.Intent = "unrecognized";
                reply.Record.Transcript += " (replay limit reached)";
            }
            ctx.Update((_, a) => a.Replies.Add(reply.Record));
            // No recognized reply: ask once more, then stop (possibly voicemail). Never redialed.
            if (reply.HungUp || !ctx.Line.IsUp || ++unanswered >= 2)
            {
                await ctx.HangupAsync().ConfigureAwait(false);
                return AttemptResult.PlayedUnconfirmed;
            }
        }
    }
}
