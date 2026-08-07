using System.Buffers;
using System.IO;
using System.Text;

namespace ProjectDashboard.Services.History;

/// <summary>
/// Structural error in a fast-export stream. Parsing never guesses: any byte sequence
/// outside the supported grammar stops the pipeline here, carrying the exact position.
/// </summary>
public sealed class FastExportFormatException : Exception
{
    /// <summary>Offset of the offending byte in the stream.</summary>
    public long ByteOffset { get; }

    /// <summary>1-based line count of LF-terminated lines read outside data payloads. LFs inside payloads are not counted.</summary>
    public long LineNumber { get; }

    public FastExportFormatException(string reason, long byteOffset, long lineNumber)
        : base($"{reason} (byte offset {byteOffset}, line {lineNumber})")
    {
        ByteOffset = byteOffset;
        LineNumber = lineNumber;
    }
}

/// <summary>C-style path quoting matching git's quote.c plus fast-export's extra rule of quoting any path containing SP.</summary>
public static class PathQuoting
{
    public static bool NeedsQuoting(ReadOnlySpan<byte> path)
    {
        foreach (var b in path)
            if (b is (byte)'"' or (byte)'\\' or (byte)' ' or < 0x20 or >= 0x7f)
                return true;
        return false;
    }

    public static byte[] Quote(ReadOnlySpan<byte> path)
    {
        if (!NeedsQuoting(path)) return path.ToArray();

        var result = new List<byte>(path.Length + 16) { (byte)'"' };
        foreach (var b in path)
        {
            switch (b)
            {
                case (byte)'\a': result.Add((byte)'\\'); result.Add((byte)'a'); break;
                case (byte)'\b': result.Add((byte)'\\'); result.Add((byte)'b'); break;
                case (byte)'\f': result.Add((byte)'\\'); result.Add((byte)'f'); break;
                case (byte)'\n': result.Add((byte)'\\'); result.Add((byte)'n'); break;
                case (byte)'\r': result.Add((byte)'\\'); result.Add((byte)'r'); break;
                case (byte)'\t': result.Add((byte)'\\'); result.Add((byte)'t'); break;
                case (byte)'\v': result.Add((byte)'\\'); result.Add((byte)'v'); break;
                case (byte)'"': result.Add((byte)'\\'); result.Add((byte)'"'); break;
                case (byte)'\\': result.Add((byte)'\\'); result.Add((byte)'\\'); break;
                default:
                    if (b is < 0x20 or >= 0x7f)
                    {
                        result.Add((byte)'\\');
                        result.Add((byte)('0' + ((b >> 6) & 7)));
                        result.Add((byte)('0' + ((b >> 3) & 7)));
                        result.Add((byte)('0' + (b & 7)));
                    }
                    else
                    {
                        result.Add(b);
                    }
                    break;
            }
        }
        result.Add((byte)'"');
        return [.. result];
    }

    /// <summary>
    /// Decodes a double-quoted token starting at input[0]. Returns false on malformed
    /// escapes or a missing closing quote. <paramref name="consumed"/> is the index just
    /// past the closing quote; callers decide whether trailing bytes are legal.
    /// </summary>
    public static bool TryUnquote(ReadOnlySpan<byte> input, out byte[] path, out int consumed)
    {
        path = [];
        consumed = 0;
        if (input.Length < 2 || input[0] != (byte)'"') return false;

        var result = new List<byte>(input.Length);
        var i = 1;
        while (i < input.Length)
        {
            var b = input[i];
            if (b == (byte)'"')
            {
                path = [.. result];
                consumed = i + 1;
                return true;
            }
            if (b == (byte)'\\')
            {
                i++;
                if (i >= input.Length) return false;
                var e = input[i];
                switch (e)
                {
                    case (byte)'a': result.Add(7); i++; break;
                    case (byte)'b': result.Add(8); i++; break;
                    case (byte)'f': result.Add(12); i++; break;
                    case (byte)'n': result.Add(10); i++; break;
                    case (byte)'r': result.Add(13); i++; break;
                    case (byte)'t': result.Add(9); i++; break;
                    case (byte)'v': result.Add(11); i++; break;
                    case (byte)'"': result.Add((byte)'"'); i++; break;
                    case (byte)'\\': result.Add((byte)'\\'); i++; break;
                    default:
                        if (e is >= (byte)'0' and <= (byte)'7')
                        {
                            var value = 0;
                            var digits = 0;
                            while (digits < 3 && i < input.Length && input[i] is >= (byte)'0' and <= (byte)'7')
                            {
                                value = value * 8 + (input[i] - (byte)'0');
                                i++;
                                digits++;
                            }
                            if (value > 255) return false;
                            result.Add((byte)value);
                        }
                        else
                        {
                            return false;
                        }
                        break;
                }
            }
            else
            {
                result.Add(b);
                i++;
            }
        }
        return false;
    }
}

