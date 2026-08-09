using System.IO;
using System.Text;

namespace ProjectDashboard.Services;

/// <summary>
/// Minimal best-effort logger so swallowed failures are observable instead of silent.
/// Writes to log.txt under AppPaths.LocalDir and the debug output. Never throws.
/// An entry carries the exception's stack trace and inner chain: the summary line names what
/// failed, and a report of a swallowed failure is only actionable with where it came from.
/// The file rolls aside once it reaches <see cref="MaxLogBytes"/> so a long-running session
/// cannot grow it without bound.
/// </summary>
public static class Log
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>Log files kept in all: the live one plus the rolled generations behind it.</summary>
    private const int KeptLogFiles = 3;

    private static readonly string LogPath = AppPaths.LogFile;

    private static readonly object Gate = new();

    public static void Warn(string context, Exception? ex = null) => Write("WARN", context, ex);
    public static void Error(string context, Exception? ex = null) => Write("ERROR", context, ex);

    private static void Write(string level, string context, Exception? ex)
    {
        var summary = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {context}"
                    + (ex is null ? "" : $" :: {ex.GetType().Name}: {ex.Message}");
        var entry = ex is null ? summary : summary + Environment.NewLine + Detail(ex);

        System.Diagnostics.Debug.WriteLine(entry);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RollIfOversized(LogPath, MaxLogBytes, KeptLogFiles);
                // FileShare.ReadWrite | Delete so a concurrent reader or a delete/rotate of
                // log.txt cannot make the append throw: parallel test collections append here
                // while another path may remove the file.
                using var stream = new FileStream(
                    LogPath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(entry + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    /// <summary>
    /// The exception's full text, indented so it reads as belonging to the summary line above it.
    /// Exception.ToString carries the stack trace and walks the inner chain, an
    /// AggregateException's several inners included.
    /// </summary>
    private static string Detail(Exception ex)
    {
        var text = new StringBuilder();
        foreach (var line in ex.ToString().Split('\n'))
            text.Append("    ").Append(line.TrimEnd('\r')).Append(Environment.NewLine);
        return text.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Rolls <paramref name="path"/> aside once it reaches <paramref name="maxBytes"/>, leaving
    /// <paramref name="keep"/> files in all — the live path plus path.1, path.2, and so on — and
    /// discarding the generation that falls off the end. Best effort: a roll that loses a race
    /// with another process costs a larger file, never a lost entry, so nothing here throws.
    /// </summary>
    internal static void RollIfOversized(string path, long maxBytes, int keep)
    {
        try
        {
            var live = new FileInfo(path);
            if (!live.Exists || live.Length < maxBytes) return;

            if (keep < 2)
            {
                File.Delete(path);
                return;
            }

            for (var generation = keep - 1; generation >= 1; generation--)
            {
                var source = generation == 1 ? path : Rolled(path, generation - 1);
                if (File.Exists(source))
                    File.Move(source, Rolled(path, generation), overwrite: true);
            }
        }
        catch
        {
            // Same contract as the write: a logger that throws is worse than a large log.
        }
    }

    private static string Rolled(string path, int generation) =>
        Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            $"{Path.GetFileNameWithoutExtension(path)}.{generation}{Path.GetExtension(path)}");
}
