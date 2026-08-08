using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>Submodule listing, divergence, and the guarded init/update/sync/deinit operations.</summary>
public class SubmoduleServiceTests
{
    private readonly GitService _git = new();
    private readonly SubmoduleService _subs;

    public SubmoduleServiceTests() => _subs = new SubmoduleService(_git);

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>Adds <paramref name="child"/> at <paramref name="path"/> and commits the superproject.</summary>
    private static async Task AddSubmoduleAsync(TempRepo super, TempRepo child, string path)
    {
        await super.GitAsync("submodule", "add", "--", child.FileUrl, path);
        await super.CommitAllAsync($"add submodule {path}");
    }

    private static string Full(TempRepo super, string path) =>
        Path.Combine(super.Path, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The listing, asserting the read reached the index. Every fixture below has a readable
    /// one, so a tri-state error here means the fixture broke, not the case under test.
    /// </summary>
    private async Task<List<SubmoduleEntry>> ListAsync(string repoPath)
    {
        var result = await _subs.GetSubmodulesAsync(repoPath);
        Assert.False(result.HasError, result.ErrorText);
        return result.Submodules;
    }

    // ── Listing matrix ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSubmodules_NoSubmodules_ReturnsEmpty()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("sub-none");
        Assert.Empty(await ListAsync(repo.Path));
    }

