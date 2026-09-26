using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace PbxVoice.Conversation;

/// <summary>
/// The conversation prompt: plain text with the placeholders <c>{{display_name}}</c>,
/// <c>{{call}}</c>, and <c>{{brief}}</c>. The default is built in (<c>conversation-prompt.txt</c>);
/// an operator can override it with a file of the same name in the state directory.
/// </summary>
internal sealed partial record PromptTemplate(string Text, string Source, string Sha256)
{
    public const int MaxBytes = 16 * 1024;
    public const string BuiltInSource = "built_in";
    public const string FileSource = "file";

    private static readonly string[] Names = { "display_name", "call", "brief" };
    private static readonly string[] Required = { "call", "brief" };

    [GeneratedRegex(@"\{\{\s*([^{}]*?)\s*\}\}")]
    internal static partial Regex Placeholder();

    /// <summary>The default prompt exactly as shipped, comments included.</summary>
    public static string BuiltInFile { get; } = ReadResource();

    public static PromptTemplate BuiltIn { get; } = Parse(BuiltInFile, BuiltInSource);

    /// <summary>
    /// Validates a prompt file and strips its comment lines. The hash is of the file as written, so
    /// <c>sha256sum conversation-prompt.txt</c> matches the <c>prompt_sha256</c> in call records.
    /// </summary>
    public static PromptTemplate Parse(string file, string source)
    {
        if (Encoding.UTF8.GetByteCount(file) > MaxBytes)
            throw new InvalidDataException($"the conversation prompt is over {MaxBytes / 1024} KB");
        var lines = file.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith('#'));
        string text = string.Join('\n', lines).Trim() + "\n";

        var counts = new Dictionary<string, int>();
        foreach (Match m in Placeholder().Matches(text))
        {
            string name = m.Groups[1].Value;
            if (!Names.Contains(name))
                throw new InvalidDataException($"the conversation prompt has an unknown placeholder {{{{{name}}}}}; the placeholders are {{{{display_name}}}}, {{{{call}}}}, and {{{{brief}}}}");
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        foreach (string name in Required)
        {
            if (counts.GetValueOrDefault(name) != 1)
                throw new InvalidDataException($"the conversation prompt must contain {{{{{name}}}}} exactly once");
        }
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file))).ToLowerInvariant();
        return new PromptTemplate(text, source, sha);
    }

    private static string ReadResource()
    {
        using var stream = typeof(PromptTemplate).Assembly.GetManifestResourceStream("PbxVoice.conversation-prompt.txt")
            ?? throw new InvalidOperationException("the built-in conversation prompt is missing from the build");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// The prompt for the next call: the operator's file when it is present and valid, else the
/// built-in default. Like the policy, a broken edit keeps the last valid prompt and reports why.
/// </summary>
internal sealed class PromptProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    private PromptTemplate _current = PromptTemplate.BuiltIn;
    private DateTime? _stamp;

    public PromptProvider(string path)
    {
        _path = path;
        lock (_lock)
            Refresh();
    }

    /// <summary>For tests and hosts without a state directory: always the built-in prompt.</summary>
    public static PromptProvider BuiltInOnly() => new("");

    public string Path => _path;

    public string? LastError { get; private set; }

    public PromptTemplate Current
    {
        get
        {
            lock (_lock)
            {
                Refresh();
                return _current;
            }
        }
    }

    private void Refresh()
    {
        if (_path.Length == 0)
            return;
        if (!File.Exists(_path))
        {
            if (_stamp is not null)
            {
                _stamp = null;
                _current = PromptTemplate.BuiltIn;
                LastError = null;
                Log.Information("Conversation prompt file removed; using the built-in prompt");
            }
            return;
        }
        var stamp = File.GetLastWriteTimeUtc(_path);
        if (stamp == _stamp)
            return;
        _stamp = stamp;
        try
        {
            _current = PromptTemplate.Parse(File.ReadAllText(_path), PromptTemplate.FileSource);
            LastError = null;
            Log.Information("Conversation prompt loaded from {Path} (sha256 {Sha})", _path, _current.Sha256[..12]);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            Log.Warning("Conversation prompt {Path} not used: {Error}; still using the {Source} prompt", _path, ex.Message, _current.Source);
        }
    }
}
