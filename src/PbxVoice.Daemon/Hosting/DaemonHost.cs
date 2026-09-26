using System.Reflection;
using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Control;
using PbxVoice.Policy;
using PbxVoice.Scheduling;
using PbxVoice.Service;
using PbxVoice.Sip;
using PbxVoice.Speech;
using PbxVoice.Xai;
using Serilog;

namespace PbxVoice.Hosting;

/// <summary>
/// The long-lived daemon: one SIP registration, the scheduler, the executor, the pre-flight
/// check, record retention, and the control socket. The daemon, not an LLM, fires every call
/// (PR-SCHED-2).
/// </summary>
internal sealed class DaemonHost
{
    public static readonly TimeSpan PreflightLead = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    public static string Version =>
        typeof(DaemonHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static async Task<int> RunAsync(CancellationToken stop)
    {
        Func<string, string?> env = Environment.GetEnvironmentVariable;
        var paths = StatePaths.FromEnvironment();
        paths.EnsureCreated();
        if (!File.Exists(paths.Policy))
        {
            Log.Error("No policy file at {Path}. Copy docs/policy.example.json there and edit it.", paths.Policy);
            return 2;
        }

        PolicyProvider policy;
        Secrets secrets;
        SipBot.SipConfig sipConfig;
        try
        {
            policy = new PolicyProvider(paths.Policy);
            secrets = Secrets.Load(paths.Secrets, env);
            sipConfig = secrets.ToSipConfig();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Log.Error("{Error}", ex.Message);
            return 2;
        }
        if (secrets.FileWarning is { } warning)
            Log.Warning("{Warning}", warning);

        using var aorLock = AorLock.TryAcquire(StatePaths.LocksDirectory(env), $"{sipConfig.Username}@{sipConfig.Server}");
        if (aorLock is null)
        {
            Log.Error("Another pbx-voice daemon is already running for {User}@{Server}", sipConfig.Username, sipConfig.Server);
            return 3;
        }

        ITextToSpeech tts;
        ISpeechToText stt;
        XaiSpeech? xai = null;
        if (secrets.HasXaiKey)
        {
            xai = new XaiSpeech(secrets["XAI_API_KEY"]!);
            tts = xai;
            stt = xai;
        }
        else
        {
            Log.Warning("No XAI_API_KEY: alarms use the built-in wake tone and replies fall back to speech detection; messages cannot be scheduled");
            var none = new UnavailableSpeech();
            tts = none;
            stt = none;
        }

        // Silero VAD finds the callee's reply. There is no fallback detector: a host that cannot
        // load ONNX Runtime cannot run the daemon (`pbx-voice selftest` checks this).
        SileroClassifier vad;
        try
        {
            vad = SileroClassifier.Load();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cannot load the Silero voice-activity model (ONNX Runtime); run `pbx-voice selftest`");
            return 4;
        }
        using var vadLifetime = vad;
        Log.Information("Silero VAD loaded in {Ms} ms", (int)vad.LoadTime.TotalMilliseconds);

        var time = TimeProvider.System;
        var clips = new ClipStore(paths.Clips, tts);
        var calls = new CallStore(paths.Calls);
        var schedules = new ScheduleStore(paths.Schedules);
        using var phone = new SipPhoneLine(sipConfig, secrets.LocalSipPort, policy.Current.Register,
            () => policy.Current.InboundRejectStatus, vad, time);
        var executor = new Executor(time, phone, policy, calls, clips, new ReplyListener(stt, time));
        executor.Recover();

        (DateTimeOffset At, int? Status)? lastPing = null;
        var host = new HostStatus
        {
            Version = Version,
            StateDirectory = paths.Root,
            XaiKeyPresent = secrets.HasXaiKey,
            LastPing = () => lastPing,
        };
        var service = new PbxVoiceService(time, policy, schedules, calls, clips, executor, phone, host);
        await using var control = new ControlServer(paths.ControlSocket, service.HandleAsync);
        control.Start();

        Log.Information("pbx-voice {Version} daemon for {User}@{Server}; state in {State}; register={Register}",
            Version, sipConfig.Username, sipConfig.Server, paths.Root, policy.Current.Register);

        var nextPing = time.GetUtcNow();
        var nextPrune = time.GetUtcNow();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                FireDueSchedules(schedules, executor, now);

                if (now >= nextPrune)
                {
                    int removed = calls.Prune(now - TimeSpan.FromDays(policy.Current.RetentionDays));
                    if (removed > 0)
                        Log.Information("Removed {Count} call records past retention", removed);
                    nextPrune = now + TimeSpan.FromHours(6);
                }
                if (now >= nextPing)
                {
                    lastPing = (now, await phone.PingServerAsync(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false));
                    nextPing = now + PingInterval;
                }
                await PreflightAsync(schedules, phone, time, stop, p => lastPing = p).ConfigureAwait(false);

                if (!await executor.RunDueAsync(stop).ConfigureAwait(false))
                    await Task.Delay(Tick, time, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            xai?.Dispose();
            Log.Information("pbx-voice daemon stopping");
        }
        return 0;
    }

    internal static void FireDueSchedules(ScheduleStore schedules, Executor executor, DateTimeOffset now)
    {
        List<(Schedule Schedule, DateTimeOffset Fire)> due;
        lock (schedules.Sync)
            due = schedules.CollectDue(now);
        foreach (var (s, fire) in due)
        {
            var record = executor.Create(s.Id, s.Type, s.ToTarget(), s.Options, s.Clips, s.Text, fire, s.Notes);
            Log.Information("Schedule {ScheduleId} fired for {Fire:O}: call {CallId}", s.Id, fire, record.CallId);
        }
    }

    /// <summary>
    /// PR-ALARM-7: about ten minutes before an alarm fires, check PBX reachability (SIP OPTIONS) and
    /// registration. After a 402, 403, or 404, re-register once (PR-REG-9) and record the result.
    /// A rejected password (401/407) is recorded and not retried. Each fire is checked once.
    /// </summary>
    internal static async Task PreflightAsync(ScheduleStore schedules, IPhoneLine phone, TimeProvider time,
        CancellationToken stop, Action<(DateTimeOffset, int?)>? onPing = null)
    {
        var now = time.GetUtcNow();
        List<Schedule> due;
        lock (schedules.Sync)
        {
            due = schedules.All.Where(s => s.Status == "active" && s.Type == CallType.Alarm
                                           && s.NextFire is { } f && now >= f - PreflightLead && now < f
                                           && s.Preflight?.ForFire != f).ToList();
        }
        foreach (var s in due)
        {
            var record = new PreflightRecord { At = now, ForFire = s.NextFire!.Value };
            record.OptionsStatus = await phone.PingServerAsync(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
            record.PbxReachable = record.OptionsStatus is not null;
            onPing?.Invoke((now, record.OptionsStatus));
            var reg = phone.Registration;
            if (reg.HardFailure)
            {
                record.Reregister = new ReregisterRecord { At = now, Before = reg.LastError ?? reg.State };
                if (reg.ErrorStatus is 402 or 403 or 404)
                {
                    record.Reregister.Attempted = true;
                    phone.Reregister();
                    record.Reregister.Registered = await phone.WaitForRegistrationAsync(Executor.ReregisterWait, stop).ConfigureAwait(false);
                }
                reg = phone.Registration;
            }
            record.RegistrationState = reg.State;
            record.RegistrationError = reg.LastError;
            lock (schedules.Sync)
            {
                s.Preflight = record;
                schedules.Save();
            }
            if (!record.PbxReachable || !reg.Registered)
                Log.Warning("Pre-flight for alarm {ScheduleId} at {Fire:O}: PBX reachable={Reachable}, registration={State} {Error}",
                    s.Id, s.NextFire, record.PbxReachable, reg.State, reg.LastError);
        }
    }
}
