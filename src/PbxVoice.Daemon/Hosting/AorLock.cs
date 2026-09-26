using System.Security.Cryptography;
using System.Text;

namespace PbxVoice.Hosting;

/// <summary>
/// One daemon per SIP account (address of record). The lock is an exclusive open of
/// <c>locks/aor-{hash}.lock</c>; on Linux .NET takes an advisory <c>flock</c> for
/// <see cref="FileShare.None"/>, which the kernel releases if the process dies.
/// </summary>
internal sealed class AorLock : IDisposable
{
    private readonly FileStream _stream;

    private AorLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    public string Path { get; }

    public static string FileFor(string locksDirectory, string aor)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(aor.ToLowerInvariant())))[..16].ToLowerInvariant();
        return System.IO.Path.Combine(locksDirectory, $"aor-{hash}.lock");
    }

    /// <summary>Takes the lock, or returns null when another process holds it.</summary>
    public static AorLock? TryAcquire(string locksDirectory, string aor)
    {
        StatePaths.CreatePrivateDirectory(locksDirectory);
        string path = FileFor(locksDirectory, aor);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = StatePaths.FileMode;
            var stream = new FileStream(path, options);
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n"));
            stream.Flush();
            return new AorLock(stream, path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
