using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PbxVoice.Hosting;
using Serilog;

namespace PbxVoice.Mcp;

/// <summary>
/// The MCP front over Streamable HTTP at <c>/mcp</c> (PR-SAFE-8). A bearer token is required on
/// every request. By default it listens on 127.0.0.1 only (deployment mode A); for a separate
/// host (mode B) put it behind an HTTPS reverse proxy.
/// </summary>
internal sealed class McpHttpFront : IAsyncDisposable
{
    public const string DefaultListen = "127.0.0.1:8765";
    public const string Path = "/mcp";

    private readonly WebApplication _app;

    private McpHttpFront(WebApplication app) => _app = app;

    /// <summary>The address actually bound (useful when the port was 0).</summary>
    public Uri Endpoint { get; private set; } = new("http://127.0.0.1/");

    public static async Task<McpHttpFront> StartAsync(IPEndPoint listen, string token, PbxVoiceTools tools, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // the daemon logs through Serilog
        builder.WebHost.UseKestrel(k => k.Listen(listen));
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "pbx-voice", Version = DaemonHost.Version })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(tools, Json.Compact);

        var app = builder.Build();
        byte[] expected = Encoding.UTF8.GetBytes("Bearer " + token);
        app.Use(async (context, next) =>
        {
            byte[] presented = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapMcp(Path);
        await app.StartAsync(ct).ConfigureAwait(false);

        var front = new McpHttpFront(app);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is not null)
            front.Endpoint = new Uri(new Uri(address.Replace("[::]", "localhost", StringComparison.Ordinal)), Path);
        return front;
    }

    /// <summary>Parses <c>host:port</c> (an IP address, or <c>localhost</c>).</summary>
    public static IPEndPoint ParseListen(string value)
    {
        int colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out int port) || port is < 0 or > 65535)
            throw new FormatException($"MCP listen address must be host:port, got '{value}'");
        string host = value[..colon].Trim('[', ']');
        var ip = host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(host);
        return new IPEndPoint(ip, port);
    }

    /// <summary>
    /// The bearer token in <c>mcp-token</c>, created on first start with 32 random bytes (mode 0600).
    /// The operator copies it into the agent's MCP configuration.
    /// </summary>
    public static string LoadOrCreateToken(string path)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
                return existing;
        }
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        SecureFile.WriteAllText(path, token + "\n");
        Log.Information("Created MCP token at {Path}", path);
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// <c>pbx-voice mcp-stdio</c>: an MCP server on stdin/stdout for agents that start their MCP servers
/// as processes (for example the Grok CLI). It forwards every tool call to the running daemon's
/// control socket. Stdout carries only the protocol; logs go to stderr.
/// </summary>
internal static class McpStdio
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var paths = StatePaths.FromEnvironment();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "pbx-voice", Version = DaemonHost.Version })
            .WithStdioServerTransport()
            .WithTools(new PbxVoiceTools(new SocketBackend(paths.ControlSocket), new PlacementRateLimiter(TimeProvider.System)), Json.Compact);
        await builder.Build().RunAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
