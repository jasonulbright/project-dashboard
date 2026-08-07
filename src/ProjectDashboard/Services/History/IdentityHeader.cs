using System.Text;

namespace ProjectDashboard.Services.History;

/// <summary>
/// Rewrites the identity in an author/committer/tagger header line. The header grammar is
/// <c>&lt;role&gt; NAME &lt;EMAIL&gt; WHEN</c>; only NAME and EMAIL are remapped, and the
/// trailing timestamp/zone bytes (WHEN) are preserved verbatim so unchanged fields keep
/// byte fidelity. Names and emails are decoded as UTF-8, so non-ASCII identities round-trip.
/// A line whose bytes are not valid UTF-8 is refused outright: the decode would replace
/// them, so a match could not be trusted and an unmapped field would not survive the
/// re-encode byte for byte. <see cref="IsRoleLine"/> names those lines for the caller.
/// </summary>
public static class IdentityHeader
{
    private static readonly string[] Roles = ["author ", "committer ", "tagger "];
    private static readonly byte[][] RoleBytes = [.. Roles.Select(Encoding.ASCII.GetBytes)];

    /// <summary>True when the line carries an ident role prefix. Role names are ASCII, so the raw bytes decide it without a decode.</summary>
    public static bool IsRoleLine(ReadOnlySpan<byte> line)
    {
        foreach (var role in RoleBytes)
            if (line.StartsWith(role)) return true;
        return false;
    }

    public static bool TryRewrite(byte[] line, IReadOnlyList<IdentityMapping> mappings, out byte[] rewritten)
    {
        rewritten = line;
        if (!System.Text.Unicode.Utf8.IsValid(line)) return false;
        var text = Encoding.UTF8.GetString(line);

        var role = Roles.FirstOrDefault(r => text.StartsWith(r, StringComparison.Ordinal));
        if (role is null) return false;

        var rest = text[role.Length..];
        // Email is the last angle-bracket pair; the name precedes it, WHEN follows it.
        var gt = rest.LastIndexOf('>');
        if (gt < 0) return false;
        var lt = rest.LastIndexOf('<', gt);
        if (lt < 0) return false;

        var name = rest[..lt].TrimEnd(' ');
        var email = rest[(lt + 1)..gt];
        var when = rest[(gt + 1)..];

        foreach (var mapping in mappings)
        {
            if (mapping.OldName is { } on && !string.Equals(on, name, StringComparison.Ordinal)) continue;
            if (mapping.OldEmail is { } oe && !string.Equals(oe, email, StringComparison.Ordinal)) continue;

            var newName = mapping.NewName ?? name;
            var newEmail = mapping.NewEmail ?? email;
            if (string.Equals(newName, name, StringComparison.Ordinal)
                && string.Equals(newEmail, email, StringComparison.Ordinal))
                return false;

            var namePart = newName.Length > 0 ? newName + " " : "";
            rewritten = Encoding.UTF8.GetBytes($"{role}{namePart}<{newEmail}>{when}");
            return true;
        }
        return false;
    }
}
