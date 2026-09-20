using System.Diagnostics;
using System.Text;

namespace Cursor.Telegram;

public static class GitWorkingTree
{
    public static async Task<string> DescribeAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return "Репозиторий не найден. Задайте Cursor:RepoPath.";
        }

        var status = await RunGitAsync(repoPath, ["status", "--short"], cancellationToken).ConfigureAwait(false);
        var diff = await RunGitAsync(repoPath, ["diff", "--stat"], cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("git status --short");
        sb.AppendLine(string.IsNullOrWhiteSpace(status) ? "(чисто)" : status.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("git diff --stat");
        sb.AppendLine(string.IsNullOrWhiteSpace(diff) ? "(нет unstaged diff)" : diff.TrimEnd());
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> RunGitAsync(
        string repoPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return "Не удалось запустить git.";
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            return string.IsNullOrWhiteSpace(stderr) ? $"git exited {process.ExitCode}" : stderr.Trim();
        }

        return stdout;
    }
}