/// <summary>
/// Buffered byte cursor over the spool. Lines are LF-delimited raw bytes; payload reads
/// and skips are exact-count and never split on newlines. Tracks the logical byte offset
/// and an out-of-payload line count for error reporting.
/// </summary>
internal sealed class StreamCursor
{
    // Paths are capped at PATH_MAX-scale; a longer "line" means the stream is binary garbage.
    private const int MaxLineLength = 1024 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _eof;
    private byte[]? _peekedLine;
    private bool _peekedHasLf;

    public StreamCursor(Stream stream) => _stream = stream;

    /// <summary>Offset of the next unread byte.</summary>
    public long Position { get; private set; }

    public long LineNumber { get; private set; } = 1;

    private int Buffered => _end - _start;

    private bool FillMore()
    {
        if (_eof) return false;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }
        else if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, Buffered);
                _end -= _start;
                _start = 0;
            }
            else
            {
                if (_buffer.Length >= MaxLineLength)
                    throw new FastExportFormatException(
                        "line exceeds 1 MiB without LF — not a fast-export stream or the stream is corrupted",
                        Position, LineNumber);
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }
        }
        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            _eof = true;
            return false;
        }
        _end += read;
        return true;
    }

    /// <summary>Peeks the next line without consuming. False at clean EOF. hasLf=false means the final line is unterminated.</summary>
    public bool TryPeekLine(out byte[] line, out bool hasLf)
    {
        if (_peekedLine is not null)
        {
            line = _peekedLine;
            hasLf = _peekedHasLf;
            return true;
        }
        var scanned = 0;
        while (true)
        {
            var window = _buffer.AsSpan(_start, Buffered);
            var lf = window[scanned..].IndexOf((byte)'\n');
            if (lf >= 0)
            {
                _peekedLine = window[..(scanned + lf)].ToArray();
                _peekedHasLf = true;
                line = _peekedLine;
                hasLf = true;
                return true;
            }
            scanned = window.Length;
            if (!FillMore())
            {
                if (scanned == 0)
                {
                    line = [];
                    hasLf = false;
                    return false;
                }
                _peekedLine = _buffer.AsSpan(_start, scanned).ToArray();
                _peekedHasLf = false;
                line = _peekedLine;
                hasLf = false;
                return true;
            }
        }
    }

    public void ConsumeLine()
    {
        if (_peekedLine is null) throw new InvalidOperationException("no peeked line to consume");
        var total = _peekedLine.Length + (_peekedHasLf ? 1 : 0);
        _start += total;
        Position += total;
        LineNumber++;
        _peekedLine = null;
    }

    /// <summary>Consumes one LF if it is the next byte. Implements the optional LF after a data payload.</summary>
    public bool TryConsumeLf()
    {
        if (_peekedLine is not null) throw new InvalidOperationException("peeked line pending");
        if (Buffered == 0 && !FillMore()) return false;
        if (_buffer[_start] != (byte)'\n') return false;
        _start++;
        Position++;
        LineNumber++;
        return true;
    }

    public byte[] ReadExactly(long count, string context)
    {
        if (count > int.MaxValue)
            throw new FastExportFormatException($"{context} of {count} bytes cannot be materialized", Position, LineNumber);
        var result = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            if (Buffered == 0 && !FillMore())
                throw new FastExportFormatException(
                    $"stream truncated inside {context}: expected {count} bytes, got {filled}",
                    Position, LineNumber);
            var take = (int)Math.Min(Buffered, count - filled);
            Buffer.BlockCopy(_buffer, _start, result, filled, take);
            _start += take;
            filled += take;
            Position += take;
        }
        return result;
    }

    public void SkipExactly(long count, string context)
    {
        var fromBuffer = Math.Min(Buffered, count);
        _start += (int)fromBuffer;
        Position += fromBuffer;
        var remaining = count - fromBuffer;
        if (remaining == 0) return;

        if (_stream.CanSeek)
        {
            // The window is drained, so the physical position equals the logical one and a
            // relative seek is safe. Seeking past EOF does not fault — verify by length.
            var target = Position + remaining;
            if (target > _stream.Length)
                throw new FastExportFormatException(
                    $"stream truncated inside {context}: expected {count} bytes, got {count - remaining + Math.Max(0, _stream.Length - Position)}",
                    Position, LineNumber);
            _stream.Seek(remaining, SeekOrigin.Current);
            Position = target;
            return;
        }

        var scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                var read = _stream.Read(scratch, 0, (int)Math.Min(scratch.Length, remaining));
                if (read == 0)
                    throw new FastExportFormatException(
                        $"stream truncated inside {context}: {remaining} bytes missing",
                        Position, LineNumber);
                remaining -= read;
                Position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}

