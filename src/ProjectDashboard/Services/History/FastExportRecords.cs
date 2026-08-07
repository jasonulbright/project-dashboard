using System.Text;

namespace ProjectDashboard.Services.History;

/// <summary>Byte range inside the spool file holding a data payload.</summary>
public readonly record struct SpoolSlice(long Offset, long Length);

/// <summary>
/// Payload of a `data N` block. Blob payloads stay on the spool (<see cref="SourceSlice"/>)
/// and are copied through byte-for-byte on emission; commit/tag messages are materialized
/// into <see cref="InlineBytes"/> so they are editable. Assigning <see cref="InlineBytes"/>
/// replaces the payload; emission then ignores the slice.
/// </summary>
public sealed class DataBlock
{
    public SpoolSlice SourceSlice { get; internal set; }

    public byte[]? InlineBytes { get; set; }

    /// <summary>
    /// One LF followed the payload in the source stream. fast-import consumes that LF as
    /// part of the data command, so it must round-trip or re-emission shifts by one byte.
    /// </summary>
    public bool TrailingLf { get; set; }

    public long Length => InlineBytes?.LongLength ?? SourceSlice.Length;
}

/// <summary>
/// A path token from a file command. <see cref="RawToken"/> preserves the exact source
/// bytes (including C-style quoting) so an unmodified path re-emits byte-identically;
/// <see cref="PathBytes"/> is the decoded path. Building from decoded bytes re-quotes
/// with fast-export's rules (quote when C-escaping is needed or the path contains SP).
/// </summary>
public sealed class GitPath
{
    public byte[] RawToken { get; private set; }
    public byte[] PathBytes { get; private set; }

    private GitPath(byte[] rawToken, byte[] pathBytes)
    {
        RawToken = rawToken;
        PathBytes = pathBytes;
    }

    internal static GitPath FromParsed(byte[] rawToken, byte[] pathBytes) => new(rawToken, pathBytes);

    public static GitPath FromDecoded(byte[] pathBytes) => new(PathQuoting.Quote(pathBytes), pathBytes);

    public override string ToString() => Encoding.UTF8.GetString(PathBytes);
}

public abstract class FastExportRecord
{
    /// <summary>Offset of the record's first byte in the source stream.</summary>
    public long ByteOffset { get; internal set; }
}

/// <summary>A standalone LF outside any data payload (record separator / commit terminator).</summary>
public sealed class BlankRecord : FastExportRecord
{
}

public sealed class BlobRecord : FastExportRecord
{
    public long? Mark { get; set; }
    public string? OriginalOid { get; set; }
    public DataBlock Data { get; set; } = new();
}

/// <summary>Parent linkage line of a commit: `from &lt;dataref&gt;` or `merge &lt;dataref&gt;`.</summary>
public sealed class ParentLink
{
    public required bool IsMerge { get; init; }

    /// <summary>Dataref bytes exactly as written (`:N`, hex oid, or ref name).</summary>
    public required byte[] DataRef { get; set; }

    public string DataRefText => Encoding.UTF8.GetString(DataRef);
}

public abstract class FileCommand
{
    /// <summary>Exact source line (no LF). Emission uses this verbatim; mutators rebuild it.</summary>
    public byte[] RawLine { get; internal set; } = [];
}

public sealed class FileModify : FileCommand
{
    public string Mode { get; private set; } = "";

    /// <summary>`:N`, hex oid, or `inline`, exactly as written.</summary>
    public byte[] DataRef { get; private set; } = [];

    public GitPath Path { get; private set; } = null!;

    /// <summary>Payload following the M line when the dataref is `inline`. Kept as a spool slice, never materialized.</summary>
    public DataBlock? Inline { get; internal set; }

    public bool IsInline => Utf8Ascii.IsExactly(DataRef, "inline");

    public long? MarkRef =>
        DataRef.Length > 1 && DataRef[0] == (byte)':' && Utf8Ascii.TryParseLong(DataRef.AsSpan(1), out var m) ? m : null;

    internal static FileModify Parsed(byte[] rawLine, string mode, byte[] dataRef, GitPath path) =>
        new() { RawLine = rawLine, Mode = mode, DataRef = dataRef, Path = path };

    public void Repoint(long mark) => Repoint(Encoding.ASCII.GetBytes(":" + mark));

    public void Repoint(byte[] dataRef)
    {
        DataRef = dataRef;
        Rebuild();
    }

    public void SetMode(string mode)
    {
        Mode = mode;
        Rebuild();
    }

    public void SetPath(GitPath path)
    {
        Path = path;
        Rebuild();
    }

