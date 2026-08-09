using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The overlay's shipped templates, realized. A row's name and its two commands are declared
/// inside DataTemplates, which nothing in a parse-only check ever instantiates: a template that
/// binds a command through the wrong ancestor, or a chip whose style trigger names a value the
/// control does not have, fails only once a container exists.
///
/// Loading shipped markup means joining the collection that owns the one Application: the brushes
/// its theme dictionaries build belong to that thread, and a body realizing them elsewhere at the
/// same time reaches them across threads.
/// </summary>
[Collection("shipped-markup")]
public class OperationHistoryViewTests
{
    [Fact]
    public void TheOverlaysTemplates_RealizeWithTheirNamesAndCommands() => StaHost.Run(() =>
    {
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!,
            history: new OperationHistory(TestEnv.NewDir("ops-view")));
        var view = new OperationHistoryView { DataContext = vm };

        var record = OperationRecord.For("C:\\repo", OperationCategory.Rewrite, "History rewrite",
            OperationOutcome.Failed, "fatal: bad object", DateTimeOffset.UtcNow, backupStamp: "20260101-000000000");
        vm.OperationHistoryRows =
        [
            new OperationHistoryRow
            {
                Record = record,
                When = "2026-01-02 03:04:05",
                Label = "History rewrite",
                Outcome = ProjectDetailViewModel.OutcomeLabel(OperationOutcome.Failed),
                BackupState = RecordedBackupState.Available,
                Backup = ProjectDetailViewModel.BackupLabel(RecordedBackupState.Available),
                Recovery = "",
                Detail = "fatal: bad object"
            }
        ];
        vm.OperationHistoryFilters =
        [
            new OperationHistoryFilter { Key = OperationHistoryFilter.AllKey, Label = "All", Count = 1, IsActive = true },
            new OperationHistoryFilter { Key = "Rewrite", Label = "Rewrite", Count = 1 }
        ];

        var window = new Window { Content = view, Width = 1100, Height = 700, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();

            var row = Assert.Single(Descendants<ListBoxItem>(view));
            Assert.Equal("2026-01-02 03:04:05, History rewrite, Failed, Backup on disk",
                AutomationProperties.GetName(row));

            // The chips reach the view model through the UserControl ancestor, not the row's own
            // data context, and the active one is the only one styled as such.
            var chips = Descendants<Wpf.Ui.Controls.Button>(view)
                .Where(b => b.Command == vm.SelectOperationHistoryFilterCommand).ToList();
            Assert.Equal(2, chips.Count);
            Assert.Equal(["All (1)", "Rewrite (1)"], chips.Select(c => c.Content?.ToString()));
            Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Primary, chips[0].Appearance);
            Assert.NotEqual(Wpf.Ui.Controls.ControlAppearance.Primary, chips[1].Appearance);

            Assert.Contains(Descendants<Wpf.Ui.Controls.Button>(row),
                b => b.Command == vm.OpenBackupForRecordCommand && ReferenceEquals(b.CommandParameter, vm.OperationHistoryRows[0]));

            // The verbatim message is behind an expander, so a long git error never crowds the list.
            var expander = Assert.Single(Descendants<Expander>(row));
            Assert.Equal(Visibility.Visible, expander.Visibility);
        }
        finally { window.Close(); }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
