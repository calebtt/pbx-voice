using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PbxVoice.Tests;

/// <summary>
/// The Grok and Cursor manifests and marketplace files describe one plugin, so a release bump or a
/// description change has to reach all of them.
/// </summary>
public class PluginManifestTests
{
    private static JsonElement Json(string relative) =>
        JsonDocument.Parse(File.ReadAllText(PolicyTests.RepoFile(relative))).RootElement;

    private static readonly JsonElement Grok = Json("plugin/.grok-plugin/plugin.json");
    private static readonly JsonElement Cursor = Json("plugin/.cursor-plugin/plugin.json");

    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("description")]
    public void The_Grok_and_Cursor_manifests_agree(string field) =>
        Assert.Equal(Grok.GetProperty(field).GetString(), Cursor.GetProperty(field).GetString());

    [Theory]
    [InlineData(".grok-plugin/marketplace.json")]
    [InlineData(".cursor-plugin/marketplace.json")]
    public void Marketplace_entries_match_the_manifest(string marketplace)
    {
        var entry = Json(marketplace).GetProperty("plugins").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == Grok.GetProperty("name").GetString());
        Assert.Equal(Grok.GetProperty("description").GetString(), entry.GetProperty("description").GetString());
    }

    private static string DaemonVersion() =>
        Regex.Match(File.ReadAllText(PolicyTests.RepoFile("src/PbxVoice.Daemon/PbxVoice.Daemon.csproj")), "<Version>(.+?)</Version>").Groups[1].Value;

    [Fact]
    public void The_manifest_version_is_the_daemon_version() =>
        Assert.Equal(DaemonVersion(), Grok.GetProperty("version").GetString());

    [Fact]
    public void The_README_release_example_uses_the_daemon_version()
    {
        string readme = File.ReadAllText(PolicyTests.RepoFile("README.md"));
        Assert.Equal(DaemonVersion(), Regex.Match(readme, @"(?m)^v=(\S+)$").Groups[1].Value);
    }

    [Fact]
    public void The_Cursor_manifest_points_at_the_MCP_config()
    {
        Assert.Matches("^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$", Cursor.GetProperty("name").GetString());
        Assert.True(File.Exists(PolicyTests.RepoFile(Path.Combine("plugin", Cursor.GetProperty("mcpServers").GetString()!))));
    }
}