/// <summary>
/// Streaming parser for `git fast-export` output. Reads records without ever materializing
/// blob payloads: blobs become spool slices, so the input stream must remain available (and
/// seekable) for the emission pass. Unknown or unsupported constructs throw
/// <see cref="FastExportFormatException"/> — nothing is skipped silently.
/// </summary>
public sealed class FastExportReader
{
    private readonly StreamCursor _cursor;
    private bool _doneSeen;

    public FastExportReader(Stream source)
    {
        _cursor = new StreamCursor(source);
    }

    public long Position => _cursor.Position;

    /// <summary>Next record, or null at end of stream. Bytes after a `done` record are a loud error, never dropped.</summary>
    public FastExportRecord? ReadRecord()
    {
        if (!_cursor.TryPeekLine(out var line, out var hasLf))
            return null;

        if (_doneSeen)
            throw new FastExportFormatException("bytes follow the done record", _cursor.Position, _cursor.LineNumber);

        var offset = _cursor.Position;

        if (line.Length == 0)
        {
            RequireLf(hasLf, "blank separator");
            _cursor.ConsumeLine();
            return new BlankRecord { ByteOffset = offset };
        }

        if (Utf8Ascii.IsExactly(line, "blob")) return ParseBlob(offset, hasLf);
        if (Utf8Ascii.HasPrefix(line, "commit ")) return ParseCommit(offset, hasLf);
        if (Utf8Ascii.HasPrefix(line, "tag ")) return ParseTag(offset, hasLf);
        if (Utf8Ascii.HasPrefix(line, "reset ")) return ParseReset(offset, hasLf);
        if (Utf8Ascii.IsExactly(line, "alias")) return ParseAlias(offset, hasLf);
        if (Utf8Ascii.HasPrefix(line, "feature ") || Utf8Ascii.IsExactly(line, "feature"))
            return ConsumeRawLine(new FeatureRecord { ByteOffset = offset, RawLine = line }, hasLf);
        if (Utf8Ascii.HasPrefix(line, "option "))
            return ConsumeRawLine(new OptionRecord { ByteOffset = offset, RawLine = line }, hasLf);
        if (Utf8Ascii.HasPrefix(line, "progress ") || Utf8Ascii.IsExactly(line, "progress"))
            return ConsumeRawLine(new ProgressRecord { ByteOffset = offset, RawLine = line }, hasLf);
        if (Utf8Ascii.IsExactly(line, "done"))
        {
            _cursor.ConsumeLine();
            _doneSeen = true;
            return new DoneRecord { ByteOffset = offset };
        }
        if (Utf8Ascii.HasPrefix(line, "cat-blob ") || Utf8Ascii.HasPrefix(line, "ls "))
            throw new FastExportFormatException(
                $"'{FirstWord(line)}' is a fast-import query command and cannot appear in an export stream",
                offset, _cursor.LineNumber);

        throw new FastExportFormatException(
            $"unknown record type '{Excerpt(line)}'", offset, _cursor.LineNumber);
    }

