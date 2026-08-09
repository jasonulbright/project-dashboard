using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services.Update;

/// <summary>
/// Reads the project's latest published release and compares it with this build.
///
/// The request is an anonymous public GET carrying no account, repository, or usage data —
/// what the endpoint observes is an address, a timestamp, and the header naming this app and
/// its version. Nothing is fetched but that one JSON document: a newer release is reported
/// with a link to its page, and the artifact is never downloaded, never verified on the
/// user's behalf, and never run.
///
/// The launch check is fire-and-forget after the window is up and is cancelled at shutdown;
/// it blocks nothing and gates nothing. Its failures are quiet in the UI and logged — the
/// user did not ask, the unauthenticated quota is shared per address, and offline is the
/// normal state of a laptop. A check the user asked for reports its own reason instead.
/// </summary>
public class UpdateCheckService : IHostedService
{
    /// <summary>
    /// Floor between two launch checks. A failed check sets it as well as a successful one:
    /// the failures are the case most likely to repeat, and retrying them every launch is
    /// what spends a shared address's unauthenticated quota.
    /// </summary>
    public static readonly TimeSpan LaunchCooldown = TimeSpan.FromHours(24);

    /// <summary>Wall-clock budget for one read, headers and body together.</summary>
    private static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Response bytes read before the body is abandoned. The document carries four fields;
    /// a body past this size is not one this app can use, and buffering it is work an
    /// unauthenticated endpoint can ask for without being asked.
    /// </summary>
    private const int MaxBodyBytes = 256 * 1024;

    private readonly SettingsService _settings;
    private readonly CancellationTokenSource _stopping = new();

    public UpdateCheckService(SettingsService settings) => _settings = settings;

