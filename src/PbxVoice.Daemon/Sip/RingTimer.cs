namespace PbxVoice.Sip;

internal enum RingExpiry { None, Setup, Ring }

/// <summary>
/// Ring timing for one outbound call (PR-SCHED-6). The setup timer runs from the INVITE; the
/// first 180/183 stops it and starts the ring timer. Whichever expires first calls
/// <c>onExpire</c> once, and <see cref="Expired"/> says which it was.
/// </summary>
internal sealed class RingTimer : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _ring;
    private readonly TimeSpan _setup;
    private readonly Action _onExpire;
    private readonly object _lock = new();
    private ITimer? _setupTimer;
    private ITimer? _ringTimer;
    private bool _ringing;
    private bool _stopped;
    private int _expired;

    public RingTimer(TimeProvider time, TimeSpan ring, TimeSpan setup, Action onExpire)
    {
        _time = time;
        _ring = ring;
        _setup = setup;
        _onExpire = onExpire;
    }

    public RingExpiry Expired => (RingExpiry)Volatile.Read(ref _expired);

    public void Start()
    {
        lock (_lock)
            _setupTimer = _time.CreateTimer(_ => Expire(RingExpiry.Setup), null, _setup, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The first ringing response; later ones change nothing.</summary>
    public void OnRinging()
    {
        lock (_lock)
        {
            if (_ringing || _stopped || Expired != RingExpiry.None)
                return;
            _ringing = true;
            _setupTimer?.Dispose();
            _setupTimer = null;
            _ringTimer = _time.CreateTimer(_ => Expire(RingExpiry.Ring), null, _ring, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A final response arrived or the call was cancelled for another reason.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            _setupTimer?.Dispose();
            _ringTimer?.Dispose();
        }
    }

    private void Expire(RingExpiry which)
    {
        lock (_lock)
        {
            if (_stopped)
                return;
        }
        if (Interlocked.CompareExchange(ref _expired, (int)which, 0) == 0)
            _onExpire();
    }

    public void Dispose() => Stop();
}
