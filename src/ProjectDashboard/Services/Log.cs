using System.IO;

namespace ProjectDashboard.Services;

/// <summary>
/// Minimal best-effort logger so swallowed failures are observable instead of silent.
/// Writes to %LOCALAPPDATA%\ProjectDashboard\log.txt and the debug output. Never throws.
/// </summary>
public static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectDashboard", "log.txt");

    private static readonly object Gate = new();

    public static void Warn(string context, Exception? ex = null) => Write("WARN", context, ex);
    public static void Error(string context, Exception? ex = null) => Write("ERROR", context, ex);

    private static void Write(string level, string context, Exception? ex)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {context}"
                 + (ex is null ? "" : $" :: {ex.GetType().Name}: {ex.Message}");

        System.Diagnostics.Debug.WriteLine(line);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
