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

    internal static IReadOnlyList<string> BuildAcpArguments(
        string? model,
        string? mode,
        bool endlessRetries = false)
    {
        var args = new List<string>();
        AppendFlag(args, "--model", model);
        // CLI --mode only accepts plan|ask. Default agent mode is omitting the flag.
        if (!IsDefaultAgentMode(mode))
        {
            AppendFlag(args, "--mode", mode);
        }

        if (endlessRetries)
        {
            // Hidden CLI flag: resume retriable transport failures instead of ending the turn.
            // The caller bounds it with an idle watchdog.
            args.Add("--endless-retries");
        }

        args.Add("acp");
        return args;
    }

    private static bool IsDefaultAgentMode(string? mode)
    {
        var trimmed = mode?.Trim() ?? "";
        return trimmed.Length == 0
               || trimmed.Equals("agent", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendFlag(List<string> args, string flag, string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return;
        }

        args.Add(flag);
        args.Add(trimmed);
    }

    private static string QuoteCmd(string value)
    {
        if (value.IndexOfAny([' ', '"', '&', '|', '^', '<', '>']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "") + "\"";
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

    public static Process Start(
        string agentPath,
        string workingDirectory,
        string apiKey,
        ILogger logger,
        string? model = null,
        string? mode = null,
        bool endlessRetries = false)
    {
        var start = CreateStartInfo(agentPath, workingDirectory, apiKey, model, mode, endlessRetries);
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

    internal static ProcessStartInfo CreateStartInfo(
        string agentPath,
        string workingDirectory,
        string apiKey,
        string? model = null,
        string? mode = null,
        bool endlessRetries = false)
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

        var args = BuildAcpArguments(model, mode, endlessRetries);
        if (NeedsCmdWrapper(agentPath))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            // cmd /s strips the first and last quote of the /c payload, so wrap the
            // whole command: ""C:\path\agent.cmd" --model auto acp"
            var quotedPath = "\"" + agentPath.Replace("\"", "") + "\"";
            start.Arguments = "/d /s /c \"" + quotedPath + " " + string.Join(' ', args.Select(QuoteCmd)) + "\"";
        }
        else
        {
            start.FileName = agentPath;
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
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
