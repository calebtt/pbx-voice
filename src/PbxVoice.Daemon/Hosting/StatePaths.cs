using System.Runtime.InteropServices;

namespace PbxVoice.Hosting;

/// <summary>
/// The state directory: <c>SIPBOT_STATE_DIR</c>, else <c>$XDG_STATE_HOME/pbx-voice</c>, else
/// <c>$HOME/.local/state/pbx-voice</c>. The directory is mode 0700 and every file in it 0600.
/// </summary>
internal sealed class StatePaths
{
    public const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public StatePaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string Policy => Path.Combine(Root, "policy.json");
    public string Secrets => Path.Combine(Root, "secrets.env");
    public string Clips => Path.Combine(Root, "clips");
    public string Calls => Path.Combine(Root, "calls");
    public string Tmp => Path.Combine(Root, "tmp");
    /// <summary>
    /// <c>control.sock</c> in the state directory, unless that path is too long for a Unix socket
    /// (about 108 bytes). Then a short private path, the same for the daemon and <c>ctl</c>:
    /// <c>$XDG_RUNTIME_DIR/pbx-voice-{hash}.sock</c>, or <c>/tmp/pbx-voice-{user}/{hash}.sock</c>.
    /// </summary>
    public string ControlSocket => SocketPathFor(Root, Environment.GetEnvironmentVariable);

    internal const int MaxSocketPathBytes = 100;

    internal static string SocketPathFor(string root, Func<string, string?> env)
    {
        string inState = Path.Combine(root, "control.sock");
        if (System.Text.Encoding.UTF8.GetByteCount(inState) <= MaxSocketPathBytes)
            return inState;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..16].ToLowerInvariant();
        string dir = env("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime)
            ? runtime
            : Path.Combine(Path.GetTempPath(), "pbx-voice-" + Environment.UserName);
        return Path.Combine(dir, $"pbx-voice-{hash}.sock");
    }
    public string McpToken => Path.Combine(Root, "mcp-token");

    /// <summary>The operator's conversation prompt (optional; the built-in default applies without it).</summary>
    public string ConversationPrompt => Path.Combine(Root, "conversation-prompt.txt");

    /// <summary>Where a daemon started by <c>pbx-voice start</c> or the stdio shim writes its log.</summary>
    public string DaemonLog => Path.Combine(Root, "daemon.log");

    public static StatePaths FromEnvironment() => new(ResolveRoot(Environment.GetEnvironmentVariable));

    public static string ResolveRoot(Func<string, string?> env)
    {
        string? explicitDir = env("SIPBOT_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir))
            return explicitDir;
        string? xdg = env("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "pbx-voice");
        return Path.Combine(HomeDirectory(env), ".local", "state", "pbx-voice");
    }

    /// <summary>
    /// Per-AOR lock files live under <c>$HOME/.local/state/pbx-voice/locks</c> whatever the state
    /// directory is, so two daemons pointed at different state directories still exclude each other.
    /// </summary>
    public static string LocksDirectory(Func<string, string?> env) =>
        Path.Combine(HomeDirectory(env), ".local", "state", "pbx-voice", "locks");

    private static string HomeDirectory(Func<string, string?> env) =>
        env("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Clips, Calls, Tmp })
            CreatePrivateDirectory(dir);
    }

    public static void CreatePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return;
        }
        Directory.CreateDirectory(dir, DirMode);
        // An existing directory keeps its old mode; tighten it, since it holds call content.
        if ((File.GetUnixFileMode(dir) & ~DirMode) != 0)
            File.SetUnixFileMode(dir, DirMode);
    }

    public static bool IsPrivate(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode)0b000_111_111) == 0;
}

/// <summary>Writes state files atomically with mode 0600.</summary>
internal static class SecureFile
{
    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = StatePaths.FileMode;
        using (var fs = new FileStream(tmp, options))
        {
            fs.Write(contents);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