    private FastExportRecord ConsumeRawLine(FastExportRecord record, bool hasLf)
    {
        RequireLf(hasLf, "record");
        _cursor.ConsumeLine();
        return record;
    }

    private BlobRecord ParseBlob(long offset, bool hasLf)
    {
        RequireLf(hasLf, "blob record");
        _cursor.ConsumeLine();
        var blob = new BlobRecord { ByteOffset = offset };

        var line = PeekRequired("blob record");
        if (Utf8Ascii.HasPrefix(line, "mark :"))
        {
            blob.Mark = ParseMark(line);
            _cursor.ConsumeLine();
            line = PeekRequired("blob record");
        }
        if (Utf8Ascii.HasPrefix(line, "original-oid "))
        {
            blob.OriginalOid = Encoding.ASCII.GetString(line, 13, line.Length - 13);
            _cursor.ConsumeLine();
            line = PeekRequired("blob record");
        }
        if (!Utf8Ascii.HasPrefix(line, "data "))
            throw Unexpected(line, "blob record (expected data)");
        blob.Data = ReadDataBlock(materialize: false, "blob payload");
        return blob;
    }

    private CommitRecord ParseCommit(long offset, bool hasLf)
    {
        RequireLf(hasLf, "commit record");
        var commitLine = PeekRequired("commit record");
        var commit = new CommitRecord { ByteOffset = offset, RefNameBytes = commitLine[7..] };
        _cursor.ConsumeLine();

        // Header block: mark first (fast-export's fixed order — emission relies on it),
        // then original-oid / author / committer verbatim. Anything else is a loud stop:
        // encoding and gpgsig only appear when export ran without the mandated flags.
        var line = PeekRequired("commit header");
        if (Utf8Ascii.HasPrefix(line, "mark :"))
        {
            commit.Mark = ParseMark(line);
            _cursor.ConsumeLine();
            line = PeekRequired("commit header");
        }
        var sawCommitter = false;
        while (true)
        {
            if (Utf8Ascii.HasPrefix(line, "original-oid "))
            {
                commit.OriginalOid = Encoding.ASCII.GetString(line, 13, line.Length - 13);
                commit.HeaderLines.Add(line);
            }
            else if (Utf8Ascii.HasPrefix(line, "author "))
            {
                commit.HeaderLines.Add(line);
            }
            else if (Utf8Ascii.HasPrefix(line, "committer "))
            {
                sawCommitter = true;
                commit.HeaderLines.Add(line);
            }
            else if (Utf8Ascii.HasPrefix(line, "data "))
            {
                break;
            }
            else if (Utf8Ascii.HasPrefix(line, "mark :"))
            {
                throw Unexpected(line, "commit header (mark must be the first header)");
            }
            else if (Utf8Ascii.HasPrefix(line, "encoding ") || Utf8Ascii.HasPrefix(line, "gpgsig "))
            {
                throw Unexpected(line, "commit header (unsupported header — the export must run with --reencode=yes and stripped signatures)");
            }
            else
            {
                throw Unexpected(line, "commit header");
            }
            _cursor.ConsumeLine();
            line = PeekRequired("commit header");
        }
        if (!sawCommitter)
            throw new FastExportFormatException("commit record has no committer line", _cursor.Position, _cursor.LineNumber);

        commit.Message = ReadDataBlock(materialize: true, "commit message");

        // Parent block: one optional `from`, then any number of `merge` lines.
        var sawMerge = false;
        while (_cursor.TryPeekLine(out line, out hasLf))
        {
            if (Utf8Ascii.HasPrefix(line, "from "))
            {
                if (commit.Parents.Count > 0 || sawMerge)
                    throw Unexpected(line, "commit parents (from must be single and precede merge)");
                RequireLf(hasLf, "commit parents");
                commit.Parents.Add(new ParentLink { IsMerge = false, DataRef = line[5..] });
                _cursor.ConsumeLine();
            }
            else if (Utf8Ascii.HasPrefix(line, "merge "))
            {
                RequireLf(hasLf, "commit parents");
                sawMerge = true;
                commit.Parents.Add(new ParentLink { IsMerge = true, DataRef = line[6..] });
                _cursor.ConsumeLine();
            }
            else
            {
                break;
            }
        }

        // File command block. A blank line or any non-file line ends the commit; the
        // blank re-enters the top-level loop as a BlankRecord, preserving the byte stream.
        while (_cursor.TryPeekLine(out line, out hasLf))
        {
            if (line.Length == 0) break;
            if (Utf8Ascii.HasPrefix(line, "M "))
            {
                RequireLf(hasLf, "filemodify");
                var fm = ParseFileModify(line);
                _cursor.ConsumeLine();
                if (fm.IsInline)
                    fm.Inline = ReadDataBlock(materialize: false, "inline file payload");
                commit.FileCommands.Add(fm);
            }
            else if (Utf8Ascii.HasPrefix(line, "D "))
            {
                RequireLf(hasLf, "filedelete");
                commit.FileCommands.Add(new FileDelete { RawLine = line, Path = ParseSinglePath(line, 2) });
                _cursor.ConsumeLine();
            }
            else if (Utf8Ascii.HasPrefix(line, "C "))
            {
                RequireLf(hasLf, "filecopy");
                var (src, dst) = ParsePathPair(line, 2);
                commit.FileCommands.Add(new FileCopy { RawLine = line, Source = src, Destination = dst });
                _cursor.ConsumeLine();
            }
            else if (Utf8Ascii.HasPrefix(line, "R "))
            {
                RequireLf(hasLf, "filerename");
                var (src, dst) = ParsePathPair(line, 2);
                commit.FileCommands.Add(new FileRename { RawLine = line, Source = src, Destination = dst });
                _cursor.ConsumeLine();
            }
            else if (Utf8Ascii.IsExactly(line, "deleteall"))
            {
                RequireLf(hasLf, "deleteall");
                commit.FileCommands.Add(new FileDeleteAll { RawLine = line });
                _cursor.ConsumeLine();
            }
            else if (Utf8Ascii.HasPrefix(line, "N "))
            {
                throw Unexpected(line, "commit body (notemodify is not supported by this stage)");
            }
            else if (Utf8Ascii.HasPrefix(line, "cat-blob ") || Utf8Ascii.HasPrefix(line, "ls "))
            {
                throw Unexpected(line, "commit body (fast-import query commands cannot appear in an export stream)");
            }
            else
            {
                break;
            }
        }
        return commit;
    }

