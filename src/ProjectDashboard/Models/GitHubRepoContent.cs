namespace ProjectDashboard.Models;

/// <summary>One downloadable file attached to a release.</summary>
public sealed class ReleaseAsset
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";

    /// <summary>Size in the largest unit that keeps the number under 1024.</summary>
    public string SizeLabel => FormatSize(Size);

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        foreach (var unit in (string[])["KB", "MB", "GB", "TB"])
        {
            value /= 1024;
            if (value < 1024) return $"{value:0.#} {unit}";
        }
        return $"{value:0.#} PB";
    }
}

/// <summary>One GitHub release, drafts included.</summary>
public sealed class Release
{
    public string TagName { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Release notes as markdown ("" when the release has none).</summary>
    public string Body { get; init; } = "";
    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
    /// <summary>Null for drafts — a draft has no publish moment.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
    public string Url { get; init; } = "";

    /// <summary>Tag plus name, or the tag alone when the release is untitled.</summary>
    public string DisplayTitle => Name.Length == 0 ? TagName : $"{TagName} — {Name}";

    /// <summary>"draft", "prerelease", or "" — a draft outranks the prerelease flag.</summary>
    public string StateLabel => IsDraft ? "draft" : IsPrerelease ? "prerelease" : "";

    /// <summary>
    /// <see cref="StateLabel"/> carrying its own leading separator; empty for a published release,
    /// which a composed name would otherwise end a separator on.
    /// </summary>
    public string StateSuffix => StateLabel.Length == 0 ? "" : $" {StateLabel}";
}

/// <summary>One workflow run from the Actions list.</summary>
public sealed class WorkflowRun
{
    public long Id { get; init; }
    /// <summary>Workflow name (not the per-run title).</summary>
    public string Name { get; init; } = "";
    public string DisplayTitle { get; init; } = "";
    public string Branch { get; init; } = "";
    public string Event { get; init; } = "";
    /// <summary>"queued" | "in_progress" | "completed" (lowercase).</summary>
    public string Status { get; init; } = "";
    /// <summary>"success" | "failure" | "cancelled" | ... ; "" until the run completes.</summary>
    public string Conclusion { get; init; } = "";
    /// <summary>Null until the run actually starts (gh serializes queued runs with a zero time).</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>Last activity on the run; for a completed run this is when it finished.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
    public string Url { get; init; } = "";

    public bool IsCompleted => Status == "completed";

    /// <summary>Conclusion once the run is over, else its status — never both, never blank.</summary>
    public string OutcomeLabel => IsCompleted ? (Conclusion.Length > 0 ? Conclusion : "completed")
        : Status.Length > 0 ? Status.Replace('_', ' ') : "unknown";

    /// <summary>
    /// <see cref="Branch"/> carrying its own leading separator and preposition; empty when the run
    /// reports no head branch, which a composed name would otherwise end a preposition on.
    /// </summary>
    public string BranchSuffix => Branch.Length == 0 ? "" : $", on {Branch}";

    /// <summary>
    /// Wall-clock the run has taken. A run still going is measured to now, so the value
    /// is only as fresh as the last list fetch.
    /// </summary>
    public string ElapsedLabel => FormatElapsed(StartedAt, IsCompleted ? UpdatedAt : null, DateTimeOffset.Now);

    /// <summary>
    /// "" when the run has not started. A null <paramref name="ended"/> means still
    /// running and is measured against <paramref name="now"/>.
    /// </summary>
    internal static string FormatElapsed(DateTimeOffset? started, DateTimeOffset? ended, DateTimeOffset now)
    {
        if (started is not { } from) return "";
        var span = (ended ?? now) - from;
        // A clock skew between the runner and this machine can put the end before the
        // start; a negative duration is not a duration.
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
            : $"{span.Seconds}s";
    }
}

/// <summary>One job within a workflow run, with the steps it ran.</summary>
public sealed class WorkflowJob
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>"queued" | "in_progress" | "completed" (lowercase).</summary>
    public string Status { get; init; } = "";
    /// <summary>"success" | "failure" | "skipped" | ... ; "" until the job completes.</summary>
    public string Conclusion { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];
    public string Url { get; init; } = "";

    public string OutcomeLabel => Conclusion.Length > 0 ? Conclusion : Status.Replace('_', ' ');
    public string ElapsedLabel => WorkflowRun.FormatElapsed(StartedAt, CompletedAt, DateTimeOffset.Now);
}

/// <summary>
/// One workflow run's log as it was read. <see cref="Truncated"/> is what the text alone cannot
/// establish: a capture the cap cut short and one that ends where the run ended look the same to
/// a reader, and a search or a saved copy taken from the first is a partial answer.
/// <see cref="Cap"/> is the bound that actually applied, so a surface names the budget it hit
/// rather than the one it asked for.
/// </summary>
public sealed record WorkflowRunLog(string Text, bool Truncated, int Cap);

/// <summary>One numbered line of a workflow run log, as the viewer lists it.</summary>
public sealed record WorkflowLogLine(int Number, string Text);

/// <summary>One step within a workflow job.</summary>
public sealed class WorkflowStep
{
    /// <summary>1-based position in the job, as GitHub reports it.</summary>
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string Conclusion { get; init; } = "";

    public string OutcomeLabel => Conclusion.Length > 0 ? Conclusion : Status.Replace('_', ' ');
}

