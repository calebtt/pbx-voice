using System.Diagnostics;
using System.Text.Json;
using PbxVoice.Calls;
using PbxVoice.Speech;
using Serilog;

namespace PbxVoice.Conversation;

internal sealed record ConversationSettings(
    IVoiceSessionFactory? Sessions,
    TimeProvider Time,
    string Model,
    string ReasoningEffort,
    string Voice,
    string Language,
    string DisplayName)
{
    /// <summary>PR-CONV-3: the session must open within 3 s of the attempt starting.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>After end_call, how long to let the last words play before hanging up.</summary>
    public TimeSpan EndDrain { get; init; } = TimeSpan.FromSeconds(6);
}

/// <summary>
/// An answered conversation call (PR-CONV-2 to PR-CONV-8). The daemon plays the disclosure, opens
/// the Grok Voice session, speaks the message verbatim, bridges audio both ways, runs the model's
/// tools, enforces the time limit, and computes the outcome from the evidence.
/// </summary>
internal static class ConversationFlow
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static async Task<AttemptResult> RunAsync(AttemptContext ctx, ConversationSettings settings)
    {
        var call = ctx.Call;
        var brief = call.Conversation!.Brief;
        var line = ctx.Line;
        long answeredAt = Clock.ElapsedMilliseconds;
        long answeredTicks = settings.Time.GetTimestamp();

        // 1. Open the session while the disclosure plays (PR-CONV-3, PR-SAFE-6).
        IVoiceSession? session = settings.Sessions?.Create();
        var config = new VoiceSessionConfig(
            Instructions.Build(brief, call.Contact, call.Self, settings.DisplayName, brief.MaxMinutes),
            settings.Voice, settings.Model, settings.ReasoningEffort, settings.Language, Instructions.Tools(brief));
        using var openTimeout = new CancellationTokenSource(settings.OpenTimeout, settings.Time);
        using var openCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct, openTimeout.Token);
        Task<bool> opening = OpenAsync(session, config, openCts.Token, () =>
            ctx.Update((c, _) => c.Conversation!.SessionOpenMs = (int)(Clock.ElapsedMilliseconds - answeredAt)));

        if (!call.Self)
        {
            var disclosure = await ctx.PlayAsync(ctx.Clip(call.Clips.Disclosure)!).ConfigureAwait(false);
            if (disclosure.End != PlayEnd.Completed)
            {
                openCts.Cancel();
                await opening.ConfigureAwait(false);
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
                return disclosure.End == PlayEnd.Cancelled ? AttemptResult.Cancelled : AttemptResult.HungUpEarly;
            }
        }

        bool open = await opening.ConfigureAwait(false);
        if (!open || session is null)
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
            if (ctx.Ct.IsCancellationRequested)
                return AttemptResult.Cancelled;
            ctx.Update((c, _) => c.Notes.Add("the voice session did not open within 3 s"));
            return await FallbackAsync(ctx).ConfigureAwait(false);
        }

        await using (session.ConfigureAwait(false))
            return await ConverseAsync(ctx, settings, session, answeredAt, answeredTicks).ConfigureAwait(false);
    }

    private static async Task<bool> OpenAsync(IVoiceSession? session, VoiceSessionConfig config, CancellationToken ct, Action onOpen)
    {
        if (session is null)
            return false;
        try
        {
            await session.ConnectAsync(config, ct).ConfigureAwait(false);
            onOpen();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("Voice session did not open: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// PR-CONV-3: with a message, fall back to the message flow (outcome degraded_to_message);
    /// with questions only, play the apology and end (failed).
    /// </summary>
    private static async Task<AttemptResult> FallbackAsync(AttemptContext ctx)
    {
        var call = ctx.Call;
        var conv = call.Conversation!;
        if (conv.Brief.Message is null)
        {
            if (ctx.Clip(call.Clips.Apology) is { } apology)
                await ctx.PlayAsync(apology).ConfigureAwait(false);
            await ctx.HangupAsync().ConfigureAwait(false);
            ctx.Update((c, _) => { c.Conversation!.Outcome = Outcome.Failed; c.Conversation.OutcomeReason = "voice session unavailable; apology played"; c.Conversation.EndedBy = "session_failed"; });
            return AttemptResult.Conversed;
        }

        var played = await ctx.PlayAsync(ctx.Clip(call.Clips.Message)!).ConfigureAwait(false);
        if (played.End == PlayEnd.Cancelled)
            return AttemptResult.Cancelled;
        if (played.End == PlayEnd.HungUp && played.Played < TimeSpan.FromMilliseconds(100))
            return AttemptResult.HungUpEarly;
        bool delivered = played.End == PlayEnd.Completed;
        bool confirmed = false;
        if (delivered && ctx.Clip(call.Clips.ConfirmPrompt) is { } prompt)
        {
            for (int ask = 0; ask < 2 && !confirmed && ctx.Line.IsUp; ask++)
            {
                if ((await ctx.PlayAsync(prompt).ConfigureAwait(false)).End != PlayEnd.Completed)
                    break;
                var reply = await ctx.Listener.ListenAsync(ctx.Line, alarm: false, ctx.Policy.Phrases, ctx.Ct).ConfigureAwait(false);
                ctx.Update((_, a) => a.Replies.Add(reply.Record));
                confirmed = reply.Intent == ReplyIntent.Confirm;
                if (reply.Intent == ReplyIntent.Repeat && (await ctx.PlayAsync(ctx.Clip(call.Clips.Message)!).ConfigureAwait(false)).End != PlayEnd.Completed)
                    break;
            }
        }
        if (confirmed && ctx.Clip(call.Clips.Closing) is { } closing && ctx.Line.IsUp)
            await ctx.PlayAsync(closing).ConfigureAwait(false);
        await ctx.HangupAsync().ConfigureAwait(false);
        ctx.Update((c, _) =>
        {
            c.Conversation!.MessageDelivered = delivered;
            c.Conversation.MessageConfirmed = confirmed;
            c.Conversation.Outcome = Outcome.DegradedToMessage;
            c.Conversation.OutcomeReason = confirmed ? "message confirmed" : delivered ? "message played, not confirmed" : "message cut off";
            c.Conversation.EndedBy = "session_failed";
        });
        return AttemptResult.Conversed;
    }

    private sealed class State
    {
        public int Seq;
        public bool Spoken;
        public bool MessagePending;
        public bool AwaitingForcedStart;
        public string? ForcedResponseId;
        public string? CurrentResponse;
        public readonly HashSet<string> Dropped = new();
        public readonly Dictionary<string, TurnRecord> CalleeTurns = new();
        public readonly Dictionary<string, TurnRecord> AssistantTurns = new();
        public bool EndRequested;
        public long EndRequestedAt;
        public string? EndedBy;
        public bool HardStopped;
    }

    private static async Task<AttemptResult> ConverseAsync(AttemptContext ctx, ConversationSettings settings, IVoiceSession session, long answeredAt, long answeredTicks)
    {
        var call = ctx.Call;
        var brief = call.Conversation!.Brief;
        var line = ctx.Line;
        var s = new State { MessagePending = brief.Message is not null, AwaitingForcedStart = brief.Message is not null };

        line.StartStreaming(pcm => _ = session.AppendAudioAsync(pcm));
        if (brief.Message is { } message)
            await session.ForceMessageAsync(message, interruptible: false).ConfigureAwait(false);
        else
            await session.CreateResponseAsync(Instructions.Opening).ConfigureAwait(false);

        // The limit counts from answer, so the disclosure and session setup are inside it.
        var remaining = TimeSpan.FromMinutes(brief.MaxMinutes) - settings.Time.GetElapsedTime(answeredTicks);
        var hardStop = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, settings.Time, CancellationToken.None);
        bool sessionOpen = true;
        try
        {
            while (true)
            {
                if (ctx.Ct.IsCancellationRequested)
                    return AttemptResult.Cancelled;
                if (!line.IsUp)
                {
                    s.EndedBy ??= "callee_hung_up";
                    break;
                }
                if (s.EndRequested)
                {
                    bool drained = !line.PlaybackPending && Clock.ElapsedMilliseconds - s.EndRequestedAt > 300;
                    if (drained || Clock.ElapsedMilliseconds - s.EndRequestedAt > settings.EndDrain.TotalMilliseconds)
                        break;
                }
                if (hardStop.IsCompleted && !s.HardStopped)
                {
                    // PR-CONV-2: the daemon ends the call at the limit whatever the model does.
                    s.HardStopped = true;
                    s.EndedBy = "max_minutes";
                    line.ClearPlayback();
                    if (s.CurrentResponse is { } current)
                        s.Dropped.Add(current);
                    if (sessionOpen)
                        await session.ForceMessageAsync($"I'm sorry, I have to go now. {settings.DisplayName} will follow up. Goodbye.", interruptible: false).ConfigureAwait(false);
                    s.EndRequested = true;
                    s.EndRequestedAt = Clock.ElapsedMilliseconds + 2000; // give the closing line time to start
                }

                if (sessionOpen)
                {
                    var readable = session.Events.WaitToReadAsync(ctx.Ct).AsTask();
                    await Task.WhenAny(readable, line.Ended, hardStop, Task.Delay(100)).ConfigureAwait(false);
                    while (session.Events.TryRead(out var ev))
                        await HandleAsync(ctx, session, s, ev, answeredAt, settings).ConfigureAwait(false);
                    if (readable.IsCompletedSuccessfully && !readable.Result)
                    {
                        sessionOpen = false;
                        if (!s.EndRequested)
                        {
                            s.EndedBy = "session_ended";
                            if (ctx.Clip(call.Clips.Closing) is { } closing && line.IsUp)
                                await ctx.PlayAsync(closing).ConfigureAwait(false);
                            break;
                        }
                    }
                }
                else
                {
                    await Task.WhenAny(line.Ended, Task.Delay(100)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            line.StopStreaming();
        }

        if (line.IsUp)
            await line.HangupAsync().ConfigureAwait(false);

        if (!s.Spoken && s.EndedBy == "callee_hung_up")
            return AttemptResult.HungUpEarly; // PR-CONV-8: before the session has spoken

        ctx.Update((c, _) =>
        {
            var conv = c.Conversation!;
            // Responses that only called a tool have no words.
            conv.Transcript.RemoveAll(t => t.Role == "assistant" && t.Text.Length == 0);
            conv.EndedBy = s.EndedBy;
            var (outcome, reason) = Evidence.Evaluate(conv);
            conv.Outcome = outcome;
            conv.OutcomeReason = reason ?? (s.EndedBy is "max_minutes" or "session_ended" ? s.EndedBy : null);
        });
        return AttemptResult.Conversed;
    }

    private static async Task HandleAsync(AttemptContext ctx, IVoiceSession session, State s, VoiceEvent ev, long answeredAt, ConversationSettings settings)
    {
        var brief = ctx.Call.Conversation!.Brief;
        int Offset() => (int)(Clock.ElapsedMilliseconds - answeredAt);
        switch (ev)
        {
            case ResponseStarted r:
                s.CurrentResponse = r.ResponseId;
                if (s.AwaitingForcedStart)
                {
                    s.AwaitingForcedStart = false;
                    s.ForcedResponseId = r.ResponseId;
                    var scripted = new TurnRecord { Seq = ++s.Seq, Role = "assistant", Text = brief.Message!, OffsetMs = Offset(), Scripted = true };
                    ctx.Update((c, _) => c.Conversation!.Transcript.Add(scripted));
                }
                else if (!s.Dropped.Contains(r.ResponseId))
                {
                    // Placed in order now; the text arrives with the response's transcript.
                    var turn = new TurnRecord { Seq = ++s.Seq, Role = "assistant", OffsetMs = Offset(), ItemId = r.ResponseId };
                    s.AssistantTurns[r.ResponseId] = turn;
                    ctx.Update((c, _) => c.Conversation!.Transcript.Add(turn));
                }
                break;

            case AudioDelta a:
                if (s.Dropped.Contains(a.ResponseId))
                    break;
                ctx.Line.EnqueueAudio(a.Pcm);
                s.Spoken = true;
                break;

            case AssistantTranscript t:
                if (s.ForcedResponseId is not null && t.Text.Trim() == brief.Message?.Trim())
                    break; // the verbatim message is already in the transcript
                if (s.AssistantTurns.TryGetValue(t.ResponseId.Length > 0 ? t.ResponseId : s.CurrentResponse ?? "", out var spoken))
                    ctx.Update((_, _) => spoken.Text = t.Text);
                break;

            case ResponseDone d:
                if (d.ResponseId == s.ForcedResponseId && s.MessagePending)
                {
                    s.MessagePending = false;
                    bool delivered = d.Status is "" or "completed";
                    ctx.Update((c, _) => c.Conversation!.MessageDelivered = delivered);
                    if (!s.EndRequested)
                        await session.CreateResponseAsync(Instructions.AfterMessage(brief)).ConfigureAwait(false);
                }
                break;

            case CalleeSpeechStarted sp:
                if (!s.CalleeTurns.ContainsKey(sp.ItemId))
                {
                    var turn = new TurnRecord { Seq = ++s.Seq, Role = "callee", OffsetMs = Offset(), ItemId = sp.ItemId };
                    s.CalleeTurns[sp.ItemId] = turn;
                    ctx.Update((c, _) => c.Conversation!.Transcript.Add(turn));
                }
                // Local barge-in (plan): the verbatim message is not interruptible; anything else is.
                if (!s.MessagePending && !s.HardStopped && s.CurrentResponse is { } speaking && ctx.Line.PlaybackPending)
                {
                    ctx.Line.ClearPlayback();
                    s.Dropped.Add(speaking);
                    await session.CancelResponseAsync().ConfigureAwait(false);
                }
                break;

            case CalleeTranscript ct:
                if (!s.CalleeTurns.TryGetValue(ct.ItemId, out var calleeTurn))
                {
                    calleeTurn = new TurnRecord { Seq = ++s.Seq, Role = "callee", OffsetMs = Offset(), ItemId = ct.ItemId };
                    s.CalleeTurns[ct.ItemId] = calleeTurn;
                    ctx.Update((c, _) => c.Conversation!.Transcript.Add(calleeTurn));
                }
                ctx.Update((_, _) => calleeTurn.Text = ct.Text);
                break;

            case FunctionCall f:
                string output = Dispatch(ctx, s, f);
                await session.SendFunctionOutputAsync(f.CallId, output).ConfigureAwait(false);
                if (!s.EndRequested)
                    await session.CreateResponseAsync().ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The model's tools (PR-CONV-4): they only record, or end the call.</summary>
    private static string Dispatch(AttemptContext ctx, State s, FunctionCall f)
    {
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(f.ArgumentsJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return """{"ok":false,"error":"arguments are not JSON"}""";
        }
        string Arg(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var brief = ctx.Call.Conversation!.Brief;

        switch (f.Name)
        {
            case "record_answer":
                string name = Arg("name");
                if (brief.Ask.All(a => a.Name != name))
                    return JsonSerializer.Serialize(new { ok = false, error = $"unknown question name '{name}'" });
                var answer = new AnswerRecord { Value = Arg("value"), Quote = Arg("quote"), RecordedSeq = s.Seq };
                ctx.Update((c, _) => c.Conversation!.Answers[name] = answer);
                return """{"ok":true}""";

            case "record_question":
                bool answered = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("answered", out var ans) && ans.ValueKind == JsonValueKind.True;
                var question = new CalleeQuestion { Text = Arg("text"), Answered = answered };
                ctx.Update((c, _) => c.Conversation!.CalleeQuestions.Add(question));
                return """{"ok":true}""";

            case "confirm_received":
                ctx.Update((c, _) => c.Conversation!.ModelReportedConfirmation = true);
                return """{"ok":true}""";

            case "end_call":
                string reason = Arg("reason") is { Length: > 0 } r ? r : "done";
                var claim = new EndClaim { Reason = reason, Quote = Arg("quote") is { Length: > 0 } q ? q : null };
                ctx.Update((c, _) => c.Conversation!.EndClaim = claim);
                if (!s.EndRequested)
                {
                    s.EndRequested = true;
                    s.EndRequestedAt = Clock.ElapsedMilliseconds;
                    s.EndedBy = "end_call";
                }
                return """{"ok":true,"note":"the call will end after your last words"}""";

            default:
                return JsonSerializer.Serialize(new { ok = false, error = $"unknown tool '{f.Name}'" });
        }
    }
}
