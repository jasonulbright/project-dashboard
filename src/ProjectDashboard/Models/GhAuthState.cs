namespace ProjectDashboard.Models;

/// <summary>
/// One account the GitHub CLI holds for one host. Scopes are the names gh itself prints; no
/// token value is read, carried, or displayed — the status read never asks for one.
/// </summary>
public sealed record GhAccount(
    string Host,
    string Login,
    bool Active,
    IReadOnlyList<string> Scopes,
    string State,
    string Error)
{
    /// <summary>
    /// gh reports "success" only for an account whose token passed its own check; "error" and
    /// "timeout" are accounts that are configured and cannot be used. Treating a configured
    /// account as a working one would name a login for operations that will fail.
    /// </summary>
    public bool IsUsable => State.Equals("success", StringComparison.OrdinalIgnoreCase);

    /// <summary>Display form for the account table; empty when gh reported no scopes.</summary>
    public string ScopeList => string.Join(", ", Scopes);
}

/// <summary>
/// What the GitHub CLI reports about every host it knows. A null <see cref="GhAuthState"/> is a
/// read that failed; an instance holding no accounts is the answer "signed in nowhere".
/// </summary>
public sealed record GhAuthState(IReadOnlyList<GhAccount> Accounts)
{
    public bool AnySignedIn => Accounts.Any(a => a.IsUsable);

    /// <summary>
    /// Accounts gh holds for one host. Hosts compare case-insensitively and without a leading
    /// "www.", because a remote URL carries whichever form the user cloned with while gh stores
    /// the canonical one.
    /// </summary>
    public IReadOnlyList<GhAccount> ForHost(string host)
    {
        var wanted = NormalizeHost(host);
        return wanted.Length == 0
            ? []
            : [.. Accounts.Where(a => NormalizeHost(a.Host) == wanted)];
    }

    /// <summary>
    /// The account gh targets for a host, or null when it holds none there — or holds some and
    /// marks none active, which is a state its own commands would have to be told to resolve.
    /// </summary>
    public GhAccount? ActiveFor(string host) => ForHost(host).FirstOrDefault(a => a.Active);

    public static string NormalizeHost(string? host)
    {
        var trimmed = (host ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        return trimmed.StartsWith("www.", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
    }
}
