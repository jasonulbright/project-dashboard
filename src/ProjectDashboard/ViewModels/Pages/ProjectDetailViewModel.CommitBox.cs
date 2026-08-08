using ProjectDashboard.Models;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The commit box's guidance: live subject and body counters, the one structural
/// warning worth naming, and the subjects already in this repository's history.
///
/// Nothing here writes a message on the reader's behalf and nothing blocks a commit; the
/// counters report, and the subject list offers back only text the repository already carries.
/// </summary>
public partial class ProjectDetailViewModel
{
    public CommitMessageGuide CommitGuide => CommitMessageGuide.For(CommitMessage);

    partial void OnCommitMessageChanged(string value) => OnPropertyChanged(nameof(CommitGuide));

    /// <summary>
    /// Subjects from the history already loaded into <see cref="Commits"/> — no second git
    /// call, so the list is exactly as deep as the History tab has been paged out to.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<string> _recentSubjects = [];

    /// <summary>Enough to recognise a convention in use without turning the list into a scroll.</summary>
    private const int RecentSubjectCount = 15;

    partial void OnCommitsChanged(ObservableCollection<GitCommit> value) => RefreshRecentSubjects();

    private void RefreshRecentSubjects() =>
        RecentSubjects = Commits
            .Select(c => c.Message.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(RecentSubjectCount)
            .ToList();

    /// <summary>
    /// The picker's bound value. It is cleared as soon as the pick is applied: the control
    /// offers subjects to copy down, and one left showing would read as the message state.
    /// </summary>
    [ObservableProperty] private string? _selectedRecentSubject;

    private bool _clearingRecentSubject;

    partial void OnSelectedRecentSubjectChanged(string? value)
    {
        if (_clearingRecentSubject || value is null) return;

        CommitMessage = CommitMessageGuide.WithSubject(CommitMessage, value);
        _clearingRecentSubject = true;
        try { SelectedRecentSubject = null; }
        finally { _clearingRecentSubject = false; }
    }
}
