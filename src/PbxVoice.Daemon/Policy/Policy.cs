using System.Globalization;
using System.Text.Json;

namespace PbxVoice.Policy;

/// <summary>
/// The operator's <c>policy.json</c>: contacts, quiet hours, caps, phrase lists, and dialing
/// settings. The agent can read it through <c>list_contacts</c> and <c>status</c> and has no way to
/// write it (PR-SAFE-2).
/// </summary>
internal sealed class PolicyFile
{
    /// <summary>IANA zone for daily caps and for <c>{time}</c> in calls placed with <c>call_now</c>.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>Who the calls are made for; used in the disclosure clip (PR-SAFE-6).</summary>
    public string DisplayName { get; set; } = "the account owner";

    public Dictionary<string, Contact> Contacts { get; set; } = new();
    public bool AllowUnlistedNumbers { get; set; }
    public QuietHours? QuietHours { get; set; }
    public Limits Limits { get; set; } = new();
    public Phrases Phrases { get; set; } = new();
    public SpeechSettings Speech { get; set; } = new();

    /// <summary>Register the extension (PR-IN-3). Turn off only once outbound works unregistered (PR-REG-8).</summary>
    public bool Register { get; set; } = true;

    /// <summary>
    /// Dial even while registration is down (PR-REG-8). Set false for a PBX that refuses an
    /// authenticated INVITE from an unregistered extension; the daemon then waits for registration
    /// until the attempt's ring budget is gone.
    /// </summary>
    public bool DialWhileUnregistered { get; set; } = true;

    /// <summary>Cancel when neither ringing nor a final response arrives this long after the INVITE (PR-SCHED-6).</summary>
    public int SetupLimitSeconds { get; set; } = 7;

    /// <summary>The one final response every inbound INVITE gets (PR-IN-2). 480 is what P-P1 chose.</summary>
    public int InboundRejectStatus { get; set; } = 480;

    public int RetentionDays { get; set; } = 30;
}

internal sealed class Contact
{
    public string Number { get; set; } = "";
    public bool Self { get; set; }
}

internal sealed class QuietHours
{
    public string Start { get; set; } = "21:00";
    public string End { get; set; } = "08:00";
    public string Tz { get; set; } = "UTC";

    /// <summary>Only <c>non_self</c> is defined (PR-SAFE-3).</summary>
    public string AppliesTo { get; set; } = "non_self";
}

internal sealed class Limits
{
    public int CallsPerDay { get; set; } = 20;
    public int ConversationMinutesPerDay { get; set; } = 30;
    public int MaxConversationMinutes { get; set; } = 5;
    public int MaxAlarmAttempts { get; set; } = 10;
    public int MaxMessageAttempts { get; set; } = 3;
    public int MaxRingSeconds { get; set; } = 90;
    public int MinRetryMinutes { get; set; } = 1;
}

internal sealed class Phrases
{
    public List<string> Awake { get; set; } = new() { "i'm up", "i am up", "i'm awake", "awake" };
    public List<string> Snooze { get; set; } = new() { "snooze", "five more minutes", "ten more minutes" };
    public List<string> Confirm { get; set; } = new() { "got it", "okay", "ok", "yes", "thanks", "thank you" };
    public List<string> Repeat { get; set; } = new() { "repeat", "say that again", "what" };
}

internal sealed class SpeechSettings
{
    /// <summary>xAI voice for rendered clips.</summary>
    public string Voice { get; set; } = "eve";
    public string Language { get; set; } = "en";
    public double Speed { get; set; } = 1.0;
}

internal static class PolicyLoader
{
    public static PolicyFile Parse(string json)
    {
        var policy = JsonSerializer.Deserialize<PolicyFile>(json, Json.Options)
                     ?? throw new InvalidDataException("policy.json is empty");
        Validate(policy);
        return policy;
    }

    public static PolicyFile Load(string path) => Parse(File.ReadAllText(path));

