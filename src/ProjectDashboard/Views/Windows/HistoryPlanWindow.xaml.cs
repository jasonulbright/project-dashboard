using System.ComponentModel;
using System.Windows;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Windows;

/// <summary>
/// Reorder, drop, squash and reword planning for a range of commits. Nothing here touches a
/// repository: it hands back the planned list, which <see cref="HistoryPlan.Resolve"/> turns
/// into the one combined todo that runs.
///
/// Every action has both a button and a key binding, so the whole surface works without a
/// mouse: Alt+Up/Alt+Down move, Ctrl+D drops, Ctrl+S squashes, Ctrl+R rewords.
/// </summary>
public partial class HistoryPlanWindow
{
    private readonly HistoryPlanViewModel _viewModel;
    private bool _accepted;

    private HistoryPlanWindow(IReadOnlyList<PlannedCommit> planned)
    {
        _viewModel = new HistoryPlanViewModel(planned);
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += (_, _) => PlanList.Focus();
    }

    /// <summary>The accepted plan, oldest first, or null when the dialog was cancelled or dismissed.</summary>
    public static Task<IReadOnlyList<PlannedCommit>?> ShowAsync(IReadOnlyList<PlannedCommit> planned)
    {
        var window = new HistoryPlanWindow(planned) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        return Task.FromResult(window._accepted ? window._viewModel.Planned : null);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanApply) return;
        _accepted = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _accepted = false;
        DialogResult = false;
    }
}

/// <summary>
/// Live state of one open dialog. The preview, the status line and the apply gate are all
/// recomputed from one <see cref="HistoryPlan.Resolve"/> call after every move and mark, so the
/// gate and the text describe the same resolution the apply will hand to a driver. The preview
/// is that resolution's compiled result, so it is the history the apply produces or nothing.
/// </summary>
public sealed partial class HistoryPlanViewModel : ObservableObject
{
    private readonly List<string> _originalOrder;

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private ObservableCollection<string> _preview = [];
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canApply;

    /// <summary>Message-entry seam: title, prompt, initial text → the text, or null when cancelled.</summary>
    internal Func<string, string, string, Task<string?>> PromptForCommitMessageAsync { get; set; } =
        CommitMessagePromptWindow.ShowAsync;

    public HistoryPlanViewModel(IReadOnlyList<PlannedCommit> planned)
    {
        Commits = new ObservableCollection<PlannedCommit>(planned);
        _originalOrder = planned.Select(c => c.Sha).ToList();
        foreach (var commit in Commits) commit.PropertyChanged += OnCommitChanged;
        SelectedIndex = Commits.Count - 1;
        Recompute();
    }

    public ObservableCollection<PlannedCommit> Commits { get; }

    public IReadOnlyList<PlannedCommit> Planned => Commits.ToList();

    private void OnCommitChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

    [RelayCommand]
    private void MoveUp()
    {
        var index = SelectedIndex;
        if (!HistoryPlan.MoveUp(Commits, index)) return;
        SelectedIndex = index - 1;
        Recompute();
    }

    [RelayCommand]
    private void MoveDown()
    {
        var index = SelectedIndex;
        if (!HistoryPlan.MoveDown(Commits, index)) return;
        SelectedIndex = index + 1;
        Recompute();
    }

    [RelayCommand]
    private void ToggleDrop()
    {
        if (Selected is not { } commit) return;
        commit.Drop = !commit.Drop;
    }

    [RelayCommand]
    private void ToggleSquash()
    {
        if (Selected is not { } commit) return;
        commit.SquashIntoPrevious = !commit.SquashIntoPrevious;
    }

    /// <summary>
    /// Replaces the selected commit's message. A folded or dropped commit has no message the
    /// replay would write, so the mark is cleared as the entry is accepted rather than leaving
    /// a plan that contradicts itself.
    /// </summary>
    [RelayCommand]
    private async Task Reword()
    {
        if (Selected is not { } commit) return;
        var message = await PromptForCommitMessageAsync(
            "Reword commit", $"New message for {commit.ShortSha}", commit.EffectiveSubject);
        if (string.IsNullOrWhiteSpace(message)) return;
        commit.NewMessage = message;
    }

    [RelayCommand]
    private void ResetPlan()
    {
        var byId = Commits.ToDictionary(c => c.Sha, StringComparer.OrdinalIgnoreCase);
        foreach (var commit in Commits)
        {
            commit.Drop = false;
            commit.SquashIntoPrevious = false;
            commit.NewMessage = null;
        }
        Commits.Clear();
        foreach (var sha in _originalOrder) Commits.Add(byId[sha]);
        SelectedIndex = Commits.Count - 1;
        Recompute();
    }

    private PlannedCommit? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Commits.Count ? Commits[SelectedIndex] : null;

    private void Recompute()
    {
        var resolution = HistoryPlan.Resolve(Commits, _originalOrder);
        Preview = new ObservableCollection<string>(resolution.Preview);
        CanApply = resolution.IsValid;
        StatusText = resolution.Refusal
            ?? $"Ready: {resolution.Scope.Summary} — {resolution.Preview.Count} commit(s) after the replay.";
    }
}
