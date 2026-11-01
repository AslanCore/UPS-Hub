using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpsHub.Core.Update;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(SemVer Version, string Tag, string Name, string Notes, Uri? HtmlUrl, DateTimeOffset? Published,
    IReadOnlyList<ReleaseAsset> Assets)
{
    public const string ChecksumsName = "SHA256SUMS.txt";
    public const string SignatureName = "SHA256SUMS.txt.minisig";

    public ReleaseAsset? Installer => Assets.FirstOrDefault(a =>
        a.Name.StartsWith("UpsHub-Setup-", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => a.Name.Equals(ChecksumsName, StringComparison.OrdinalIgnoreCase));
    public ReleaseAsset? Signature => Assets.FirstOrDefault(a => a.Name.Equals(SignatureName, StringComparison.OrdinalIgnoreCase));
}

public enum UpdateCheckOutcome { UpdateAvailable, UpToDate, NoReleases, Skipped, Failed }

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release, string Message, bool FromCache = false);

/// <summary>What the checker remembers between runs (stored next to settings.json).</summary>
public sealed class UpdateState
{
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? ETag { get; set; }
    /// <summary>Body of the last 200 response, replayed on 304 Not Modified.</summary>
    public string? CachedJson { get; set; }
    public string? SkippedVersion { get; set; }
    public string? LastResult { get; set; }
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, UpdateJsonContext.Default.UpdateState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Looks for a newer release on GitHub (unauthenticated <c>GET /repos/{owner}/{repo}/releases/latest</c>,
/// which skips drafts and pre-releases). Sends <c>If-None-Match</c> with the last ETag so repeat checks are
/// answered with 304 and don't use up the 60-requests-an-hour anonymous limit.
/// </summary>
public sealed class UpdateChecker
{
    public static readonly TimeSpan AutoInterval = TimeSpan.FromDays(1);
    private readonly HttpClient _http;
    private readonly Uri _apiBase;
    private readonly string _repo;
    private readonly TimeProvider _time;

    public UpdateChecker(HttpClient http, string repo, Uri? apiBase = null, TimeProvider? time = null)
    {
        _http = http;
        _repo = repo;
        _apiBase = apiBase ?? new Uri("https://api.github.com/");
        _time = time ?? TimeProvider.System;
    }

    /// <summary>"owner/repo" from a GitHub URL such as https://github.com/AslanCore/UPS-Hub.</summary>
    public static string? RepoFromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1].Replace(".git", "")}" : null;
    }

    public static HttpClient CreateHttpClient(string userAgentVersion)
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UPS-Hub", userAgentVersion));
        return h;
    }

    /// <summary>Is an automatic check due? (At most once a day.)</summary>
    public bool IsDue(UpdateState s) => s.LastCheckUtc is not { } last || _time.GetUtcNow() - last >= AutoInterval;

    public async Task<UpdateCheckResult> CheckAsync(SemVer current, UpdateState state, bool manual, CancellationToken ct = default)
    {
        var url = new Uri(_apiBase, $"repos/{_repo}/releases/latest");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (state.ETag is { Length: > 0 } etag && state.CachedJson is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

        UpdateCheckResult result;
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            state.LastCheckUtc = _time.GetUtcNow();
            string? json = null;
            bool cached = false;
            if (resp.StatusCode == HttpStatusCode.NotModified && state.CachedJson is not null) { json = state.CachedJson; cached = true; }
            else if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                state.ETag = null; state.CachedJson = null;
                result = new(UpdateCheckOutcome.NoReleases, null, "No releases have been published yet.");
                state.LastResult = result.Message;
                return result;
            }
            else if (resp.IsSuccessStatusCode)
            {
                json = await resp.Content.ReadAsStringAsync(ct);
                state.ETag = resp.Headers.ETag?.ToString();
                state.CachedJson = json;
            }
            else
            {
                var why = (int)resp.StatusCode is 403 or 429 && resp.Headers.TryGetValues("x-ratelimit-remaining", out var rl) && rl.FirstOrDefault() == "0"
                    ? "GitHub's rate limit was reached; UPS Hub will try again later."
                    : $"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}.";
                result = new(UpdateCheckOutcome.Failed, null, why);
                state.LastResult = result.Message;
                return result;
            }

            var rel = ParseRelease(json!);
            if (rel is null) result = new(UpdateCheckOutcome.NoReleases, null, "The latest release has no usable version tag.", cached);
            else if (rel.Version <= current) result = new(UpdateCheckOutcome.UpToDate, rel, $"UPS Hub {current} is up to date.", cached);
            else if (!manual && state.SkippedVersion is { } skip && SemVer.TryParse(skip) is { } sv && sv >= rel.Version)
                result = new(UpdateCheckOutcome.Skipped, rel, $"Version {rel.Version} was skipped.", cached);
            else result = new(UpdateCheckOutcome.UpdateAvailable, rel, $"UPS Hub {rel.Version} is available.", cached);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new(UpdateCheckOutcome.Failed, null, "GitHub didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            result = new(UpdateCheckOutcome.Failed, null, "Couldn't reach GitHub: " + ex.Message);
        }
        catch (JsonException)
        {
            result = new(UpdateCheckOutcome.Failed, null, "GitHub sent a response UPS Hub couldn't read.");
        }
        state.LastResult = result.Message;
        return result;
    }

    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) return null;
        var tag = Str(r, "tag_name");
        if (SemVer.TryParse(tag) is not { } ver) return null;
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (Str(a, "name") is { } n && Uri.TryCreate(Str(a, "browser_download_url"), UriKind.Absolute, out var u))
                    assets.Add(new ReleaseAsset(n, u, a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0));
        Uri.TryCreate(Str(r, "html_url"), UriKind.Absolute, out var html);
        DateTimeOffset? pub = DateTimeOffset.TryParse(Str(r, "published_at"), out var p) ? p : null;
        return new ReleaseInfo(ver, tag!, Str(r, "name") is { Length: > 0 } nm ? nm : tag!, Str(r, "body") ?? "", html, pub, assets);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
