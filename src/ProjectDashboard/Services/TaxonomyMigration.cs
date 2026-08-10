using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// The one-time pass that brings the stored manifests and the newly editable lists into
/// agreement. Nothing ever constrained a manifest field to the compiled-in lists, so a record
/// written by hand, imported, or left behind by an older build can hold a value no list names.
/// Those values are adopted into the lists rather than left to render as off-list forever.
/// </summary>
public static class TaxonomyMigration
{
    /// <summary>The version a settings file carries once this pass has run.</summary>
    internal const int Version = 1;

    public enum Outcome
    {
        /// <summary>The file already carried the version; nothing was read and nothing written.</summary>
        AlreadyRun,

        /// <summary>The pass ran and the version reached disk.</summary>
        Recorded,

        /// <summary>The pass ran and the write failed, so it runs again next launch.</summary>
        NotRecorded,
    }

    /// <summary>
    /// Runs at most once per settings file. Returns what it did, so a caller that cares — the
    /// tests — can tell "already migrated" from "migrated nothing", which look the same on disk.
    /// </summary>
    public static Result Run(SettingsService settings, ManifestStore manifests)
    {
        var current = settings.Load();
        if (current.SettingsSchemaVersion >= Version) return new Result(Outcome.AlreadyRun, 0);

        var config = current.Taxonomy ?? new TaxonomyConfig();
        var stored = manifests.Snapshot().Values.Select(e => e.Manifest).ToList();
        var adopted = 0;

        foreach (var field in Taxonomy.Fields)
        {
            var entries = Taxonomy.Entries(config, field);
            // First-seen order, so the same index migrated twice would produce the same list.
            foreach (var value in stored
                         .Select(m => Taxonomy.ValueOf(m, field).Trim())
                         .Where(v => v.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (entries.Any(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase))) continue;
                entries.Add(new TaxonomyEntry { Name = value });
                adopted++;
            }
        }

        current.Taxonomy = config;
        current.SettingsSchemaVersion = Version;

        if (settings.Save(current)) return new Result(Outcome.Recorded, adopted);

        Log.Warn("could not record the metadata-list migration; it will run again next launch");
        return new Result(Outcome.NotRecorded, adopted);
    }

    /// <summary>What the pass did, and how many values it took in from stored records.</summary>
    public sealed record Result(Outcome Outcome, int Adopted);
}
