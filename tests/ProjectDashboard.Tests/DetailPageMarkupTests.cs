using System.Runtime.ExceptionServices;
using System.Windows;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The detail page's markup, loaded for real. Every StaticResource, style key, converter, and
/// x:Static reference in it is resolved at parse time and by nothing the compiler checks — a
/// misspelled brush or a style declared in the wrong scope builds cleanly and throws the first
/// time a reader opens the page.
/// </summary>
[Collection("detail-page-markup")]
public class DetailPageMarkupTests
{
    /// <summary>
    /// One test rather than one per view: an Application and the brushes in its dictionaries
    /// belong to the thread that built them, and a second STA thread cannot read them.
    /// </summary>
    [Fact]
    public void TheDetailPageAndItsOverlays_ResolveEveryResourceTheirMarkupNames()
        => RunSta(() =>
        {
            Assert.NotNull(new ProjectDetailPage(NewViewModel()).Content);
            Assert.NotNull(new TagsView { DataContext = NewViewModel() }.Content);
            Assert.NotNull(new ReflogView { DataContext = NewViewModel() }.Content);
        });

    private static ProjectDetailViewModel NewViewModel() =>
        new(null!, new GitService(), null!);

    /// <summary>
    /// WPF needs an STA thread, and the page's markup reaches app-level resources, so the
    /// Application and its merged dictionaries have to exist before anything is parsed.
    /// </summary>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current as ProjectDashboard.App ?? new ProjectDashboard.App();
                app.InitializeComponent();
                action();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}

/// <summary>
/// One Application per process is a WPF invariant, and each of these tests creates one on its
/// own thread — serializing them keeps two from racing to be it.
/// </summary>
[CollectionDefinition("detail-page-markup", DisableParallelization = true)]
public sealed class DetailPageMarkupCollection;
