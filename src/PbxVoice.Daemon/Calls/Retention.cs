using PbxVoice.Audio;

namespace PbxVoice.Calls;

/// <summary>
/// The retention pass (PR-SAFE-7): finished call records older than <c>retention_days</c>, then
/// rendered clips older than that which no remaining record uses (#28).
/// </summary>
internal static class Retention
{
    public static (int Records, int Clips) Run(CallStore calls, ClipStore clips, DateTimeOffset cutoff)
    {
        int records = calls.Prune(cutoff);
        int removedClips = clips.Prune(cutoff.UtcDateTime, calls.ClipIdsInUse());
        return (records, removedClips);
    }
}
