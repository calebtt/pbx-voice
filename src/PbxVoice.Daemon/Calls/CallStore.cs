using System.Text.Json;
using PbxVoice.Hosting;

namespace PbxVoice.Calls;

/// <summary>
/// Call records, one 0600 JSON file per call in <c>calls/</c>, also kept in memory for
/// <c>list_calls</c>. Every change goes through <see cref="Update"/>, which holds the store lock and
/// writes the file, so readers on the control socket never see a record mid-change.
/// </summary>
internal sealed class CallStore
{
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, CallRecord> _records = new();

    public CallStore(string dir)
    {
        _dir = dir;
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<CallRecord>(File.ReadAllText(file), Json.Options);
                if (record is not null && record.CallId.Length > 0)
                    _records[record.CallId] = record;
            }
            catch (JsonException)
            {
                // A damaged record is left on disk for the operator and ignored here.
            }
        }
    }

    public void Add(CallRecord record)
    {
        lock (_lock)
        {
            _records[record.CallId] = record;
            Write(record);
        }
    }

    /// <summary>Applies <paramref name="change"/> under the store lock and saves the record.</summary>
    public void Update(CallRecord record, Action<CallRecord> change)
    {
        lock (_lock)
        {
            change(record);
            Write(record);
        }
    }

    /// <summary>A deep copy, safe to serialize while the executor keeps working on the record.</summary>
    public CallRecord? Snapshot(string callId)
    {
        lock (_lock)
            return _records.TryGetValue(callId, out var r) ? Clone(r) : null;
    }

    public List<CallRecord> SnapshotAll()
    {
        lock (_lock)
            return _records.Values.Select(Clone).ToList();
    }

    /// <summary>Records that still have work: pending, or in progress when the daemon stopped.</summary>
    public List<CallRecord> Open()
    {
        lock (_lock)
            return _records.Values.Where(r => r.Status != CallStatus.Done).ToList();
    }

    /// <summary>Dials placed in [start, end), for the daily call cap (PR-SAFE-4).</summary>
    public int AttemptsBetween(DateTimeOffset start, DateTimeOffset end)
    {
        lock (_lock)
            return _records.Values.Sum(r => r.Attempts.Count(a => a.StartedAt >= start && a.StartedAt < end));
    }

    /// <summary>Deletes finished records created before <paramref name="cutoff"/> (PR-SAFE-7).</summary>
    public int Prune(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            var old = _records.Values.Where(r => r.Status == CallStatus.Done && r.CreatedAt < cutoff).ToList();
            foreach (var r in old)
            {
                _records.Remove(r.CallId);
                File.Delete(PathFor(r.CallId));
            }
            return old.Count;
        }
    }

    private void Write(CallRecord record) =>
        SecureFile.WriteAllText(PathFor(record.CallId), JsonSerializer.Serialize(record, Json.Options));

    private string PathFor(string callId) => Path.Combine(_dir, callId + ".json");

    private static CallRecord Clone(CallRecord r) =>
        JsonSerializer.Deserialize<CallRecord>(JsonSerializer.Serialize(r, Json.Options), Json.Options)!;
}
