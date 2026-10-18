using System.Text.Json.Nodes;
using Cursor.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent.Tests;

public sealed class CliNetworkConfigTests
{
    [Theory]
    [InlineData("http1", true)]
    [InlineData("HTTP/1", true)]
    [InlineData("http2", false)]
    [InlineData("auto", null)]
    [InlineData("", null)]
    [InlineData("что-то", null)]
    public void ParsesProtocol(string value, bool? expected) =>
        Assert.Equal(expected, CliNetworkConfig.ParseProtocol(value));

    [Fact]
    public void KeepsEverythingElseInTheCliConfig()
    {
        const string json = """
            {
              "authInfo": { "email": "me@example.com" },
              "network": { "useHttp1ForAgent": false },
              "approvalMode": "allowlist"
            }
            """;

        Assert.True(CliNetworkConfig.TryPatch(json, useHttp1: true, out var updated));

        var root = JsonNode.Parse(updated)!;
        Assert.True(root["network"]!["useHttp1ForAgent"]!.GetValue<bool>());
        Assert.Equal("me@example.com", root["authInfo"]!["email"]!.GetValue<string>());
        Assert.Equal("allowlist", root["approvalMode"]!.GetValue<string>());
    }

    [Fact]
    public void AddsNetworkSectionWhenMissing()
    {
        Assert.True(CliNetworkConfig.TryPatch("""{"version":1}""", useHttp1: true, out var updated));

        var root = JsonNode.Parse(updated)!;
        Assert.True(root["network"]!["useHttp1ForAgent"]!.GetValue<bool>());
        Assert.Equal(1, root["version"]!.GetValue<int>());
    }

    [Fact]
    public void LeavesFileUntouchedWhenProtocolAlreadyMatches() =>
        Assert.False(CliNetworkConfig.TryPatch(
            """{"network":{"useHttp1ForAgent":true}}""",
            useHttp1: true,
            out _));

    [Fact]
    public void WritesOnlyWhenAskedForAProtocol()
    {
        var dir = Directory.CreateTempSubdirectory("cli-config").FullName;
        try
        {
            var path = Path.Combine(dir, "cli-config.json");

            CliNetworkConfig.Apply("auto", NullLogger.Instance, path);
            Assert.False(File.Exists(path));

            CliNetworkConfig.Apply("http1", NullLogger.Instance, path);
            var written = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.True(written["network"]!["useHttp1ForAgent"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
