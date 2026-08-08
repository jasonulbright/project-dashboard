using ProjectDashboard.Services.History;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// How a scrub check may be reported to a reader, worst first. The declaration order is
/// load-bearing: <see cref="RewriteScrubVerdict.Overall"/> takes the minimum across checks,
/// so one unverified check can never hide behind a clean one.
/// </summary>
public enum ScrubVerdict
{
    OccurrencesRemain,
    NotVerified,
    CleanWithinScope,
    VerifiedClean,
}

/// <summary>One check rendered for the result screen: a short label, a sentence, and the evidence behind it.</summary>
public sealed record ScrubVerdictLine(ScrubVerdict Verdict, string Label, string Headline, string Detail)
{
    /// <summary>True for the one verdict that asserts the content is gone from the whole repository.</summary>
    public bool ClaimsClean => Verdict == ScrubVerdict.VerifiedClean;

    /// <summary>
    /// True when the reader must not treat the content as removed. Bound by the summary block,
    /// which is the one row a reader may act on without reading the per-check rows.
    /// </summary>
    public bool IsProblem => Verdict is ScrubVerdict.OccurrencesRemain or ScrubVerdict.NotVerified;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>What a screen reader announces for the row; the record default would read out property syntax.</summary>
    public override string ToString() => $"{Label}. {Headline} {Detail}".TrimEnd();
}

