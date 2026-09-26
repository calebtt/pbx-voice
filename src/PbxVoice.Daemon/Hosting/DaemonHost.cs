using System.Reflection;
using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Control;
using PbxVoice.Conversation;
using PbxVoice.Mcp;
using PbxVoice.Policy;
using PbxVoice.Service;
using PbxVoice.Sip;
using PbxVoice.Speech;
using PbxVoice.Xai;
using Serilog;

namespace PbxVoice.Hosting;

/// <summary>
/// The daemon: one SIP registration, the executor, record retention, the control socket, and the
/// MCP front. It places calls when asked (<c>call_now</c>); when to call is up to the agent's own
/// scheduler.
/// </summary>
internal sealed class DaemonHost
{
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
            Log.Warning("No XAI_API_KEY: alarms use the built-in wake tone and replies fall back to speech detection; messages and conversations are refused");
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
        using var phone = new SipPhoneLine(sipConfig, secrets.LocalSipPort, policy.Current.Register,
            () => policy.Current.InboundRejectStatus, vad, time);
        IVoiceSessionFactory? sessions = secrets.HasXaiKey ? new XaiVoiceSessionFactory(secrets["XAI_API_KEY"]!) : null;
        var prompts = new PromptProvider(paths.ConversationPrompt);
        if (prompts.LastError is { } promptError)
            Log.Warning("The conversation prompt file has an error ({Error}); conversation calls use the built-in prompt until it is fixed", promptError);
        var executor = new Executor(time, phone, policy, calls, clips, new ReplyListener(stt, time), sessions, prompts);
        executor.Recover();
        string oldSchedules = Path.Combine(paths.Root, "schedules.json");
        if (File.Exists(oldSchedules))
            Log.Warning("{File} is no longer used: pbx-voice has no scheduler, so the agent's own scheduler calls call_now at the right time. Its schedules will not fire", oldSchedules);

        (DateTimeOffset At, int? Status)? lastPing = null;
        var host = new HostStatus
        {
            Version = Version,
            StateDirectory = paths.Root,
            XaiKeyPresent = secrets.HasXaiKey,
            LastPing = () => lastPing,
        };
        var service = new PbxVoiceService(time, policy, calls, clips, executor, phone, host);
        await using var control = new ControlServer(paths.ControlSocket, service.HandleAsync);
        try
        {
            control.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Net.Sockets.SocketException or IOException)
        {
            Log.Error("Cannot open the control socket {Path}: {Error}", paths.ControlSocket, ex.Message);
            return 5;
        }

        // The agent's interface (PR-API-1, PR-SAFE-8).
        McpHttpFront? mcp = null;
        string listen = Environment.GetEnvironmentVariable("PBX_VOICE_MCP_LISTEN") is { Length: > 0 } l ? l : McpHttpFront.DefaultListen;
        if (!listen.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            System.Net.IPEndPoint endpoint;
            try
            {
                endpoint = McpHttpFront.ParseListen(listen);
            }
            catch (FormatException ex)
            {
                Log.Error("{Error}", ex.Message);
                return 2;
            }
            if (!System.Net.IPAddress.IsLoopback(endpoint.Address))
                Log.Warning("MCP listens on {Address}, which is not loopback: put it behind an HTTPS reverse proxy (deployment mode B)", endpoint);
            string token = McpHttpFront.LoadOrCreateToken(paths.McpToken);
            var tools = new PbxVoiceTools(new ServiceBackend(service), new PlacementRateLimiter(time));
            try
            {
                mcp = await McpHttpFront.StartAsync(endpoint, token, tools, stop).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // Kestrel reports a taken port as an IOException wrapping AddressInUseException.
                Log.Error("Cannot listen for MCP on {Address}: {Error}. Stop whatever is using it, or set PBX_VOICE_MCP_LISTEN to another host:port (or off)",
                    endpoint, ex.InnerException?.Message ?? ex.Message);
                return 5;
            }
            Log.Information("MCP front on {Endpoint} (bearer token in {TokenFile})", mcp.Endpoint, paths.McpToken);
        }
        await using var mcpLifetime = mcp;

        Log.Information("pbx-voice {Version} daemon for {User}@{Server}; state in {State}; register={Register}",
            Version, sipConfig.Username, sipConfig.Server, paths.Root, policy.Current.Register);

        var nextPing = time.GetUtcNow();
        var nextPrune = time.GetUtcNow();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
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
}
