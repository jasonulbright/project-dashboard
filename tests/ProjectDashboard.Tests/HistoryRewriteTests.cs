using System.Text;
using System.Text.RegularExpressions;
using ProjectDashboard.Services.History;
using Xunit;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

public class RewriteOptionsTests
{
    private static RewriteOptions WithOps(params ContentOp[] ops) => new() { ContentOps = ops };

    [Fact]
    public void ScopeOutsideAllFilesIsRefused()
    {
        var options = new RewriteOptions
        {
            ContentOps = [new LiteralReplace { Find = [1], Replace = [] }],
            Scope = (RewriteScope)7
        };
        var ex = Assert.Throws<NotSupportedException>(options.Validate);
        Assert.Contains("scope", ex.Message);
        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void CommitMessageRewritingIsRefused()
    {
        var options = new RewriteOptions
        {
            ContentOps = [new LiteralReplace { Find = [1], Replace = [] }],
            ReplaceInCommitMessages = true
        };
        var ex = Assert.Throws<NotSupportedException>(options.Validate);
        Assert.Contains("commit-message", ex.Message);
    }

    [Fact]
    public void EmptyOpListIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => WithOps().Validate());
        Assert.Contains("no content operations", ex.Message);
    }

    [Fact]
    public void EmptyLiteralFindIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => WithOps(new LiteralReplace { Find = [], Replace = [1] }).Validate());
        Assert.Contains("at least one byte", ex.Message);
    }

    [Fact]
    public void MalformedRegexPatternIsRefusedBeforeAnyWork()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => WithOps(new RegexReplace { Pattern = "(", Replacement = "x" }).Validate());
    }

    [Fact]
    public void ValidLiteralAndRegexOpsPass()
    {
        WithOps(
            new LiteralReplace { Find = Encoding.UTF8.GetBytes("secret"), Replace = [] },
            new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }).Validate();
    }
}

public class BlobTransformerTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static BlobTransformer Literal(string find, string replace) =>
        new([new LiteralReplace { Find = Bytes(find), Replace = Bytes(replace) }]);

    [Fact]
    public void OverlappingCandidatesReplaceLeftToRight()
    {
        var result = Literal("aa", "b").Transform(Bytes("aaa"));
        Assert.Equal(TransformClass.Changed, result.Class);
        Assert.Equal(Bytes("ba"), result.Bytes);

        Assert.Equal(Bytes("bb"), Literal("aa", "b").Transform(Bytes("aaaa")).Bytes);
        Assert.Equal(Bytes("aX"), Literal("ab", "X").Transform(Bytes("aab")).Bytes);
    }

    [Fact]
    public void ReplacementContainingTheNeedleDoesNotRescanItsOwnOutput()
    {
        var result = Literal("a", "aa").Transform(Bytes("aa"));
        Assert.Equal(Bytes("aaaa"), result.Bytes);
    }

    [Fact]
    public void EmptyReplacementDeletesEveryMatch()
    {
        var result = Literal("secret", "").Transform(Bytes("a secret and a secret\n"));
        Assert.Equal(Bytes("a  and a \n"), result.Bytes);
    }

    [Fact]
    public void ReplacementLongerThanFindGrowsThePayload()
    {
        var result = Literal("k", "[REDACTED]").Transform(Bytes("k=1\n"));
        Assert.Equal(Bytes("[REDACTED]=1\n"), result.Bytes);
    }

    [Fact]
    public void AbsentNeedleClassifiesUnchangedWithoutAllocating()
    {
        var payload = Bytes("nothing to see\n");
        var result = Literal("secret", "X").Transform(payload);
        Assert.Equal(TransformClass.Unchanged, result.Class);
        Assert.Null(result.Bytes);
        Assert.Null(BlobTransformer.ReplaceLiteral(payload, Bytes("secret"), Bytes("X")));
    }

    [Fact]
    public void SingleByteFindAtBothEndsReplaces()
    {
        Assert.Equal(Bytes("XbX"), Literal("a", "X").Transform(Bytes("aba")).Bytes);
    }

    [Fact]
    public void InvalidUtf8PayloadIsSkippedEvenForLiteralOps()
    {
        byte[] payload = [0x00, 0xFF, .. Bytes("secret"), 0x80, 0xFE];
        var result = Literal("secret", "X").Transform(payload);
        Assert.Equal(TransformClass.BinarySkipped, result.Class);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void IdenticalReplacementClassifiesUnchanged()
    {
        var result = Literal("abc", "abc").Transform(Bytes("xxabcxx"));
        Assert.Equal(TransformClass.Unchanged, result.Class);
    }

    [Fact]
    public void RegexReplacesOverUnicodeContentAndPreservesSurroundingBytes()
    {
        var transformer = new BlobTransformer(
            [new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }]);
        var result = transformer.Transform(Bytes("café token-42 日本 🚀\n"));
        Assert.Equal(TransformClass.Changed, result.Class);
        Assert.Equal(Bytes("café token-X 日本 🚀\n"), result.Bytes);
    }

    [Fact]
    public void RegexWithNoMatchClassifiesUnchanged()
    {
        var transformer = new BlobTransformer(
            [new RegexReplace { Pattern = "token-[0-9]+", Replacement = "token-X" }]);
        Assert.Equal(TransformClass.Unchanged, transformer.Transform(Bytes("no tokens here\n")).Class);
    }

    [Fact]
    public void RegexOverPayloadAboveTheLimitIsRefusedLoudly()
    {
        var transformer = new BlobTransformer(
            [new RegexReplace { Pattern = "x", Replacement = "y" }], regexPayloadLimit: 8);
        var ex = Assert.Throws<NotSupportedException>(() => transformer.Transform(Bytes("0123456789")));
        Assert.Contains("regex transform limit", ex.Message);
    }

    [Fact]
    public void LiteralOpsIgnoreThePayloadRegexLimit()
    {
        var transformer = new BlobTransformer(
            [new LiteralReplace { Find = Bytes("5"), Replace = Bytes("V") }], regexPayloadLimit: 8);
        Assert.Equal(Bytes("01234V6789"), transformer.Transform(Bytes("0123456789")).Bytes);
    }

    [Fact]
    public void LiteralOpProducingInvalidUtf8BeforeARegexOpFailsLoudly()
    {
        var transformer = new BlobTransformer(
        [
            new LiteralReplace { Find = Bytes("a"), Replace = [0xFF] },
            new RegexReplace { Pattern = "b", Replacement = "c" }
        ]);
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.Transform(Bytes("ab")));
        Assert.Contains("invalid UTF-8", ex.Message);
    }

    [Fact]
    public void OpsApplyInOrderOverEachOthersOutput()
    {
        var transformer = new BlobTransformer(
        [
            new LiteralReplace { Find = Bytes("secret"), Replace = Bytes("token-9") },
            new RegexReplace { Pattern = "token-[0-9]+", Replacement = "[GONE]" }
        ]);
        Assert.Equal(Bytes("a [GONE] b\n"), transformer.Transform(Bytes("a secret b\n")).Bytes);
    }
}