    private static void Validate(PolicyFile p)
    {
        var errors = new List<string>();
        void Zone(string field, string id)
        {
            if (!TryZone(id, out _))
                errors.Add($"{field}: unknown time zone '{id}'");
        }

        Zone("timezone", p.Timezone);
        foreach (var (name, contact) in p.Contacts)
        {
            if (string.IsNullOrWhiteSpace(name))
                errors.Add("contacts: empty contact name");
            if (string.IsNullOrWhiteSpace(contact.Number))
                errors.Add($"contacts.{name}: number is required");
        }
        if (p.QuietHours is { } q)
        {
            Zone("quiet_hours.tz", q.Tz);
            if (!TimeOnly.TryParseExact(q.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add("quiet_hours.start must be HH:mm");
            if (!TimeOnly.TryParseExact(q.End, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add("quiet_hours.end must be HH:mm");
            if (q.AppliesTo != "non_self")
                errors.Add("quiet_hours.applies_to must be non_self");
        }
        if (p.Limits.CallsPerDay < 1) errors.Add("limits.calls_per_day must be at least 1");
        if (p.Limits.MaxAlarmAttempts is < 1 or > 50) errors.Add("limits.max_alarm_attempts must be 1-50");
        if (p.Limits.MaxMessageAttempts is < 1 or > 50) errors.Add("limits.max_message_attempts must be 1-50");
        if (p.Limits.MaxRingSeconds is < 5 or > 300) errors.Add("limits.max_ring_seconds must be 5-300");
        if (p.Limits.MinRetryMinutes < 1) errors.Add("limits.min_retry_minutes must be at least 1");
        foreach (var (name, list) in new[] { ("awake", p.Phrases.Awake), ("snooze", p.Phrases.Snooze), ("confirm", p.Phrases.Confirm), ("repeat", p.Phrases.Repeat) })
        {
            if (list is null || list.Count == 0 || list.Any(string.IsNullOrWhiteSpace))
                errors.Add($"phrases.{name} needs at least one non-empty phrase");
        }
        if (p.SetupLimitSeconds is < 2 or > 60) errors.Add("setup_limit_seconds must be 2-60");
        if (p.InboundRejectStatus is not (480 or 486 or 603)) errors.Add("inbound_reject_status must be 480, 486, or 603");
        if (p.RetentionDays < 1) errors.Add("retention_days must be at least 1");
        if (errors.Count > 0)
            throw new InvalidDataException("policy.json: " + string.Join("; ", errors));
    }

    public static bool TryZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    public static TimeZoneInfo Zone(string id) =>
        TryZone(id, out var zone) ? zone : throw new ArgumentException($"unknown time zone '{id}'");
}

/// <summary>
/// Reads <c>policy.json</c> when it changes, so operator edits apply without a restart. A broken
/// edit keeps the last good policy and is reported through <see cref="LastError"/>.
/// </summary>
internal sealed class PolicyProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    private PolicyFile _current;
    private DateTime _stamp;

    public PolicyProvider(string path)
    {
        _path = path;
        _current = PolicyLoader.Load(path);
        _stamp = File.GetLastWriteTimeUtc(path);
    }

    /// <summary>For tests: a fixed policy that never reloads.</summary>
    public PolicyProvider(PolicyFile fixedPolicy)
    {
        _path = "";
        _current = fixedPolicy;
    }

    public string? LastError { get; private set; }

    public PolicyFile Current
    {
        get
        {
            lock (_lock)
            {
                if (_path.Length > 0 && File.Exists(_path))
                {
                    var stamp = File.GetLastWriteTimeUtc(_path);
                    if (stamp != _stamp)
                    {
                        _stamp = stamp;
                        try
                        {
                            _current = PolicyLoader.Load(_path);
                            LastError = null;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
                        {
                            LastError = ex.Message;
                        }
                    }
                }
                return _current;
            }
        }
    }

    /// <summary>For tests: swap the fixed policy.</summary>
    public void Replace(PolicyFile policy)
    {
        lock (_lock)
            _current = policy;
    }
}
