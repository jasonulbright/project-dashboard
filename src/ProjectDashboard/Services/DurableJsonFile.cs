using System.IO;
using System.Text.Json;

namespace ProjectDashboard.Services;

/// <summary>
/// Crash-safe persistence for the JSON state files. A write lands in a sibling
/// .tmp that is flushed to disk and atomically swapped in (the previous version
/// survives as .bak), so an interrupted write can never truncate the live file.
/// A file that no longer parses is quarantined as .corrupt-&lt;utcstamp&gt; —
/// never discarded — and the .bak is tried before the caller falls back to
/// defaults. This is the required pattern for every durable store.
/// </summary>
internal static class DurableJsonFile
{
    public static void Write(string path, string json)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            // Force the tmp contents to disk BEFORE the swap; otherwise a power cut
            // can commit the rename while the data is still in the OS write cache.
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }

    /// <summary>Null means nothing usable on disk: missing file, or corrupt with no readable backup.</summary>
    public static T? Read<T>(string path, JsonSerializerOptions options) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (JsonException ex)
        {
            var quarantine = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
            Log.Error($"Corrupt JSON in {path} — quarantining as {Path.GetFileName(quarantine)}, trying .bak", ex);
            try { File.Move(path, quarantine); }
            catch (Exception moveEx) { Log.Error($"Failed to quarantine {path}", moveEx); }

            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    var bakJson = File.ReadAllText(bak);
                    var recovered = JsonSerializer.Deserialize<T>(bakJson, options);
                    // Restore the live file now; recovery held only in memory would
                    // evaporate on a session that never saves, and the NEXT launch
                    // would then silently start from defaults.
                    try { Write(path, bakJson); }
                    catch (Exception writeEx) { Log.Error($"Failed to restore {path} from {bak}", writeEx); }
                    Log.Warn($"Recovered {path} from {bak}");
                    return recovered;
                }
                catch (Exception bakEx)
                {
                    Log.Error($"Backup {bak} is also unreadable", bakEx);
                }
            }

            Log.Error($"No usable backup for {path} — starting from defaults");
            return null;
        }
    }
}
