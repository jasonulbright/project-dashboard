using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace ProjectDashboard.Services.History;

public enum TransformClass
{
    Unchanged,
    Changed,
    BinarySkipped
}

/// <summary><see cref="Bytes"/> is non-null only when <see cref="Class"/> is Changed.</summary>
public readonly record struct TransformResult(TransformClass Class, byte[]? Bytes);

/// <summary>
/// Applies a fixed op list to one blob payload at a time. A payload that is not valid
/// UTF-8 is classified binary and skipped for every op — literal ops included — so a
/// structured binary file is never byte-patched into corruption. Output equal to the
/// input classifies as Unchanged even when an op matched, so reports never count a
/// rewrite that changed nothing. Memory bound: one materialized payload plus its
/// transformed copy; payloads above int.MaxValue bytes cannot materialize and are
/// refused by the caller before reaching here.
/// </summary>
public sealed class BlobTransformer
{
    public const long DefaultRegexPayloadLimit = 200L * 1024 * 1024;

    /// <summary>Caps regex backtracking on pathological pattern/content pairs; hit, it throws RegexMatchTimeoutException.</summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IReadOnlyList<ContentOp> _ops;
    private readonly Dictionary<RegexReplace, Regex> _compiled = [];
    private readonly long _regexPayloadLimit;

    public BlobTransformer(IReadOnlyList<ContentOp> ops, long regexPayloadLimit = DefaultRegexPayloadLimit)
    {
        _ops = ops;
        _regexPayloadLimit = regexPayloadLimit;
        foreach (var op in ops)
            if (op is RegexReplace regex)
                _compiled[regex] = new Regex(regex.Pattern, regex.Options | RegexOptions.CultureInvariant, RegexMatchTimeout);
    }

    public long RegexPayloadLimit => _regexPayloadLimit;

    /// <summary>True when at least one op is a regex, so the payload limit can gate before a blob is materialized.</summary>
    public bool HasRegexOp => _compiled.Count > 0;

    public TransformResult Transform(byte[] payload)
    {
        if (!Utf8.IsValid(payload))
            return new TransformResult(TransformClass.BinarySkipped, null);

        var current = payload;
        foreach (var op in _ops)
        {
            switch (op)
            {
                case LiteralReplace literal:
                    current = ReplaceLiteral(current, literal.Find, literal.Replace) ?? current;
                    break;
                case RegexReplace regex:
                    if (current.LongLength > _regexPayloadLimit)
                        throw new NotSupportedException(
                            $"a {current.LongLength}-byte payload exceeds the {_regexPayloadLimit}-byte regex transform limit");
                    string text;
                    try
                    {
                        text = StrictUtf8.GetString(current);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        // Only reachable when an earlier literal op wrote invalid UTF-8
                        // into a valid-UTF-8 payload; the op list conflicts with itself.
                        throw new InvalidOperationException(
                            "an earlier literal op produced invalid UTF-8, so a regex op cannot apply — reorder or fix the op list", ex);
                    }
                    var replaced = _compiled[regex].Replace(text, regex.Replacement);
                    if (!ReferenceEquals(replaced, text))
                        current = StrictUtf8.GetBytes(replaced);
                    break;
                default:
                    throw new NotSupportedException($"content op {op.GetType().Name} has no transform");
            }
        }

        if (ReferenceEquals(current, payload) || current.AsSpan().SequenceEqual(payload))
            return new TransformResult(TransformClass.Unchanged, null);
        return new TransformResult(TransformClass.Changed, current);
    }

    /// <summary>
    /// Left-to-right non-overlapping byte replace. Returns null when the needle is absent
    /// so a miss allocates nothing. The scan resumes after each consumed match, so of two
    /// overlapping candidates the left one always wins.
    /// </summary>
    public static byte[]? ReplaceLiteral(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> find, ReadOnlySpan<byte> replace)
    {
        var next = payload.IndexOf(find);
        if (next < 0) return null;

        var result = new MemoryStream(payload.Length);
        var position = 0;
        while (next >= 0)
        {
            result.Write(payload.Slice(position, next));
            result.Write(replace);
            position += next + find.Length;
            next = payload[position..].IndexOf(find);
        }
        result.Write(payload[position..]);
        return result.ToArray();
    }
}
