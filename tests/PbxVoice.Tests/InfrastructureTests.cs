using System.Net;
using System.Text;
using System.Text.Json;
using PbxVoice.Audio;
using PbxVoice.Calls;
using PbxVoice.Control;
using PbxVoice.Hosting;
using PbxVoice.Policy;
using PbxVoice.Xai;
using Xunit;

namespace PbxVoice.Tests;

public class StateFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pbx-voice-state-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void State_directories_are_0700_and_files_0600()
    {
        if (OperatingSystem.IsWindows())
            return;
        var root = Path.Combine(_dir, "state");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var paths = new StatePaths(root);
        paths.EnsureCreated();
        SecureFile.WriteAllText(paths.Policy, "{}");

        Assert.Equal(StatePaths.DirMode, File.GetUnixFileMode(root));
        Assert.Equal(StatePaths.DirMode, File.GetUnixFileMode(paths.Calls));
        Assert.Equal(StatePaths.FileMode, File.GetUnixFileMode(paths.Policy));
    }

    [Fact]
    public void State_directory_rule()
    {
        Assert.Equal("/x", StatePaths.ResolveRoot(k => k == "SIPBOT_STATE_DIR" ? "/x" : k == "XDG_STATE_HOME" ? "/xdg" : "/home/u"));
        Assert.Equal("/xdg/pbx-voice", StatePaths.ResolveRoot(k => k == "XDG_STATE_HOME" ? "/xdg" : k == "HOME" ? "/home/u" : null));
        Assert.Equal("/home/u/.local/state/pbx-voice", StatePaths.ResolveRoot(k => k == "HOME" ? "/home/u" : null));
    }

    [Fact]
    public void A_long_state_path_moves_the_control_socket_somewhere_short()
    {
        string shortRoot = "/home/u/.local/state/pbx-voice";
        Assert.Equal(shortRoot + "/control.sock", StatePaths.SocketPathFor(shortRoot, _ => null));

        string longRoot = "/tmp/" + new string('x', 120) + "/state";
        string path = StatePaths.SocketPathFor(longRoot, k => k == "XDG_RUNTIME_DIR" ? _dir : null);
        Assert.StartsWith(_dir + "/pbx-voice-", path);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) <= 107);
        Assert.Equal(path, StatePaths.SocketPathFor(longRoot, k => k == "XDG_RUNTIME_DIR" ? _dir : null)); // stable for ctl
    }

    [Fact]
    public void Only_one_daemon_per_extension()
    {
        using var first = AorLock.TryAcquire(_dir, "101@pbx.example.test");
        Assert.NotNull(first);
        Assert.Null(AorLock.TryAcquire(_dir, "101@PBX.example.test"));
        using var other = AorLock.TryAcquire(_dir, "104@pbx.example.test");
        Assert.NotNull(other);
        first!.Dispose();
        using var again = AorLock.TryAcquire(_dir, "101@pbx.example.test");
        Assert.NotNull(again);
    }

    [Fact]
    public void Secrets_come_from_the_file_with_the_environment_winning()
    {
        var file = Path.Combine(_dir, "secrets.env");
        File.WriteAllText(file, "# comment\nexport SIP_SERVER=pbx.example.test\nSIP_USERNAME='101'\nSIP_PASSWORD=\"p=w\"\nSIP_PORT=5080\nXAI_API_KEY=file-key\nSIP_EXTENDED_RETRY=0\n");
        var secrets = Secrets.Load(file, k => k == "XAI_API_KEY" ? "env-key" : null);
        var sip = secrets.ToSipConfig();

        Assert.Equal("env-key", secrets["XAI_API_KEY"]);
        Assert.Equal("pbx.example.test:5080", sip.Server);
        Assert.Equal("101", sip.Username);
        Assert.Equal("p=w", sip.Password);
        Assert.True(sip.RegistrationRetry.Extended);
        Assert.Null(secrets["SIP_EXTENDED_RETRY"]);
    }

    [Fact]
    public void Missing_sip_settings_are_an_error()
    {
        var secrets = Secrets.Load(Path.Combine(_dir, "none.env"), _ => null);
        Assert.Throws<InvalidOperationException>(() => secrets.ToSipConfig());
    }

    [Fact]
    public void Call_records_survive_a_restart_and_old_ones_are_pruned()
    {
        var store = new CallStore(_dir);
        var now = DateTimeOffset.UtcNow;
        store.Add(new CallRecord { CallId = "old", Status = CallStatus.Done, CreatedAt = now.AddDays(-40) });
        store.Add(new CallRecord { CallId = "new", Status = CallStatus.Pending, CreatedAt = now });

        var reloaded = new CallStore(_dir);
        Assert.Equal(2, reloaded.SnapshotAll().Count);
        Assert.Equal(1, reloaded.Prune(now.AddDays(-30)));
        Assert.Equal("new", new CallStore(_dir).SnapshotAll().Single().CallId);
    }
}

public class PolicyTests
{
    internal static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PbxVoice.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void The_example_policy_parses()
    {
        var p = PolicyLoader.Load(RepoFile("docs/policy.example.json"));
        Assert.True(p.Contacts["me"].Self);
        Assert.Equal(480, p.InboundRejectStatus);
    }

