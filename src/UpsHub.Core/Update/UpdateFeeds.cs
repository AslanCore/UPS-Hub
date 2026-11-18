using System.Net;
using System.Text;
using System.Text.Json;

namespace UpsHub.Core.Update;

/// <summary>Where a release candidate came from. AslanCore Cloud is preferred when both offer the same version.</summary>
public enum UpdateSource { Cloud, GitHub }

/// <summary>
/// A release whose version, installer name, download URL and SHA-256 were proven by the release key:
/// for AslanCore Cloud the signed manifest carries them; for GitHub the signed SHA256SUMS.txt of that
/// same release lists the installer named after the tag's version. Nothing else is ever installed.
/// </summary>
public sealed record VerifiedRelease(UpdateSource Source, SemVer Version, string InstallerName, Uri InstallerUrl, long InstallerSize,
    string InstallerSha256, string Notes, Uri? HtmlUrl, DateTimeOffset? Published)
{
    public static string InstallerNameFor(SemVer v) => $"UpsHub-Setup-{v}.exe";
    public string SourceName => UpdateSources.Name(Source);
}

public enum SourceStatus { Found, NoRelease, Unavailable, Rejected }

public sealed record SourceResult(UpdateSource Source, SourceStatus Status, VerifiedRelease? Release, string Message, bool FromCache = false);

public static class UpdateSources
{
    public static string Name(UpdateSource s) => s == UpdateSource.Cloud ? "AslanCore Cloud" : "GitHub";
}

/// <summary>Fixed update endpoints. Not shown in the UI.</summary>
public static class UpdateEndpoints
{
    public const string BlobHost = "ssgmx26jwyimwt8g.public.blob.vercel-storage.com";
    public const string CloudHost = "aslancorecloud.vercel.app";
    /// <summary>Signed stable-channel manifest; its detached minisign signature is the same URL + ".minisig".</summary>
    public static readonly Uri CloudManifest = new($"https://{BlobHost}/upshub/stable/manifest.json");
    public static Uri SignatureOf(Uri u) => new UriBuilder(u) { Path = u.AbsolutePath + ".minisig", Query = "" }.Uri;
}

/// <summary>Hard caps on what an update source may send.</summary>
public static class UpdateLimits
{
    public const int ManifestBytes = 64 * 1024;
    public const int SignatureBytes = 4 * 1024;
    public const int ChecksumsBytes = 64 * 1024;
    public const int ApiBytes = 1024 * 1024;
    public const long InstallerBytes = 512L * 1024 * 1024;
    public const int NotesChars = 16 * 1024;
}

/// <summary>Only https (unless a test relaxes it), no user-info, and only listed hosts (".example.com" = any subdomain).</summary>
public sealed class UrlPolicy(bool requireHttps, params string[] hosts)
{
    public bool RequireHttps { get; } = requireHttps;
    public IReadOnlyList<string> Hosts { get; } = hosts;

    public static readonly UrlPolicy Cloud = new(true, UpdateEndpoints.BlobHost, UpdateEndpoints.CloudHost);
    public static readonly UrlPolicy GitHub = new(true, "api.github.com", "github.com", ".githubusercontent.com");

