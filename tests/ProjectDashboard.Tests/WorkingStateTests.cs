using ProjectDashboard.Models;

namespace ProjectDashboard.Tests;

public class WorkingStateTests
{
    private const string Hash = "0000000000000000000000000000000000000000";

    [Fact]
    public void HeaderOnly_ParsesBranchAndCleanState()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n");

        Assert.Equal("main", state.Branch);
        Assert.False(state.Detached);
        Assert.False(state.NoCommitsYet);
        Assert.False(state.HasUpstream);
        Assert.False(state.IsDirty);
        Assert.Empty(state.Files);
    }

    [Fact]
    public void UpstreamAndAheadBehind_AreParsed()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n" +
            "# branch.upstream origin/main\n" +
            "# branch.ab +3 -2\n");

        Assert.Equal("origin/main", state.Upstream);
        Assert.True(state.HasUpstream);
        Assert.Equal(3, state.Ahead);
        Assert.Equal(2, state.Behind);
    }

    [Fact]
    public void DetachedHead_SetsFlagAndEmptyBranch()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head (detached)\n");

        Assert.True(state.Detached);
        Assert.Equal("", state.Branch);
    }

    [Fact]
    public void FreshRepoWithNoCommits_SetsNoCommitsYet()
    {
        var state = WorkingState.Parse(
            "# branch.oid (initial)\n" +
            "# branch.head main\n" +
            "? untracked.txt\n");

        Assert.True(state.NoCommitsYet);
        Assert.Equal("main", state.Branch);
        Assert.Single(state.Files);
        Assert.True(state.Files[0].IsUntracked);
    }

    [Fact]
    public void StagedAndUnstagedSplit_IncludesDoubleStateFile()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n" +
            $"1 M. N... 100644 100644 100644 {Hash} {Hash} staged-only.txt\n" +
            $"1 .M N... 100644 100644 100644 {Hash} {Hash} unstaged-only.txt\n" +
            $"1 MM N... 100644 100644 100644 {Hash} {Hash} both.txt\n" +
            $"1 A. N... 100644 100644 100644 {Hash} {Hash} added.txt\n");

        Assert.Equal(["staged-only.txt", "both.txt", "added.txt"], state.Staged.Select(f => f.Path));
        Assert.Equal(["unstaged-only.txt", "both.txt"], state.Unstaged.Select(f => f.Path));

        var both = state.Files.Single(f => f.Path == "both.txt");
        Assert.Equal('M', both.IndexStatus);
        Assert.Equal('M', both.WorktreeStatus);
        Assert.True(both.HasStagedChange);
        Assert.True(both.HasUnstagedChange);
        Assert.True(state.IsDirty);
    }

    [Fact]
    public void UntrackedFile_CountsAsUnstagedOnly()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n" +
            "? fresh.txt\n");

        var file = Assert.Single(state.Files);
        Assert.True(file.IsUntracked);
        Assert.Equal("fresh.txt", file.Path);
        Assert.False(file.HasStagedChange);
        Assert.True(file.HasUnstagedChange);
        Assert.Equal("U", file.UnstagedLabel);
    }

    [Fact]
    public void StagedRename_CarriesOriginalPath()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n" +
            $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 new-name.txt\told-name.txt\n");

        var file = Assert.Single(state.Files);
        Assert.Equal("new-name.txt", file.Path);
        Assert.Equal("old-name.txt", file.OrigPath);
        Assert.Equal('R', file.IndexStatus);
        Assert.True(file.HasStagedChange);
    }

    [Fact]
    public void RenameWithSpacedPaths_SplitsOnTab()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n" +
            $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 new name.txt\told name.txt\n");

        var file = Assert.Single(state.Files);
        Assert.Equal("new name.txt", file.Path);
        Assert.Equal("old name.txt", file.OrigPath);
    }

    [Fact]
    public void ConflictRows_AreIsolatedFromStagedAndUnstaged()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n" +
            $"u UU N... 100644 100644 100644 100644 {Hash} {Hash} {Hash} conflicted.txt\n" +
            $"1 .M N... 100644 100644 100644 {Hash} {Hash} normal.txt\n");

        Assert.True(state.HasConflicts);
        var conflict = Assert.Single(state.Conflicted);
        Assert.Equal("conflicted.txt", conflict.Path);
        Assert.False(conflict.HasStagedChange);
        Assert.False(conflict.HasUnstagedChange);
        Assert.Equal("!", conflict.StagedLabel);
        Assert.Equal("!", conflict.UnstagedLabel);

        Assert.DoesNotContain(state.Staged, f => f.Path == "conflicted.txt");
        Assert.Equal(["normal.txt"], state.Unstaged.Select(f => f.Path));
    }

    [Fact]
    public void UnicodeAndSpacedPaths_ArriveIntact()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\n" +
            "# branch.head main\n" +
            $"1 .M N... 100644 100644 100644 {Hash} {Hash} 项目 计划.txt\n" +
            "? my notes ünïcode.md\n");

        Assert.Equal(2, state.Files.Count);
        Assert.Equal("项目 计划.txt", state.Files[0].Path);
        Assert.Equal("my notes ünïcode.md", state.Files[1].Path);
    }

    [Fact]
    public void CrlfLineEndings_ParseSameAsLf()
    {
        var state = WorkingState.Parse(
            $"# branch.oid {Hash}\r\n" +
            "# branch.head main\r\n" +
            "# branch.upstream origin/main\r\n" +
            "# branch.ab +1 -0\r\n" +
            "? fresh.txt\r\n");

        Assert.Equal("main", state.Branch);
        Assert.Equal("origin/main", state.Upstream);
        Assert.Equal(1, state.Ahead);
        Assert.Equal(0, state.Behind);
        Assert.Equal("fresh.txt", Assert.Single(state.Files).Path);
    }

    [Fact]
    public void EmptyInput_YieldsCleanDefaultState()
    {
        var state = WorkingState.Parse("");

        Assert.False(state.IsDirty);
        Assert.Equal("", state.Branch);
        Assert.Equal(RepoActivity.None, state.Activity);
    }
}
