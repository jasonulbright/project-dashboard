namespace ProjectDashboard.Models;

/// <summary>
/// What the commit box says about the message being typed (X-05). Guidance only: git accepts
/// any of these messages, so nothing here blocks a commit — an over-long subject is reported,
/// never truncated or refused.
/// </summary>
public sealed record CommitMessageGuide(
    int SubjectLength,
    bool SubjectOverLimit,
    int LongestBodyLine,
    bool BodyOverLimit,
    string CounterText,
    string Warning)
{
    /// <summary>Subject width most tools show without eliding, and the width `git log --oneline` fits.</summary>
    public const int SubjectLimit = 50;

    /// <summary>Body width that leaves room for the four-space indent `git log` adds.</summary>
    public const int BodyLimit = 72;

    public bool HasWarning => Warning.Length > 0;

    public static CommitMessageGuide For(string? message)
    {
        var lines = (message ?? "").Replace("\r\n", "\n").Split('\n');
        var subject = lines[0];
        // Line 2 is the separator git reads the body after, so it is neither subject nor body.
        var body = lines.Skip(2).ToList();
        var longestBody = body.Count == 0 ? 0 : body.Max(l => l.Length);

        var counter = body.Count == 0
            ? $"subject {subject.Length}/{SubjectLimit}"
            : $"subject {subject.Length}/{SubjectLimit} · body {longestBody}/{BodyLimit}";

        return new CommitMessageGuide(
            subject.Length,
            subject.Length > SubjectLimit,
            longestBody,
            longestBody > BodyLimit,
            counter,
            WarningFor(lines, subject));
    }

    /// <summary>
    /// The one structural problem worth naming, or none. An empty box is not a problem the
    /// counter should scold about — the commit command already refuses that on its own.
    /// </summary>
    private static string WarningFor(string[] lines, string subject)
    {
        if (lines.All(l => l.Trim().Length == 0)) return "";
        if (subject.Trim().Length == 0)
            return "The first line is blank — git takes the first line as the subject.";
        if (lines.Length > 1 && lines[1].Trim().Length > 0)
            return "Leave line 2 blank — git reads everything after it as the body.";
        return "";
    }

    /// <summary>Replaces the subject line, keeping whatever body is already typed under it.</summary>
    public static string WithSubject(string? message, string subject)
    {
        var lines = (message ?? "").Replace("\r\n", "\n").Split('\n');
        return lines.Length <= 1 ? subject : string.Join("\n", lines.Skip(1).Prepend(subject));
    }
}
