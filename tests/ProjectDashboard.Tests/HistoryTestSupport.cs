using System.Diagnostics;
using System.IO;
using System.Text;
using ProjectDashboard.Services.History;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// Locates git the way the app does (known install dirs, then PATH) and fails loudly when
/// it is absent — every history test depends on a real git.exe.
/// </summary>
public static class GitGuard
{
    private static readonly Lazy<string> Resolved = new(() =>
    {
        var exe = HistoryPipeline.ResolveGitExecutable();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--version");
            using var probe = Process.Start(psi)!;
            var version = probe.StandardOutput.ReadToEnd();
            probe.StandardError.ReadToEnd();
            probe.WaitForExit(10_000);
            if (probe.ExitCode == 0 && version.Contains("git version"))
                return exe;
        }
        catch
        {
            // fall through to the loud failure below
        }
        throw new InvalidOperationException(
            "git.exe was not found in the known install locations or on PATH. " +
            "History engine tests require Git for Windows.");
    });

    public static string GitExe => Resolved.Value;
}

/// <summary>
/// Self-provisioned repo under %TEMP%\pd-fixtures\engine-&lt;random&gt; with a source repo,
/// a pipeline working dir, and an import target path. Dispose deletes the whole tree,
/// clearing read-only attributes first (git object files are read-only on Windows).
/// </summary>
public sealed class FixtureRepo : IDisposable
{
    public string Root { get; }
    public string SourcePath { get; }
    public string WorkDir { get; }
    public string TargetPath { get; }

    public FixtureRepo(bool bareSource = false, string prefix = "engine2-")
    {
        Root = Path.Combine(Path.GetTempPath(), "pd-fixtures",
            prefix + Guid.NewGuid().ToString("N")[..8]);
        SourcePath = Path.Combine(Root, "src");
        WorkDir = Path.Combine(Root, "work");
        TargetPath = Path.Combine(Root, "target.git");
        Directory.CreateDirectory(SourcePath);

        if (bareSource)
        {
            Git("init", "--bare", "-q", "-b", "main", ".");
        }
        else
        {
            Git("init", "-q", "-b", "main", ".");
            Git("config", "user.name", "Fixture");
            Git("config", "user.email", "fixture@example.com");
            Git("config", "commit.gpgsign", "false");
            Git("config", "tag.gpgsign", "false");
            Git("config", "core.autocrlf", "false");
        }
    }

    public string Git(params string[] args) => RunGit(SourcePath, args, null, null);

    public string Git(IDictionary<string, string> extraEnv, params string[] args) =>
        RunGit(SourcePath, args, null, extraEnv);

    public string GitWithStdin(byte[] stdin, params string[] args) => RunGit(SourcePath, args, stdin, null);

    public void Write(string relativePath, string content) =>
        WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(content));

    public void WriteBytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(SourcePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public void CommitAll(string message, IDictionary<string, string>? extraEnv = null)
    {
        RunGit(SourcePath, ["add", "-A"], null, null);
        RunGit(SourcePath, ["commit", "-q", "-m", message], null, extraEnv);
    }

    public static string RunGit(string workingDirectory, string[] args, byte[]? stdin, IDictionary<string, string>? extraEnv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GitGuard.GitExe,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        // Isolate from the developer's config: no global hooks, signing, or autocrlf.
        psi.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "NUL";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (extraEnv is not null)
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            process.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
            process.StandardInput.Close();
        }
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"git {string.Join(' ', args)} timed out in {workingDirectory}");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {process.ExitCode} in {workingDirectory}:\n{stderr}\n{stdout}");
        return stdout;
    }

    public void Dispose() => TryDeleteRecursive(Root);

    public static void TryDeleteRecursive(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 3)
            {
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"fixture cleanup failed for {path}: {ex.Message}");
            }
        }
    }
}

public static class HistoryTestSupport
{
    /// <summary>
    /// Full round trip plus both identity proofs: the re-emitted stream must be
    /// byte-identical to the spool, and every source ref must resolve to the same object
    /// id in the import target (with fsck --strict clean).
    /// </summary>
    public static async Task<(HistoryPipelineResult Pipeline, IdentityVerificationResult Verify)> RoundTripAsync(FixtureRepo fixture)
    {
        var pipeline = new HistoryPipeline(GitGuard.GitExe);
        var result = await pipeline.RunAsync(new HistoryPipelineOptions
        {
            SourceRepository = fixture.SourcePath,
            WorkingDirectory = fixture.WorkDir,
            TargetBareRepository = fixture.TargetPath,
            ExportTimeout = TimeSpan.FromMinutes(3),
            ImportTimeout = TimeSpan.FromMinutes(3)
        });

        var reemitPath = Path.Combine(fixture.Root, "reemit.bin");
        await using (var destination = File.Create(reemitPath))
            await HistoryPipeline.EmitAsync(result.Records, result.SpoolPath, destination);
        AssertFilesByteIdentical(result.SpoolPath, reemitPath);

        var verify = await IdentityVerifier.VerifyAsync(
            GitGuard.GitExe, fixture.SourcePath, fixture.TargetPath, TimeSpan.FromMinutes(1));
        Assert.True(verify.Success, verify.Describe());

        // for-each-ref excludes HEAD, so ref-set equality alone cannot prove the target
        // HEAD matches the source (attached or detached).
        Assert.Equal(DescribeHead(fixture.SourcePath), DescribeHead(fixture.TargetPath));
        return (result, verify);
    }

    /// <summary>HEAD as `ref &lt;branch&gt;` when attached, `detached &lt;sha&gt;` otherwise.</summary>
    public static string DescribeHead(string repository)
    {
        try
        {
            return "ref " + FixtureRepo.RunGit(repository, ["symbolic-ref", "HEAD"], null, null).Trim();
        }
        catch (InvalidOperationException)
        {
            return "detached " + FixtureRepo.RunGit(repository, ["rev-parse", "--verify", "HEAD"], null, null).Trim();
        }
    }

    public static void AssertFilesByteIdentical(string expectedPath, string actualPath)
    {
        using var expected = File.OpenRead(expectedPath);
        using var actual = File.OpenRead(actualPath);
        if (expected.Length != actual.Length)
            Assert.Fail($"re-emitted stream length {actual.Length} != spool length {expected.Length}");

        var bufferA = new byte[64 * 1024];
        var bufferB = new byte[64 * 1024];
        long offset = 0;
        while (true)
        {
            var readA = expected.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            var readB = actual.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
            Assert.Equal(readA, readB);
            if (readA == 0) return;
            for (var i = 0; i < readA; i++)
            {
                if (bufferA[i] != bufferB[i])
                    Assert.Fail($"streams differ at byte offset {offset + i}: spool=0x{bufferA[i]:X2} re-emit=0x{bufferB[i]:X2}");
            }
            offset += readA;
        }
    }
}
