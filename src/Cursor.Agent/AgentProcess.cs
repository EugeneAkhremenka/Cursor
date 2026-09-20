using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cursor.Agent;

internal static class AgentProcess
{
    public static string ResolveAgentPath(string agentPath)
    {
        if (string.IsNullOrWhiteSpace(agentPath))
        {
            agentPath = "agent";
        }

        if (Path.IsPathRooted(agentPath) && File.Exists(agentPath))
        {
            return agentPath;
        }

        if (File.Exists(agentPath))
        {
            return Path.GetFullPath(agentPath);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localBin = Path.Combine(home, ".local", "bin", agentPath);
        if (File.Exists(localBin))
        {
            return localBin;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { "", ".cmd", ".exe" }
            : new[] { "" };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, agentPath + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return agentPath;
    }

    public static Process Start(string agentPath, string workingDirectory, string apiKey, ILogger logger)
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var start = new ProcessStartInfo
        {
            FileName = agentPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8
        };
        start.ArgumentList.Add("acp");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            start.Environment["CURSOR_API_KEY"] = apiKey;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {agentPath}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start Cursor CLI '{agentPath}'. Install it with curl https://cursor.com/install -fsS | bash",
                ex);
        }

        _ = PumpStderrAsync(process, logger);
        return process;
    }

    private static async Task PumpStderrAsync(Process process, ILogger logger)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    logger.LogInformation("agent: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ACP stderr pump ended");
        }
    }
}
