using System.Globalization;
using PbxVoice.Calls;
using SipBot;

namespace PbxVoice.Policy;

/// <summary>
/// The policy checks, enforced in the daemon at scheduling time and again at fire time
/// (PR-SAFE-1 to PR-SAFE-4, PR-ALARM-5). The agent cannot change any of them.
/// </summary>
internal static class PolicyGuard
{
    /// <summary>
    /// Resolves <paramref name="to"/> through the contact list. A number that is not a contact is
    /// refused unless <c>allow_unlisted_numbers</c> is on. Every target goes through SipBotLib's
    /// normalizer, including its unsafe-target checks, before any policy decision uses it.
    /// </summary>
    public static Target? Resolve(PolicyFile policy, string to, string server, out string? error)
    {
        error = null;
        to = (to ?? "").Trim();
        if (to.Length == 0)
        {
            error = "'to' is required";
            return null;
        }

        string? name = null;
        Contact? contact = null;
        foreach (var (key, value) in policy.Contacts)
        {
            if (string.Equals(key, to, StringComparison.OrdinalIgnoreCase)
                || (Digits(value.Number).Length > 0 && Digits(value.Number) == Digits(to) && LooksLikeNumber(to)))
            {
                name = key;
                contact = value;
                break;
            }
        }

        if (contact is null && !policy.AllowUnlistedNumbers)
        {
            error = $"'{to}' is not a contact in policy.json, and unlisted numbers are not allowed";
            return null;
        }

        string number = contact?.Number ?? to;
        string uri;
        try
        {
            uri = SipUriNormalizer.Normalize(number, server);
        }
        catch (ArgumentException ex)
        {
            error = $"target rejected: {ex.Message}";
            return null;
        }
        return new Target(name ?? "(unlisted)", uri, Mask(number), contact?.Self ?? false);
    }

    /// <summary>Checks that do not depend on time: alarm only to <c>self</c>; conversation not built yet.</summary>
    public static string? CheckType(CallType type, Target target)
    {
        if (type == CallType.Alarm && !target.Self)
            return $"an alarm can only call a contact marked self; '{target.Contact}' is not";
        return null;
    }

    /// <summary>True when <paramref name="at"/> falls inside quiet hours and they apply to this target.</summary>
    public static bool InQuietHours(PolicyFile policy, Target target, DateTimeOffset at)
    {
        if (target.Self || policy.QuietHours is not { } q)
            return false;
        var zone = PolicyLoader.Zone(q.Tz);
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var start = TimeOnly.ParseExact(q.Start, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(q.End, "HH:mm", CultureInfo.InvariantCulture);
        return start <= end
            ? local >= start && local < end
            : local >= start || local < end;
    }

    /// <summary>The policy day (in <c>timezone</c>) that contains <paramref name="at"/>, as a UTC range.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Day(PolicyFile policy, DateTimeOffset at)
    {
        var zone = PolicyLoader.Zone(policy.Timezone);
        var localDate = TimeZoneInfo.ConvertTime(at, zone).Date;
        var start = Scheduling.Recurrence.ResolveLocal(localDate, zone);
        var end = Scheduling.Recurrence.ResolveLocal(localDate.AddDays(1), zone);
        return (start, end);
    }

    /// <summary>"+15555550100" becomes "***0100". Short extensions are shown as they are.</summary>
    public static string Mask(string number)
    {
        string digits = Digits(number);
        if (digits.Length <= 4 || !LooksLikeNumber(number))
            return digits.Length <= 4 && LooksLikeNumber(number) ? number.Trim() : "***";
        return "***" + digits[^4..];
    }

    private static bool LooksLikeNumber(string s) =>
        s.Trim().TrimStart('+').All(c => char.IsDigit(c) || c is ' ' or '-' or '(' or ')' or '.');

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
}