    [Theory]
    [InlineData("""{"timezone":"Mars/Olympus"}""", "unknown time zone")]
    [InlineData("""{"contacts":{"x":{"number":""}}}""", "number is required")]
    [InlineData("""{"inbound_reject_status":200}""", "inbound_reject_status")]
    [InlineData("""{"quiet_hours":{"start":"9pm","end":"08:00","tz":"UTC"}}""", "quiet_hours.start")]
    [InlineData("""{"phrases":{"awake":[]}}""", "phrases.awake")]
    public void Invalid_policies_are_rejected(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PolicyLoader.Parse(json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_broken_edit_keeps_the_last_good_policy()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """{"contacts":{"me":{"number":"101","self":true}}}""");
            var provider = new PolicyProvider(file);
            File.WriteAllText(file, "{ not json");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
            Assert.True(provider.Current.Contacts.ContainsKey("me"));
            Assert.NotNull(provider.LastError);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("+15555550100", "***0100")]
    [InlineData("104", "104")]
    [InlineData("sip:someone@example.com", "***")]
    public void Numbers_are_masked(string number, string masked) => Assert.Equal(masked, PolicyGuard.Mask(number));
}

public class XaiTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public byte[] Body = Array.Empty<byte>();
        public Func<HttpResponseMessage> Respond = () => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            return Respond();
        }
    }

    [Fact]
    public async Task Tts_asks_for_8kHz_mulaw_and_reads_base64_audio()
    {
        var rec = new Recorder
        {
            Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { audio = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }), Encoding.UTF8, "application/json"),
            },
        };
        using var xai = new XaiSpeech("test-key", handler: rec);

        var audio = await xai.SynthesizeMuLawAsync("Hello", "eve", "en", 1.0, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, audio);
        Assert.Equal("https://api.x.ai/v1/tts", rec.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", rec.Request.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(rec.Body);
        Assert.Equal("mulaw", body.RootElement.GetProperty("output_format").GetProperty("codec").GetString());
        Assert.Equal(8000, body.RootElement.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
        Assert.Equal("eve", body.RootElement.GetProperty("voice_id").GetString());
    }

    [Fact]
    public void Tts_audio_may_come_raw_or_as_a_wav()
    {
        var raw = new byte[] { 9, 8, 7 };
        Assert.Equal(raw, XaiSpeech.ExtractAudio(raw, "audio/basic"));
        Assert.Equal(raw, XaiSpeech.ExtractAudio(MuLawWav.Build(raw), "audio/wav"));
    }

    [Fact]
    public async Task Stt_sends_raw_mulaw_as_a_multipart_form_with_the_file_last()
    {
        var rec = new Recorder
        {
            Respond = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"I'm up.","words":[]}""") },
        };
        using var xai = new XaiSpeech("test-key", handler: rec);

        var result = await xai.TranscribeMuLawAsync(new byte[800], new[] { "i'm up", "snooze" }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("I'm up.", result.Text);
        Assert.Equal("multipart/form-data", rec.Request!.Content!.Headers.ContentType!.MediaType);
        string form = Encoding.UTF8.GetString(rec.Body);
        Assert.Contains("name=audio_format", form);
        Assert.Contains("mulaw", form);
        Assert.Contains("name=sample_rate", form);
        Assert.Contains("name=keyterm", form);
        Assert.True(form.LastIndexOf("name=file", StringComparison.Ordinal) > form.LastIndexOf("name=keyterm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stt_errors_are_failures_not_exceptions()
    {
        var rec = new Recorder { Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") } };
        using var xai = new XaiSpeech("test-key", handler: rec);
        var result = await xai.TranscribeMuLawAsync(new byte[800], Array.Empty<string>(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("503", result.Error);
    }

    [Fact]
    public void Mulaw_wav_round_trip_and_silence_bytes_never_match_the_keep_alive_frame()
    {
        var data = Enumerable.Repeat((byte)0x7F, 320).ToArray();
        Assert.Equal(data, MuLawWav.Read(MuLawWav.Build(data)));
        Assert.DoesNotContain((byte)0x7F, new Clip("x", data).MuLaw);
    }
}

public class ControlSocketTests
{
    [Fact]
    public async Task A_request_round_trips_over_the_socket()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var h = new Harness();
        await using var server = new ControlServer(h.Paths.ControlSocket, h.Service.HandleAsync);
        server.Start();

        string ok = await ControlClient.SendAsync(h.Paths.ControlSocket, "list_contacts", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        string error = await ControlClient.SendAsync(h.Paths.ControlSocket, "call_now", JsonDocument.Parse("""{"type":"alarm","to":"mom"}""").RootElement, CancellationToken.None);

        Assert.StartsWith("""{"ok":true""", ok);
        Assert.Contains("***0101", ok);
        Assert.StartsWith("""{"ok":false""", error);
        Assert.Contains("self", error);
        Assert.Equal(StatePaths.FileMode, File.GetUnixFileMode(h.Paths.ControlSocket));
    }
}
