using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Cursor.Agent;

/// <summary>
/// The CLI picks HTTP/1 vs HTTP/2 only from <c>network.useHttp1ForAgent</c> in its own
/// config file — there is no flag and no environment variable — so the worker writes it
/// there before starting the agent. The server can still force a protocol on its side.
/// </summary>
internal static class CliNetworkConfig
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>true = HTTP/1, false = HTTP/2, null = leave the CLI config alone.</summary>
    public static bool? ParseProtocol(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "http1" or "http/1" or "http1.1" => true,
            "http2" or "http/2" => false,
            _ => null
        };

    public static string ResolveConfigPath()
    {
        var configDir = Environment.GetEnvironmentVariable("CURSOR_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir))
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            configDir = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor")
                : Path.Combine(xdg, "cursor");
        }

        return Path.Combine(configDir, "cli-config.json");
    }

    /// <summary>
    /// Returns false when the file already asks for this protocol, so an unchanged config
    /// is never rewritten (it also holds the CLI's auth and permissions).
    /// </summary>
    public static bool TryPatch(string? json, bool useHttp1, out string updated)
    {
        updated = "";
        var root = string.IsNullOrWhiteSpace(json)
            ? new JsonObject()
            : JsonNode.Parse(json) as JsonObject
              ?? throw new JsonException("cli-config.json is not a JSON object.");

        if (root["network"] is not JsonObject network)
        {
            network = new JsonObject();
            root["network"] = network;
        }

        if (network["useHttp1ForAgent"] is JsonValue current
            && current.TryGetValue<bool>(out var existing)
            && existing == useHttp1)
        {
            return false;
        }

        network["useHttp1ForAgent"] = useHttp1;
        updated = root.ToJsonString(WriteOptions);
        return true;
    }

    public static void Apply(string? protocol, ILogger logger, string? configPath = null)
    {
        var useHttp1 = ParseProtocol(protocol);
        if (useHttp1 is null)
        {
            var trimmed = protocol?.Trim() ?? "";
            if (trimmed.Length > 0 && !trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Cursor:AgentHttpProtocol '{Value}' is not auto, http1 or http2; leaving the CLI config alone.",
                    trimmed);
            }

            return;
        }

        var path = configPath ?? ResolveConfigPath();
        try
        {
            var json = File.Exists(path) ? File.ReadAllText(path) : null;
            if (!TryPatch(json, useHttp1.Value, out var updated))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, updated);
            logger.LogInformation(
                "Switched Cursor CLI to {Protocol} in {Path}",
                useHttp1.Value ? "HTTP/1" : "HTTP/2",
                path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not set the CLI network protocol in {Path}", path);
        }
    }
}