/// <summary>One notification thread on the current repository.</summary>
public sealed class GitHubNotification
{
    /// <summary>Thread id — the handle mark-as-read addresses. Always digits.</summary>
    public string ThreadId { get; init; } = "";
    /// <summary>Why it arrived: "mention", "review_requested", "subscribed", ...</summary>
    public string Reason { get; init; } = "";
    public bool Unread { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Subject kind: "Issue", "PullRequest", "Release", "Discussion", ...</summary>
    public string SubjectType { get; init; } = "";
    /// <summary>Browser URL for the subject; "" when the REST url maps to no web page.</summary>
    public string WebUrl { get; init; } = "";

    public string ReasonLabel => Reason.Replace('_', ' ');
}

/// <summary>Repo-level settings surfaced on the Repo tab.</summary>
public sealed class RepoSettings
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Homepage { get; init; } = "";
    public IReadOnlyList<string> Topics { get; init; } = [];
    /// <summary>Lowercase: "public" | "private" | "internal".</summary>
    public string Visibility { get; init; } = "";
    public bool IsArchived { get; init; }
    public string DefaultBranch { get; init; } = "";
    /// <summary>owner/name of the parent repo when this is a fork; "" otherwise.</summary>
    public string ParentSlug { get; init; } = "";
    public bool IsFork => ParentSlug.Length > 0;

    // Null means the flag was absent from the response — not "off". Rendering an
    // unread toggle as off would invite a save that turns the feature off for real.
    public bool? HasIssues { get; init; }
    public bool? HasWiki { get; init; }
    public bool? HasProjects { get; init; }

    /// <summary>Topics as the comma-separated string the editor round-trips.</summary>
    public string TopicsText => string.Join(", ", Topics);
}

/// <summary>
/// How a fork's branch stands against the same branch on its parent. Callers hold this as a
/// nullable: null is "the comparison did not answer", which (0, 0) — an identical branch — must
/// never stand in for, because a sync offered on the strength of it names a count nothing read.
/// </summary>
public sealed record ForkDivergence(int Ahead, int Behind)
{
    /// <summary>True when neither side carries a commit the other lacks.</summary>
    public bool InSync => Ahead == 0 && Behind == 0;
}

/// <summary>
/// One row of the workflow picker on the Actions tab. The unfiltered row is a choice of its own
/// rather than a null selection, on the same terms as <see cref="MilestoneChoice"/>, and it is
/// distinguished by <see cref="Name"/> rather than by its label — a workflow actually called
/// "Any workflow" would otherwise select every run instead of its own.
/// </summary>
public sealed record WorkflowChoice(string? Name)
{
    /// <summary>The row that filters to no particular workflow.</summary>
    public static WorkflowChoice Any { get; } = new((string?)null);

    public string Label => Name ?? "Any workflow";
}

/// <summary>One issue/PR label defined on a repo.</summary>
public sealed class Label
{
    public string Name { get; init; } = "";
    /// <summary>Hex color without the leading '#'.</summary>
    public string Color { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>One milestone defined on a repo.</summary>
public sealed class Milestone
{
    /// <summary>REST milestone number — the handle mutations address, distinct from the title.</summary>
    public int Number { get; init; }
    public string Title { get; init; } = "";
    /// <summary>"open" | "closed".</summary>
    public string State { get; init; } = "";
    public DateTimeOffset? DueOn { get; init; }
    /// <summary>Null means the count could not be fetched — not zero.</summary>
    public int? OpenIssues { get; init; }
    public int? ClosedIssues { get; init; }

    /// <summary>Issues in this milestone across both states, or null when either count is missing.</summary>
    public int? TotalIssues => OpenIssues is { } open && ClosedIssues is { } closed ? open + closed : null;
}

/// <summary>
/// The milestone one issue-list read is filtered to. <see cref="Number"/> is what reaches gh,
/// which resolves a numeric value as a milestone number rather than as a title, so a milestone
/// whose title reads as a number is still addressed unambiguously. <see cref="Title"/> is what a
/// surface describing that read names, since a page is labelled from the query that produced it
/// rather than from a picker the reader may have moved on since.
/// </summary>
public sealed record MilestoneFacet(int Number, string Title);

/// <summary>
/// One row of a milestone picker. The two pickers each carry a row that selects no milestone, and
/// it is a choice of its own rather than a null selection: a combo box with no item selected
/// renders blank, which reads as a picker that failed to load rather than as one set to "any".
/// </summary>
public sealed record MilestoneChoice(string Label, Milestone? Milestone)
{
    /// <summary>The filter picker's unfiltered row.</summary>
    public static MilestoneChoice Any { get; } = new("Any milestone", null);

    /// <summary>The compose picker's row for an issue that joins no milestone.</summary>
    public static MilestoneChoice None { get; } = new("None", null);

    /// <summary>The facet a filter selection sends, or null for the unfiltered row.</summary>
    public MilestoneFacet? Facet => Milestone is null ? null : new MilestoneFacet(Milestone.Number, Milestone.Title);

    /// <summary>A closed milestone is still worth filtering to, and is named as closed.</summary>
    public static MilestoneChoice For(Milestone milestone) =>
        new(milestone.State == "closed" ? $"{milestone.Title} (closed)" : milestone.Title, milestone);
}
