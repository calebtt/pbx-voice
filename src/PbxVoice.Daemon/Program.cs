using System.Net.Sockets;
using System.Text.Json;
using PbxVoice;
using PbxVoice.Control;
using PbxVoice.Hosting;
using PbxVoice.Service;
using Serilog;
using Serilog.Events;

const string Usage = """
    pbx-voice: scheduled phone calls over your own SIP extension

    Usage:
      pbx-voice daemon                    run the daemon (foreground; logs to stderr)
      pbx-voice ctl <operation> [json]    call an operation on the running daemon
      pbx-voice paths                     show where state, policy, and secrets live
      pbx-voice selftest                  check that this host can run the daemon (ONNX Runtime, Silero VAD)
      pbx-voice version

    Operations: schedule_call, call_now, wait_for_call, list_schedules, cancel_schedule,
    cancel_call, list_calls, get_call, list_contacts, status

    Examples:
      pbx-voice ctl status
      pbx-voice ctl schedule_call '{"type":"alarm","to":"me","repeat":{"days":"weekdays","time":"05:30","tz":"America/Chicago"}}'
      pbx-voice ctl call_now '{"type":"message","to":"mom","text":"My flight lands at 3:40."}'
      pbx-voice ctl wait_for_call '{"call_id":"c_...","timeout_sec":300}'
    """;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("SIPSorcery", LogEventLevel.Warning)
    // MinimalSileroVad tries CUDA first and logs the expected failure with a stack trace; the
    // daemon ships the CPU build of ONNX Runtime, so that attempt always fails.
    .Filter.ByExcluding(e => e.MessageTemplate.Text.StartsWith("CUDA execution provider unavailable", StringComparison.Ordinal))
    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
    .CreateLogger();

try
{
    switch (args.FirstOrDefault())
    {
        case "daemon":
        {
            // Not disposed: signal handlers can still run while the process is exiting.
            var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });
            return await DaemonHost.RunAsync(stop.Token);
        }
        case "ctl":
            return await CtlAsync(args.Skip(1).ToArray());
        case "paths":
        {
            var paths = StatePaths.FromEnvironment();
            Console.WriteLine($"state directory: {paths.Root}");
            Console.WriteLine($"policy:          {paths.Policy}");
            Console.WriteLine($"secrets:         {paths.Secrets} (optional; the environment wins)");
            Console.WriteLine($"control socket:  {paths.ControlSocket}");
            Console.WriteLine($"locks:           {StatePaths.LocksDirectory(Environment.GetEnvironmentVariable)}");
            return 0;
        }
        case "selftest":
            return SelfTest.Run(Console.Out);
        case "version" or "--version":
            Console.WriteLine(DaemonHost.Version);
            return 0;
        default:
            Console.Error.WriteLine(Usage);
            return args.Length == 0 || args[0] is "help" or "--help" or "-h" ? 0 : 1;
    }
}
finally
{
    Log.CloseAndFlush();
}

static async Task<int> CtlAsync(string[] ctlArgs)
{
    if (ctlArgs.Length == 0 || !PbxVoiceService.Operations.Contains(ctlArgs[0]))
    {
        Console.Error.WriteLine($"ctl needs an operation: {string.Join(", ", PbxVoiceService.Operations)}");
        return 1;
    }
    JsonElement request;
    try
    {
        request = JsonDocument.Parse(ctlArgs.Length > 1 ? ctlArgs[1] : "{}").RootElement.Clone();
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"arguments are not JSON: {ex.Message}");
        return 1;
    }

    string response;
    try
    {
        response = await ControlClient.SendAsync(StatePaths.FromEnvironment().ControlSocket, ctlArgs[0], request, CancellationToken.None);
    }
    catch (SocketException ex)
    {
        Console.Error.WriteLine($"cannot reach the daemon ({ex.SocketErrorCode}); is `pbx-voice daemon` running?");
        return 2;
    }

    using var doc = JsonDocument.Parse(response);
    bool ok = doc.RootElement.GetProperty("ok").GetBoolean();
    var body = ok ? doc.RootElement.GetProperty("result") : doc.RootElement.GetProperty("error");
    (ok ? Console.Out : Console.Error).WriteLine(JsonSerializer.Serialize(body, Json.Options));
    return ok ? 0 : 1;
}
