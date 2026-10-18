using System.Diagnostics;
using System.Globalization;

namespace Cursor.Telegram;

internal static class UserWorkspace
{
    public static string DefaultRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cursor.TelegramHost",
            "workspaces");

    public static string Ensure(long userId, string? root)
    {
        var baseDir = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root.Trim();
        var path = Path.GetFullPath(Path.Combine(baseDir, userId.ToString(CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(path);
        TryGitInit(path);
        return path;
    }

    private static void TryGitInit(string path)
    {
        if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "init" },
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
        }
        catch (Exception)
        {
        }
    }
}
