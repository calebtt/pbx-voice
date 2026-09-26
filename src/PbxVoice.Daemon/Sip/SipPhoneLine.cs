using System.Collections.Concurrent;
using System.Net;
using PbxVoice.Calls;
using PbxVoice.Speech;
using Serilog;
using SIPSorcery.SIP;
using SipBot;

namespace PbxVoice.Sip;

/// <summary>
/// The SIP line on SipBotLib: one <see cref="SipClient"/> with extended registration retry
/// (PR-REG-4), outbound calls timed from the ringing event (PR-SCHED-6, PR-DIAL-1), and one
/// immediate final response to every inbound INVITE (PR-IN-2).
/// </summary>
internal sealed class SipPhoneLine : IPhoneLine, IDisposable
{
    private readonly SIPTransport _transport;
    private readonly SipClient _client;
    private readonly TimeProvider _time;
    private readonly Func<int> _inboundStatus;
    private readonly ISpeechClassifier _vad;
    private readonly ConcurrentDictionary<string, DateTime> _rejected = new();

    public SipPhoneLine(SipConfig config, int localPort, bool register, Func<int> inboundStatus, ISpeechClassifier vad, TimeProvider time)
    {
        _time = time;
        _vad = vad;
        _inboundStatus = inboundStatus;
        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Any, localPort));
        _client = new SipClient(_transport, config, registrationExpirySeconds: 120);
        _client.IncomingCall += RejectInbound;
        if (register)
            _client.StartRegistration();
    }

    public string Server => _client.Server;

    public RegistrationInfo Registration => new(
        _client.RegistrationState.ToString(),
        _client.IsRegistered,
        _client.LastRegistrationError,
        _client.LastSuccessfulRegistration == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(_client.LastSuccessfulRegistration, DateTimeKind.Utc)));

    public void Reregister() => _client.StartRegistration();

    public async Task<bool> WaitForRegistrationAsync(TimeSpan max, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + max;
        while (!_client.IsRegistered)
        {
            if (_time.GetUtcNow() >= deadline || ct.IsCancellationRequested)
                return false;
            if (_client.RegistrationState == RegistrationState.HardFailure)
                return false;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return true;
    }

    public async Task<DialResult> DialAsync(DialRequest request, CancellationToken ct)
    {
        var endpoint = new CallAudioEndPoint();
        using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timer = new RingTimer(_time, request.RingTime, request.SetupLimit, () => dialCts.Cancel());
        long started = _time.GetTimestamp();
        int? ringingAfterMs = null, ringingStatus = null;

        void OnRinging(SipClient _, int status)
        {
            ringingStatus ??= status;
            ringingAfterMs ??= (int)_time.GetElapsedTime(started).TotalMilliseconds;
            timer.OnRinging();
        }

        // SIPSorcery's own ring timeout counts from the INVITE; it is only a safety cap here.
        int cap = (int)Math.Ceiling((request.SetupLimit + request.RingTime).TotalSeconds) + 30;
        _client.CallRinging += OnRinging;
        bool answered;
        try
        {
            timer.Start();
            answered = await _client.CallAsync(request.Uri, endpoint, endpoint, ringTimeoutSeconds: cap, cancellationToken: dialCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            timer.Stop();
            _client.CallRinging -= OnRinging;
        }

        if (answered)
        {
            var call = new SipActiveCall(_client, endpoint, _vad);
            if (ct.IsCancellationRequested)
            {
                await call.HangupAsync().ConfigureAwait(false);
                await call.DisposeAsync().ConfigureAwait(false);
                return new DialResult(DialKind.Cancelled, 200, "cancelled after answer", null, ringingAfterMs, ringingStatus);
            }
            return new DialResult(DialKind.Answered, 200, "answered", call, ringingAfterMs, ringingStatus);
        }

        await endpoint.StopPacerAsync().ConfigureAwait(false);
        string failure = _client.LastOutboundFailure ?? "not answered";
        int? status = DialClassifier.ParseStatus(failure);
        if (ct.IsCancellationRequested)
            return new DialResult(DialKind.Cancelled, status, "cancelled", null, ringingAfterMs, ringingStatus);
        return timer.Expired switch
        {
            RingExpiry.Ring => new DialResult(DialKind.NoAnswer, status, $"ring timeout after {request.RingTime.TotalSeconds:0} s of ringing", null, ringingAfterMs, ringingStatus),
            RingExpiry.Setup => new DialResult(DialKind.SipFailure, status,
                $"no ringing or final response within {request.SetupLimit.TotalSeconds:0} s of the INVITE", null, ringingAfterMs, ringingStatus),
            _ => new DialResult(DialClassifier.FromFinalStatus(status), status, failure, null, ringingAfterMs, ringingStatus),
        };
    }

    public async Task<int?> PingServerAsync(TimeSpan timeout, CancellationToken ct)
    {
        var uri = SIPURI.ParseSIPURIRelaxed(_client.Server);
        var request = SIPRequest.GetRequest(SIPMethodsEnum.OPTIONS, uri);
        var tx = new SIPNonInviteTransaction(_transport, request, null);
        var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        tx.NonInviteTransactionFinalResponseReceived += (local, remote, t, response) =>
        {
            result.TrySetResult(response.StatusCode);
            return Task.FromResult(System.Net.Sockets.SocketError.Success);
        };
        tx.NonInviteTransactionFailed += (t, reason) => result.TrySetResult(null);
        tx.SendRequest();
        var winner = await Task.WhenAny(result.Task, Task.Delay(timeout, _time, ct)).ConfigureAwait(false);
        return winner == result.Task ? result.Task.Result : null;
    }

    /// <summary>
    /// v1 answers no inbound calls: each INVITE gets one final response on its own UAS
    /// transaction, with a To tag and no provisional response, so the PBX sends the caller onward
    /// (voicemail, or the extension's forwarding). Checked live as P-L1 and P-P1.
    /// </summary>
    private void RejectInbound(SipClient _, SIPRequest invite)
    {
        string callId = invite.Header.CallId;
        var now = DateTime.UtcNow;
        foreach (var (id, at) in _rejected)
        {
            if (now - at > TimeSpan.FromMinutes(2))
                _rejected.TryRemove(id, out DateTime _);
        }
        if (!_rejected.TryAdd(callId, now))
            return; // a retransmission; the transaction answers it

        var status = (SIPResponseStatusCodesEnum)_inboundStatus();
        try
        {
            var tx = new UASInviteTransaction(_transport, invite, null);
            var response = SIPResponse.GetResponse(invite, status, null);
            response.Header.To.ToTag ??= CallProperties.CreateNewTag();
            tx.SendFinalResponse(response);
            Log.Information("Inbound call rejected with {Status}", (int)status);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not reject an inbound call");
        }
    }

    public void Dispose()
    {
        _client.IncomingCall -= RejectInbound;
        _client.Dispose();
        _transport.Shutdown();
    }
}
