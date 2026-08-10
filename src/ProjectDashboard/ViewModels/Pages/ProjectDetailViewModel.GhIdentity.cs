using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Which GitHub CLI account this repository's remote would be reached by, named on the page that
/// offers the actions. The app holds no credentials of its own, so every GitHub action here runs
/// as whichever account gh has made active for the remote's host — a fact that is invisible until
/// an action fails, and that a reader with more than one account cannot infer from the repository.
///
/// Detection only. Nothing here runs `gh auth switch`, `gh auth login`, or any other command that
/// changes which account gh targets: that setting is global to the machine and every other tool on
/// it. Where an account is the answer, the exact command is shown as text for the reader to run.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Await seam for the load-time read, like <see cref="SigningRefresh"/>.</summary>
    internal Task GhIdentityRefresh { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private bool _ghIdentityVisible;
    [ObservableProperty] private string _ghIdentityText = "";

    /// <summary>True where the line names something that blocks or misdirects a GitHub action.</summary>
    [ObservableProperty] private bool _ghIdentityWarning;

    /// <summary>
    /// The gh command that resolves what the line names, shown as text and never run. "" when the
    /// state needs none.
    /// </summary>
    [ObservableProperty] private string _ghIdentityCommand = "";
    [ObservableProperty] private bool _ghIdentityCommandVisible;

    private void ResetGhIdentity()
    {
        GhIdentityVisible = false;
        GhIdentityText = "";
        GhIdentityWarning = false;
        GhIdentityCommand = "";
        GhIdentityCommandVisible = false;
    }

    /// <summary>
    /// The status read the line stands on. Overridable on the same terms as the other remote
    /// reads, so every state this surface can show is reachable without spawning gh.
    /// </summary>
    internal virtual Task<GhAuthState?> FetchAuthStateAsync(bool refresh)
        => _gitHubService.GetAuthStateAsync(refresh);

    private async Task SafeRefreshGhIdentityAsync(bool refresh)
    {
        try { await RefreshGhIdentityAsync(refresh); }
        catch (Exception ex) { Log.Warn("gh account read failed", ex); }
    }

    private async Task RefreshGhIdentityAsync(bool refresh)
    {
        var gen = _generation;
        var remote = CurrentRemote();
        if (remote is null)
        {
            ResetGhIdentity();
            return;
        }

        var state = await FetchAuthStateAsync(refresh);
        if (!IsCurrent(gen)) return;
        // The read is answered for the app, not for one repository, and a project switch during it
        // leaves the incoming repository's remote as the one the answer has to be read against.
        var current = CurrentRemote();
        if (current is null)
        {
            ResetGhIdentity();
            return;
        }

        var line = DescribeGhIdentity(state, current);
        GhIdentityText = line.Text;
        GhIdentityWarning = line.Warning;
        GhIdentityCommand = line.Command;
        GhIdentityCommandVisible = line.Command.Length > 0;
        GhIdentityVisible = line.Text.Length > 0;
    }

    /// <summary>
    /// Re-reads gh's account state and the line drawn from it. The only refresh there is: the read
    /// otherwise happens once per session and once per repository load off the held answer, and
    /// nothing polls for a fact that changes only when the reader runs gh themselves.
    /// </summary>
    [RelayCommand]
    private Task RecheckGhIdentity() => GhIdentityRefresh = SafeRefreshGhIdentityAsync(true);

    /// <summary>
    /// The remote this page's GitHub actions would reach. A card for a repository with no local
    /// clone carries its slug instead of a URL, and it is a github.com repository by construction —
    /// discovery reads it from the signed-in account's own list.
    /// </summary>
    private GitRemote? CurrentRemote()
    {
        if (Project is null) return null;
        if (Project.IsRemoteOnly)
        {
            var parts = Project.RemoteSlug.Split('/', 2);
            return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
                ? new GitRemote("github.com", parts[0], parts[1])
                : null;
        }
        return GitRemote.Parse(Project.GitStatus.RemoteUrl);
    }

    /// <summary>What the identity line says, kept pure so every state is testable as a value.</summary>
    internal sealed record GhIdentityLine(string Text, bool Warning, string Command);

    internal const string GhAuthUnreadable =
        "GitHub CLI account unknown — its sign-in state could not be read, so no account is named here.";

    /// <summary>
    /// The line for one remote against one status read. A null <paramref name="state"/> is a read
    /// that failed and never an answer about accounts: it names nothing and claims nothing.
    ///
    /// The mismatch worth naming is the one the reader cannot see and the app cannot fix: the
    /// remote's owner is itself another account gh holds for that host, and the active account is
    /// a different one, so every action runs as somebody who may not be able to see the repository
    /// at all. An owner that is an organisation, or an account gh does not hold, is not that case
    /// and is not warned about — membership is not something this read establishes.
    /// </summary>
    internal static GhIdentityLine DescribeGhIdentity(GhAuthState? state, GitRemote? remote)
    {
        if (remote is null) return new GhIdentityLine("", false, "");
        // The form gh stores, not the form the clone URL used: a command naming a www host would
        // have gh configure a host of that name rather than sign in to the one the remote means.
        var host = GhAuthState.NormalizeHost(remote.Host);
        if (state is null) return new GhIdentityLine(GhAuthUnreadable, false, "");

        // Answered before the session questions below, and with no command: signing in to a host
        // whose repositories this app does not read would resolve nothing the line is naming.
        if (!remote.IsGitHub)
        {
            var elsewhere = state.ActiveFor(host);
            return new GhIdentityLine(
                (elsewhere is { IsUsable: true } ? $"{elsewhere.Login} @ {host} — " : $"{host} — ")
                + "this app's GitHub surfaces read github.com only, so they stay empty for this repository.",
                true, "");
        }

        var onHost = state.ForHost(host);
        if (onHost.Count == 0)
            return new GhIdentityLine(
                state.Accounts.Count == 0
                    ? "Not signed in to the GitHub CLI — GitHub actions are unavailable for this repository."
                    : $"No GitHub CLI account for {host} — GitHub actions are unavailable for this repository.",
                true, $"gh auth login --hostname {host}");

        var active = state.ActiveFor(host);
        if (active is null)
            return new GhIdentityLine(
                $"No active GitHub CLI account for {host} — gh holds accounts there but targets none of them.",
                true, $"gh auth switch --hostname {host}");

        if (!active.IsUsable)
            return new GhIdentityLine(
                $"{active.Login} @ {host} — the GitHub CLI reports this account's sign-in as "
                + $"{DescribeAccountState(active)}, so GitHub actions here will fail until it is renewed.",
                true, $"gh auth login --hostname {host}");

        var owner = onHost.FirstOrDefault(
            a => !a.Active && a.IsUsable && a.Login.Equals(remote.Owner, StringComparison.OrdinalIgnoreCase));
        return owner is null
            ? new GhIdentityLine($"{active.Login} @ {host}", false, "")
            : new GhIdentityLine(
                $"{active.Login} @ {host} — this repository belongs to {owner.Login}, another account you are "
                + $"signed in to. GitHub actions here run as {active.Login} and a private repository can read "
                + "as missing to them.",
                true, $"gh auth switch --hostname {host} --user {owner.Login}");
    }

    private static string DescribeAccountState(GhAccount account) =>
        account.State.Equals("timeout", StringComparison.OrdinalIgnoreCase)
            ? "unverified — the check timed out"
            : "failed";
}