    private void Rebuild()
    {
        var mode = Encoding.ASCII.GetBytes(Mode);
        var line = new byte[2 + mode.Length + 1 + DataRef.Length + 1 + Path.RawToken.Length];
        var i = 0;
        line[i++] = (byte)'M';
        line[i++] = (byte)' ';
        mode.CopyTo(line, i); i += mode.Length;
        line[i++] = (byte)' ';
        DataRef.CopyTo(line, i); i += DataRef.Length;
        line[i++] = (byte)' ';
        Path.RawToken.CopyTo(line, i);
        RawLine = line;
    }
}

public sealed class FileDelete : FileCommand
{
    public GitPath Path { get; internal set; } = null!;
}

public sealed class FileCopy : FileCommand
{
    public GitPath Source { get; internal set; } = null!;
    public GitPath Destination { get; internal set; } = null!;
}

public sealed class FileRename : FileCommand
{
    public GitPath Source { get; internal set; } = null!;
    public GitPath Destination { get; internal set; } = null!;
}

public sealed class FileDeleteAll : FileCommand
{
}

public sealed class CommitRecord : FastExportRecord
{
    /// <summary>Bytes after `commit ` — the full ref name, raw (ref names may hold non-ASCII bytes).</summary>
    public byte[] RefNameBytes { get; set; } = [];

    public string RefName => Encoding.UTF8.GetString(RefNameBytes);

    /// <summary>Mark number. Emitted first after the commit line, matching fast-export's fixed order.</summary>
    public long? Mark { get; set; }

    public string? OriginalOid { get; set; }

    /// <summary>
    /// Raw header lines between the mark and the message data block, in stream order
    /// (original-oid, author, committer). Every line is grammar-validated at parse time;
    /// emission replays them verbatim.
    /// </summary>
    public List<byte[]> HeaderLines { get; } = [];

    /// <summary>Message payload. Always materialized (<see cref="DataBlock.InlineBytes"/> non-null).</summary>
    public DataBlock Message { get; set; } = new();

    /// <summary>`from` then `merge` lines, in stream order.</summary>
    public List<ParentLink> Parents { get; } = [];

    public List<FileCommand> FileCommands { get; } = [];
}

public sealed class TagRecord : FastExportRecord
{
    /// <summary>Bytes after `tag ` — the tag name without the refs/tags/ prefix.</summary>
    public byte[] TagNameBytes { get; set; } = [];

    public string TagName => Encoding.UTF8.GetString(TagNameBytes);

    public long? Mark { get; set; }

    public string? OriginalOid { get; set; }

    /// <summary>Dataref bytes of the `from` line, or null when absent.</summary>
    public byte[]? FromRef { get; set; }

    /// <summary>
    /// Raw header lines between the mark and the message data block, in stream order
    /// (from, original-oid, tagger — fast-export's tag order differs from its commit order).
    /// </summary>
    public List<byte[]> HeaderLines { get; } = [];

    public DataBlock Message { get; set; } = new();
}

public sealed class ResetRecord : FastExportRecord
{
    public byte[] RefNameBytes { get; set; } = [];

    public string RefName => Encoding.UTF8.GetString(RefNameBytes);

    /// <summary>Dataref of the optional `from` line. A bare reset (no from) precedes root commits.</summary>
    public byte[]? FromRef { get; set; }
}

public sealed class FeatureRecord : FastExportRecord
{
    public byte[] RawLine { get; set; } = [];
}

public sealed class OptionRecord : FastExportRecord
{
    public byte[] RawLine { get; set; } = [];
}

public sealed class ProgressRecord : FastExportRecord
{
    public byte[] RawLine { get; set; } = [];
}

public sealed class DoneRecord : FastExportRecord
{
}

public sealed class AliasRecord : FastExportRecord
{
    public long Mark { get; set; }

    /// <summary>Dataref bytes of the `to` line.</summary>
    public byte[] ToRef { get; set; } = [];
}

/// <summary>ASCII helpers over raw stream bytes.</summary>
internal static class Utf8Ascii
{
    public static bool TryParseLong(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        if (digits.Length == 0) return false;
        foreach (var b in digits)
        {
            if (b is < (byte)'0' or > (byte)'9') return false;
            value = checked(value * 10 + (b - (byte)'0'));
        }
        return true;
    }

    public static bool HasPrefix(ReadOnlySpan<byte> line, string asciiPrefix)
    {
        if (line.Length < asciiPrefix.Length) return false;
        for (var i = 0; i < asciiPrefix.Length; i++)
            if (line[i] != (byte)asciiPrefix[i]) return false;
        return true;
    }

    public static bool IsExactly(ReadOnlySpan<byte> line, string ascii) =>
        line.Length == ascii.Length && HasPrefix(line, ascii);
}