    private TagRecord ParseTag(long offset, bool hasLf)
    {
        RequireLf(hasLf, "tag record");
        var tagLine = PeekRequired("tag record");
        var tag = new TagRecord { ByteOffset = offset, TagNameBytes = tagLine[4..] };
        _cursor.ConsumeLine();

        var line = PeekRequired("tag header");
        if (Utf8Ascii.HasPrefix(line, "mark :"))
        {
            tag.Mark = ParseMark(line);
            _cursor.ConsumeLine();
            line = PeekRequired("tag header");
        }
        while (true)
        {
            if (Utf8Ascii.HasPrefix(line, "from "))
            {
                tag.FromRef = line[5..];
                tag.HeaderLines.Add(line);
            }
            else if (Utf8Ascii.HasPrefix(line, "original-oid "))
            {
                tag.OriginalOid = Encoding.ASCII.GetString(line, 13, line.Length - 13);
                tag.HeaderLines.Add(line);
            }
            else if (Utf8Ascii.HasPrefix(line, "tagger "))
            {
                tag.HeaderLines.Add(line);
            }
            else if (Utf8Ascii.HasPrefix(line, "data "))
            {
                break;
            }
            else
            {
                throw Unexpected(line, "tag header");
            }
            _cursor.ConsumeLine();
            line = PeekRequired("tag header");
        }
        tag.Message = ReadDataBlock(materialize: true, "tag message");
        return tag;
    }

