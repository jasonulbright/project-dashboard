using System.Text;

namespace ProjectDashboard.Tests;

/// <summary>
/// The canonical repository shapes these tests run against, and where each one is built. Each
/// shape has exactly one builder: a second builder for a shape listed here diverges silently,
/// since a fix applied to one leaves the other carrying the defect.
///
/// BUILDERS
///
/// <see cref="FixtureRepo"/> (HistoryTestSupport.cs) — a source repo plus a work dir and a target
/// bare path, the layout every engine test needs. `bareSource: true` makes the source bare, which
/// is what a fast-import-built history needs. Use it for anything running
/// <see cref="ProjectDashboard.Services.History.HistoryPipeline"/> or
/// <see cref="ProjectDashboard.Services.History.HistoryRewriter"/>.
///
/// <see cref="SurgeryRepo"/> (SurgeryTestSupport.cs) — a working repo on `main` whose commits are
/// named by their subjects, with helpers for ref state, subjects, and range shas. Use it for
/// <see cref="ProjectDashboard.Services.Surgery.SurgeryCoordinator"/> and the rebase driver.
///
/// <see cref="TempRepo"/> (TempRepo.cs) — the smallest working repo, for view-model and
/// GitService tests that only need a repository to exist.
///
/// <see cref="SyntheticHistory"/> (below) — a linear history of any length, written as one
/// fast-import stream. The only builder for a scale fixture; both the 1,000- and the
/// 10,000-commit runs use it.
///
/// SHAPES ALREADY COVERED, and the suite that owns each
///
///   linear, merge, octopus, empty commit, detached HEAD ....... HistoryIdentityTests
///   annotated/lightweight/unicode/blob tags ................... HistoryIdentityTests
///   nested tags, mismatched tag-name headers .................. HistoryIdentityTests
///   binary payloads, NUL and high bytes ....................... HistoryIdentityTests
///   unicode paths, authors, messages; spaced/quoted paths ..... HistoryIdentityTests
///   renames, copies, mode changes ............................. HistoryIdentityTests
///   content scrub, needle survivors, payload ceilings ......... HistoryRewriterTests
///   non-UTF-8 commit encodings, signed tags ................... HistoryRewriterTests
///   glob / explicit-path / commit-range scoping, purge ........ HistoryScopedRewriterTests
///   bracketed paths (PathGlob vs git pathspec) ................ HistoryScopedRewriterTests
///   a crafted bare holding paths Windows cannot check out ..... RewriteCoordinatorTests.CraftBare
///   commit signing configured on ............................... SurgeryCoordinatorTests
///   scale: 1,000 and 10,000 linear commits .................... this file's builder
/// </summary>
public static class SyntheticHistory
{
    /// <summary>The author and committer every synthetic commit carries, so runs are reproducible.</summary>
    private const string Ident = "Fixture <fixture@example.com>";

    /// <summary>
    /// A fast-import stream for a linear history of <paramref name="commitCount"/> commits. Every
    /// third commit's payload carries <paramref name="needle"/>, so a scrub over the result has a
    /// known number of blobs to change; paths cycle through <paramref name="pathCount"/> files
    /// under <paramref name="directoryCount"/> directories, so blobs are shared across commits and
    /// a path- or directory-scoped run selects a predictable slice.
    /// </summary>
    public static string BuildStream(int commitCount, string needle, int pathCount = 20, int directoryCount = 1)
    {
        var stream = new StringBuilder(commitCount * 220);
        for (var i = 1; i <= commitCount; i++)
        {
            var blobMark = i * 2 - 1;
            var commitMark = i * 2;
            var content = i % 3 == 0 ? $"revision {i} holds {needle}\n" : $"revision {i} is clean\n";
            stream.Append($"blob\nmark :{blobMark}\ndata {Encoding.UTF8.GetByteCount(content)}\n{content}");
            var message = $"commit {i}\n";
            stream.Append($"commit refs/heads/main\nmark :{commitMark}\n");
            stream.Append($"author {Ident} {1700000000 + i} +0000\n");
            stream.Append($"committer {Ident} {1700000000 + i} +0000\n");
            stream.Append($"data {message.Length}\n{message}");
            if (i > 1) stream.Append($"from :{(i - 1) * 2}\n");
            var path = directoryCount > 1
                ? $"dir{i % directoryCount}/file{i % pathCount}.txt"
                : $"file{i % pathCount}.txt";
            stream.Append($"M 100644 :{blobMark} {path}\n");
            stream.Append('\n');
        }
        return stream.ToString();
    }

    /// <summary>Number of commits <see cref="BuildStream"/> gives a payload carrying the needle.</summary>
    public static int NeedleCommits(int commitCount) => commitCount / 3;

    /// <summary>Writes the stream into <paramref name="fixture"/>'s source repository, which must be bare.</summary>
    public static void Import(FixtureRepo fixture, string stream) =>
        fixture.GitWithStdin(Encoding.UTF8.GetBytes(stream), "fast-import", "--quiet");
}