/// <summary>One labelled number from a <see cref="RewriteReport"/>.</summary>
public sealed record RewriteFact(string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// Turns a <see cref="RewriteReport"/> into the exact words the wizard shows. Pure and
/// total: every combination of hits, coverage, and scope maps to one verdict, so no
/// rendering path can invent a clean bill the report does not support.
/// </summary>
public static class RewriteScrubVerdict
{
    public const string RemainLabel = "Occurrences still present";
    public const string NotVerifiedLabel = "NOT verified — coverage incomplete";
    public const string WithinScopeLabel = "Cleaned within the selected scope";
    public const string CleanLabel = "Verified clean";

    private const int MaxListed = 20;

    /// <summary>
    /// The whole verdict rule. A hit is direct evidence the needle survived, so it outranks
    /// every coverage flag. Both coverage flags are tested before the scope flag: WithinScopeOnly
    /// is derived from the requested scope, not from a search, so a check whose search never ran
    /// or covered only part of what it took on carries the scope flag set with nothing behind it.
    /// Only a check that covered its own responsibility may claim anything; a scoped one then
    /// claims no more than its scope, and an unscoped one is the single clean bill.
    /// </summary>
    public static ScrubVerdict For(ScrubCheckResult check)
    {
        if (check.Hits.Count > 0) return ScrubVerdict.OccurrencesRemain;
        if (!check.Performed) return ScrubVerdict.NotVerified;
        if (!check.Complete) return ScrubVerdict.NotVerified;
        if (check.WithinScopeOnly) return ScrubVerdict.CleanWithinScope;
        return ScrubVerdict.VerifiedClean;
    }

    /// <summary>One check as displayed text, naming the payloads its coverage missed.</summary>
    public static ScrubVerdictLine Describe(ScrubCheckResult check, IReadOnlyList<BinarySkip> skips)
    {
        var kind = KindLabel(check.Kind);
        var needle = check.Needle;
        return For(check) switch
        {
            ScrubVerdict.OccurrencesRemain => new ScrubVerdictLine(
                ScrubVerdict.OccurrencesRemain, RemainLabel,
                $"{kind}: {check.Hits.Count} occurrence(s) of “{needle}” are still present in the rewritten history.",
                Join(HitList(check.Hits), NoteText(check))),

            ScrubVerdict.CleanWithinScope => new ScrubVerdictLine(
                ScrubVerdict.CleanWithinScope, WithinScopeLabel,
                $"{kind}: “{needle}” was cleaned within the selected scope; occurrences elsewhere were left by design. " +
                "This is not a claim that the repository is clean everywhere.",
                Join(NoteText(check), CoverageText(check, skips))),

            // The count is carried only by a commit-scoped run; an unscoped message or identity
            // rewrite verifies the whole history and reports zero, which under a clean label
            // reads as "nothing was checked" — the opposite of what the check found.
            ScrubVerdict.VerifiedClean => new ScrubVerdictLine(
                ScrubVerdict.VerifiedClean, CleanLabel,
                check.CommitsChecked > 0
                    ? $"{kind}: “{needle}” is gone — verified across {check.CommitsChecked} commit(s)."
                    : $"{kind}: “{needle}” is gone — verified across the rewritten history.",
                NoteText(check)),

            _ => new ScrubVerdictLine(
                ScrubVerdict.NotVerified, NotVerifiedLabel,
                $"{kind}: nothing matched “{needle}”, but the check did not cover everything. " +
                $"This is NOT proof that “{needle}” was removed.",
                Join(CoverageText(check, skips), NoteText(check))),
        };
    }

    /// <summary>
    /// The one-line verdict for a whole run: the worst of its checks. An empty check list is
    /// reported as unverified, never as success — a run that verified nothing proves nothing.
    /// </summary>
    public static ScrubVerdictLine Overall(RewriteReport report)
    {
        if (report.ScrubChecks.Count == 0)
            return new ScrubVerdictLine(ScrubVerdict.NotVerified, NotVerifiedLabel,
                "No content verification ran for this rewrite, so nothing here proves any content was removed.",
                "Purge and identity operations report counts, not a content search. Inspect the repository yourself before treating anything as gone.");

        var lines = report.ScrubChecks.Select(c => Describe(c, report.BinarySkips)).ToList();
        var worst = lines.Min(l => l.Verdict);
        var affected = lines.Count(l => l.Verdict == worst);
        var total = lines.Count;
        return worst switch
        {
            ScrubVerdict.OccurrencesRemain => new ScrubVerdictLine(worst, RemainLabel,
                $"{affected} of {total} check(s) still find the content in the rewritten history.",
                "The rewrite did not remove everything it was asked to remove."),
            ScrubVerdict.NotVerified => new ScrubVerdictLine(worst, NotVerifiedLabel,
                $"{affected} of {total} check(s) could not cover everything, so this rewrite cannot be reported as clean.",
                "An empty result from an incomplete check is silence, not proof."),
            ScrubVerdict.CleanWithinScope => new ScrubVerdictLine(worst, WithinScopeLabel,
                $"Clean within the selected scope across {total} check(s); occurrences outside the scope were left by design.",
                "Nothing here describes content outside the scope you chose."),
            _ => new ScrubVerdictLine(worst, CleanLabel,
                $"All {total} check(s) verified clean across the rewritten history.",
                ""),
        };
    }

    /// <summary>
    /// Plain, actionable text for a refusal, with the raw reason kept underneath — the raw
    /// text carries the file list, tag names, and paths the guidance only summarises.
    /// </summary>
    public static string DescribeRefusal(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "The rewrite was refused, but no reason was reported. Nothing was changed.";

        var guidance = Guidance(reason);
        return guidance.Length == 0 ? reason.Trim() : guidance + "\n\n" + reason.Trim();
    }

    private static string Guidance(string reason)
    {
        bool Has(string needle) => reason.Contains(needle, StringComparison.OrdinalIgnoreCase);
        if (Has("busy with another operation") || Has("repository is busy"))
            return "Another operation is already running on this repository. Wait for it to finish, then start the rewrite again. Nothing was changed.";
        if (Has("changed after the dry run"))
            return "This repository changed after the dry run, so the report on screen no longer describes it and applying that history would discard what landed since. Run the dry run again. Nothing was changed.";
        if (Has("uncommitted change"))
            return "The working tree has uncommitted changes. Commit or stash the files listed below, then start the rewrite again. Nothing was changed.";
        if (Has("nested tag"))
            return "A tag in this repository points at another tag object, which git's export cannot round-trip. Re-create or delete the listed tag, then start again. Nothing was changed.";
        if (Has("can never check out on Windows"))
            return "The rewrite would produce a path Windows cannot check out. Change the replacement text or narrow the scope so the path stays legal. Nothing was changed.";
        if (Has("backup failed"))
            return "The safety backup could not be taken, so no rewrite was attempted. Fix the backup problem and start again.";
        if (Has("failed fsck"))
            return "The rewritten history failed git's integrity check, so it was not applied. Nothing was changed.";
        if (Has("could not be read by git"))
            return "Git could not read this repository. Check that the folder is a working git repository. Nothing was changed.";
        return "";
    }

    /// <summary>
    /// The skips a given check is answerable for, partitioned by the reason that recorded them.
    /// The mark cannot carry this: a message skip and an identity skip both have a null mark, so
    /// partitioning on it makes each of those checks name the other's gap as its own. A blob skip
    /// is whatever neither reason claims, so a new payload-level reason lands with the content
    /// checks rather than silently with none.
    /// </summary>
    internal static List<BinarySkip> SkipsFor(string kind, IReadOnlyList<BinarySkip> skips) => kind switch
    {
        "literal" or "regex" => skips.Where(IsContentSkip).ToList(),
        "message-literal" or "message-regex" =>
            skips.Where(s => s.Reason == ScopedRewriteOutcome.MessageNotUtf8).ToList(),
        "identity" => skips.Where(s => s.Reason == ScopedRewriteOutcome.IdentityNotUtf8).ToList(),
        _ => skips.ToList(),
    };

    private static bool IsContentSkip(BinarySkip skip) =>
        skip.Reason != ScopedRewriteOutcome.MessageNotUtf8 && skip.Reason != ScopedRewriteOutcome.IdentityNotUtf8;

    internal static string KindLabel(string kind) => kind switch
    {
        "literal" => "File contents (literal)",
        "regex" => "File contents (regex)",
        "message-literal" => "Commit and tag messages (literal)",
        "message-regex" => "Commit and tag messages (regex)",
        "identity" => "Author and committer identities",
        _ => kind,
    };

    private static string CoverageText(ScrubCheckResult check, IReadOnlyList<BinarySkip> skips)
    {
        var parts = new List<string>();
        if (!check.Performed)
            parts.Add("The verification could not run for this needle.");
        var named = SkipsFor(check.Kind, skips);
        if (named.Count > 0)
            parts.Add("Payloads the scrub could not read: " + Listed(named.Select(SkipText)) + ".");
        return string.Join(" ", parts);
    }

    private static string SkipText(BinarySkip skip) => $"{SkipLocation(skip)} — {skip.Reason}";

    /// <summary>
    /// Where a skipped payload sits, in the reader's terms. A message and an identity header
    /// both carry a null mark, so only the reason separates them.
    /// </summary>
    internal static string SkipLocation(BinarySkip skip) =>
        skip.Path
        ?? skip.Reason switch
        {
            ScopedRewriteOutcome.MessageNotUtf8 => "(a commit or tag message)",
            ScopedRewriteOutcome.IdentityNotUtf8 => "(an author, committer, or tagger identity)",
            _ => $"(unnamed blob, mark {skip.Mark})",
        };

    private static string HitList(IReadOnlyList<string> hits) => "Still found at: " + Listed(hits) + ".";

    private static string Listed(IEnumerable<string> items)
    {
        var all = items.ToList();
        var head = string.Join("; ", all.Take(MaxListed));
        return all.Count > MaxListed ? head + $"; … (+{all.Count - MaxListed} more)" : head;
    }

    private static string NoteText(ScrubCheckResult check) =>
        string.IsNullOrWhiteSpace(check.Note) ? "" : "Note: " + check.Note.Trim();

    private static string Join(params string[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>The report's counters as labelled rows, in the order the wizard lists them.</summary>
public static class RewriteReportFacts
{
    public static IReadOnlyList<RewriteFact> For(RewriteReport report) =>
    [
        new("Scope", report.ScopeDescription),
        new("Commits in scope", report.InScopeCommitCount.ToString("N0")),
        new("Commits rewritten", report.CommitMap.Count.ToString("N0")),
        new("Commits whose content changed", report.CommitsWithChangedTrees.Count.ToString("N0")),
        // A git snapshot inherits, so an in-scope edit reaches descendants that never re-touch
        // the path. Naming that count keeps a scoped run from reading as surgically contained.
        new("…of those, outside the selected scope", report.OutOfScopeCommitsWithChangedTrees.ToString("N0")),
        new("File contents changed", report.BlobsChanged.ToString("N0")),
        new("Size change", report.BytesDelta.ToString("+#,##0;-#,##0;0") + " bytes"),
        new("Messages changed", report.MessagesChanged.ToString("N0")),
        new("Identity lines rewritten", report.IdentitiesRewritten.ToString("N0")),
        new("File entries purged", report.FileCommandsRemoved.ToString("N0")),
        new("Commits pruned as empty", report.CommitsPruned.ToString("N0")),
        new("Shared blobs split", report.BlobsSplit.ToString("N0")),
        new("Payloads skipped", report.BinarySkips.Count.ToString("N0")),
    ];

    /// <summary>Each skipped payload as one readable line, so an incomplete scrub names its gaps.</summary>
    public static IReadOnlyList<string> SkipLines(RewriteReport report) =>
        report.BinarySkips
            .Select(s => $"{RewriteScrubVerdict.SkipLocation(s)} — {s.Reason} ({s.Size:N0} bytes)")
            .ToList();
}