    private ResetRecord ParseReset(long offset, bool hasLf)
    {
        RequireLf(hasLf, "reset record");
        var resetLine = PeekRequired("reset record");
        var reset = new ResetRecord { ByteOffset = offset, RefNameBytes = resetLine[6..] };
        _cursor.ConsumeLine();

        if (_cursor.TryPeekLine(out var line, out hasLf) && Utf8Ascii.HasPrefix(line, "from "))
        {
            RequireLf(hasLf, "reset record");
            reset.FromRef = line[5..];
            _cursor.ConsumeLine();
        }
        return reset;
    }

    private AliasRecord ParseAlias(long offset, bool hasLf)
    {
        RequireLf(hasLf, "alias record");
        _cursor.ConsumeLine();
        var markLine = PeekRequired("alias record");
        if (!Utf8Ascii.HasPrefix(markLine, "mark :"))
            throw Unexpected(markLine, "alias record (expected mark)");
        var mark = ParseMark(markLine);
        _cursor.ConsumeLine();
        var toLine = PeekRequired("alias record");
        if (!Utf8Ascii.HasPrefix(toLine, "to "))
            throw Unexpected(toLine, "alias record (expected to)");
        _cursor.ConsumeLine();
        return new AliasRecord { ByteOffset = offset, Mark = mark, ToRef = toLine[3..] };
    }

    private DataBlock ReadDataBlock(bool materialize, string context)
    {
        var line = PeekRequired(context);
        if (!Utf8Ascii.HasPrefix(line, "data "))
            throw Unexpected(line, $"{context} (expected data)");
        if (line.Length > 5 && line[5] == (byte)'<')
            throw new FastExportFormatException(
                "delimited data (data <<) is not part of fast-export output and is not supported",
                _cursor.Position, _cursor.LineNumber);
        if (!Utf8Ascii.TryParseLong(line.AsSpan(5), out var length))
            throw Unexpected(line, $"{context} (malformed data length)");
        _cursor.ConsumeLine();

        var block = new DataBlock { SourceSlice = new SpoolSlice(_cursor.Position, length) };
        if (materialize)
            block.InlineBytes = _cursor.ReadExactly(length, context);
        else
            _cursor.SkipExactly(length, context);
        block.TrailingLf = _cursor.TryConsumeLf();
        return block;
    }

    private FileModify ParseFileModify(byte[] line)
    {
        // M <mode> <dataref> <path>; the path is the remainder of the line and is the only
        // token that may be quoted.
        var span = line.AsSpan(2);
        var sp1 = span.IndexOf((byte)' ');
        if (sp1 <= 0) throw Unexpected(line, "filemodify");
        var mode = span[..sp1];
        foreach (var b in mode)
            if (b is < (byte)'0' or > (byte)'9')
                throw Unexpected(line, "filemodify (malformed mode)");
        var rest = span[(sp1 + 1)..];
        var sp2 = rest.IndexOf((byte)' ');
        if (sp2 <= 0) throw Unexpected(line, "filemodify");
        var dataRef = rest[..sp2];
        var pathToken = rest[(sp2 + 1)..];
        if (pathToken.Length == 0) throw Unexpected(line, "filemodify (missing path)");
        return FileModify.Parsed(line, Encoding.ASCII.GetString(mode), dataRef.ToArray(), ParsePathToken(pathToken, line, wholeRemainder: true));
    }

    private GitPath ParseSinglePath(byte[] line, int start) =>
        ParsePathToken(line.AsSpan(start), line, wholeRemainder: true);

