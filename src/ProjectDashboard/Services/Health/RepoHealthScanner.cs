using System.IO;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Health;

/// <summary>One lock file found under the git directory, and whether it is old enough to look abandoned.</summary>
public sealed record LockFile(string RelativePath, DateTime CreatedUtc, bool Stale);

/// <summary>
/// The git one repository's health page runs. Every read here is read-only and leaseless: the page
/// describes a repository, it never claims one. Skipping a repository another operation holds is
/// the caller's decision, made against <see cref="RepoBusyRegistry"/> before a path is handed over
/// — the same split the safety rollup uses.
///
/// The split by cost is the point of the type. The quick tier is local reads bounded by a short
/// budget and runs on tab activation; the deep tier reads every object or reaches a network and is
/// entered only on an explicit press. Nothing here escalates from one to the other on its own: a
/// connectivity pass that comes back clean does not go on to read object contents, because a
/// reader who asked for the cheap answer did not ask to wait for the expensive one.
/// </summary>
public sealed class RepoHealthScanner
{
    /// <summary>Budget for one quick-tier read. The whole tier is a handful of these, run together.</summary>
    internal static readonly TimeSpan QuickReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>`fsck --connectivity-only` skips content hashing, so it is bounded in minutes rather than tens of them.</summary>
    internal static readonly TimeSpan ConnectivityTimeout = TimeSpan.FromMinutes(2);

    /// <summary>`fsck --strict` reads and hashes every object; the budget is sized for a repository with years of packs.</summary>
    internal static readonly TimeSpan StrictTimeout = TimeSpan.FromMinutes(20);

    /// <summary>One remote's probe. Short: with terminal prompting off, an unreachable remote fails rather than waits.</summary>
    internal static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Each pass of the large-object walk. Two passes, so the walk's own ceiling is twice this.</summary>
    internal static readonly TimeSpan ObjectWalkTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How many objects the large-object report names. The ranking holds this many, never the store.</summary>
    internal const int LargeObjectCount = 10;

    /// <summary>
    /// How old a lock file must be before it is reported as looking abandoned. The same threshold
    /// the index.lock cleanup uses, for the same reason: an index write completes in seconds even
    /// on a very large repository.
    /// </summary>
    internal static readonly TimeSpan LockStaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>Bounds one directory walk. A repository with more lock files than this has a story the count already tells.</summary>
    internal const int LockScanCeiling = 200;

    private readonly GitService _git;
    private readonly BackupService? _backups;
    private readonly SafetyScanner _safety;
    private readonly OperationHistory _history;

    public RepoHealthScanner(GitService git, BackupService? backups = null, OperationHistory? history = null)
    {
        _git = git;
        _backups = backups;
        _history = history ?? new OperationHistory();
        // The bundle verifier is shared rather than reimplemented: the rollup, the Backups browser,
        // and this page report the same bundles, and a second verifier would word one two ways.
        _safety = new SafetyScanner(git, backups, _history);
    }

    // ── Quick tier ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every local check, run together. No result here says anything about object integrity —
    /// that is the deep tier's answer and it stays unrun until asked for.
    /// </summary>
    public async Task<IReadOnlyList<HealthCheck>> QuickAsync(string repoPath, CancellationToken ct = default)
    {
        var gitDir = await _git.ResolveGitDirAsync(repoPath, ct, QuickReadTimeout);

        return
        [
            await GitVersionAsync(repoPath, ct),
            await LocksAsync(gitDir, DateTime.UtcNow),
            await ObjectStoreAsync(repoPath, ct),
            await SigningAsync(repoPath, ct),
            await HooksAsync(repoPath, gitDir, ct),
            await LfsAsync(repoPath, ct),
            await RemotesAsync(repoPath, ct),
            await BackupsOnDiskAsync(repoPath, ct),
        ];
    }

