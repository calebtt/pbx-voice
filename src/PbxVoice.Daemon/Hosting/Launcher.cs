using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using PbxVoice.Control;

namespace PbxVoice.Hosting;

internal sealed record LaunchResult(bool Ok, string Message);

/// <summary>
/// Starts and stops the daemon on hosts without a service manager, such as Grok Bot's computer
/// (<c>pbx-voice start</c> and <c>stop</c>, and the stdio shim before a tool call). The daemon is
/// detached from whatever started it (its own session, and not its child: an MCP client that ends
/// the shim's process tree doesn't end the daemon), appends its output to <c>daemon.log</c> in the
/// state directory, and counts as running once its control socket answers.
/// </summary>
internal static class Launcher
{
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);
    public const long MaxLogBytes = 10L * 1024 * 1024;

    /// <summary>Exit code of a daemon that found another one already serving this extension.</summary>
    private const int AlreadyRunningExitCode = 3;

    /// <summary>The line the wrapper appends to the log when the daemon exits.</summary>
    internal const string ExitMarker = "pbx-voice daemon exited with code ";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement.Clone();

    public static async Task<bool> IsRunningAsync(string socketPath, CancellationToken ct)
    {
        try
        {
            await ControlClient.SendAsync(socketPath, "status", NoArgs, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return false;
        }
    }

    /// <summary>Starts the daemon unless it is already running, and waits until it answers.</summary>
    public static async Task<LaunchResult> EnsureRunningAsync(StatePaths paths, CancellationToken ct, string? executable = null)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await IsRunningAsync(paths.ControlSocket, ct).ConfigureAwait(false))
                return new LaunchResult(true, "the daemon is already running");
            executable ??= Environment.ProcessPath;
            if (executable is null)
                return new LaunchResult(false, "cannot find the pbx-voice executable to start");

            paths.EnsureCreated();
            RotateLog(paths.DaemonLog);
            if (!File.Exists(paths.DaemonLog))
                SecureFile.WriteAllText(paths.DaemonLog, "");
            long logStart = new FileInfo(paths.DaemonLog).Length;

            using (var launcher = Process.Start(StartInfo(executable, paths.DaemonLog)))
            {
                if (launcher is null)
                    return new LaunchResult(false, "could not start the daemon");
            }

            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                if (await IsRunningAsync(paths.ControlSocket, ct).ConfigureAwait(false))
                    return new LaunchResult(true, $"the daemon started; log: {paths.DaemonLog}");
                // Another start can win the race (exit code 3); its daemon answers shortly.
                if (ExitCodeSince(paths.DaemonLog, logStart) is { } code && code != AlreadyRunningExitCode)
                    return new LaunchResult(false, $"the daemon exited with code {code}. The end of {paths.DaemonLog}:\n{Tail(paths.DaemonLog, 15)}");
            }
            return new LaunchResult(false, $"the daemon did not answer within {ReadyTimeout.TotalSeconds:0} s; see {paths.DaemonLog}");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Asks the daemon to stop through its control socket and waits until it has. It refuses while a
    /// call is in progress unless <paramref name="force"/> is set.
    /// </summary>
    public static async Task<LaunchResult> StopAsync(StatePaths paths, bool force, CancellationToken ct)
    {
        string reply;
        try
        {
            var args = JsonSerializer.SerializeToElement(new { force }, Json.Compact);
            reply = await ControlClient.SendAsync(paths.ControlSocket, "shutdown", args, ct).ConfigureAwait(false);
        }
        catch (DaemonUnavailableException)
        {
            return new LaunchResult(true, "the daemon is not running");
        }
        catch (IOException)
        {
            reply = """{"ok":true}"""; // it closed the connection on its way down
        }
        using (var doc = JsonDocument.Parse(reply))
        {
            if (!doc.RootElement.GetProperty("ok").GetBoolean())
                return new LaunchResult(false, doc.RootElement.GetProperty("error").GetString() ?? "the daemon refused to stop");
        }
        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await IsRunningAsync(paths.ControlSocket, ct).ConfigureAwait(false))
                return new LaunchResult(true, "the daemon stopped");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
        return new LaunchResult(false, $"the daemon is still answering after {StopTimeout.TotalSeconds:0} s");
    }

    /// <summary>
    /// The daemon runs under a small <c>/bin/sh</c> wrapper that sends its output to the log (not to
    /// the caller's stdout, which for the stdio shim is the MCP stream) and records its exit code
    /// there. <c>setsid -f</c> forks and starts a new session, so the wrapper is neither in the
    /// caller's session nor its child. Without <c>setsid</c>, the wrapper runs in the background of
    /// a shell that exits at once, which also leaves it without a parent to be ended with.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string executable, string log)
    {
        string script = $"exec >>\"$1\" 2>&1 </dev/null; trap '' HUP; \"$0\" daemon; echo \"{ExitMarker}$?\"";
        string? setsid = FindOnPath("setsid");
        var psi = new ProcessStartInfo { UseShellExecute = false };
        if (setsid is not null)
        {
            psi.FileName = setsid;
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("/bin/sh");
        }
        else
        {
            psi.FileName = "/bin/sh";
            script = $"( {script} ) &";
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(executable);
        psi.ArgumentList.Add(log);
        return psi;
    }

    /// <summary>The exit code the wrapper logged after <paramref name="offset"/>, if the daemon has exited.</summary>
    internal static int? ExitCodeSince(string log, long offset)
    {
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Min(offset, fs.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            int? code = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith(ExitMarker, StringComparison.Ordinal) && int.TryParse(line.AsSpan(ExitMarker.Length), out int c))
                    code = c;
            }
            return code;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Keeps one previous log: over 10 MB, <c>daemon.log</c> becomes <c>daemon.log.1</c>.</summary>
    internal static void RotateLog(string log)
    {
        if (File.Exists(log) && new FileInfo(log).Length > MaxLogBytes)
            File.Move(log, log + ".1", overwrite: true);
    }

    private static string Tail(string path, int lines)
    {
        try
        {
            return string.Join('\n', File.ReadLines(path).TakeLast(lines));
        }
        catch (IOException)
        {
            return "(the log could not be read)";
        }
    }

    private static string? FindOnPath(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Append("/usr/bin").Append("/bin");
        return dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }
}
