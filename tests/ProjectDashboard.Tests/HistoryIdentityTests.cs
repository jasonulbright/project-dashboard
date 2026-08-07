using System.Diagnostics;
using System.IO;
using System.Text;
using ProjectDashboard.Services.History;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The load-bearing suite: every fixture must survive export → parse → identity re-emit →
/// import with byte-identical streams and identical ref object ids.
/// </summary>
public class HistoryIdentityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LinearHistory()
    {
        using var f = new FixtureRepo();
        f.Write("readme.md", "one\n");
        f.CommitAll("first");
        f.Write("src/app.cs", "class A {}\n");
        f.CommitAll("second");
        f.Write("readme.md", "one\ntwo\n");
        f.CommitAll("third");

        await HistoryTestSupport.RoundTripAsync(f);
    }

    [Fact]
    public async Task MergeAndOctopus()
    {
        using var f = new FixtureRepo();
        f.Write("base.txt", "base\n");
        f.CommitAll("base");

        f.Git("switch", "-q", "-c", "b1");
        f.Write("b1.txt", "b1\n");
        f.CommitAll("on b1");
        f.Git("switch", "-q", "main");
        f.Write("main2.txt", "m2\n");
        f.CommitAll("diverge main");
        f.Git("merge", "-q", "--no-ff", "b1", "-m", "merge b1");

        f.Git("switch", "-q", "-c", "o1");
        f.Write("o1.txt", "o1\n");
        f.CommitAll("on o1");
        f.Git("switch", "-q", "main");
        f.Git("switch", "-q", "-c", "o2");
        f.Write("o2.txt", "o2\n");
        f.CommitAll("on o2");
        f.Git("switch", "-q", "main");
        f.Write("main3.txt", "m3\n");
        f.CommitAll("diverge again");
        f.Git("merge", "-q", "o1", "o2", "-m", "octopus");

        // The fixture itself must contain a 3-parent commit or the test proves nothing.
        var parents = f.Git("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ');
        Assert.Equal(4, parents.Length);

        var (_, verify) = await HistoryTestSupport.RoundTripAsync(f);

        output.WriteLine("for-each-ref equality readout (merge/octopus fixture):");
        output.WriteLine("  source refs:");
        foreach (var line in verify.SourceRefLines) output.WriteLine($"    {line}");
        output.WriteLine("  target refs:");
        foreach (var line in verify.TargetRefLines) output.WriteLine($"    {line}");
        output.WriteLine($"  match={verify.RefSetsMatch} fsck={verify.FsckPassed}");
    }

    [Fact]
    public async Task AnnotatedAndLightweightTags()
    {
        using var f = new FixtureRepo();
        f.Write("a.txt", "a\n");
        f.CommitAll("first");
        f.Write("a.txt", "a\nb\n");
        f.CommitAll("second");

        f.Git("tag", "-a", "v1.0", "-m", "annotated Täg 日本\nsecond line", "HEAD~1");
        f.Git("tag", "-a", "täg-日本", "-m", "unicode tag name");
        f.Git("tag", "light-tip");
        f.Git("tag", "light-old", "HEAD~1");

        await HistoryTestSupport.RoundTripAsync(f);
    }

    [Fact]
    public async Task BinaryFileWithNulAndHighBytes()
    {
        using var f = new FixtureRepo();
        var binary = new List<byte> { 0x00, 0xFF, 0x01, 0x7F, 0x80 };
        // Embed record-shaped text inside the binary payload to stress payload counting
        // in the real pipeline, not just the unit tests.
        binary.AddRange(Encoding.ASCII.GetBytes("\ncommit refs/heads/main\nmark :1\ndata 5\n"));
        binary.AddRange([0x00, 0xFE, 0x0A, 0x0D, 0x0A]);
        f.WriteBytes("blob.bin", [.. binary]);
        f.Write("plain.txt", "text\n");
        f.CommitAll("binary");

        binary.Reverse();
        f.WriteBytes("blob.bin", [.. binary]);
        f.CommitAll("binary changed");

        await HistoryTestSupport.RoundTripAsync(f);
    }

    [Fact]
    public async Task UnicodePathAuthorAndMessage()
    {
        using var f = new FixtureRepo();
        f.Write("päth-日本語.txt", "uni\n");
        f.Write("русский/файл.md", "cyr\n");
        f.CommitAll("メッセージ 日本語 🚀\n\nbody with Türkçe and Ελληνικά", new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Ünïcode Авторъ 日本",
            ["GIT_AUTHOR_EMAIL"] = "unicode@example.com",
            ["GIT_COMMITTER_NAME"] = "Cömmïtter 委員",
            ["GIT_COMMITTER_EMAIL"] = "committer@example.com"
        });

        await HistoryTestSupport.RoundTripAsync(f);
    }

    [Fact]
    public async Task SpacedAndQuotedCharacterPaths()
    {
        using var f = new FixtureRepo();
        f.Write("a b.txt", "spaced\n");
        f.Write("dir name/x y.txt", "nested spaced\n");
        f.CommitAll("spaced paths");

        // A literal double quote is invalid in Windows file names, so the path enters the
        // index directly; core.protectNTFS=false bypasses the NTFS name check for the
        // index write. Import writes trees only, so the target never materializes it.
        var sha = f.GitWithStdin(Encoding.ASCII.GetBytes("quoted content\n"), "hash-object", "-w", "--stdin").Trim();
        f.Git("-c", "core.protectNTFS=false", "update-index", "--add", "--cacheinfo", $"100644,{sha},q\"uo te.txt");
        f.Git("commit", "-q", "-m", "quote-char path");

        var (pipeline, _) = await HistoryTestSupport.RoundTripAsync(f);

        // The quoted path must actually appear C-quoted in the stream.
        var quoted = pipeline.Index.CommitsInOrder
            .SelectMany(c => c.FileModifies)
            .Any(m => m.Path.ToString() == "q\"uo te.txt" && m.Path.RawToken[0] == (byte)'"');
        Assert.True(quoted, "expected a C-quoted path token for q\"uo te.txt in the exported stream");
    }

    [Fact]
    public async Task RenameAndCopy()
    {
        using var f = new FixtureRepo();
        f.Write("original.txt", "content\n");
        f.CommitAll("add original");
        f.Git("mv", "original.txt", "renamed.txt");
        f.Git("commit", "-q", "-m", "rename");
        File.Copy(Path.Combine(f.SourcePath, "renamed.txt"), Path.Combine(f.SourcePath, "copied.txt"));
        f.CommitAll("copy");

        await HistoryTestSupport.RoundTripAsync(f);
    }

    [Fact]
    public async Task ExecutableBitChange()
    {
        using var f = new FixtureRepo();
        f.Write("tool.sh", "#!/bin/sh\necho hi\n");
        f.CommitAll("add tool");
        f.Git("update-index", "--chmod=+x", "tool.sh");
        f.Git("commit", "-q", "-m", "make executable");

        var (pipeline, _) = await HistoryTestSupport.RoundTripAsync(f);

        var modes = pipeline.Index.CommitsInOrder
            .SelectMany(c => c.FileModifies)
            .Select(m => m.Mode)
            .ToHashSet();
        Assert.Contains("100644", modes);
        Assert.Contains("100755", modes);
    }

    [Fact]
    public async Task EmptyCommit()
    {
        using var f = new FixtureRepo();
        f.Write("a.txt", "a\n");
        f.CommitAll("first");
        f.Git("commit", "-q", "--allow-empty", "-m", "empty commit");

        await HistoryTestSupport.RoundTripAsync(f);
    }

    [Fact]
    public async Task BranchNameWithSlashes()
    {
        using var f = new FixtureRepo();
        f.Write("a.txt", "a\n");
        f.CommitAll("base");
        f.Git("switch", "-q", "-c", "feature/deep/branch-name.v2");
        f.Write("feature.txt", "feat\n");
        f.CommitAll("feature work");
        f.Git("switch", "-q", "main");

        await HistoryTestSupport.RoundTripAsync(f);
    }

    [Fact]
    public async Task ReplacedCommitExportsOriginalHistoryAndReplaceRef()
    {
        using var f = new FixtureRepo();
        f.Write("a.txt", "a\n");
        f.CommitAll("first");
        f.Write("a.txt", "b\n");
        f.CommitAll("second");

        // A replacement for the root commit: same tree, different message. With the
        // replace ref active, a plain fast-export walk substitutes it into main's
        // history, so every descendant commit id changes on import.
        var original = f.Git("rev-parse", "HEAD~1").Trim();
        var tree = f.Git("rev-parse", "HEAD~1^{tree}").Trim();
        var replacement = f.GitWithStdin(Encoding.ASCII.GetBytes("replacement first\n"), "commit-tree", tree).Trim();
        f.Git("replace", original, replacement);
        Assert.Contains(original, f.Git("replace", "-l"));

        var (_, verify) = await HistoryTestSupport.RoundTripAsync(f);

        Assert.Contains(verify.SourceRefLines, l => l.StartsWith($"refs/replace/{original} "));
        Assert.Contains(verify.TargetRefLines, l => l.StartsWith($"refs/replace/{original} "));
    }

    [Fact]
    public async Task ThousandCommitSyntheticHistory()
    {
        using var f = new FixtureRepo(bareSource: true);

        // Building 1000 commits with git subprocess pairs is minutes of process spawns;
        // one fast-import stream builds the same history in well under a second.
        var stream = new StringBuilder();
        for (var i = 1; i <= 1000; i++)
        {
            var blobMark = i * 2 - 1;
            var commitMark = i * 2;
            var content = $"content of revision {i}\n";
            stream.Append($"blob\nmark :{blobMark}\ndata {content.Length}\n{content}");
            var message = $"commit {i}\n";
            stream.Append($"commit refs/heads/main\nmark :{commitMark}\n");
            stream.Append($"author Fixture <fixture@example.com> {1700000000 + i} +0000\n");
            stream.Append($"committer Fixture <fixture@example.com> {1700000000 + i} +0000\n");
            stream.Append($"data {message.Length}\n{message}");
            if (i > 1) stream.Append($"from :{(i - 1) * 2}\n");
            stream.Append($"M 100644 :{blobMark} file{i % 20}.txt\n");
            stream.Append('\n');
        }
        f.GitWithStdin(Encoding.UTF8.GetBytes(stream.ToString()), "fast-import", "--quiet");

        var stopwatch = Stopwatch.StartNew();
        var (pipeline, verify) = await HistoryTestSupport.RoundTripAsync(f);
        stopwatch.Stop();

        Assert.Equal(1000, pipeline.Index.Commits.Count);
        Assert.Equal(2000, pipeline.Index.MaxMark);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"1000-commit round trip took {stopwatch.Elapsed} — expected well under a minute");

        using var self = Process.GetCurrentProcess();
        output.WriteLine($"1000-commit fixture: {stopwatch.Elapsed.TotalSeconds:F2}s wall, " +
                         $"{pipeline.BytesSpooled:N0} bytes spooled, {pipeline.Records.Count:N0} records, " +
                         $"peak working set {self.PeakWorkingSet64 / (1024 * 1024)} MB (process-wide)");
        output.WriteLine($"refs: {verify.SourceRefCount} source / {verify.TargetRefCount} target, match={verify.RefSetsMatch}");
    }

    [Fact]
    public void GitIsLocatableTheWayTheAppLocatesIt()
    {
        // Guard for the whole suite: known install dirs first, then PATH.
        var exe = GitGuard.GitExe;
        Assert.False(string.IsNullOrWhiteSpace(exe));
        var version = FixtureRepo.RunGit(Path.GetTempPath(), ["--version"], null, null);
        Assert.Contains("git version", version);
        output.WriteLine($"git: {exe} ({version.Trim()})");
    }
}