    public bool Allows(Uri? u)
    {
        if (u is null || !u.IsAbsoluteUri || u.UserInfo.Length > 0) return false;
        if (u.Scheme != Uri.UriSchemeHttps && (RequireHttps || u.Scheme != Uri.UriSchemeHttp)) return false;
        var h = u.IdnHost.TrimEnd('.');
        return Hosts.Any(x => x.StartsWith('.')
            ? h.EndsWith(x, StringComparison.OrdinalIgnoreCase) && h.Length > x.Length
            : h.Equals(x, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record BoundedResponse(HttpStatusCode Status, byte[]? Body, string? ETag, bool RateLimited, string? Reason);

/// <summary>GET with a host policy (checked again on the final URL after redirects) and a size cap.</summary>
public static class BoundedHttp
{
    public static async Task<BoundedResponse> SendAsync(HttpClient http, HttpRequestMessage req, UrlPolicy policy, long max, CancellationToken ct)
    {
        if (!policy.Allows(req.RequestUri)) throw new UpdateVerificationException($"{req.RequestUri} is not an approved update address.");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!policy.Allows(resp.RequestMessage?.RequestUri ?? req.RequestUri))
            throw new UpdateVerificationException("The update server redirected to an address UPS Hub doesn't trust.");
        bool rl = resp.Headers.TryGetValues("x-ratelimit-remaining", out var v) && v.FirstOrDefault() == "0";
        byte[]? body = resp.IsSuccessStatusCode ? await ReadLimitedAsync(resp.Content, max, ct) : null;
        return new BoundedResponse(resp.StatusCode, body, resp.Headers.ETag?.ToString(), rl, resp.ReasonPhrase);
    }

    public static Task<BoundedResponse> GetAsync(HttpClient http, Uri url, UrlPolicy policy, long max, CancellationToken ct) =>
        SendAsync(http, new HttpRequestMessage(HttpMethod.Get, url), policy, max, ct);

    public static async Task<byte[]> ReadLimitedAsync(HttpContent content, long max, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } len && len > max) throw new UpdateVerificationException($"The update server sent more data than allowed ({len} bytes).");
        await using var s = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new UpdateVerificationException("The update server sent more data than allowed.");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// AslanCore Cloud feed: a small JSON manifest plus its detached minisign signature by the release key.
/// The signature is checked over the exact bytes before anything in the manifest is read.
/// </summary>
public sealed class CloudFeed(HttpClient http, Uri manifestUrl, UrlPolicy policy, Minisign.PublicKey? key, string product)
{
    public const int Schema = 1;
    public const string Channel = "stable";

    public async Task<SourceResult> FetchAsync(CancellationToken ct)
    {
        const UpdateSource S = UpdateSource.Cloud;
        var m = await BoundedHttp.GetAsync(http, manifestUrl, policy, UpdateLimits.ManifestBytes, ct);
        if (m.Status == HttpStatusCode.NotFound) return new(S, SourceStatus.NoRelease, null, "AslanCore Cloud has no release yet.");
        if (m.Body is null) return new(S, SourceStatus.Unavailable, null, $"AslanCore Cloud answered {(int)m.Status} {m.Reason}.");
        if (key is null) return new(S, SourceStatus.Rejected, null, "No release key is built into this copy of UPS Hub.");
        var s = await BoundedHttp.GetAsync(http, UpdateEndpoints.SignatureOf(manifestUrl), policy, UpdateLimits.SignatureBytes, ct);
        if (s.Body is null)
            return s.Status == HttpStatusCode.NotFound
                ? new(S, SourceStatus.Rejected, null, "AslanCore Cloud's update manifest is not signed.")
                : new(S, SourceStatus.Unavailable, null, $"AslanCore Cloud answered {(int)s.Status} {s.Reason}.");
        if (!Minisign.Verify(key, m.Body, Encoding.UTF8.GetString(s.Body), out var err))
            return new(S, SourceStatus.Rejected, null, "AslanCore Cloud's update manifest has an invalid signature: " + err);
        var rel = ParseManifest(m.Body, policy, product);
        return new(S, SourceStatus.Found, rel, $"AslanCore Cloud offers {rel.Version}.");
    }

    /// <summary>Strict parse of an already signature-checked manifest. Throws <see cref="UpdateVerificationException"/> on anything unexpected.</summary>
    public static VerifiedRelease ParseManifest(byte[] json, UrlPolicy policy, string product)
    {
        static UpdateVerificationException Bad(string why) => new("AslanCore Cloud's update manifest was rejected: " + why);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw Bad("it isn't valid JSON."); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw Bad("not an object.");
            if (!r.TryGetProperty("schema", out var sc) || !sc.TryGetInt32(out var schema) || schema != Schema) throw Bad("unsupported schema.");
            if (Str(r, "product") != product) throw Bad("it is for a different product.");
            if (Str(r, "channel") != Channel) throw Bad("it is not for the stable channel.");
            if (SemVer.TryParse(Str(r, "version")) is not { } ver || Str(r, "version")!.StartsWith('v')) throw Bad("no valid version.");
            if (ver.IsPrerelease) throw Bad("pre-releases are not offered on the stable channel.");
            if (!r.TryGetProperty("installer", out var inst) || inst.ValueKind != JsonValueKind.Object) throw Bad("no installer.");
            var name = Str(inst, "name");
            if (name != VerifiedRelease.InstallerNameFor(ver)) throw Bad("the installer name doesn't match the version.");
            if (!Uri.TryCreate(Str(inst, "url"), UriKind.Absolute, out var url) || !policy.Allows(url)) throw Bad("the installer address is not approved.");
            if (Uri.UnescapeDataString(url.Segments[^1]) != name || url.Query.Length > 0 || url.Fragment.Length > 0) throw Bad("the installer address doesn't match its name.");
            if (!inst.TryGetProperty("size", out var sz) || !sz.TryGetInt64(out var size) || size <= 0 || size > UpdateLimits.InstallerBytes) throw Bad("invalid installer size.");
            var sha = Str(inst, "sha256");
            if (sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit)) throw Bad("invalid installer SHA-256.");
            var notes = Str(r, "notes") ?? "";
            if (notes.Length > UpdateLimits.NotesChars) notes = notes[..UpdateLimits.NotesChars];
            DateTimeOffset? pub = DateTimeOffset.TryParse(Str(r, "published"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : null;
            return new VerifiedRelease(UpdateSource.Cloud, ver, name!, url, size, sha.ToLowerInvariant(), notes, null, pub);
        }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Public GitHub releases, read anonymously (<c>GET /repos/{owner}/{repo}/releases/latest</c>, which skips
/// drafts and pre-releases; ETag reuse keeps repeat checks off the 60-an-hour limit). A release only counts
/// once its own SHA256SUMS.txt is signed by the release key and lists UpsHub-Setup-&lt;tag version&gt;.exe.
/// </summary>
public sealed class GitHubFeed(HttpClient http, string repo, Uri apiBase, UrlPolicy policy, Minisign.PublicKey? key)
{
    public async Task<SourceResult> FetchAsync(UpdateState state, CancellationToken ct)
    {
        const UpdateSource S = UpdateSource.GitHub;
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBase, $"repos/{repo}/releases/latest"));
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (state.ETag is { Length: > 0 } etag && state.CachedJson is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp = await BoundedHttp.SendAsync(http, req, policy, UpdateLimits.ApiBytes, ct);
        string json;
        bool cached = false;
        if (resp.Status == HttpStatusCode.NotModified && state.CachedJson is not null) { json = state.CachedJson; cached = true; }
        else if (resp.Status == HttpStatusCode.NotFound)
        {
            state.ETag = null; state.CachedJson = null;
            return new(S, SourceStatus.NoRelease, null, "GitHub has no releases.");
        }
        else if (resp.Body is not null)
        {
            json = Encoding.UTF8.GetString(resp.Body);
            state.ETag = resp.ETag;
            state.CachedJson = json;
        }
        else
            return new(S, SourceStatus.Unavailable, null, (int)resp.Status is 403 or 429 && resp.RateLimited
                ? "GitHub's rate limit was reached; UPS Hub will try again later."
                : $"GitHub answered {(int)resp.Status} {resp.Reason}.");

        var rel = ParseRelease(json);
        if (rel is null) return new(S, SourceStatus.NoRelease, null, "GitHub's latest release has no usable version tag.", cached);
        if (rel.Version.IsPrerelease || rel.Prerelease) return new(S, SourceStatus.NoRelease, null, "GitHub's latest release is a pre-release.", cached);
        var installerName = VerifiedRelease.InstallerNameFor(rel.Version);
        var installer = rel.Assets.FirstOrDefault(a => a.Name == installerName);
        if (installer is null) return Rejected($"GitHub release {rel.Tag} has no {installerName}.");
        if (rel.Checksums is not { } sumsAsset) return Rejected($"GitHub release {rel.Tag} has no {ReleaseInfo.ChecksumsName}, so it can't be verified.");
        if (rel.Signature is not { } sigAsset) return Rejected($"GitHub release {rel.Tag} is not signed ({ReleaseInfo.SignatureName} is missing).");
        if (!policy.Allows(installer.DownloadUrl) || !policy.Allows(sumsAsset.DownloadUrl) || !policy.Allows(sigAsset.DownloadUrl))
            return Rejected($"GitHub release {rel.Tag} points to a download address UPS Hub doesn't trust.");
        if (key is null) return Rejected("No release key is built into this copy of UPS Hub.");

        var sums = await BoundedHttp.GetAsync(http, sumsAsset.DownloadUrl, policy, UpdateLimits.ChecksumsBytes, ct);
        var sig = await BoundedHttp.GetAsync(http, sigAsset.DownloadUrl, policy, UpdateLimits.SignatureBytes, ct);
        if (sums.Body is null || sig.Body is null)
            return new(S, SourceStatus.Unavailable, null, $"GitHub didn't send the checksums of {rel.Tag}.", cached);
        if (!Minisign.Verify(key, sums.Body, Encoding.UTF8.GetString(sig.Body), out var err))
            return Rejected($"GitHub release {rel.Tag} has an invalid signature: " + err);
        if (!Checksums.Parse(Encoding.UTF8.GetString(sums.Body)).TryGetValue(installerName, out var sha))
            return Rejected($"{installerName} is not listed in the signed {ReleaseInfo.ChecksumsName} of {rel.Tag}.");
        var notes = rel.Notes.Length > UpdateLimits.NotesChars ? rel.Notes[..UpdateLimits.NotesChars] : rel.Notes;
        var v = new VerifiedRelease(S, rel.Version, installerName, installer.DownloadUrl, installer.Size, sha, notes,
            rel.HtmlUrl is { } h && policy.Allows(h) ? h : null, rel.Published);
        return new(S, SourceStatus.Found, v, $"GitHub offers {rel.Version}.", cached);

        SourceResult Rejected(string m) => new(S, SourceStatus.Rejected, null, m, cached);
    }

    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) return null;
        var tag = CloudFeed.Str(r, "tag_name");
        if (SemVer.TryParse(tag) is not { } ver) return null;
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (CloudFeed.Str(a, "name") is { } n && Uri.TryCreate(CloudFeed.Str(a, "browser_download_url"), UriKind.Absolute, out var u))
                    assets.Add(new ReleaseAsset(n, u, a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0));
        Uri.TryCreate(CloudFeed.Str(r, "html_url"), UriKind.Absolute, out var html);
        DateTimeOffset? pub = DateTimeOffset.TryParse(CloudFeed.Str(r, "published_at"), out var p) ? p : null;
        bool pre = r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
        return new ReleaseInfo(ver, tag!, CloudFeed.Str(r, "name") is { Length: > 0 } nm ? nm : tag!, CloudFeed.Str(r, "body") ?? "", html, pub, assets) { Prerelease = pre };
    }
}
