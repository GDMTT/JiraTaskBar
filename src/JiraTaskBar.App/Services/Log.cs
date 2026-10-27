using System.Diagnostics;
using System.IO;
using JiraTaskBar.Core.Storage;

namespace JiraTaskBar.App.Services;

/// <summary>Minimal append-only log in %LOCALAPPDATA%\JiraTaskBar\log.txt (rolled at 1 MB).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string FilePath => AppPaths.File("log.txt");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}";
        Debug.Write(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line);
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