    private (GitPath Source, GitPath Destination) ParsePathPair(byte[] line, int start)
    {
        // <src> SP <dst>: an unquoted source cannot contain SP, so it ends at the first SP;
        // the destination is the whole remainder.
        var span = line.AsSpan(start);
        GitPath source;
        int afterSource;
        if (span.Length > 0 && span[0] == (byte)'"')
        {
            if (!PathQuoting.TryUnquote(span, out var decoded, out var consumed))
                throw Unexpected(line, "path pair (malformed quoted source path)");
            source = GitPath.FromParsed(span[..consumed].ToArray(), decoded);
            afterSource = consumed;
        }
        else
        {
            var sp = span.IndexOf((byte)' ');
            if (sp <= 0) throw Unexpected(line, "path pair");
            source = GitPath.FromParsed(span[..sp].ToArray(), span[..sp].ToArray());
            afterSource = sp;
        }
        if (afterSource >= span.Length || span[afterSource] != (byte)' ')
            throw Unexpected(line, "path pair (missing destination)");
        var dst = span[(afterSource + 1)..];
        if (dst.Length == 0) throw Unexpected(line, "path pair (missing destination)");
        return (source, ParsePathToken(dst, line, wholeRemainder: true));
    }

    private GitPath ParsePathToken(ReadOnlySpan<byte> token, byte[] line, bool wholeRemainder)
    {
        if (token[0] == (byte)'"')
        {
            if (!PathQuoting.TryUnquote(token, out var decoded, out var consumed))
                throw Unexpected(line, "path (malformed quoting)");
            if (wholeRemainder && consumed != token.Length)
                throw Unexpected(line, "path (bytes after closing quote)");
            return GitPath.FromParsed(token[..consumed].ToArray(), decoded);
        }
        return GitPath.FromParsed(token.ToArray(), token.ToArray());
    }

    private long ParseMark(byte[] line)
    {
        if (!Utf8Ascii.TryParseLong(line.AsSpan(6), out var mark))
            throw Unexpected(line, "mark (malformed number)");
        return mark;
    }

    private byte[] PeekRequired(string context)
    {
        if (!_cursor.TryPeekLine(out var line, out var hasLf))
            throw new FastExportFormatException($"stream truncated inside {context}", _cursor.Position, _cursor.LineNumber);
        RequireLf(hasLf, context);
        return line;
    }

    private void RequireLf(bool hasLf, string context)
    {
        if (!hasLf)
            throw new FastExportFormatException($"stream truncated inside {context}: final line has no LF", _cursor.Position, _cursor.LineNumber);
    }

    private FastExportFormatException Unexpected(byte[] line, string context) =>
        new($"unexpected line '{Excerpt(line)}' in {context}", _cursor.Position, _cursor.LineNumber);

    private static string FirstWord(byte[] line)
    {
        var sp = Array.IndexOf(line, (byte)' ');
        return Encoding.UTF8.GetString(line, 0, sp < 0 ? line.Length : sp);
    }

    private static string Excerpt(byte[] line)
    {
        var text = Encoding.UTF8.GetString(line, 0, Math.Min(line.Length, 120));
        return line.Length > 120 ? text + "…" : text;
    }
}

/// <summary>
/// Emits records back into fast-export stream form. With unmodified records the output is
/// byte-identical to the parsed stream. Payloads referenced by spool slices are copied
/// straight from the spool stream, never materialized.
/// </summary>
public sealed class FastExportWriter
{
    private readonly Stream _destination;
    private readonly Stream? _spool;
    private readonly MemoryStream _line = new();

    /// <param name="spool">Source of slice payloads. Required when any record carries a non-materialized data block. Must not be read elsewhere while a write is in flight.</param>
    public FastExportWriter(Stream destination, Stream? spool = null)
    {
        _destination = destination;
        _spool = spool;
    }

    public long RecordsWritten { get; private set; }
    public long BytesWritten { get; private set; }

