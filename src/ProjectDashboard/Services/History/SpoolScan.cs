using System.IO;

namespace ProjectDashboard.Services.History;

/// <summary>
/// Spool reads the transform pass makes: whole-payload materialization for the arms that
/// transform, and a chunked needle scan for the arm that must report on a payload without
/// holding it.
/// </summary>
public static class SpoolScan
{
    /// <summary>Bytes read per chunk by <see cref="LiteralsPresent"/>, on top of the needle overlap.</summary>
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    public static byte[] ReadSlice(FileStream spool, SpoolSlice slice)
    {
        var payload = new byte[slice.Length];
        spool.Seek(slice.Offset, SeekOrigin.Begin);
        spool.ReadExactly(payload);
        return payload;
    }

    /// <summary>
    /// Which of <paramref name="ops"/> have their needle inside the slice, read in bounded
    /// chunks rather than materialized. Each read keeps the previous window's last
    /// (longest needle - 1) bytes, so no needle placement can fall between two reads. Peak
    /// memory is the chunk plus that overlap: the caller is the arm that already refused a
    /// payload for its size, where materializing to scan trades a reported skip for an
    /// OutOfMemoryException that ends the whole rewrite.
    /// Ops are returned in their input order, each at most once.
    /// </summary>
    public static List<LiteralReplace> LiteralsPresent(
        FileStream spool, SpoolSlice slice, IReadOnlyList<LiteralReplace> ops, int chunkSize = DefaultChunkSize)
    {
        var found = new List<LiteralReplace>();
        if (ops.Count == 0) return found;

        var overlap = ops.Max(op => op.Find.Length) - 1;
        var buffer = new byte[overlap + Math.Max(chunkSize, 1)];
        var matched = new bool[ops.Count];
        var outstanding = ops.Count;
        var carried = 0;
        var remaining = slice.Length;

        spool.Seek(slice.Offset, SeekOrigin.Begin);
        while (remaining > 0 && outstanding > 0)
        {
            var wanted = (int)Math.Min(remaining, buffer.Length - carried);
            spool.ReadExactly(buffer, carried, wanted);
            remaining -= wanted;

            var window = buffer.AsSpan(0, carried + wanted);
            for (var i = 0; i < ops.Count; i++)
            {
                if (matched[i] || window.IndexOf(ops[i].Find.AsSpan()) < 0) continue;
                matched[i] = true;
                outstanding--;
            }

            carried = Math.Min(overlap, window.Length);
            window[(window.Length - carried)..].CopyTo(buffer);
        }

        for (var i = 0; i < ops.Count; i++)
            if (matched[i]) found.Add(ops[i]);
        return found;
    }
}
