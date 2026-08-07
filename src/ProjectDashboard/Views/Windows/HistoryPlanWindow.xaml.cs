using System.ComponentModel;
using System.Windows;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Windows;

/// <summary>
/// Reorder, drop and squash planning for a range of commits. Nothing here touches a
/// repository: it hands back the planned list, which <see cref="HistoryPlan.Resolve"/> turns
/// into the one gated operation that runs.
///
/// Every action has both a button and a key binding, so the whole surface works without a
/// mouse: Alt+Up/Alt+Down move, Ctrl+D drops, Ctrl+S squashes.
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
/// Live state of one open dialog. The preview and the apply gate are recomputed from
/// <see cref="HistoryPlan"/> after every move and mark, so what the buttons allow and what the
/// preview shows can never disagree with what the resolution will do.
/// </summary>
public sealed partial class HistoryPlanViewModel : ObservableObject
{
    private readonly List<string> _originalOrder;

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private ObservableCollection<string> _preview = [];
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canApply;

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

    [RelayCommand]
    private void ResetPlan()
    {
        var byId = Commits.ToDictionary(c => c.Sha, StringComparer.OrdinalIgnoreCase);
        foreach (var commit in Commits)
        {
            commit.Drop = false;
            commit.SquashIntoPrevious = false;
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
        Preview = new ObservableCollection<string>(HistoryPlan.Preview(Commits));
        var resolution = HistoryPlan.Resolve(Commits, _originalOrder);
        CanApply = resolution.IsValid;
        StatusText = resolution.Refusal ?? resolution.Kind switch
        {
            HistoryPlanKind.Drop => $"Ready: drop {resolution.Shas.Count} commit(s).",
            HistoryPlanKind.Squash => $"Ready: fold {resolution.Shas.Count} commit(s) into one.",
            _ => $"Ready: replay {resolution.Shas.Count} commit(s) in the new order."
        };
    }
}
