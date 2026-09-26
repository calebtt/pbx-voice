using System.Globalization;

namespace PbxVoice.Scheduling;

/// <summary>
/// Wall-clock times in an IANA zone (PR-SCHED-1). A local time that does not exist fires at the
/// first valid minute after it. A local time that occurs twice fires once, at the first occurrence.
/// </summary>
internal static class Recurrence
{
    private static readonly Dictionary<string, DayOfWeek[]> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = new[] { DayOfWeek.Monday },
        ["tue"] = new[] { DayOfWeek.Tuesday },
        ["wed"] = new[] { DayOfWeek.Wednesday },
        ["thu"] = new[] { DayOfWeek.Thursday },
        ["fri"] = new[] { DayOfWeek.Friday },
        ["sat"] = new[] { DayOfWeek.Saturday },
        ["sun"] = new[] { DayOfWeek.Sunday },
        ["weekdays"] = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        ["weekends"] = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
        ["daily"] = Enum.GetValues<DayOfWeek>(),
    };

    public static HashSet<DayOfWeek>? ParseDays(IEnumerable<string> names, out string? error)
    {
        error = null;
        var days = new HashSet<DayOfWeek>();
        foreach (var raw in names)
        {
            string name = raw.Trim();
            if (name.Length > 3 && DayNames.ContainsKey(name[..3]) && !DayNames.ContainsKey(name))
                name = name[..3]; // "monday" -> "mon"
            if (!DayNames.TryGetValue(name, out var set))
            {
                error = $"unknown day '{raw}' (use mon..sun, weekdays, weekends, or daily)";
                return null;
            }
            days.UnionWith(set);
        }
        if (days.Count == 0)
            error = "repeat.days must name at least one day";
        return error is null ? days : null;
    }

    public static bool TryParseTime(string s, out TimeOnly time) =>
        TimeOnly.TryParseExact(s, new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>The instant of a local wall-clock time in <paramref name="zone"/>.</summary>
    public static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Skipped by a forward transition: the first valid minute after it.
            var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
            for (int i = 0; i < 24 * 60 && zone.IsInvalidTime(t); i++)
                t = t.AddMinutes(1);
            return new DateTimeOffset(t, zone.GetUtcOffset(t));
        }
        if (zone.IsAmbiguousTime(local))
        {
            // Repeated by a backward transition: the first occurrence has the larger offset.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The first weekly fire strictly after <paramref name="after"/>.</summary>
    public static DateTimeOffset NextWeekly(DateTimeOffset after, IReadOnlySet<DayOfWeek> days, TimeOnly time, TimeZoneInfo zone)
    {
        var startDate = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (int d = 0; d <= 8; d++)
        {
            var date = startDate.AddDays(d);
            if (!days.Contains(date.DayOfWeek))
                continue;
            var candidate = ResolveLocal(date + time.ToTimeSpan(), zone);
            if (candidate > after)
                return candidate;
        }
        throw new InvalidOperationException("no weekly fire within 8 days");
    }
}
