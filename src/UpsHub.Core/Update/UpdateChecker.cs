using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpsHub.Core.Update;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>A GitHub release as the API describes it (unverified metadata).</summary>
public sealed record ReleaseInfo(SemVer Version, string Tag, string Name, string Notes, Uri? HtmlUrl, DateTimeOffset? Published,
    IReadOnlyList<ReleaseAsset> Assets)
{
    public const string ChecksumsName = "SHA256SUMS.txt";
    public const string SignatureName = "SHA256SUMS.txt.minisig";
    public bool Prerelease { get; init; }

    public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => a.Name.Equals(ChecksumsName, StringComparison.Ordinal));
    public ReleaseAsset? Signature => Assets.FirstOrDefault(a => a.Name.Equals(SignatureName, StringComparison.Ordinal));
}

public enum UpdateCheckOutcome { UpdateAvailable, UpToDate, NoReleases, Skipped, Failed }

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, VerifiedRelease? Release, string Message, IReadOnlyList<SourceResult> Sources);

/// <summary>What the checker remembers between runs (stored next to settings.json).</summary>
public sealed class UpdateState
{
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? ETag { get; set; }
    /// <summary>Body of the last 200 GitHub response, replayed on 304 Not Modified.</summary>
    public string? CachedJson { get; set; }
    public string? SkippedVersion { get; set; }
    public string? LastResult { get; set; }
    /// <summary>Version an automatic install was last started for, how many times, and when.</summary>
    public string? PendingVersion { get; set; }
    public int PendingAttempts { get; set; }
    public DateTimeOffset? PendingSinceUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateState))]
public partial class UpdateJsonContext : JsonSerializerContext { }

public sealed class UpdateStateStore(string directory)
{
    public string FilePath => Path.Combine(directory, "update-state.json");

    public UpdateState Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), UpdateJsonContext.Default.UpdateState) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(UpdateState s)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, UpdateJsonContext.Default.UpdateState));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed record UpdateFeedOptions
{
    /// <summary>Primary feed. Null turns it off (tests).</summary>
    public Uri? CloudManifest { get; init; } = UpdateEndpoints.CloudManifest;
    public UrlPolicy CloudPolicy { get; init; } = UrlPolicy.Cloud;
    /// <summary>Second source: public GitHub releases ("owner/repo"). Null turns it off.</summary>
    public string? GitHubRepo { get; init; } = "AslanCore/UPS-Hub";
    public Uri GitHubApi { get; init; } = new("https://api.github.com/");
    public UrlPolicy GitHubPolicy { get; init; } = UrlPolicy.GitHub;
    public Minisign.PublicKey? Key { get; init; } = ReleaseKey.Public;
    public string Product { get; init; } = "AslanCore.UpsHub";
    /// <summary>Each source gets its own deadline, so a slow one never holds back the other's answer.</summary>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(25);

    public UrlPolicy PolicyFor(UpdateSource s) => s == UpdateSource.Cloud ? CloudPolicy : GitHubPolicy;
}

/// <summary>
/// Asks AslanCore Cloud and public GitHub releases at the same time, keeps only candidates the release key
/// vouches for, and offers the highest version newer than this one (AslanCore Cloud wins a tie).
/// </summary>
public sealed class UpdateChecker
{
    public static readonly TimeSpan AutoInterval = TimeSpan.FromDays(1);
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    public UpdateFeedOptions Options { get; }

    public UpdateChecker(HttpClient http, UpdateFeedOptions? options = null, TimeProvider? time = null)
    {
        _http = http;
        Options = options ?? new();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>"owner/repo" from a GitHub URL such as https://github.com/AslanCore/UPS-Hub.</summary>
    public static string? RepoFromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1].Replace(".git", "")}" : null;
    }

    public static HttpClient CreateHttpClient(string userAgentVersion)
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = Timeout.InfiniteTimeSpan };   // callers set their own deadlines (per source, per download)
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UPS-Hub", userAgentVersion));
        return h;
    }

    /// <summary>Is an automatic check due? (At most once a day.)</summary>
    public bool IsDue(UpdateState s) => s.LastCheckUtc is not { } last || _time.GetUtcNow() - last >= AutoInterval;

    public async Task<UpdateCheckResult> CheckAsync(SemVer current, UpdateState state, bool manual, CancellationToken ct = default)
    {
        var o = Options;
        var tasks = new List<Task<SourceResult>>();
        if (o.CloudManifest is { } m)
            tasks.Add(Guard(UpdateSource.Cloud, t => new CloudFeed(_http, m, o.CloudPolicy, o.Key, o.Product).FetchAsync(t), ct));
        if (o.GitHubRepo is { } repo)
            tasks.Add(Guard(UpdateSource.GitHub, t => new GitHubFeed(_http, repo, o.GitHubApi, o.GitHubPolicy, o.Key).FetchAsync(state, t), ct));
        var results = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        state.LastCheckUtc = _time.GetUtcNow();
        var r = Select(current, results, state.SkippedVersion, manual);
        state.LastResult = r.Message;
        return r;
    }

    private async Task<SourceResult> Guard(UpdateSource s, Func<CancellationToken, Task<SourceResult>> fetch, CancellationToken outer)
    {
        var name = UpdateSources.Name(s);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(Options.SourceTimeout);
        try { return await fetch(cts.Token); }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested) { return new(s, SourceStatus.Unavailable, null, $"{name} didn't answer in time."); }
        catch (HttpRequestException ex) { return new(s, SourceStatus.Unavailable, null, $"Couldn't reach {name}: {ex.Message}"); }
        catch (IOException ex) { return new(s, SourceStatus.Unavailable, null, $"Couldn't reach {name}: {ex.Message}"); }
        catch (UpdateVerificationException ex) { return new(s, SourceStatus.Rejected, null, ex.Message); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        { return new(s, SourceStatus.Rejected, null, $"{name} sent a response UPS Hub couldn't read."); }
    }

    /// <summary>Pure choice between source answers: highest verified stable version, Cloud first on a tie.</summary>
    public static UpdateCheckResult Select(SemVer current, IReadOnlyList<SourceResult> results, string? skipped, bool manual)
    {
        var best = results
            .Where(r => r.Status == SourceStatus.Found && r.Release is { Version.IsPrerelease: false })
            .Select(r => r.Release!)
            .OrderByDescending(v => v.Version)
            .ThenBy(v => v.Source == UpdateSource.Cloud ? 0 : 1)
            .FirstOrDefault();
        var problems = results.Where(r => r.Status is SourceStatus.Rejected or SourceStatus.Unavailable).Select(r => r.Message).ToList();
        var rejected = results.Where(r => r.Status == SourceStatus.Rejected).Select(r => r.Message).ToList();
        string Warn() => rejected.Count > 0 ? " " + string.Join(" ", rejected) : "";

        if (best is null)
        {
            if (problems.Count == 0) return new(UpdateCheckOutcome.NoReleases, null, "No releases have been published yet.", results);
            return new(UpdateCheckOutcome.Failed, null, string.Join(" ", problems), results);
        }
        if (best.Version <= current) return new(UpdateCheckOutcome.UpToDate, best, $"UPS Hub {current} is up to date." + Warn(), results);
        if (!manual && SemVer.TryParse(skipped) is { } sv && sv >= best.Version)
            return new(UpdateCheckOutcome.Skipped, best, $"Version {best.Version} was skipped.", results);
        return new(UpdateCheckOutcome.UpdateAvailable, best, $"UPS Hub {best.Version} is available from {best.SourceName}." + Warn(), results);
    }
}
