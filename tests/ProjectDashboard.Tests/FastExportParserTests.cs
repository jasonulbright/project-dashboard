using System.IO;
using System.Text;
using ProjectDashboard.Services.History;
using Xunit;

namespace ProjectDashboard.Tests;

public class FastExportParserTests
{
    private static byte[] Bytes(string ascii) => Encoding.UTF8.GetBytes(ascii);

    private static List<FastExportRecord> ParseAll(byte[] stream)
    {
        var reader = new FastExportReader(new MemoryStream(stream));
        var records = new List<FastExportRecord>();
        while (reader.ReadRecord() is { } record)
            records.Add(record);
        return records;
    }

    private static byte[] ReEmit(IEnumerable<FastExportRecord> records, byte[] originalStream)
    {
        using var spool = new MemoryStream(originalStream);
        using var destination = new MemoryStream();
        var writer = new FastExportWriter(destination, spool);
        foreach (var record in records)
            writer.WriteRecordAsync(record).GetAwaiter().GetResult();
        return destination.ToArray();
    }

    private static void AssertRoundTrip(byte[] stream)
    {
        var records = ParseAll(stream);
        var reEmitted = ReEmit(records, stream);
        Assert.Equal(stream, reEmitted);
    }

    // ── data payload safety ────────────────────────────────────────────────

    [Fact]
    public void DataPayloadContainingRecordSyntaxIsNotParsedAsRecords()
    {
        var payload = Bytes("line1\ncommit refs/heads/x\nmark :9\ndata 5\nxxxxx");
        var stream = Concat(
            Bytes($"blob\nmark :1\ndata {payload.Length}\n"),
            payload,
            Bytes("\n"));

        var records = ParseAll(stream);

        var blob = Assert.IsType<BlobRecord>(Assert.Single(records));
        Assert.Equal(1, blob.Mark);
        Assert.Equal(payload.Length, blob.Data.SourceSlice.Length);
        Assert.True(blob.Data.TrailingLf);
        // The slice must cover exactly the payload bytes in the source stream.
        var sliced = stream.AsSpan((int)blob.Data.SourceSlice.Offset, (int)blob.Data.SourceSlice.Length).ToArray();
        Assert.Equal(payload, sliced);

        Assert.Equal(stream, ReEmit(records, stream));
    }

    [Fact]
    public void BinaryPayloadWithNulAndHighBytesRoundTrips()
    {
        var payload = new byte[] { 0x00, 0xFF, 0x0A, 0x00, 0xC3, 0xA4, 0x0A, 0x7F };
        var stream = Concat(
            Bytes($"blob\nmark :1\ndata {payload.Length}\n"),
            payload,
            Bytes("\n"));
        AssertRoundTrip(stream);
    }

    // ── quoted paths ───────────────────────────────────────────────────────

    [Fact]
    public void QuotedPathsDecodeAndRoundTrip()
    {
        var stream = Bytes(
            "commit refs/heads/main\n" +
            "mark :2\n" +
            "author A <a@example.com> 1700000000 +0000\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 4\nmsg\n" +
            "M 100644 :1 \"dir name/sp aced.txt\"\n" +
            "M 100644 :1 \"p\\303\\244th-\\346\\227\\245\\346\\234\\254.txt\"\n" +
            "M 100644 :1 \"q\\\"uo te.txt\"\n" +
            "M 100644 :1 \"tab\\there\"\n" +
            "M 100644 :1 plain.txt\n" +
            "\n");

        var records = ParseAll(stream);
        var commit = Assert.IsType<CommitRecord>(records[0]);
        var paths = commit.FileCommands.OfType<FileModify>().Select(m => m.Path).ToList();

        Assert.Equal("dir name/sp aced.txt", paths[0].ToString());
        Assert.Equal("päth-日本.txt", paths[1].ToString());
        Assert.Equal("q\"uo te.txt", paths[2].ToString());
        Assert.Equal("tab\there", paths[3].ToString());
        Assert.Equal("plain.txt", paths[4].ToString());

        // Re-quoting the decoded bytes must reproduce git's token exactly.
        foreach (var path in paths)
            Assert.Equal(path.RawToken, PathQuoting.Quote(path.PathBytes));

        Assert.Equal(stream, ReEmit(records, stream));
    }