    private async Task<HealthCheck> GitVersionAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, ["--version"], ct, QuickReadTimeout);
        if (!result.Success)
            return new HealthCheck(HealthCheckId.GitVersion, "git version", HealthState.Unknown,
                "git did not answer.", result.FirstError, HealthTier.Quick);

        var line = result.StdOut.Trim();
        return GitVersion.TokenFrom(line) is { } token
            ? new HealthCheck(HealthCheckId.GitVersion, "git version", HealthState.Ok,
                $"git {token}", line, HealthTier.Quick)
            : new HealthCheck(HealthCheckId.GitVersion, "git version", HealthState.Unknown,
                "git printed a version line this build could not read.", line, HealthTier.Quick);
    }

    /// <summary>
    /// Every lock file under the git directory, not only index.lock. A held HEAD.lock or
    /// packed-refs.lock blocks ref writes exactly as an index.lock blocks index writes, and
    /// nothing in this application looked for either before.
    /// </summary>
    internal async Task<HealthCheck> LocksAsync(string? gitDir, DateTime nowUtc)
    {
        if (gitDir is null)
            return new HealthCheck(HealthCheckId.Locks, "Lock files", HealthState.Unknown,
                "The git directory could not be resolved, so no lock file was looked for.", "",
                HealthTier.Quick);

        List<LockFile> locks;
        try
        {
            locks = await Task.Run(() => ScanLocks(gitDir, nowUtc));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not scan {gitDir} for lock files", ex);
            return new HealthCheck(HealthCheckId.Locks, "Lock files", HealthState.Unknown,
                "The git directory could not be read, so no lock file was looked for.", ex.Message,
                HealthTier.Quick);
        }

        if (locks.Count == 0)
            return new HealthCheck(HealthCheckId.Locks, "Lock files", HealthState.Ok,
                "No lock file is present.", "", HealthTier.Quick);

        var stale = locks.Count(l => l.Stale);
        var summary = stale == 0
            ? $"{locks.Count} lock file(s) present — a git process may be running right now."
            : $"{locks.Count} lock file(s) present, {stale} older than {LockStaleAfter.TotalMinutes:0} minutes.";
        var detail = string.Join(Environment.NewLine,
            locks.Select(l => $"{l.RelativePath} — created {SafetyCopy.Stamp(l.CreatedUtc)}{(l.Stale ? ", looks abandoned" : "")}"));

        return new HealthCheck(HealthCheckId.Locks, "Lock files",
            stale == 0 ? HealthState.Warn : HealthState.Bad,
            summary, detail + Environment.NewLine + HealthCopy.LocksAreReportedNotRemoved, HealthTier.Quick);
    }

    /// <summary>
    /// The lock files under a git directory: the top level, where index.lock, HEAD.lock,
    /// config.lock, packed-refs.lock and FETCH_HEAD.lock live, and every ref lock under refs/.
    /// </summary>
    private static List<LockFile> ScanLocks(string gitDir, DateTime nowUtc)
    {
        var found = new List<LockFile>();
        foreach (var path in LockPaths(gitDir))
        {
            if (found.Count >= LockScanCeiling) break;
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var created = info.CreationTimeUtc;
            found.Add(new LockFile(
                Path.GetRelativePath(gitDir, path).Replace('\\', '/'),
                created,
                nowUtc - created >= LockStaleAfter));
        }
        return found;
    }

    private static IEnumerable<string> LockPaths(string gitDir)
    {
        foreach (var path in Directory.EnumerateFiles(gitDir, "*.lock", SearchOption.TopDirectoryOnly))
            yield return path;

        var refs = Path.Combine(gitDir, "refs");
        if (!Directory.Exists(refs)) yield break;
        foreach (var path in Directory.EnumerateFiles(refs, "*.lock", SearchOption.AllDirectories))
            yield return path;
    }

    private async Task<HealthCheck> ObjectStoreAsync(string repoPath, CancellationToken ct)
    {
        var counts = await _git.CountObjectsAsync(repoPath, ct);
        return counts is null
            ? new HealthCheck(HealthCheckId.ObjectStore, "Object store", HealthState.Unknown,
                "Not measured — git could not read the object store.", "", HealthTier.Quick)
            : new HealthCheck(HealthCheckId.ObjectStore, "Object store", HealthState.Ok,
                $"{counts.TotalObjects} object(s), {HealthCopy.Kib(counts.TotalKiB)}.",
                $"{counts.LooseObjects} loose ({HealthCopy.Kib(counts.LooseKiB)}), "
                + $"{counts.PackedObjects} packed ({HealthCopy.Kib(counts.PackKiB)}). "
                + HealthCopy.SizeIsNotIntegrity,
                HealthTier.Quick);
    }

    /// <summary>
    /// Signing as this repository is configured, and labelled as configuration. Reporting a
    /// repository set to sign as one whose commits are signed would claim a verification nothing
    /// here runs.
    /// </summary>
    private async Task<HealthCheck> SigningAsync(string repoPath, CancellationToken ct)
    {
        var commit = await ConfigAsync(repoPath, "commit.gpgsign", boolean: true, ct);
        var tag = await ConfigAsync(repoPath, "tag.gpgsign", boolean: true, ct);
        var format = await ConfigAsync(repoPath, "gpg.format", boolean: false, ct);
        var key = await ConfigAsync(repoPath, "user.signingkey", boolean: false, ct);

        if (commit.Unreadable || tag.Unreadable || format.Unreadable || key.Unreadable)
            return new HealthCheck(HealthCheckId.Signing, "Signing", HealthState.Unknown,
                "This repository's signing configuration could not be read.",
                commit.Error ?? tag.Error ?? format.Error ?? key.Error ?? "", HealthTier.Quick);

        var commitOn = commit.Value == "true";
        var tagOn = tag.Value == "true";
        var detail =
            $"commit.gpgsign {Describe(commit)}; tag.gpgsign {Describe(tag)}; "
            + $"gpg.format {Describe(format)}; user.signingkey {Describe(key)}. "
            + HealthCopy.SigningIsConfigurationOnly;

        if ((commitOn || tagOn) && key.Value.Length == 0)
            return new HealthCheck(HealthCheckId.Signing, "Signing", HealthState.Warn,
                "Signing is on and no signing key is configured, so git falls back to the committer identity.",
                detail, HealthTier.Quick);

        var summary = (commitOn, tagOn) switch
        {
            (true, true) => "Commits and tags are configured to be signed.",
            (true, false) => "Commits are configured to be signed.",
            (false, true) => "Tags are configured to be signed.",
            _ => "Neither commits nor tags are configured to be signed.",
        };
        return new HealthCheck(HealthCheckId.Signing, "Signing", HealthState.Ok, summary, detail, HealthTier.Quick);
    }

    private static string Describe(ConfigValue value) =>
        value.Value.Length == 0 ? "not set" : value.Value;

    /// <summary>
    /// One configuration value. <see cref="Unreadable"/> separates a key that is not set — which
    /// git reports with exit code 1 and is an answer — from a read that failed, which is not.
    /// </summary>
    private readonly record struct ConfigValue(string Value, bool Unreadable, string? Error);

    private async Task<ConfigValue> ConfigAsync(string repoPath, string key, bool boolean, CancellationToken ct)
    {
        string[] args = boolean
            ? ["config", "--type=bool", "--get", key]
            : ["config", "--get", key];
        var result = await _git.RunAsync(repoPath, args, ct, QuickReadTimeout);
        if (result.Success) return new ConfigValue(result.StdOut.Trim(), false, null);
        // git exits 1 for a key that is not set, which is an answer; anything else is a failed read.
        return result.ExitCode == 1 && !result.TimedOut
            ? new ConfigValue("", false, null)
            : new ConfigValue("", true, result.FirstError);
    }

    /// <summary>
    /// The hooks that would run against this repository. A hooksPath pointing outside the
    /// repository is reported with the path: it is a legitimate setup and also the shape a
    /// repository carries when something else has redirected its hooks.
    /// </summary>
    private async Task<HealthCheck> HooksAsync(string repoPath, string? gitDir, CancellationToken ct)
    {
        var configured = await ConfigAsync(repoPath, "core.hooksPath", boolean: false, ct);
        if (configured.Unreadable)
            return new HealthCheck(HealthCheckId.Hooks, "Hooks", HealthState.Unknown,
                "core.hooksPath could not be read, so the hook directory is unknown.",
                configured.Error ?? "", HealthTier.Quick);

        string? directory;
        if (configured.Value.Length > 0)
        {
            try { directory = Path.GetFullPath(configured.Value, repoPath); }
            catch (Exception ex)
            {
                return new HealthCheck(HealthCheckId.Hooks, "Hooks", HealthState.Unknown,
                    $"core.hooksPath is set to '{configured.Value}', which is not a path this build could resolve.",
                    ex.Message, HealthTier.Quick);
            }
        }
        else
        {
            if (gitDir is null)
                return new HealthCheck(HealthCheckId.Hooks, "Hooks", HealthState.Unknown,
                    "The git directory could not be resolved, so the default hook directory is unknown.", "",
                    HealthTier.Quick);
            directory = Path.Combine(gitDir, "hooks");
        }

        var outside = configured.Value.Length > 0 && !IsInside(directory, repoPath);

        List<string> hooks;
        try
        {
            hooks = await Task.Run(() => Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Where(f => !f.EndsWith(".sample", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : []);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not list the hooks of {repoPath}", ex);
            return new HealthCheck(HealthCheckId.Hooks, "Hooks", HealthState.Unknown,
                $"The hook directory {directory} could not be read.", ex.Message, HealthTier.Quick);
        }

        var where = configured.Value.Length > 0
            ? $"core.hooksPath is set to {directory}."
            : $"Hooks are read from {directory}.";
        var installed = hooks.Count == 0
            ? "No hook is installed."
            : $"{hooks.Count} hook(s) installed: {string.Join(", ", hooks)}.";

        return outside
            ? new HealthCheck(HealthCheckId.Hooks, "Hooks", HealthState.Warn,
                $"Hooks run from outside this repository. {installed}",
                where + " A hook directory outside the repository is not carried by a clone and is not "
                + "described by anything under version control here.", HealthTier.Quick)
            : new HealthCheck(HealthCheckId.Hooks, "Hooks", HealthState.Ok, installed, where, HealthTier.Quick);
    }

    /// <summary>Whether <paramref name="candidate"/> is the repository directory or sits under it.</summary>
    internal static bool IsInside(string candidate, string root)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return string.Equals(full, top, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this repository routes files through LFS, and whether the filter that does it is
    /// installed. Attributes naming a filter git cannot run is the state that leaves a checkout
    /// holding pointer files instead of content.
    /// </summary>
    private async Task<HealthCheck> LfsAsync(string repoPath, CancellationToken ct)
    {
        string attributes;
        var attributesPath = Path.Combine(repoPath, ".gitattributes");
        try
        {
            attributes = File.Exists(attributesPath) ? await File.ReadAllTextAsync(attributesPath, ct) : "";
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the .gitattributes of {repoPath}", ex);
            return new HealthCheck(HealthCheckId.Lfs, "Large File Storage", HealthState.Unknown,
                "The repository's .gitattributes could not be read, so whether it uses LFS is unknown.",
                ex.Message, HealthTier.Quick);
        }

        if (!attributes.Contains("filter=lfs", StringComparison.OrdinalIgnoreCase))
            return new HealthCheck(HealthCheckId.Lfs, "Large File Storage", HealthState.NotApplicable,
                "No rule in .gitattributes routes files through LFS.", "", HealthTier.Quick);

        var version = await _git.RunAsync(repoPath, ["lfs", "version"], ct, QuickReadTimeout);
        return version.Success
            ? new HealthCheck(HealthCheckId.Lfs, "Large File Storage", HealthState.Ok,
                $"LFS is in use and installed: {version.StdOut.Trim()}", "", HealthTier.Quick)
            : new HealthCheck(HealthCheckId.Lfs, "Large File Storage", HealthState.Bad,
                "This repository routes files through LFS and git-lfs did not answer, so those files "
                + "check out as pointer text rather than content.",
                version.FirstError, HealthTier.Quick);
    }

    private async Task<HealthCheck> RemotesAsync(string repoPath, CancellationToken ct)
    {
        var remotes = await _git.GetRemotesAsync(repoPath, ct);
        if (remotes.HasError)
            return new HealthCheck(HealthCheckId.Remotes, "Remotes", HealthState.Unknown,
                "This repository's remotes could not be read.", remotes.ErrorText, HealthTier.Quick);

        return remotes.Remotes.Count == 0
            ? new HealthCheck(HealthCheckId.Remotes, "Remotes", HealthState.NotApplicable,
                "No remote is configured, so nothing here is pushed anywhere.", "", HealthTier.Quick)
            : new HealthCheck(HealthCheckId.Remotes, "Remotes", HealthState.Ok,
                $"{remotes.Remotes.Count} remote(s): {string.Join(", ", remotes.Remotes.Select(r => r.Name))}.",
                string.Join(Environment.NewLine, remotes.Remotes.Select(r => $"{r.Name} → {r.FetchUrl}")),
                HealthTier.Quick);
    }

    private async Task<HealthCheck> BackupsOnDiskAsync(string repoPath, CancellationToken ct)
    {
        if (_backups is null)
            return new HealthCheck(HealthCheckId.Backups, "Backups on disk", HealthState.Unknown,
                "No backup store is configured for this session, so what is on disk is unknown.", "",
                HealthTier.Quick);

        List<BackupHandle> handles;
        try
        {
            handles = await _backups.ListBackupsAsync(repoPath, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not list the backups of {repoPath} for the health page", ex);
            return new HealthCheck(HealthCheckId.Backups, "Backups on disk", HealthState.Unknown,
                "The backup directory could not be read.", ex.Message, HealthTier.Quick);
        }

        if (handles.Count == 0)
            return new HealthCheck(HealthCheckId.Backups, "Backups on disk", HealthState.Warn,
                SafetyCopy.BackupState(0, 0, 0, null),
                "Nothing here can be restored from this application; a destructive operation would take one first.",
                HealthTier.Quick);

        return new HealthCheck(HealthCheckId.Backups, "Backups on disk", HealthState.Ok,
            SafetyCopy.BackupState(handles.Count, 0, 0, null),
            $"Newest bundle {handles[0].UtcStamp}, listed with its local time in the Backups browser. "
            + SafetyCopy.BackupCheckLimit,
            HealthTier.Quick);
    }

    // ── Deep tier ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The rows the deep tier owns before any of them has been asked for. Rendered rather than
    /// omitted: a check nobody ran is a fact, and a page that shows only what it measured reads as
    /// though the rest had nothing to report.
    /// </summary>
    public static IReadOnlyList<HealthCheck> DeepNotRun() =>
    [
        new(HealthCheckId.Connectivity, "Object connectivity", HealthState.NotRun,
            HealthCopy.ConnectivityNotRun, "", HealthTier.Deep),
        new(HealthCheckId.Strict, "Full object check", HealthState.NotRun,
            HealthCopy.StrictNotRun, HealthCopy.StrictCost, HealthTier.Deep),
        new(HealthCheckId.Reachability, "Remote reachability", HealthState.NotRun,
            HealthCopy.NotChecked, "", HealthTier.Deep),
        new(HealthCheckId.BackupVerify, "Backup verification", HealthState.NotRun,
            HealthCopy.NotChecked, SafetyCopy.BackupCheckLimit, HealthTier.Deep),
        new(HealthCheckId.LargeObjects, "Largest objects", HealthState.NotRun,
            HealthCopy.NotChecked, HealthCopy.LargeObjectsScope, HealthTier.Deep),
    ];

    /// <summary>
    /// `fsck --connectivity-only`: git walks the graph and reports missing and dangling objects
    /// without reading object contents. This finds the failure that actually breaks a repository,
    /// and it establishes nothing about the objects themselves — which is why a clean pass reports
    /// as connectivity clean and never as healthy.
    /// </summary>
    public async Task<HealthCheck> CheckConnectivityAsync(string repoPath, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await _git.RunAsync(
            repoPath, ["fsck", "--connectivity-only", "--no-progress"], ct, ConnectivityTimeout);
        var check = FsckCheck(HealthCheckId.Connectivity, "Object connectivity", result,
            HealthCopy.ConnectivityClean, ConnectivityTimeout);
        Record(repoPath, "Check object connectivity", check, started);
        return check;
    }

    /// <summary>
    /// `fsck --strict` — the same invocation the rewrite engine trusts against a freshly built
    /// target, run here against the repository the reader owns. Never reached by escalation: it is
    /// its own press, because it reads and hashes every object.
    /// </summary>
    public async Task<HealthCheck> CheckStrictAsync(string repoPath, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await _git.RunAsync(
            repoPath, ["fsck", "--strict", "--no-progress"], ct, StrictTimeout);
        var check = FsckCheck(HealthCheckId.Strict, "Full object check", result,
            HealthCopy.StrictClean, StrictTimeout);
        Record(repoPath, "Check every object", check, started);
        return check;
    }

    /// <summary>
    /// A timed-out fsck is unknown, not clean and not corrupt: the walk did not finish, and
    /// reporting either verdict claims a result nothing produced. Notices git prints on a passing
    /// run — dangling objects, which are ordinary — are carried in the detail rather than promoted
    /// to a verdict of their own.
    /// </summary>
    private static HealthCheck FsckCheck(
        string id, string title, ProcessResult result, string cleanSummary, TimeSpan budget)
    {
        var notices = (result.StdOut + Environment.NewLine + result.StdErr).Trim();

        if (result.TimedOut)
            return new HealthCheck(id, title, HealthState.Unknown,
                $"The check did not finish inside {budget.TotalMinutes:0} minutes, so nothing was established.",
                notices, HealthTier.Deep);

        return result.Success
            ? new HealthCheck(id, title, HealthState.Ok, cleanSummary, notices, HealthTier.Deep)
            : new HealthCheck(id, title, HealthState.Bad,
                $"git reported a problem with this repository's objects: {result.FirstError}",
                notices, HealthTier.Deep);
    }

    /// <summary>
    /// Whether each remote answers, through `ls-remote` under the pinned non-interactive
    /// environment — so a remote wanting credentials fails fast rather than waiting on a prompt no
    /// window shows. Nothing is fetched and nothing is written.
    /// </summary>
    public async Task<HealthCheck> CheckReachabilityAsync(string repoPath, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var check = await ReachabilityCoreAsync(repoPath, ct);
        Record(repoPath, "Check remote reachability", check, started);
        return check;
    }

    private async Task<HealthCheck> ReachabilityCoreAsync(string repoPath, CancellationToken ct)
    {
        var remotes = await _git.GetRemotesAsync(repoPath, ct);
        if (remotes.HasError)
            return new HealthCheck(HealthCheckId.Reachability, "Remote reachability", HealthState.Unknown,
                "This repository's remotes could not be read, so none was probed.", remotes.ErrorText,
                HealthTier.Deep);

        if (remotes.Remotes.Count == 0)
            return new HealthCheck(HealthCheckId.Reachability, "Remote reachability", HealthState.NotApplicable,
                "No remote is configured, so there is nothing to reach.", "", HealthTier.Deep);

        var lines = new List<string>();
        var reached = 0;
        var probed = 0;
        foreach (var remote in remotes.Remotes)
        {
            if (ct.IsCancellationRequested) break;
            probed++;
            var result = await _git.RunAsync(
                repoPath, ["ls-remote", "--heads", remote.Name], ct, ReachabilityTimeout);
            if (result.Success)
            {
                reached++;
                var heads = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                lines.Add($"{remote.Name} answered with {heads} head(s).");
            }
            else
            {
                lines.Add($"{remote.Name} did not answer: "
                    + (result.TimedOut ? $"no reply inside {ReachabilityTimeout.TotalSeconds:0} seconds" : result.FirstError));
            }
        }

        var detail = string.Join(Environment.NewLine, lines) + Environment.NewLine + HealthCopy.ReachabilityIsNotDiagnosis;
        var unprobed = remotes.Remotes.Count - probed;

        if (unprobed > 0)
            return new HealthCheck(HealthCheckId.Reachability, "Remote reachability", HealthState.Unknown,
                $"The probe was cancelled after {probed} of {remotes.Remotes.Count} remote(s).", detail,
                HealthTier.Deep);

        return reached == probed
            ? new HealthCheck(HealthCheckId.Reachability, "Remote reachability", HealthState.Ok,
                $"All {probed} remote(s) answered.", detail, HealthTier.Deep)
            : new HealthCheck(HealthCheckId.Reachability, "Remote reachability", HealthState.Warn,
                $"{probed - reached} of {probed} remote(s) did not answer.", detail, HealthTier.Deep);
    }

    /// <summary>
    /// Every bundle on disk, through the shared verifier — the same check a restore makes first,
    /// so the answer here is the answer a restore would act on. The result is recorded on the
    /// repository's operation ledger by the verifier itself.
    /// </summary>
    public async Task<(HealthCheck Check, SafetyBackupVerification Result)> CheckBackupsAsync(
        string repoPath, CancellationToken ct = default)
    {
        var result = await _safety.VerifyBackupsAsync(repoPath, ct);
        var at = DateTimeOffset.Now;

        var state =
            result.Failed > 0 ? HealthState.Bad
            : result.Error is not null || result.Unknown > 0 ? HealthState.Unknown
            : result.OnDisk == 0 ? HealthState.Warn
            : HealthState.Ok;

        var detail = (result.Error ?? "") + (result.Error is null ? "" : " ")
            + $"{result.Checked} of {result.OnDisk} bundle(s) checked. " + SafetyCopy.BackupCheckLimit;

        return (new HealthCheck(HealthCheckId.BackupVerify, "Backup verification", state,
            SafetyCopy.BackupState(result.OnDisk, result.Failed, result.Unknown, at),
            detail, HealthTier.Deep), result);
    }

    /// <summary>
    /// The largest blobs in the object store, and the path each is known by.
    ///
    /// Two streamed passes, joined in this process — never a shell pipe, which would replace one
    /// command's exit status with the other's. The first pass ranks by size and keeps only
    /// <see cref="LargeObjectCount"/> entries, so its memory does not grow with the object store;
    /// the second names those entries and keeps only the names it was looking for. Neither pass
    /// materializes a listing.
    /// </summary>
    public async Task<(HealthCheck Check, LargeObjectScan Scan)> CheckLargeObjectsAsync(
        string repoPath, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var scan = await LargeObjectsCoreAsync(repoPath, ct);
        var check = LargeObjectCheck(scan);
        Record(repoPath, "List the largest objects", check, started);
        return (check, scan);
    }

    private static HealthCheck LargeObjectCheck(LargeObjectScan scan)
    {
        if (scan.Error is not null)
            return new HealthCheck(HealthCheckId.LargeObjects, "Largest objects", HealthState.Unknown,
                "The object store could not be walked.", scan.Error, HealthTier.Deep);

        if (scan.Objects.Count == 0)
            return new HealthCheck(HealthCheckId.LargeObjects, "Largest objects", HealthState.Ok,
                "This repository holds no blob.", HealthCopy.LargeObjectsScope, HealthTier.Deep);

        var largest = scan.Objects[0];
        return new HealthCheck(HealthCheckId.LargeObjects, "Largest objects", HealthState.Ok,
            $"{scan.Objects.Count} listed; the largest is {HealthCopy.Bytes(largest.Bytes)}.",
            HealthCopy.LargeObjectsScope + (scan.Partial ? " " + HealthCopy.LargeObjectsPartial : ""),
            HealthTier.Deep);
    }

    private async Task<LargeObjectScan> LargeObjectsCoreAsync(string repoPath, CancellationToken ct)
    {
        var ranking = new SizeRanking(LargeObjectCount);
        ProcessResult sizes;
        try
        {
            sizes = await _git.RunStreamingAsync(
                repoPath,
                ["cat-file", "--batch-check", "--batch-all-objects", "--unordered"],
                ranking.Offer, ct, ObjectWalkTimeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not walk the object store of {repoPath}", ex);
            return new LargeObjectScan([], false, ex.Message);
        }

        if (!sizes.Success && !sizes.TimedOut)
            return new LargeObjectScan([], false, sizes.FirstError);

        var top = ranking.Top();
        if (top.Count == 0)
            return new LargeObjectScan([], sizes.TimedOut || sizes.Truncated, null);

        var paths = new PathLookup(top.Select(entry => entry.Sha));
        ProcessResult named;
        try
        {
            named = await _git.RunStreamingAsync(
                repoPath, ["rev-list", "--objects", "--all"], paths.Offer, ct, ObjectWalkTimeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not name the largest objects of {repoPath}", ex);
            named = new ProcessResult(-1, "", ex.Message, TimedOut: true);
        }

        return new LargeObjectScan(
            top.Select(entry => new LargeObject(entry.Sha, entry.Bytes, paths.PathOf(entry.Sha))).ToList(),
            sizes.TimedOut || sizes.Truncated || !named.Success,
            null);
    }

    /// <summary>
    /// The N largest blobs seen so far. Offered one <c>cat-file --batch-check</c> line at a time
    /// from the pipe reader, which delivers a stream's lines in order on one thread, so the
    /// ranking needs no lock of its own.
    /// </summary>
    private sealed class SizeRanking(int keep)
    {
        private readonly List<(string Sha, long Bytes)> _top = new(keep + 1);
        private long _floor = -1;

        public void Offer(string line)
        {
            // "<sha> <type> <size>"; a missing or ambiguous object prints a different shape and is
            // skipped rather than guessed at.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[1] != "blob") return;
            if (!long.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var bytes)) return;
            if (_top.Count == keep && bytes <= _floor) return;

            _top.Add((parts[0], bytes));
            _top.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            if (_top.Count > keep) _top.RemoveAt(_top.Count - 1);
            _floor = _top[^1].Bytes;
        }

        public IReadOnlyList<(string Sha, long Bytes)> Top() => _top;
    }

    /// <summary>
    /// The path each wanted object is named by, filled from a <c>rev-list --objects</c> stream. It
    /// holds one entry per wanted object and discards every other line, so it does not grow with
    /// the walk. An object no line names keeps an empty path, which is what an unreachable object
    /// has.
    /// </summary>
    private sealed class PathLookup
    {
        private readonly Dictionary<string, string> _paths;

        public PathLookup(IEnumerable<string> wanted) =>
            _paths = wanted.ToDictionary(sha => sha, _ => "", StringComparer.Ordinal);

        public void Offer(string line)
        {
            var space = line.IndexOf(' ');
            if (space <= 0 || space == line.Length - 1) return;
            var sha = line[..space];
            if (!_paths.TryGetValue(sha, out var current) || current.Length > 0) return;
            _paths[sha] = line[(space + 1)..];
        }

        public string PathOf(string sha) => _paths.GetValueOrDefault(sha, "");
    }

    /// <summary>
    /// Best effort, like every other writer against the ledger: a record that could not be written
    /// must not turn a read-only check into a failure.
    /// </summary>
    private void Record(string repoPath, string label, HealthCheck check, DateTimeOffset started)
    {
        var outcome = check.State switch
        {
            HealthState.Ok => OperationOutcome.Succeeded,
            HealthState.Bad => OperationOutcome.Failed,
            HealthState.Warn => OperationOutcome.Succeeded,
            HealthState.NotApplicable => OperationOutcome.Succeeded,
            _ => OperationOutcome.Unknown,
        };
        try
        {
            _history.Append(OperationRecord.For(
                repoPath, OperationCategory.Maintenance, label, outcome, check.Summary, started));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not record the health check '{label}' for {repoPath}", ex);
        }
    }
}