    /// <summary>
    /// The update the most recent check found, or null while none has been found. Read as
    /// well as subscribed to: a surface built after the check completed would subscribe to
    /// an event that has already fired.
    /// </summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>Raised after <see cref="Available"/> changes, from whichever thread checked.</summary>
    public event Action? AvailableChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Not awaited: the hosted-service chain runs the window up, and a network read on it
        // would put the launch behind a timeout.
        _ = Task.Run(() => RunLaunchCheckAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunLaunchCheckAsync(CancellationToken ct)
    {
        try
        {
            await CheckAsync(manual: false, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown reached the check first; there is nothing left to report to.
        }
        catch (Exception ex)
        {
            Log.Warn("Launch update check failed", ex);
        }
    }

    /// <summary>
    /// Runs a check and records its outcome. <paramref name="manual"/> is what the user
    /// asked for: it ignores the cooldown and its result is reported on the page that asked.
    /// A check refused by the toggle or by the cooldown reads nothing and sends nothing.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        var settings = _settings.Load();
        if (!settings.EnableUpdateCheck)
            return new UpdateCheckResult(UpdateOutcome.Disabled, DisabledStatus);

        // A found release is a fact about the repository, not about the process that read it.
        // Publishing the record before anything else is read is what carries the notice across
        // a relaunch inside the cooldown and across a check that cannot reach GitHub; an answer
        // this run gets replaces it below.
        var recorded = Hydrate(settings, AppVersionInfo.Current);
        PublishAvailable(recorded);

        var now = DateTimeOffset.UtcNow;
        if (!manual && WithinCooldown(settings, now))
            return new UpdateCheckResult(UpdateOutcome.Cooldown, LastOutcomeStatus(settings), recorded);

        ReleaseFetch fetch;
        try
        {
            fetch = await FetchLatestAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            fetch = ReleaseFetch.Unreachable($"Couldn't reach GitHub — {Trim(ex.Message)}");
        }

        var result = Interpret(fetch, AppVersionInfo.Current);
        Record(result, manual, now);
        return result;
    }

    /// <summary>Reported when the toggle is off, on every path that could have read.</summary>
    public const string DisabledStatus = "Update checks are off.";

    /// <summary>True when the last recorded check is younger than <see cref="LaunchCooldown"/>.</summary>
    internal static bool WithinCooldown(AppSettings settings, DateTimeOffset now)
    {
        if (settings.LastUpdateCheckUtc is not { } last) return false;
        // A stamp in the future is a clock that moved, not a check that has not aged. Treating
        // it as inside the cooldown would suppress checks until the clock caught up.
        if (last > now) return false;
        return now - last < LaunchCooldown;
    }

    /// <summary>
    /// The offer a previous answer recorded, re-derived rather than replayed. Two things are
    /// checked again because neither holds by having once held:
    /// the link is editable text on disk that would reach the shell, so it is measured against
    /// the pinned releases path again; and the reader may have installed the release between
    /// sessions, so the tag is ordered against the build that is running now. Either failing
    /// yields no offer, which is the same state as never having found one.
    /// </summary>
    internal static AvailableUpdate? Hydrate(AppSettings settings, Version current)
    {
        var tag = settings.LastUpdateTagName;
        if (tag.Length == 0) return null;
        if (ReleaseVersion.Compare(tag, current) != VersionComparison.Newer) return null;

        if (!ReleaseLink.TryNormalize(settings.LastUpdateReleaseUrl, out var target))
        {
            Log.Warn("Refused a recorded update link that is not under this project's releases page.");
            return null;
        }

        return new AvailableUpdate(tag, target);
    }

    private void PublishAvailable(AvailableUpdate? update)
    {
        if (Equals(Available, update)) return;

        Available = update;
        try { AvailableChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn("Update-available subscriber threw", ex); }
    }

    private static string LastOutcomeStatus(AppSettings settings) =>
        settings.LastUpdateCheckStatus.Length > 0 ? settings.LastUpdateCheckStatus : "Not checked yet.";

    /// <summary>
    /// What one fetch means. Pure, so every branch is assertable without a socket, a settings
    /// file, or a window.
    /// </summary>
    internal static UpdateCheckResult Interpret(ReleaseFetch fetch, Version current)
    {
        if (!fetch.Reached)
            return new UpdateCheckResult(UpdateOutcome.Failed, fetch.TransportError ?? "The check failed.");

        switch (fetch.StatusCode)
        {
            case 200:
                break;
            case 404:
                return new UpdateCheckResult(UpdateOutcome.Failed, "No releases published yet.");
            case 403:
            case 429:
                return new UpdateCheckResult(UpdateOutcome.Failed, RateLimitStatus(fetch.RateLimitReset));
            case >= 300 and < 400:
                return new UpdateCheckResult(UpdateOutcome.Failed,
                    "GitHub redirected the request; the check doesn't follow a redirect to another address.");
            default:
                return new UpdateCheckResult(UpdateOutcome.Failed, $"GitHub answered {fetch.StatusCode}.");
        }

        if (!TryReadRelease(fetch.Body, out var release))
            return new UpdateCheckResult(UpdateOutcome.Unknown, "GitHub's answer couldn't be read.");

        if (release.Draft || release.Prerelease)
            return new UpdateCheckResult(UpdateOutcome.Unknown,
                "The latest release is a draft or a pre-release, so this build wasn't compared against it.");

        var comparison = ReleaseVersion.Compare(release.TagName, current);
        if (comparison == VersionComparison.Unreadable)
            return new UpdateCheckResult(UpdateOutcome.Unknown,
                "The latest release is tagged in a form this check can't compare.");

        if (comparison != VersionComparison.Newer)
            return new UpdateCheckResult(UpdateOutcome.UpToDate, $"Up to date (v{current}).");

        if (!ReleaseLink.TryNormalize(release.HtmlUrl, out var target))
            return new UpdateCheckResult(UpdateOutcome.Unknown,
                "The latest release links somewhere other than this project's releases page, so it wasn't offered.");

        return new UpdateCheckResult(
            UpdateOutcome.UpdateAvailable,
            $"Version {release.TagName} is available.",
            new AvailableUpdate(release.TagName, target));
    }

    private static string RateLimitStatus(DateTimeOffset? reset) =>
        reset is { } when
            ? $"GitHub's rate limit is reached; it resets at {when.ToLocalTime():HH:mm}."
            : "GitHub's rate limit is reached; the check will retry later.";

    /// <summary>
    /// Publishes the outcome and persists it. The write is best-effort: a check that cannot
    /// record itself is still a check, and a failed settings write must not surface as an
    /// update failure.
    /// </summary>
    private void Record(UpdateCheckResult result, bool manual, DateTimeOffset now)
    {
        switch (result.Outcome)
        {
            case UpdateOutcome.Failed:
                // Logged on both paths. Quiet means quiet in the UI, not swallowed.
                Log.Warn($"Update check failed — {result.Status}");
                break;
            case UpdateOutcome.Unknown when manual:
                // An answer this build cannot order against is not actionable on the launch
                // path, where it would be logged again every cooldown for as long as it lasts.
                Log.Warn($"Update check inconclusive — {result.Status}");
                break;
        }

        // Only an outcome that read an answer supersedes the previous one: a refused or
        // unreachable check knows nothing, and clearing an offer on it would drop a real one.
        // An answer that names no newer version does clear it, so a release withdrawn between
        // two checks leaves no notice pointing at a page that no longer describes it.
        var answered = result.Outcome
            is UpdateOutcome.UpdateAvailable or UpdateOutcome.UpToDate or UpdateOutcome.Unknown;
        if (answered) PublishAvailable(result.Update);

        try
        {
            var settings = _settings.Load();
            settings.LastUpdateCheckUtc = now;
            settings.LastUpdateCheckStatus = result.Status;
            if (answered)
            {
                // Written together: a tag with a stale link, or a link with a stale tag, is a
                // record that would offer one release under another's address.
                settings.LastUpdateTagName = result.Update?.TagName ?? "";
                settings.LastUpdateReleaseUrl = result.Update?.ReleaseUrl ?? "";
            }
            if (!_settings.Save(settings))
                Log.Warn("Update check could not record its outcome; the cooldown will not hold.");
        }
        catch (Exception ex)
        {
            Log.Warn("Update check could not record its outcome", ex);
        }
    }

    // ── The read ────────────────────────────────────────────────────────────────

    /// <summary>The four fields this check reads. Everything else in the document is ignored.</summary>
    internal sealed record ReleaseDocument(string TagName, string HtmlUrl, bool Draft, bool Prerelease);

    internal static bool TryReadRelease(string body, out ReleaseDocument release)
    {
        release = new ReleaseDocument("", "", false, false);
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            release = new ReleaseDocument(
                Text(root, "tag_name"),
                Text(root, "html_url"),
                Flag(root, "draft"),
                Flag(root, "prerelease"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// A flag absent or of the wrong kind reads as true for a refusal flag: a document that
    /// does not say a release is published is not one to prompt on.
    /// </summary>
    private static bool Flag(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) || value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => true
        };

    private static readonly Lazy<HttpClient> Client = new(CreateClient);

    private static HttpClient CreateClient()
    {
        // A redirect is the one way a response picks the next host, and the host here is
        // pinned; a redirected request is reported rather than followed.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = FetchBudget };
        // The endpoint refuses a request that names no caller.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ProjectDashboard/{AppVersionInfo.Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>
    /// The one outbound read this feature makes. Virtual so a test supplies the response
    /// without a socket — the suite reaches no network at all.
    /// </summary>
    protected internal virtual async Task<ReleaseFetch> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Client.Value.GetAsync(
                ReleaseLink.LatestReleaseEndpoint, HttpCompletionOption.ResponseHeadersRead, ct);

            var body = await ReadCappedAsync(response, ct);
            return new ReleaseFetch((int)response.StatusCode, body, RateLimitReset(response), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ReleaseFetch.Unreachable("The check timed out.");
        }
        catch (HttpRequestException ex)
        {
            return ReleaseFetch.Unreachable($"Couldn't reach GitHub — {Trim(ex.Message)}");
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0) break;
            filled += read;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, filled);
    }

    private static DateTimeOffset? RateLimitReset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-reset", out var values)) return null;
        foreach (var value in values)
        {
            if (!long.TryParse(value, out var epochSeconds)) continue;
            if (epochSeconds is < 0 or > 253_402_300_799) continue;
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }
        return null;
    }

    /// <summary>Keeps a transport message to one line of a status area.</summary>
    private static string Trim(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= 160 ? line : line[..160];
    }
}