    [Fact]
    public async Task GetSubmodules_CleanSubmodule_ReportsInitializedAndInSync()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sub-child");
        using var super = await TempRepo.CreateWithCommitAsync("sub-super");
        await AddSubmoduleAsync(super, child, "lib");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.Equal("lib", entry.Name);
        Assert.Equal("lib", entry.Path);
        Assert.Equal(child.FileUrl, entry.Url);
        Assert.True(entry.DeclaredInGitmodules);
        Assert.True(entry.RecordedInIndex);
        Assert.True(entry.WorkingTreeExists);
        Assert.True(entry.IsInitialized);
        Assert.Equal(SubmoduleGitDir.Linked, entry.GitDir);
        Assert.Equal(await child.HeadShaAsync(), entry.RecordedSha);
        Assert.Equal(entry.RecordedSha, entry.CurrentSha);
        Assert.False(entry.CommitDiffersFromRecorded);
        Assert.False(entry.IsDirty);
        Assert.False(entry.HasNestedSubmodules);
        Assert.Null(entry.TrackedBranch);
    }

    [Fact]
    public async Task GetSubmodules_DirtySubmodule_ReportsModifiedAndUntracked()
    {
        using var child = await TempRepo.CreateWithCommitAsync("dirty-child");
        using var super = await TempRepo.CreateWithCommitAsync("dirty-super");
        await AddSubmoduleAsync(super, child, "lib");

        File.WriteAllText(Path.Combine(Full(super, "lib"), "file.txt"), "edited\n");
        File.WriteAllText(Path.Combine(Full(super, "lib"), "extra.txt"), "new\n");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.True(entry.HasModifiedContent);
        Assert.True(entry.HasUntrackedContent);
        Assert.True(entry.IsDirty);
        Assert.False(entry.CommitDiffersFromRecorded);
    }

    [Fact]
    public async Task GetSubmodules_CommitInsideSubmodule_ReportsDivergenceFromRecordedSha()
    {
        using var child = await TempRepo.CreateWithCommitAsync("ahead-child");
        using var super = await TempRepo.CreateWithCommitAsync("ahead-super");
        await AddSubmoduleAsync(super, child, "lib");
        var recorded = await child.HeadShaAsync();

        var sub = Full(super, "lib");
        File.WriteAllText(Path.Combine(sub, "file.txt"), "local work\n");
        await Git.RunAsync(sub, "commit", "-am", "local work");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.Equal(recorded, entry.RecordedSha);
        Assert.NotEqual(recorded, entry.CurrentSha);
        Assert.True(entry.CommitDiffersFromRecorded);
        Assert.False(entry.IsDirty);
        Assert.Equal("main", entry.CheckedOutBranch);
        Assert.False(entry.IsDetached);

        var divergence = await _subs.GetDivergenceAsync(super.Path, entry);
        Assert.Equal(new SubmoduleDivergence(Ahead: 1, Behind: 0), divergence);
    }

    [Fact]
    public async Task GetSubmodules_Uninitialized_KeepsRecordedShaAndReportsNoGitDir()
    {
        using var child = await TempRepo.CreateWithCommitAsync("uninit-child");
        using var super = await TempRepo.CreateWithCommitAsync("uninit-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("submodule", "deinit", "-f", "--", "lib");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.True(entry.DeclaredInGitmodules);
        Assert.True(entry.RecordedInIndex);
        Assert.True(entry.WorkingTreeExists);
        Assert.False(entry.IsInitialized);
        Assert.Equal(SubmoduleGitDir.None, entry.GitDir);
        Assert.Equal(await child.HeadShaAsync(), entry.RecordedSha);
        // The empty directory must not make git answer for the SUPERPROJECT.
        Assert.Equal("", entry.CurrentSha);
        Assert.False(entry.CommitDiffersFromRecorded);
        Assert.Null(await _subs.GetDivergenceAsync(super.Path, entry));
    }

    [Fact]
    public async Task GetSubmodules_MissingWorkingTree_StillListedFromDeclarationAndIndex()
    {
        using var child = await TempRepo.CreateWithCommitAsync("gone-child");
        using var super = await TempRepo.CreateWithCommitAsync("gone-super");
        await AddSubmoduleAsync(super, child, "lib");
        TestEnv.TryDeleteTree(Full(super, "lib"));

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.True(entry.DeclaredInGitmodules);
        Assert.True(entry.RecordedInIndex);
        Assert.False(entry.WorkingTreeExists);
        Assert.False(entry.IsInitialized);
        Assert.Equal(SubmoduleGitDir.None, entry.GitDir);
        Assert.NotEqual("", entry.RecordedSha);
        Assert.Equal("", entry.CurrentSha);
    }

    [Fact]
    public async Task GetSubmodules_ConflictedGitlink_RecordsOurSideAndFlagsTheConflict()
    {
        using var child = await TempRepo.CreateWithCommitAsync("conflict-child");
        using var super = await TempRepo.CreateWithCommitAsync("conflict-super");
        await AddSubmoduleAsync(super, child, "lib");
        var ours = await child.HeadShaAsync();
        const string mergeBase = "1111111111111111111111111111111111111111";
        const string theirs = "2222222222222222222222222222222222222222";

        // The index a submodule merge conflict leaves: the stage-0 entry is gone and stages
        // 1/2/3 hold base/ours/theirs. The leading mode-0 line drops stage 0 first, so the
        // fixture is byte-identical to what `git merge` produces for a conflicted gitlink.
        await Git.RunWithStdinAsync(super.Path,
            $"0 0000000000000000000000000000000000000000\tlib\n" +
            $"160000 {mergeBase} 1\tlib\n160000 {ours} 2\tlib\n160000 {theirs} 3\tlib\n",
            "update-index", "--index-info");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.True(entry.IsConflicted);
        Assert.True(entry.RecordedInIndex);
        Assert.Equal(ours, entry.RecordedSha);
        Assert.NotEqual(theirs, entry.RecordedSha);
        // The checkout still sits on OUR commit, which is not a divergence.
        Assert.Equal(ours, entry.CurrentSha);
        Assert.False(entry.CommitDiffersFromRecorded);
    }

    [Fact]
    public async Task GetSubmodules_MergedGitlink_IsNotFlaggedConflicted()
    {
        using var child = await TempRepo.CreateWithCommitAsync("unconflicted-child");
        using var super = await TempRepo.CreateWithCommitAsync("unconflicted-super");
        await AddSubmoduleAsync(super, child, "lib");

        Assert.False(Assert.Single(await ListAsync(super.Path)).IsConflicted);
    }

    [Fact]
    public async Task GetSubmodules_DeclaredPathVariant_DoesNotDoubleListOneSubmodule()
    {
        using var child = await TempRepo.CreateWithCommitAsync("norm-child");
        using var super = await TempRepo.CreateWithCommitAsync("norm-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("config", "-f", ".gitmodules", "submodule.lib.path", "./lib");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.Equal("lib", entry.Path);
        Assert.True(entry.DeclaredInGitmodules);
        Assert.True(entry.RecordedInIndex);
        Assert.True(entry.IsInitialized);
    }

    [Fact]
    public async Task GetSubmodules_UnicodeAndSpacedPaths_RoundTrip()
    {
        using var child = await TempRepo.CreateWithCommitAsync("uni-child");
        using var super = await TempRepo.CreateWithCommitAsync("uni-super");
        await AddSubmoduleAsync(super, child, "vendor libs/sub-ünïcodé");
        await AddSubmoduleAsync(super, child, "plain");

        var entries = await ListAsync(super.Path);
        Assert.Equal(2, entries.Count);

        var spaced = entries.Single(e => e.Path == "vendor libs/sub-ünïcodé");
        Assert.Equal("vendor libs/sub-ünïcodé", spaced.Name);
        Assert.True(spaced.IsInitialized);
        Assert.Equal(await child.HeadShaAsync(), spaced.RecordedSha);
        Assert.Equal(spaced.RecordedSha, spaced.CurrentSha);
    }

    [Fact]
    public async Task GetSubmodules_NestedSubmodule_IsReportedWithoutRecursing()
    {
        using var grandchild = await TempRepo.CreateWithCommitAsync("nest-grand");
        using var child = await TempRepo.CreateWithCommitAsync("nest-child");
        await child.GitAsync("submodule", "add", "--", grandchild.FileUrl, "inner");
        await child.CommitAllAsync("add nested submodule");

        using var super = await TempRepo.CreateWithCommitAsync("nest-super");
        await AddSubmoduleAsync(super, child, "lib");

        // One level only: the listing names the superproject's submodule, never the
        // submodule's own.
        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.Equal("lib", entry.Path);
        Assert.True(entry.HasNestedSubmodules);
    }

    [Fact]
    public async Task GetSubmodules_NonAbsorbedGitlinkWithoutDeclaration_IsListedAsEmbedded()
    {
        using var child = await TempRepo.CreateWithCommitAsync("embed-child");
        using var super = await TempRepo.CreateWithCommitAsync("embed-super");

        // A clone added straight to the index keeps a real .git DIRECTORY and gets no
        // .gitmodules section: a gitlink git tracks but never declared.
        await Git.RunAsync(super.Path, "clone", child.FileUrl, "embedded");
        await super.GitAsync("add", "embedded");
        await super.CommitAllAsync("embed a clone");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.Equal("embedded", entry.Path);
        Assert.Equal("embedded", entry.Name);
        Assert.False(entry.DeclaredInGitmodules);
        Assert.True(entry.RecordedInIndex);
        Assert.True(entry.IsInitialized);
        Assert.Equal(SubmoduleGitDir.Embedded, entry.GitDir);
        Assert.Equal(await child.HeadShaAsync(), entry.CurrentSha);
    }

    [Fact]
    public async Task GetSubmodules_TrackedBranchSetting_IsSurfaced()
    {
        using var child = await TempRepo.CreateWithCommitAsync("branch-child");
        using var super = await TempRepo.CreateWithCommitAsync("branch-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("config", "-f", ".gitmodules", "submodule.lib.branch", "main");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.Equal("main", entry.TrackedBranch);
    }

    [Fact]
    public async Task GetSubmodules_DetachedCheckout_ReportsNoBranch()
    {
        using var child = await TempRepo.CreateWithCommitAsync("det-child");
        using var super = await TempRepo.CreateWithCommitAsync("det-super");
        await AddSubmoduleAsync(super, child, "lib");
        await Git.RunAsync(Full(super, "lib"), "checkout", "--detach", "HEAD");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.True(entry.IsDetached);
        Assert.Null(entry.CheckedOutBranch);
    }

    // ── Operations ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithInit_ClonesAnUninitializedSubmodule()
    {
        using var child = await TempRepo.CreateWithCommitAsync("upd-child");
        using var super = await TempRepo.CreateWithCommitAsync("upd-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("submodule", "deinit", "-f", "--", "lib");
        Assert.False((await ListAsync(super.Path))[0].IsInitialized);

        var result = await _subs.UpdateAsync(super.Path, new SubmoduleUpdateRequest { Init = true, Path = "lib" });
        Assert.True(result.Success, result.FirstError);

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.True(entry.IsInitialized);
        Assert.Equal(entry.RecordedSha, entry.CurrentSha);
    }

    [Fact]
    public async Task Update_WithDepth_ClonesShallowWhenTheSubmoduleHasNoGitDirYet()
    {
        using var child = await TempRepo.CreateWithCommitAsync("depth-child");
        child.WriteFile("file.txt", "second\n");
        await child.CommitAllAsync("second");
        child.WriteFile("file.txt", "third\n");
        await child.CommitAllAsync("third");

        using var super = await TempRepo.CreateWithCommitAsync("depth-super");
        await AddSubmoduleAsync(super, child, "lib");

        // A fresh clone of the superproject carries no .git/modules, so the update really
        // clones the submodule and --depth applies.
        using var fresh = await TempRepo.CloneFromAsync(super, "depth-fresh");
        var result = await _subs.UpdateAsync(fresh.Path,
            new SubmoduleUpdateRequest { Init = true, Depth = 1, Path = "lib" });
        Assert.True(result.Success, result.FirstError);

        var count = (await Git.RunAsync(Full(fresh, "lib"), "rev-list", "--count", "HEAD")).Trim();
        Assert.Equal("1", count);
    }

    [Fact]
    public async Task Update_WithDepth_AfterDeinit_StaysFullDepth()
    {
        using var child = await TempRepo.CreateWithCommitAsync("depth2-child");
        child.WriteFile("file.txt", "second\n");
        await child.CommitAllAsync("second");
        child.WriteFile("file.txt", "third\n");
        await child.CommitAllAsync("third");

        using var super = await TempRepo.CreateWithCommitAsync("depth2-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("submodule", "deinit", "-f", "--", "lib");

        // deinit keeps .git/modules/lib, so no clone happens and git has nothing to
        // shorten: the depth request is honestly a no-op here.
        var result = await _subs.UpdateAsync(super.Path,
            new SubmoduleUpdateRequest { Init = true, Depth = 1, Path = "lib" });
        Assert.True(result.Success, result.FirstError);

        var count = (await Git.RunAsync(Full(super, "lib"), "rev-list", "--count", "HEAD")).Trim();
        Assert.Equal("3", count);
    }

    [Fact]
    public async Task Update_NegativeDepth_IsRefused()
    {
        using var super = await TempRepo.CreateWithCommitAsync("depth-bad");
        var result = await _subs.UpdateAsync(super.Path, new SubmoduleUpdateRequest { Depth = 0 });
        Assert.False(result.Success);
        Assert.Contains("--depth", result.FirstError);
    }

    [Fact]
    public async Task Init_RegistersTheSubmoduleUrlWithoutCloning()
    {
        using var child = await TempRepo.CreateWithCommitAsync("init-child");
        using var super = await TempRepo.CreateWithCommitAsync("init-super");
        await AddSubmoduleAsync(super, child, "lib");
        await super.GitAsync("submodule", "deinit", "-f", "--", "lib");

        Assert.True((await _subs.InitAsync(super.Path, "lib")).Success);

        var url = (await super.GitAsync("config", "--get", "submodule.lib.url")).Trim();
        Assert.Equal(child.FileUrl, url);
        Assert.False((await ListAsync(super.Path))[0].IsInitialized);
    }

    [Fact]
    public async Task Sync_CopiesTheGitmodulesUrlIntoTheRepoConfig()
    {
        using var child = await TempRepo.CreateWithCommitAsync("sync-child");
        using var moved = await TempRepo.CreateWithCommitAsync("sync-moved");
        using var super = await TempRepo.CreateWithCommitAsync("sync-super");
        await AddSubmoduleAsync(super, child, "lib");

        await super.GitAsync("config", "-f", ".gitmodules", "submodule.lib.url", moved.FileUrl);
        Assert.True((await _subs.SyncAsync(super.Path, "lib")).Success);

        Assert.Equal(moved.FileUrl, (await super.GitAsync("config", "--get", "submodule.lib.url")).Trim());
    }

    // ── Destructive guards ───────────────────────────────────────────────────

    [Fact]
    public async Task Deinit_WithoutConfirmation_IsRefusedAndLeavesTheCheckout()
    {
        using var child = await TempRepo.CreateWithCommitAsync("guard-child");
        using var super = await TempRepo.CreateWithCommitAsync("guard-super");
        await AddSubmoduleAsync(super, child, "lib");

        var result = await _subs.DeinitAsync(super.Path, new SubmoduleDeinitRequest { Path = "lib" });
        Assert.False(result.Success);
        Assert.Contains("ConfirmDiscard", result.FirstError);
        Assert.True(File.Exists(Path.Combine(Full(super, "lib"), "file.txt")));
        Assert.True((await ListAsync(super.Path))[0].IsInitialized);
    }

    [Fact]
    public async Task Deinit_Confirmed_ClearsTheWorkingTree()
    {
        using var child = await TempRepo.CreateWithCommitAsync("deinit-child");
        using var super = await TempRepo.CreateWithCommitAsync("deinit-super");
        await AddSubmoduleAsync(super, child, "lib");

        var result = await _subs.DeinitAsync(super.Path,
            new SubmoduleDeinitRequest { Path = "lib", ConfirmDiscard = true });
        Assert.True(result.Success, result.FirstError);

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.False(entry.IsInitialized);
        Assert.False(File.Exists(Path.Combine(Full(super, "lib"), "file.txt")));
    }

    [Fact]
    public async Task Deinit_DirtySubmodule_NeedsForceOnTopOfConfirmation()
    {
        using var child = await TempRepo.CreateWithCommitAsync("deinit-dirty-child");
        using var super = await TempRepo.CreateWithCommitAsync("deinit-dirty-super");
        await AddSubmoduleAsync(super, child, "lib");
        File.WriteAllText(Path.Combine(Full(super, "lib"), "file.txt"), "uncommitted\n");

        var unforced = await _subs.DeinitAsync(super.Path,
            new SubmoduleDeinitRequest { Path = "lib", ConfirmDiscard = true });
        Assert.False(unforced.Success);
        Assert.True((await ListAsync(super.Path))[0].IsInitialized);

        var forced = await _subs.DeinitAsync(super.Path,
            new SubmoduleDeinitRequest { Path = "lib", ConfirmDiscard = true, Force = true });
        Assert.True(forced.Success, forced.FirstError);
        Assert.False((await ListAsync(super.Path))[0].IsInitialized);
    }

    [Fact]
    public async Task Deinit_BlankPath_IsRefusedRatherThanWidenedToEverySubmodule()
    {
        using var child = await TempRepo.CreateWithCommitAsync("blank-child");
        using var super = await TempRepo.CreateWithCommitAsync("blank-super");
        await AddSubmoduleAsync(super, child, "lib");

        var result = await _subs.DeinitAsync(super.Path,
            new SubmoduleDeinitRequest { Path = "   ", ConfirmDiscard = true });
        Assert.False(result.Success);
        Assert.True((await ListAsync(super.Path))[0].IsInitialized);
    }

    [Fact]
    public async Task Update_ForceWithoutConfirmation_IsRefusedAndKeepsLocalWork()
    {
        using var child = await TempRepo.CreateWithCommitAsync("force-child");
        using var super = await TempRepo.CreateWithCommitAsync("force-super");
        await AddSubmoduleAsync(super, child, "lib");
        File.WriteAllText(Path.Combine(Full(super, "lib"), "file.txt"), "local work\n");

        var result = await _subs.UpdateAsync(super.Path,
            new SubmoduleUpdateRequest { Path = "lib", Force = true });
        Assert.False(result.Success);
        Assert.Contains("ConfirmDiscard", result.FirstError);
        Assert.Equal("local work\n", File.ReadAllText(Path.Combine(Full(super, "lib"), "file.txt")));
    }

    [Fact]
    public async Task Update_ForceConfirmed_ResetsTheSubmoduleCheckout()
    {
        using var child = await TempRepo.CreateWithCommitAsync("force-ok-child");
        using var super = await TempRepo.CreateWithCommitAsync("force-ok-super");
        await AddSubmoduleAsync(super, child, "lib");
        File.WriteAllText(Path.Combine(Full(super, "lib"), "file.txt"), "local work\n");

        var result = await _subs.UpdateAsync(super.Path,
            new SubmoduleUpdateRequest { Path = "lib", Force = true, ConfirmDiscard = true });
        Assert.True(result.Success, result.FirstError);
        Assert.Equal("line one\n", File.ReadAllText(Path.Combine(Full(super, "lib"), "file.txt")));
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParseGitmodulesConfig_KeepsDottedNamesSpacedPathsAndBranch()
    {
        var configZ =
            "submodule.libs/a.b.path\nlibs/a.b\0" +
            "submodule.libs/a.b.url\nhttps://example.invalid/a.b.git\0" +
            "submodule.libs/a.b.branch\nrelease/2.0\0" +
            "submodule.vendor libs/sub-ünïcodé.path\nvendor libs/sub-ünïcodé\0" +
            "submodule.vendor libs/sub-ünïcodé.url\n../uni.git\0" +
            "submodule.ignored.url\nno-path-so-not-checkoutable\0" +
            "valuelesskey\0";

        var declared = SubmoduleService.ParseGitmodulesConfig(configZ);
        Assert.Equal(2, declared.Count);

        Assert.Equal("libs/a.b", declared[0].Name);
        Assert.Equal("libs/a.b", declared[0].Path);
        Assert.Equal("https://example.invalid/a.b.git", declared[0].Url);
        Assert.Equal("release/2.0", declared[0].Branch);

        Assert.Equal("vendor libs/sub-ünïcodé", declared[1].Path);
        Assert.Null(declared[1].Branch);
    }

    [Fact]
    public void ParseGitmodulesConfig_SecondSectionClaimingOnePath_IsDropped()
    {
        var configZ =
            "submodule.first.path\nlib\0submodule.first.url\nhttps://example.invalid/one.git\0" +
            "submodule.second.path\nlib\0submodule.second.url\nhttps://example.invalid/two.git\0";

        var declared = Assert.Single(SubmoduleService.ParseGitmodulesConfig(configZ));
        Assert.Equal("first", declared.Name);
    }

    [Fact]
    public void ResolveInsideRepo_RejectsAbsoluteAndEscapingDeclarations()
    {
        var root = Path.Combine(TestEnv.Root, "resolve-super");

        Assert.Equal(Path.Combine(root, "lib"), SubmoduleService.ResolveInsideRepo(root, "lib"));
        Assert.Equal(Path.Combine(root, "vendor", "lib"), SubmoduleService.ResolveInsideRepo(root, "vendor/lib"));

        Assert.Null(SubmoduleService.ResolveInsideRepo(root, "../outside"));
        Assert.Null(SubmoduleService.ResolveInsideRepo(root, "vendor/../../outside"));
        Assert.Null(SubmoduleService.ResolveInsideRepo(root, @"C:\Windows"));
        Assert.Null(SubmoduleService.ResolveInsideRepo(root, "."));
        Assert.Null(SubmoduleService.ResolveInsideRepo(root, ""));
    }

    /// <summary>
    /// A repository checked out at a drive root: the root already ends in a separator, and
    /// a containment check that appends one of its own rejects every submodule under it.
    /// The check reads only the path strings, so no drive-root fixture is created.
    /// </summary>
    [Fact]
    public void ResolveInsideRepo_AcceptsSubmodulesOfARepoAtADriveRoot()
    {
        Assert.Equal(@"C:\lib", SubmoduleService.ResolveInsideRepo(@"C:\", "lib"));
        Assert.Equal(@"C:\vendor\lib", SubmoduleService.ResolveInsideRepo(@"C:\", "vendor/lib"));
        Assert.Null(SubmoduleService.ResolveInsideRepo(@"C:\", "."));
        Assert.Null(SubmoduleService.ResolveInsideRepo(@"C:\", @"D:\other"));

        // A root handed in with a trailing separator resolves the same as one without.
        Assert.Equal(@"C:\repo\lib", SubmoduleService.ResolveInsideRepo(@"C:\repo\", "lib"));
        Assert.Equal(@"C:\repo\lib", SubmoduleService.ResolveInsideRepo(@"C:\repo", "lib"));
    }

    [Fact]
    public void ParseGitmodulesConfig_NormalizesDeclaredPathsToTheFormTheIndexUses()
    {
        foreach (var variant in new[] { "./lib", "lib/", @"lib\", "././lib/", ".//lib" })
        {
            var declared = Assert.Single(SubmoduleService.ParseGitmodulesConfig(
                $"submodule.lib.path\n{variant}\0submodule.lib.url\nhttps://example.invalid/lib.git\0"));
            Assert.Equal("lib", declared.Path);
        }

        // A "." component inside the path is as inert as a leading one.
        var nested = Assert.Single(SubmoduleService.ParseGitmodulesConfig(
            "submodule.nested.path\na/./b\0submodule.nested.url\nhttps://example.invalid/b.git\0"));
        Assert.Equal("a/b", nested.Path);

        // Two sections whose paths differ only in those decorations claim one submodule.
        var single = Assert.Single(SubmoduleService.ParseGitmodulesConfig(
            "submodule.first.path\n.//lib\0submodule.second.path\nlib/\0"));
        Assert.Equal("first", single.Name);

        var nestedSingle = Assert.Single(SubmoduleService.ParseGitmodulesConfig(
            "submodule.first.path\na/./b\0submodule.second.path\na/b\0"));
        Assert.Equal("first", nestedSingle.Name);
    }

    [Fact]
    public async Task GetSubmodules_DeclarationEscapingTheRepo_IsListedButNeverEntered()
    {
        using var super = await TempRepo.CreateWithCommitAsync("escape-super");
        super.WriteFile(".gitmodules", """
            [submodule "escape"]
            	path = ../outside
            	url = https://example.invalid/outside.git
            """);
        await super.CommitAllAsync("hand-written .gitmodules");

        var entry = Assert.Single(await ListAsync(super.Path));
        Assert.Equal("../outside", entry.Path);
        Assert.True(entry.DeclaredInGitmodules);
        Assert.False(entry.WorkingTreeExists);
        Assert.False(entry.IsInitialized);
        Assert.Equal(SubmoduleGitDir.None, entry.GitDir);
        Assert.Equal("", entry.CurrentSha);
    }

    [Fact]
    public void ParseGitlinks_TakesOnlyMode160000AndKeepsSpacedPaths()
    {
        var lsFiles =
            "100644 e1ce5e441d88a28fa05b5d5c9d6f29dce188e078 0\t.gitmodules\0" +
            "160000 70524b4ed1d8efcdb64249a843fd327e45c43259 0\tvendor libs/sub-ünïcodé\0" +
            "100644 16f5c2d3aa9656fc424352e4cfaa2523c809778b 0\tfile.txt\0" +
            "160000 c440451ac29a1a3772d838cb3701632561a8a7cf 0\tlib\0";

        var links = SubmoduleService.ParseGitlinks(lsFiles);
        Assert.Equal(2, links.Count);
        Assert.Equal("70524b4ed1d8efcdb64249a843fd327e45c43259", links["vendor libs/sub-ünïcodé"].Sha);
        Assert.Equal("c440451ac29a1a3772d838cb3701632561a8a7cf", links["lib"].Sha);
        Assert.All(links.Values, link => Assert.False(link.Conflicted));
    }

    [Fact]
    public void ParseGitlinks_UnmergedStages_TakeOursAndNeverTheLastRecord()
    {
        const string mergeBase = "1111111111111111111111111111111111111111";
        const string ours = "2222222222222222222222222222222222222222";
        const string theirs = "3333333333333333333333333333333333333333";

        var conflicted = SubmoduleService.ParseGitlinks(
            $"160000 {mergeBase} 1\tlib\0160000 {ours} 2\tlib\0160000 {theirs} 3\tlib\0");
        var link = Assert.Single(conflicted).Value;
        Assert.True(link.Conflicted);
        Assert.Equal(ours, link.Sha);

        // Deleted on our side: stage 2 is absent, so no commit is claimed as recorded.
        var deletedByUs = SubmoduleService.ParseGitlinks(
            $"160000 {mergeBase} 1\tlib\0160000 {theirs} 3\tlib\0");
        Assert.True(deletedByUs["lib"].Conflicted);
        Assert.Equal("", deletedByUs["lib"].Sha);
    }
}
