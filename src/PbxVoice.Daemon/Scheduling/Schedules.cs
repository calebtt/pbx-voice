using System.Text.Json;
using PbxVoice.Calls;
using PbxVoice.Hosting;
using PbxVoice.Policy;

namespace PbxVoice.Scheduling;

internal sealed class RepeatSpec
{
    public List<string> Days { get; set; } = new();
    public string Time { get; set; } = "";
    public string Tz { get; set; } = "";
}

/// <summary>The pre-flight check about ten minutes before an alarm fires (PR-ALARM-7).</summary>
internal sealed class PreflightRecord
{
    public DateTimeOffset At { get; set; }
    public DateTimeOffset ForFire { get; set; }
    public bool PbxReachable { get; set; }
    public int? OptionsStatus { get; set; }
    public string RegistrationState { get; set; } = "";
    public string? RegistrationError { get; set; }
    public ReregisterRecord? Reregister { get; set; }
}

internal sealed class Schedule
{
    public string Id { get; set; } = "";
    public CallType Type { get; set; }
    public string To { get; set; } = "";
    public string Contact { get; set; } = "";
    public bool Self { get; set; }
    public string TargetUri { get; set; } = "";
    public string MaskedNumber { get; set; } = "";
    public DateTimeOffset? At { get; set; }
    public RepeatSpec? Repeat { get; set; }
    public string? Text { get; set; }
    public Brief? Brief { get; set; }
    public CallOptions Options { get; set; } = new();
    public ClipSet Clips { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NextFire { get; set; }
    public DateTimeOffset? LastFire { get; set; }
    /// <summary><c>active</c>, <c>done</c> (a one-off that fired), or <c>cancelled</c>.</summary>
    public string Status { get; set; } = "active";
    public PreflightRecord? Preflight { get; set; }

    public Target ToTarget() => new(Contact, TargetUri, MaskedNumber, Self);

    /// <summary>The next fire strictly after <paramref name="after"/>, or null for a one-off.</summary>
    public DateTimeOffset? FireAfter(DateTimeOffset after)
    {
        if (Repeat is null)
            return null;
        var days = Recurrence.ParseDays(Repeat.Days, out _)!;
        Recurrence.TryParseTime(Repeat.Time, out var time);
        return Recurrence.NextWeekly(after, days, time, PolicyLoader.Zone(Repeat.Tz));
    }
}

/// <summary><c>schedules.json</c>. Callers hold <see cref="Sync"/> while reading or changing schedules.</summary>
internal sealed class ScheduleStore
{
    private readonly string _path;
    private readonly List<Schedule> _schedules;

    public ScheduleStore(string path)
    {
        _path = path;
        _schedules = File.Exists(path)
            ? JsonSerializer.Deserialize<List<Schedule>>(File.ReadAllText(path), Json.Options) ?? new()
            : new();
    }

    public object Sync { get; } = new();

    public IReadOnlyList<Schedule> All => _schedules;

    public Schedule? Find(string id) => _schedules.FirstOrDefault(s => s.Id == id);

    public void Add(Schedule schedule)
    {
        _schedules.Add(schedule);
        Save();
    }

    public void Save() => SecureFile.WriteAllText(_path, JsonSerializer.Serialize(_schedules, Json.Options));

    /// <summary>
    /// Collects every fire at or before <paramref name="now"/> and advances each schedule. A daemon
    /// that was down gets one call per missed fire (at most 14 per schedule); the executor then
    /// runs those inside their grace window and records the rest as missed (PR-SCHED-4).
    /// </summary>
    public List<(Schedule Schedule, DateTimeOffset Fire)> CollectDue(DateTimeOffset now)
    {
        var due = new List<(Schedule, DateTimeOffset)>();
        bool changed = false;
        foreach (var s in _schedules.Where(s => s.Status == "active" && s.NextFire is { } f && f <= now))
        {
            var fires = new List<DateTimeOffset> { s.NextFire!.Value };
            DateTimeOffset? next = s.FireAfter(s.NextFire.Value);
            while (next is { } n && n <= now)
            {
                fires.Add(n);
                next = s.FireAfter(n);
            }
            foreach (var fire in fires.TakeLast(14))
                due.Add((s, fire));
            s.LastFire = fires[^1];
            s.NextFire = next;
            if (next is null)
                s.Status = "done";
            changed = true;
        }
        if (changed)
            Save();
        return due;
    }
}
