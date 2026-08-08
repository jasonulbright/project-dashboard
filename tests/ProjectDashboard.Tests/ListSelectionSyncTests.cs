using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ProjectDashboard.Helpers;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Both directions of the Changes tab's file selection, on live lists wired the way the page
/// wires them. A selection made on one side clears the other side's selection through the view
/// model, and that write has to reach the other list: rows left highlighted beside a diff of the
/// other side arm buttons that then refuse, naming a selection the reader can see.
/// </summary>
public class ListSelectionSyncTests
{
    /// <summary>
    /// The page's arrangement: two extended-selection lists reporting their own selection to the
    /// view model, and the view model's selections written back onto them.
    /// <paramref name="suppression"/> is the part under test — how a write is kept from
    /// re-entering as a fresh user selection.
    /// </summary>
    private sealed class Wiring
    {
        internal ProjectDetailViewModel ViewModel { get; }
        internal ListBox Unstaged { get; }
        internal ListBox Staged { get; }

        internal Wiring(ISelectionSuppression suppression)
        {
            ViewModel = new ProjectDetailViewModel(null!, new GitService(), null!)
            {
                UnstagedFiles = Files("a.txt", "b.txt", "c.txt"),
                StagedFiles = Files("s1.txt", "s2.txt")
            };
            Unstaged = NewList(ViewModel.UnstagedFiles);
            Staged = NewList(ViewModel.StagedFiles);

            Unstaged.SelectionChanged += (_, e) => suppression.Push(Unstaged,
                () => ViewModel.SetUnstagedSelection(Chosen(Unstaged), Added(e)));
            Staged.SelectionChanged += (_, e) => suppression.Push(Staged,
                () => ViewModel.SetStagedSelection(Chosen(Staged), Added(e)));

            ViewModel.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ProjectDetailViewModel.SelectedUnstagedFiles):
                        suppression.Restore(Unstaged, ViewModel.SelectedUnstagedFiles);
                        break;
                    case nameof(ProjectDetailViewModel.SelectedStagedFiles):
                        suppression.Restore(Staged, ViewModel.SelectedStagedFiles);
                        break;
                }
            };
        }

        internal void Select(ListBox list, params string[] paths)
        {
            foreach (var file in list.Items.Cast<WorkingFile>().Where(f => paths.Contains(f.Path)))
                list.SelectedItems.Add(file);
        }

        internal static IReadOnlyList<WorkingFile> Chosen(ListBox list) =>
            list.SelectedItems.Cast<WorkingFile>().ToList();

        private static WorkingFile? Added(SelectionChangedEventArgs e) =>
            e.AddedItems.OfType<WorkingFile>().LastOrDefault();

        private static ObservableCollection<WorkingFile> Files(params string[] paths) =>
            new(paths.Select(p => new WorkingFile { Path = p, WorktreeStatus = 'M' }));

        private static ListBox NewList(IEnumerable<WorkingFile> items) =>
            new() { ItemsSource = items, SelectionMode = SelectionMode.Extended };
    }

    private interface ISelectionSuppression
    {
        void Push(ListBox list, Action push);
        void Restore(ListBox list, IReadOnlyList<WorkingFile> wanted);
    }

    /// <summary>
    /// One flag for both directions, and the failure it produces: the cross-list clear runs while
    /// a push is in flight, so the write onto the OTHER list is dropped and its rows stay
    /// highlighted against a view model holding nothing. Kept as the proof that the test below
    /// is not vacuous.
    /// </summary>
    private sealed class OneFlagForBothLists : ISelectionSuppression
    {
        private bool _syncing;

        public void Push(ListBox list, Action push)
        {
            if (_syncing) return;
            _syncing = true;
            try { push(); }
            finally { _syncing = false; }
        }

        public void Restore(ListBox list, IReadOnlyList<WorkingFile> wanted)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                list.SelectedItems.Clear();
                foreach (var file in wanted) list.SelectedItems.Add(file);
            }
            finally { _syncing = false; }
        }
    }

    private sealed class PerList(ListSelectionSync sync) : ISelectionSuppression
    {
        public void Push(ListBox list, Action push) => sync.Push(list, push);

        public void Restore(ListBox list, IReadOnlyList<WorkingFile> wanted) =>
            sync.Restore(list, wanted);
    }

    [Fact]
    public void ASelectionOnOneSide_ClearsTheOtherSidesRows() =>
        RunSta(() =>
        {
            var wiring = new Wiring(new PerList(new ListSelectionSync()));
            wiring.Select(wiring.Unstaged, "a.txt", "b.txt");
            Assert.Equal(2, wiring.ViewModel.SelectedUnstagedFiles.Count);

            wiring.Select(wiring.Staged, "s1.txt");

            Assert.Empty(wiring.ViewModel.SelectedUnstagedFiles);
            Assert.Empty(wiring.Unstaged.SelectedItems);
            Assert.Equal(["s1.txt"], wiring.ViewModel.SelectedStagedFiles.Select(f => f.Path));
            Assert.Equal(["s1.txt"], Wiring.Chosen(wiring.Staged).Select(f => f.Path));
        });

    /// <summary>The arrangement's own failure mode, and why one flag cannot serve both lists.</summary>
    [Fact]
    public void WithOneFlagForBothLists_TheOtherSidesRowsStayHighlighted() =>
        RunSta(() =>
        {
            var wiring = new Wiring(new OneFlagForBothLists());
            wiring.Select(wiring.Unstaged, "a.txt", "b.txt");

            wiring.Select(wiring.Staged, "s1.txt");

            Assert.Empty(wiring.ViewModel.SelectedUnstagedFiles);
            Assert.Equal(2, wiring.Unstaged.SelectedItems.Count);
        });

    /// <summary>
    /// The suppression each direction needs: a push must not come back as a fresh user selection
    /// on the list being written to, or a restored multi-selection collapses to its last row.
    /// </summary>
    [Fact]
    public void AWriteOntoAList_DoesNotComeBackAsAUserSelection() =>
        RunSta(() =>
        {
            var sync = new ListSelectionSync();
            var wiring = new Wiring(new PerList(sync));
            var wanted = wiring.ViewModel.UnstagedFiles.Take(2).ToList();

            sync.Restore(wiring.Unstaged, wanted);

            Assert.Equal(2, wiring.Unstaged.SelectedItems.Count);
            Assert.Empty(wiring.ViewModel.SelectedUnstagedFiles);
        });

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