    [Fact]
    public void QuoteUnquoteIsSymmetricForAllEscapeClasses()
    {
        byte[][] cases =
        [
            Bytes("plain.txt"),
            Bytes("with space"),
            Bytes("q\"uote"),
            Bytes("back\\slash"),
            [0xC3, 0xA4, (byte)'.', (byte)'t', (byte)'x', (byte)'t'],
            [(byte)'a', 0x07, 0x08, 0x0C, 0x0A, 0x0D, 0x09, 0x0B, (byte)'z'],
            [(byte)'h', 0x01, 0x1F, 0x7F, 0x80, 0xFF, (byte)'i'],
        ];
        foreach (var original in cases)
        {
            var quoted = PathQuoting.Quote(original);
            if (quoted[0] != (byte)'"')
            {
                Assert.Equal(original, quoted);
                continue;
            }
            Assert.True(PathQuoting.TryUnquote(quoted, out var decoded, out var consumed));
            Assert.Equal(quoted.Length, consumed);
            Assert.Equal(original, decoded);
        }
    }

    [Fact]
    public void CopyAndRenameParseQuotedSourceAndDestination()
    {
        var stream = Bytes(
            "commit refs/heads/main\n" +
            "mark :2\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 4\nmsg\n" +
            "C \"s p\" dest1\n" +
            "R dest1 \"d 2\"\n" +
            "D \"d 2\"\n" +
            "deleteall\n" +
            "\n");

        var records = ParseAll(stream);
        var commit = Assert.IsType<CommitRecord>(records[0]);
        var copy = Assert.IsType<FileCopy>(commit.FileCommands[0]);
        Assert.Equal("s p", copy.Source.ToString());
        Assert.Equal("dest1", copy.Destination.ToString());
        var rename = Assert.IsType<FileRename>(commit.FileCommands[1]);
        Assert.Equal("dest1", rename.Source.ToString());
        Assert.Equal("d 2", rename.Destination.ToString());
        Assert.IsType<FileDelete>(commit.FileCommands[2]);
        Assert.IsType<FileDeleteAll>(commit.FileCommands[3]);

        Assert.Equal(stream, ReEmit(records, stream));
    }

    // ── loud failure modes ─────────────────────────────────────────────────

    [Fact]
    public void UnknownCommandFailsWithByteOffsetAndLine()
    {
        var stream = Bytes("blob\nmark :1\ndata 2\nhi\nfrobnicate now\n");
        var reader = new FastExportReader(new MemoryStream(stream));
        Assert.IsType<BlobRecord>(reader.ReadRecord());

        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        // "frobnicate" starts right after "blob\nmark :1\ndata 2\nhi\n" = 23 bytes.
        Assert.Equal(23, ex.ByteOffset);
        Assert.Equal(5, ex.LineNumber);
        Assert.Contains("unknown record type", ex.Message);
        Assert.Contains("frobnicate", ex.Message);
        Assert.Contains("byte offset 23", ex.Message);
    }

    [Fact]
    public void TruncatedDataPayloadFailsWithOffset()
    {
        var stream = Bytes("blob\nmark :1\ndata 100\nshort");
        var reader = new FastExportReader(new MemoryStream(stream));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        Assert.Contains("truncated", ex.Message);
        Assert.Contains("blob payload", ex.Message);
    }

    [Fact]
    public void TruncatedMaterializedPayloadFailsWithOffset()
    {
        var stream = Bytes(
            "commit refs/heads/main\n" +
            "mark :1\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 50\nshort message");
        var reader = new FastExportReader(new MemoryStream(stream));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        Assert.Contains("truncated", ex.Message);
        Assert.Contains("commit message", ex.Message);
        Assert.Contains("expected 50 bytes, got 13", ex.Message);
    }

    [Fact]
    public void TruncatedHeaderFailsLoudly()
    {
        var stream = Bytes("commit refs/heads/main\nmark :1\n");
        var reader = new FastExportReader(new MemoryStream(stream));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        Assert.Contains("truncated", ex.Message);
        Assert.Contains("commit header", ex.Message);
    }

    [Fact]
    public void FinalLineWithoutLfFailsLoudly()
    {
        var stream = Bytes("blob\nmark :1\ndata 2\nhi\nblob");
        var reader = new FastExportReader(new MemoryStream(stream));
        Assert.IsType<BlobRecord>(reader.ReadRecord());
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        Assert.Contains("no LF", ex.Message);
    }

    [Fact]
    public void OverflowingDataLengthIsAFormatErrorWithOffset()
    {
        var stream = Bytes("blob\nmark :1\ndata 99999999999999999999\nx\n");
        var reader = new FastExportReader(new MemoryStream(stream));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        // The data line starts after "blob\nmark :1\n" = 13 bytes.
        Assert.Equal(13, ex.ByteOffset);
        Assert.Equal(3, ex.LineNumber);
        Assert.Contains("malformed data length", ex.Message);
    }

