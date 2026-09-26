using SipBot;

namespace PbxVoice.Hosting;

/// <summary>
/// Credentials and connection settings: the environment first, then the optional
/// <c>secrets.env</c> in the state directory. Tool arguments never carry any of these.
/// </summary>
/// <remarks>
/// <c>SIP_EXTENDED_RETRY</c> and <c>SIP_TRACE</c> belong to <c>sipbot serve</c> and are not read
/// here: the daemon always uses extended registration retry and never traces SIP messages.
/// </remarks>
internal sealed class Secrets
{
    private static readonly string[] Keys =
        { "SIP_SERVER", "SIP_PORT", "SIP_USERNAME", "SIP_PASSWORD", "SIP_FROMNAME", "SIP_LOCAL_PORT", "XAI_API_KEY" };

    private readonly Dictionary<string, string> _values;

    private Secrets(Dictionary<string, string> values, string? fileWarning)
    {
        _values = values;
        FileWarning = fileWarning;
    }

    /// <summary>Set when <c>secrets.env</c> is readable by other users.</summary>
    public string? FileWarning { get; }

    public string? this[string key] => _values.TryGetValue(key, out var v) ? v : null;

    public bool HasXaiKey => !string.IsNullOrWhiteSpace(this["XAI_API_KEY"]);

    public static Secrets Load(string secretsFile, Func<string, string?> env)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? warning = null;
        if (File.Exists(secretsFile))
        {
            foreach (var (key, value) in ParseEnvFile(File.ReadAllLines(secretsFile)))
            {
                if (Keys.Contains(key))
                    values[key] = value;
            }
            if (!StatePaths.IsPrivate(secretsFile))
                warning = $"{secretsFile} is readable by other users; it should be mode 0600";
        }
        foreach (var key in Keys)
        {
            if (env(key) is { Length: > 0 } v)
                values[key] = v;
        }
        return new Secrets(values, warning);
    }

    internal static IEnumerable<(string Key, string Value)> ParseEnvFile(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export "))
                line = line["export ".Length..].TrimStart();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            yield return (key, value);
        }
    }

    /// <summary>The SIP account. Extended registration retry is always on for the daemon (PR-REG-4).</summary>
    public SipConfig ToSipConfig()
    {
        string server = this["SIP_SERVER"] ?? "";
        string user = this["SIP_USERNAME"] ?? "";
        if (server.Length == 0 || user.Length == 0)
            throw new InvalidOperationException("SIP_SERVER and SIP_USERNAME must be set in the environment or in secrets.env.");
        int port = int.TryParse(this["SIP_PORT"], out var p) && p > 0 ? p : 5060;
        // SipClient takes the server string as given, so a non-default port goes into it.
        if (port != 5060 && !server.Contains(':'))
            server = $"{server}:{port}";
        var config = new SipConfig
        {
            Server = server,
            Port = port,
            Username = user,
            Password = this["SIP_PASSWORD"] ?? "",
            FromName = this["SIP_FROMNAME"] is { Length: > 0 } from ? from : user,
        };
        config.RegistrationRetry.Extended = true;
        return config;
    }

    public int LocalSipPort => int.TryParse(this["SIP_LOCAL_PORT"], out var p) && p >= 0 ? p : 0;
}
