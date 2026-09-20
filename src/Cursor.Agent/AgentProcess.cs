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

        foreach (var candidate in WellKnownPaths(agentPath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { "", ".cmd", ".exe", ".bat" }
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

    public static IEnumerable<string> WellKnownPaths(string agentPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "bin", agentPath);

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localApp))
        {
            yield break;
        }

        var cursorAgent = Path.Combine(localApp, "cursor-agent");
        yield return Path.Combine(cursorAgent, "agent.cmd");
        yield return Path.Combine(cursorAgent, "agent.exe");
        yield return Path.Combine(cursorAgent, "cursor-agent.cmd");
        yield return Path.Combine(cursorAgent, "cursor-agent.exe");
        yield return Path.Combine(cursorAgent, agentPath);
        yield return Path.Combine(cursorAgent, agentPath + ".cmd");
        yield return Path.Combine(cursorAgent, agentPath + ".exe");
    }

    public static Process Start(string agentPath, string workingDirectory, string apiKey, ILogger logger)
    {
        var start = CreateStartInfo(agentPath, workingDirectory, apiKey);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {agentPath}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start Cursor CLI '{agentPath}'. On Windows: irm 'https://cursor.com/install?win32=true' | iex, then set Cursor:AgentPath to %LOCALAPPDATA%\\cursor-agent\\agent.cmd",
                ex);
        }

        _ = PumpStderrAsync(process, logger);
        return process;
    }

    internal static ProcessStartInfo CreateStartInfo(string agentPath, string workingDirectory, string apiKey)
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var start = new ProcessStartInfo
        {
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

        if (NeedsCmdWrapper(agentPath))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = "/d /s /c \"" + agentPath.Replace("\"", "") + "\" acp";
        }
        else
        {
            start.FileName = agentPath;
            start.ArgumentList.Add("acp");
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            start.Environment["CURSOR_API_KEY"] = apiKey;
        }

        return start;
    }

    internal static bool NeedsCmdWrapper(string agentPath)
    {
        var ext = Path.GetExtension(agentPath);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
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