    [Fact]
    public void OverflowingMarkIsAFormatErrorWithOffset()
    {
        var stream = Bytes("blob\nmark :99999999999999999999\ndata 1\nx\n");
        var reader = new FastExportReader(new MemoryStream(stream));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        // The mark line starts after "blob\n" = 5 bytes.
        Assert.Equal(5, ex.ByteOffset);
        Assert.Equal(2, ex.LineNumber);
        Assert.Contains("malformed number", ex.Message);
    }

    [Fact]
    public void MaxLongDataLengthParsesAndFailsAsTruncationNotOverflow()
    {
        // long.MaxValue itself is a valid length; only the digit run past it is malformed.
        var stream = Bytes("blob\ndata 9223372036854775807\nhi");
        var reader = new FastExportReader(new MemoryStream(stream));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void EmptyDeletePathIsAFormatErrorWithOffset()
    {
        var stream = Bytes(
            "commit refs/heads/x\n" +
            "mark :1\n" +
            "committer A <a@x> 1 +0000\n" +
            "data 2\nm\n" +
            "D \n" +
            "\n");
        var reader = new FastExportReader(new MemoryStream(stream));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        // The D line starts after commit(20) + mark(8) + committer(26) + data line(7) + payload(2) = 63 bytes.
        Assert.Equal(63, ex.ByteOffset);
        Assert.Equal(5, ex.LineNumber);
        Assert.Contains("empty path token", ex.Message);
    }

    [Fact]
    public void DoneWithoutTrailingLfIsRefused()
    {
        var reader = new FastExportReader(new MemoryStream(Bytes("done")));
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        Assert.Equal(0, ex.ByteOffset);
        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("done record", ex.Message);
        Assert.Contains("no LF", ex.Message);
    }

    [Fact]
    public void CatBlobAndLsAreRefused()
    {
        var catBlob = Assert.Throws<FastExportFormatException>(() => ParseAll(Bytes("cat-blob :1\n")));
        Assert.Contains("query command", catBlob.Message);

        var ls = Assert.Throws<FastExportFormatException>(() => ParseAll(Bytes("ls :1 path.txt\n")));
        Assert.Contains("query command", ls.Message);
    }

    [Fact]
    public void NotemodifyInsideCommitIsRefused()
    {
        var stream = Bytes(
            "commit refs/notes/commits\n" +
            "mark :2\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 4\nmsg\n" +
            "N :1 :2\n" +
            "\n");
        var ex = Assert.Throws<FastExportFormatException>(() => ParseAll(stream));
        Assert.Contains("notemodify", ex.Message);
    }

    [Fact]
    public void DelimitedDataIsRefused()
    {
        var ex = Assert.Throws<FastExportFormatException>(() => ParseAll(Bytes("blob\ndata <<EOF\nx\nEOF\n")));
        Assert.Contains("delimited data", ex.Message);
    }

    [Fact]
    public void BytesAfterDoneAreRefused()
    {
        var reader = new FastExportReader(new MemoryStream(Bytes("done\nblob\n")));
        Assert.IsType<DoneRecord>(reader.ReadRecord());
        var ex = Assert.Throws<FastExportFormatException>(() => reader.ReadRecord());
        Assert.Contains("follow the done record", ex.Message);
    }

    [Fact]
    public void MarkAfterOtherHeadersIsRefused()
    {
        var stream = Bytes(
            "commit refs/heads/main\n" +
            "original-oid 0123456789012345678901234567890123456789\n" +
            "mark :2\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 4\nmsg\n\n");
        var ex = Assert.Throws<FastExportFormatException>(() => ParseAll(stream));
        Assert.Contains("mark must be the first header", ex.Message);
    }

    // ── full grammar round trip ────────────────────────────────────────────

    [Fact]
    public void HandcraftedFullGrammarStreamRoundTripsByteIdentically()
    {
        // Covers records the mandated fast-export flags never produce: feature, option,
        // progress, alias, inline filemodify, copy/rename, deleteall, done, and a
        // taggerless tag.
        var stream = Bytes(
            "feature done\n" +
            "option git quiet\n" +
            "progress starting\n" +
            "blob\nmark :1\ndata 3\nabc\n" +
            "reset refs/heads/x\n" +
            "commit refs/heads/x\n" +
            "mark :2\n" +
            "original-oid 0123456789012345678901234567890123456789\n" +
            "author A U <a@example.com> 1700000000 +0000\n" +
            "committer A U <a@example.com> 1700000001 +0000\n" +
            "data 4\nmsg\n" +
            "M 100644 :1 \"a b\"\n" +
            "M 100644 inline in.txt\ndata 2\nxy\n" +
            "M 100755 0123456789012345678901234567890123456789 byoid.txt\n" +
            "D old.txt\n" +
            "C \"s p\" dest1\n" +
            "R dest1 \"d 2\"\n" +
            "deleteall\n" +
            "\n" +
            "commit refs/heads/x\n" +
            "mark :3\n" +
            "committer A U <a@example.com> 1700000002 +0000\n" +
            "data 6\nmerge\n" +
            "from :2\n" +
            "merge :1\n" +
            "merge 0123456789012345678901234567890123456789\n" +
            "\n" +
            "alias\nmark :4\nto :3\n" +
            "\n" +
            "reset refs/tags/lt\nfrom :3\n" +
            "\n" +
            "tag ancient\nfrom :3\ndata 3\nold\n" +
            "\n" +
            "tag modern\nmark :5\nfrom :3\noriginal-oid 0123456789012345678901234567890123456789\n" +
            "tagger T <t@example.com> 1700000003 +0000\ndata 3\nnew\n" +
            "\n" +
            "progress done\n" +
            "done\n");

        var records = ParseAll(stream);
        Assert.Equal(stream, ReEmit(records, stream));

        var commit = Assert.IsType<CommitRecord>(records.First(r => r is CommitRecord));
        var inline = commit.FileCommands.OfType<FileModify>().Single(m => m.IsInline);
        Assert.NotNull(inline.Inline);
        Assert.Equal(2, inline.Inline!.SourceSlice.Length);

        var merge = Assert.IsType<CommitRecord>(records.Last(r => r is CommitRecord));
        Assert.Equal(3, merge.Parents.Count);
        Assert.False(merge.Parents[0].IsMerge);
        Assert.True(merge.Parents[1].IsMerge);
        Assert.Equal(":2", merge.Parents[0].DataRefText);
    }

    [Fact]
    public void EmptyMessageAndEmptyBlobRoundTrip()
    {
        var stream = Bytes(
            "blob\nmark :1\ndata 0\n\n" +
            "commit refs/heads/main\n" +
            "mark :2\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 0\n\n" +
            "M 100644 :1 empty.txt\n" +
            "\n");
        AssertRoundTrip(stream);
    }

    // ── stage-2 mutation hooks ─────────────────────────────────────────────

    [Fact]
    public void RepointRebuildsTheRawLineAndPreservesQuoting()
    {
        var stream = Bytes(
            "commit refs/heads/main\n" +
            "mark :2\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 4\nmsg\n" +
            "M 100644 :1 \"p\\303\\244th\"\n" +
            "\n");
        var records = ParseAll(stream);
        var commit = Assert.IsType<CommitRecord>(records[0]);
        var modify = commit.FileCommands.OfType<FileModify>().Single();

        modify.Repoint(77);

        Assert.Equal(77, modify.MarkRef);
        Assert.Equal("M 100644 :77 \"p\\303\\244th\"", Encoding.ASCII.GetString(modify.RawLine));
    }

    [Fact]
    public void IndexCapturesMarksParentsFileModifiesAndMaxMark()
    {
        var stream = Bytes(
            "blob\nmark :1\ndata 3\nabc\n" +
            "commit refs/heads/main\n" +
            "mark :2\n" +
            "original-oid aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" +
            "committer A <a@example.com> 1700000000 +0000\n" +
            "data 4\nmsg\n" +
            "M 100644 :1 f.txt\n" +
            "\n" +
            "commit refs/heads/main\n" +
            "mark :9\n" +
            "committer A <a@example.com> 1700000001 +0000\n" +
            "data 4\nnext\n" +
            "from :2\n" +
            "merge :1\n" +
            "\n");

        var index = new FastExportIndex();
        var reader = new FastExportReader(new MemoryStream(stream));
        while (reader.ReadRecord() is { } record)
            index.Add(record);

        Assert.Equal(9, index.MaxMark);
        Assert.Single(index.Blobs);
        Assert.Equal(3, index.Blobs[1].Payload.Length);
        Assert.Equal(2, index.Commits.Count);
        Assert.Equal("refs/heads/main", index.Commits[2].RefName);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", index.Commits[2].OriginalOid);
        Assert.Single(index.Commits[2].FileModifies);
        Assert.Equal([":2", ":1"], index.Commits[9].Parents);
        Assert.Equal([2L, 9L], index.CommitsInOrder.Select(c => c.Mark));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