    public async Task WriteRecordAsync(FastExportRecord record, CancellationToken ct = default)
    {
        switch (record)
        {
            case BlankRecord:
                Ascii("\n");
                break;
            case BlobRecord blob:
                Ascii("blob\n");
                if (blob.Mark is { } bm) Ascii($"mark :{bm}\n");
                if (blob.OriginalOid is { } boid) Ascii($"original-oid {boid}\n");
                await FlushLineAsync(ct);
                await WriteDataAsync(blob.Data, ct);
                break;
            case CommitRecord commit:
                Ascii("commit ");
                Raw(commit.RefNameBytes);
                Ascii("\n");
                if (commit.Mark is { } cm) Ascii($"mark :{cm}\n");
                foreach (var header in commit.HeaderLines) { Raw(header); Ascii("\n"); }
                await FlushLineAsync(ct);
                await WriteDataAsync(commit.Message, ct);
                foreach (var parent in commit.Parents)
                {
                    Ascii(parent.IsMerge ? "merge " : "from ");
                    Raw(parent.DataRef);
                    Ascii("\n");
                }
                foreach (var command in commit.FileCommands)
                {
                    Raw(command.RawLine);
                    Ascii("\n");
                    if (command is FileModify { Inline: { } inline })
                    {
                        await FlushLineAsync(ct);
                        await WriteDataAsync(inline, ct);
                    }
                }
                await FlushLineAsync(ct);
                break;
            case TagRecord tag:
                Ascii("tag ");
                Raw(tag.TagNameBytes);
                Ascii("\n");
                if (tag.Mark is { } tm) Ascii($"mark :{tm}\n");
                foreach (var header in tag.HeaderLines) { Raw(header); Ascii("\n"); }
                await FlushLineAsync(ct);
                await WriteDataAsync(tag.Message, ct);
                break;
            case ResetRecord reset:
                Ascii("reset ");
                Raw(reset.RefNameBytes);
                Ascii("\n");
                if (reset.FromRef is { } rfrom)
                {
                    Ascii("from ");
                    Raw(rfrom);
                    Ascii("\n");
                }
                break;
            case FeatureRecord feature: Raw(feature.RawLine); Ascii("\n"); break;
            case OptionRecord option: Raw(option.RawLine); Ascii("\n"); break;
            case ProgressRecord progress: Raw(progress.RawLine); Ascii("\n"); break;
            case DoneRecord: Ascii("done\n"); break;
            case AliasRecord alias:
                Ascii($"alias\nmark :{alias.Mark}\nto ");
                Raw(alias.ToRef);
                Ascii("\n");
                break;
            default:
                throw new NotSupportedException($"record type {record.GetType().Name} has no emission");
        }
        await FlushLineAsync(ct);
        RecordsWritten++;
    }

    public Task FlushAsync(CancellationToken ct = default) => _destination.FlushAsync(ct);

    private void Ascii(string text)
    {
        foreach (var c in text) _line.WriteByte((byte)c);
    }

    private void Raw(byte[] bytes) => _line.Write(bytes, 0, bytes.Length);

    private async Task FlushLineAsync(CancellationToken ct)
    {
        if (_line.Length == 0) return;
        BytesWritten += _line.Length;
        await _destination.WriteAsync(_line.GetBuffer().AsMemory(0, (int)_line.Length), ct);
        _line.SetLength(0);
    }

    private async Task WriteDataAsync(DataBlock data, CancellationToken ct)
    {
        Ascii($"data {data.Length}\n");
        await FlushLineAsync(ct);
        if (data.InlineBytes is { } inline)
        {
            BytesWritten += inline.Length;
            await _destination.WriteAsync(inline, ct);
        }
        else if (data.SourceSlice.Length > 0)
        {
            if (_spool is null)
                throw new InvalidOperationException("record carries a spool slice but the writer has no spool stream");
            _spool.Seek(data.SourceSlice.Offset, SeekOrigin.Begin);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                var remaining = data.SourceSlice.Length;
                while (remaining > 0)
                {
                    var read = _spool.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0)
                        throw new EndOfStreamException(
                            $"spool ends inside slice at offset {data.SourceSlice.Offset} length {data.SourceSlice.Length}");
                    await _destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    BytesWritten += read;
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        if (data.TrailingLf)
        {
            Ascii("\n");
            await FlushLineAsync(ct);
        }
    }
}
