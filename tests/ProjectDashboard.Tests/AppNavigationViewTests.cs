using System.Reflection;
using System.Runtime.ExceptionServices;
using ProjectDashboard.Views.Windows;
using Wpf.Ui.Controls;

namespace ProjectDashboard.Tests;

/// <summary>
/// Pins the two library facts the sidebar reconcile relies on: the navigation
/// dictionaries are additive-only and keyed by per-instance item Id, so fresh
/// item instances per rebuild leak entries while reused instances keep the
/// count flat. WPF controls require an STA thread; no Application is needed.
/// </summary>
public class AppNavigationViewTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>
    /// Total entries across every base-class dictionary whose values are
    /// navigation items — the registration state RegisterDynamicMenuItems feeds.
    /// </summary>
    private static int CountRegisteredEntries(NavigationView nav)
    {
        var total = 0;
        var found = false;
        for (var type = (Type?)nav.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!field.FieldType.IsGenericType) continue;
                var args = field.FieldType.GetGenericArguments();
                if (args.Length != 2 || !typeof(INavigationViewItem).IsAssignableFrom(args[1])) continue;
                if (field.GetValue(nav) is not System.Collections.IDictionary dictionary) continue;
                found = true;
                total += dictionary.Count;
            }
        }
        Assert.True(found,
            "no navigation-item dictionaries found — the library internals this test guards have moved");
        return total;
    }

    [Fact]
    public void ReusedItemInstances_KeepDictionariesBounded_AcrossRebuilds()
    {
        RunSta(() =>
        {
            var nav = new AppNavigationView();
            var parent = new NavigationViewItem { Content = "Projects" };
            nav.MenuItems.Add(parent);

            var pooled = Enumerable.Range(0, 5)
                .Select(i => new NavigationViewItem { Content = $"project-{i}" })
                .ToList();

            var counts = new List<int>();
            for (var rebuild = 0; rebuild < 3; rebuild++)
            {
                parent.MenuItems.Clear();
                foreach (var item in pooled)
                    parent.MenuItems.Add(item);
                nav.RegisterDynamicMenuItems();
                counts.Add(CountRegisteredEntries(nav));
            }

            Assert.True(counts[0] > 0, "nested items never registered at all");
            Assert.Equal(counts[0], counts[1]);
            Assert.Equal(counts[0], counts[2]);
        });
    }

    [Fact]
    public void FreshItemInstancesPerRebuild_GrowDictionaries()
    {
        RunSta(() =>
        {
            var nav = new AppNavigationView();
            var parent = new NavigationViewItem { Content = "Projects" };
            nav.MenuItems.Add(parent);

            var counts = new List<int>();
            for (var rebuild = 0; rebuild < 3; rebuild++)
            {
                parent.MenuItems.Clear();
                for (var i = 0; i < 5; i++)
                    parent.MenuItems.Add(new NavigationViewItem { Content = $"project-{i}" });
                nav.RegisterDynamicMenuItems();
                counts.Add(CountRegisteredEntries(nav));
            }

            // Every rebuild of five fresh items strands the previous five entries.
            // If this stops holding, the upstream defect is fixed and the item
            // pool in MainWindow.RefreshSidebarProjects can be retired.
            Assert.True(counts[2] > counts[1] && counts[1] > counts[0],
                $"expected monotonic growth from fresh instances, got {string.Join(",", counts)}");
        });
    }
}
