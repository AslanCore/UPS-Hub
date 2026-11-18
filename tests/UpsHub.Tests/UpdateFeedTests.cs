using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UpsHub.Core.Model;
using UpsHub.Core.Shutdown;
using UpsHub.Core.Update;

namespace UpsHub.Tests;

/// <summary>A tiny HTTP server standing in for AslanCore Cloud, api.github.com and the download hosts.</summary>
internal sealed class FakeGitHub : IAsyncDisposable
{
    private readonly HttpListener _l = new();
    private readonly CancellationTokenSource _cts = new();
    public Uri Base { get; }
    public int Port { get; }
    public Dictionary<string, Func<HttpListenerRequest, (int Status, byte[] Body, string? ETag)>> Routes { get; } = new();
    public List<string> Log { get; } = new();

    public FakeGitHub()
    {
        using (var s = new TcpListener(IPAddress.Loopback, 0)) { s.Start(); Port = ((IPEndPoint)s.LocalEndpoint).Port; }
        Base = new Uri($"http://127.0.0.1:{Port}/");
        _l.Prefixes.Add(Base.ToString());
        _l.Prefixes.Add($"http://localhost:{Port}/");
        _l.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(ctx));   // one slow route must not hold up the others
        }
    }

    private async Task Serve(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            lock (Log) Log.Add($"{path} inm={ctx.Request.Headers["If-None-Match"]}");
            Func<HttpListenerRequest, (int, byte[], string?)>? h;
            lock (Routes) Routes.TryGetValue(path, out h);
            var (status, body, etag) = h is not null ? h(ctx.Request) : (404, "{\"message\":\"Not Found\"}"u8.ToArray(), null);
            ctx.Response.StatusCode = status;
            if (etag is not null) ctx.Response.Headers["ETag"] = etag;
            if (status == 403) ctx.Response.Headers["x-ratelimit-remaining"] = "0";
            if (status is 301 or 302) { ctx.Response.RedirectLocation = Encoding.UTF8.GetString(body); body = Array.Empty<byte>(); }
            if (status != 304) { ctx.Response.ContentLength64 = body.Length; await ctx.Response.OutputStream.WriteAsync(body); }
            ctx.Response.Close();
        }
        catch { }
    }

    public void Route(string path, Func<HttpListenerRequest, (int, byte[], string?)> h) { lock (Routes) Routes[path] = h; }
    public int Hits(string path) { lock (Log) return Log.Count(l => l.StartsWith(path + " ")); }

    public static (int, byte[], string?) Ok(string s, string? etag = null) => (200, Encoding.UTF8.GetBytes(s), etag);
    public static (int, byte[], string?) Ok(byte[] b) => (200, b, null);

    public string ReleaseJson(string tag, IEnumerable<string> assets, bool prerelease = false) => $$"""
        { "tag_name": "{{tag}}", "name": "UPS Hub {{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "{{Base}}releases/tag/{{tag}}", "published_at": "2026-11-20T10:00:00Z",
          "body": "### Changes\n- Faster discovery\n- Fixes",
          "assets": [ {{string.Join(",", assets.Select(a => $$"""{ "name": "{{a}}", "size": 0, "browser_download_url": "{{Base}}gh/{{tag}}/{{a}}" }"""))}} ] }
        """;

    public async ValueTask DisposeAsync() { _cts.Cancel(); _l.Stop(); _l.Close(); await Task.CompletedTask; }
}

/// <summary>Both update sources on one fake host, signed with a throwaway release key.</summary>
internal sealed class Feeds : IAsyncDisposable
{
    public FakeGitHub Host { get; } = new();
    public Minisign.SecretKey Key { get; } = Minisign.SecretKey.Generate();
    public static readonly UrlPolicy Loopback = new(false, "127.0.0.1");
    public const string ApiPath = "/repos/AslanCore/UPS-Hub/releases/latest";
    public const string ManifestPath = "/upshub/stable/manifest.json";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public UpdateFeedOptions Options => new()
    {
        CloudManifest = new Uri(Host.Base, ManifestPath.TrimStart('/')), CloudPolicy = Loopback,
        GitHubRepo = "AslanCore/UPS-Hub", GitHubApi = Host.Base, GitHubPolicy = Loopback,
        Key = Key.Public, SourceTimeout = Timeout,
    };

    public static byte[] Bytes(int n = 40_000) => RandomNumberGenerator.GetBytes(n);
    public static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    public string ManifestJson(string version, byte[] installer, string? name = null, string? url = null, string product = "AslanCore.UpsHub",
        string channel = "stable", long? size = null, string? sha = null)
    {
        name ??= $"UpsHub-Setup-{version}.exe";
        url ??= $"{Host.Base}cloud/{version}/{name}";
        return $$"""
            { "schema": 1, "product": "{{product}}", "channel": "{{channel}}", "version": "{{version}}", "published": "2026-11-21T09:00:00Z",
              "installer": { "name": "{{name}}", "url": "{{url}}", "size": {{size ?? installer.Length}}, "sha256": "{{sha ?? Sha(installer)}}" } }
            """;
    }

    /// <summary>Publish a Cloud release: installer, manifest and its signature (by <paramref name="signWith"/>, default the release key).</summary>
    public byte[] Cloud(string version, byte[]? installer = null, string? json = null, Minisign.SecretKey? signWith = null, bool sign = true,
        Func<byte[], byte[]>? tamperAfterSigning = null)
    {
        installer ??= Bytes();
        var inst = installer;
        Host.Route($"/cloud/{version}/UpsHub-Setup-{version}.exe", _ => FakeGitHub.Ok(inst));
        var manifest = Encoding.UTF8.GetBytes(json ?? ManifestJson(version, installer));
        var sig = Minisign.Sign(signWith ?? Key, manifest, "timestamp:1\tfile:manifest.json\thashed");
        var served = tamperAfterSigning?.Invoke(manifest) ?? manifest;
        Host.Route(ManifestPath, _ => FakeGitHub.Ok(served));
        if (sign) Host.Route(ManifestPath + ".minisig", _ => FakeGitHub.Ok(sig));
        return installer;
    }

    /// <summary>Publish a GitHub release with installer, SHA256SUMS.txt and its signature.</summary>
    public byte[] GitHub(string version, byte[]? installer = null, string? tag = null, string? sumsInstallerName = null, Minisign.SecretKey? signWith = null,
        bool sign = true, bool prerelease = false, string? assetName = null, Func<string, string>? tamperSums = null)
    {
        installer ??= Bytes();
        tag ??= "v" + version;
        var name = assetName ?? $"UpsHub-Setup-{version}.exe";
        var sums = Checksums.Format(new[] { (sumsInstallerName ?? name, Sha(installer)) });
        var sig = Minisign.Sign(signWith ?? Key, Encoding.UTF8.GetBytes(sums), "timestamp:1\tfile:SHA256SUMS.txt\thashed");
        var served = tamperSums?.Invoke(sums) ?? sums;
        var inst = installer;
        Host.Route($"/gh/{tag}/{name}", _ => FakeGitHub.Ok(inst));
        Host.Route($"/gh/{tag}/SHA256SUMS.txt", _ => FakeGitHub.Ok(served));
        var assets = new List<string> { name, "SHA256SUMS.txt" };
        if (sign) { Host.Route($"/gh/{tag}/SHA256SUMS.txt.minisig", _ => FakeGitHub.Ok(sig)); assets.Add("SHA256SUMS.txt.minisig"); }
        var json = Host.ReleaseJson(tag, assets, prerelease);
        Host.Route(ApiPath, _ => FakeGitHub.Ok(json));
        return installer;
    }

    public Task<UpdateCheckResult> Check(string current, UpdateState? state = null, bool manual = true) =>
        new UpdateChecker(new HttpClient(), Options).CheckAsync(SemVer.TryParse(current)!, state ?? new UpdateState(), manual);

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

public class UpdateSourceSelectionTests
{
    [Fact]
    public async Task Cloud_newer_than_GitHub_wins()
    {
        await using var f = new Feeds();
        f.Cloud("0.7.0");
        f.GitHub("0.6.0");
        var r = await f.Check("0.5.0");
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, r.Outcome);
        Assert.Equal(UpdateSource.Cloud, r.Release!.Source);
        Assert.Equal("0.7.0", r.Release.Version.ToString());
        Assert.Contains("AslanCore Cloud", r.Message);
    }

    [Fact]
    public async Task Newer_GitHub_release_beats_a_stale_Cloud_manifest()
    {
        await using var f = new Feeds();
        f.Cloud("0.6.0");
        var inst = f.GitHub("0.7.0");
        var r = await f.Check("0.5.0");
        Assert.Equal(UpdateSource.GitHub, r.Release!.Source);
        Assert.Equal("0.7.0", r.Release.Version.ToString());
        Assert.Equal(Feeds.Sha(inst), r.Release.InstallerSha256);
        Assert.EndsWith("/gh/v0.7.0/UpsHub-Setup-0.7.0.exe", r.Release.InstallerUrl.AbsolutePath);
    }

    [Fact]
    public async Task Equal_versions_prefer_Cloud()
    {
        await using var f = new Feeds();
        f.Cloud("0.6.0");
        f.GitHub("0.6.0");
        var r = await f.Check("0.5.0");
        Assert.Equal(UpdateSource.Cloud, r.Release!.Source);
    }

    [Fact]
    public async Task Versions_compare_numerically_not_as_text()
    {
        await using var f = new Feeds();
        f.Cloud("0.9.0");
        f.GitHub("0.10.0");
        var r = await f.Check("0.5.0");
        Assert.Equal("0.10.0", r.Release!.Version.ToString());
        Assert.Equal(UpdateSource.GitHub, r.Release.Source);
    }

    [Fact]
    public async Task Both_older_or_equal_is_up_to_date()
    {
        await using var f = new Feeds();
        f.Cloud("0.5.0");
        f.GitHub("0.4.0");
        var r = await f.Check("0.5.0");
        Assert.Equal(UpdateCheckOutcome.UpToDate, r.Outcome);
        Assert.Equal("0.5.0", r.Release!.Version.ToString());
    }

    [Fact]
    public async Task Hanging_GitHub_never_holds_back_a_newer_Cloud_release()
    {
        await using var f = new Feeds { Timeout = TimeSpan.FromSeconds(1) };
        f.Cloud("0.7.0");
        f.Host.Route(Feeds.ApiPath, _ => { Thread.Sleep(8000); return (500, Array.Empty<byte>(), null); });
        var sw = Stopwatch.StartNew();
        var r = await f.Check("0.5.0");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, r.Outcome);
        Assert.Equal(UpdateSource.Cloud, r.Release!.Source);
        Assert.Contains(r.Sources, s => s.Source == UpdateSource.GitHub && s.Status == SourceStatus.Unavailable && s.Message.Contains("in time"));
    }

    [Fact]
    public async Task Unreachable_Cloud_falls_back_to_GitHub_and_both_down_fails_softly()
    {
        await using var f = new Feeds();
        f.Host.Route(Feeds.ManifestPath, _ => (503, Array.Empty<byte>(), null));
        f.GitHub("0.6.0");
        var r = await f.Check("0.5.0");
        Assert.Equal(UpdateSource.GitHub, r.Release!.Source);

        f.Host.Route(Feeds.ApiPath, _ => (403, "{}"u8.ToArray(), null));
        var r2 = await f.Check("0.5.0");
        Assert.Equal(UpdateCheckOutcome.Failed, r2.Outcome);
        Assert.Null(r2.Release);
        Assert.Contains("rate limit", r2.Message);
        Assert.Contains("AslanCore Cloud answered 503", r2.Message);

        var dead = new UpdateChecker(new HttpClient(), f.Options with { CloudManifest = new Uri("http://127.0.0.1:1/m.json"), GitHubApi = new Uri("http://127.0.0.1:1/") });
        Assert.Equal(UpdateCheckOutcome.Failed, (await dead.CheckAsync(SemVer.TryParse("0.5.0")!, new UpdateState(), true)).Outcome);
    }

    [Fact]
    public async Task Nothing_published_anywhere_is_reported_gracefully()
    {
        await using var f = new Feeds();
        var state = new UpdateState();
        var r = await f.Check("0.5.0", state);
        Assert.Equal(UpdateCheckOutcome.NoReleases, r.Outcome);
        Assert.NotNull(state.LastCheckUtc);
    }

    [Fact]
    public async Task Badly_signed_Cloud_claim_is_ignored_and_the_verified_GitHub_release_is_used()
    {
        await using var f = new Feeds();
        f.Cloud("9.9.9", signWith: Minisign.SecretKey.Generate());   // attacker key
        f.GitHub("0.6.0");
        var r = await f.Check("0.5.0");
        Assert.Equal("0.6.0", r.Release!.Version.ToString());
        Assert.Equal(UpdateSource.GitHub, r.Release.Source);
        Assert.Contains("invalid signature", r.Message);
        Assert.Contains(r.Sources, s => s.Source == UpdateSource.Cloud && s.Status == SourceStatus.Rejected);
    }

    [Fact]
    public async Task Manifest_edited_after_signing_or_unsigned_is_rejected()
    {
        await using var f = new Feeds();
        f.Cloud("0.6.0", tamperAfterSigning: m => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(m).Replace("0.6.0", "0.6.1")));
        var r = await f.Check("0.5.0");
        Assert.Equal(UpdateCheckOutcome.Failed, r.Outcome);
        Assert.Contains("invalid signature", r.Message);

        await using var g = new Feeds();
        g.Cloud("0.6.0", sign: false);
        var r2 = await g.Check("0.5.0");
        Assert.Null(r2.Release);
        Assert.Contains("not signed", r2.Message);
    }

    [Fact]
    public async Task Invalid_GitHub_signatures_are_rejected_and_Cloud_still_answers()
    {
        await using var f = new Feeds();
        f.Cloud("0.6.0");
        f.GitHub("0.8.0", signWith: Minisign.SecretKey.Generate());
        var r = await f.Check("0.5.0");
        Assert.Equal(UpdateSource.Cloud, r.Release!.Source);
        Assert.Contains("different key", r.Message);

        f.GitHub("0.8.0", sign: false);
        Assert.Contains("not signed", (await f.Check("0.5.0")).Message);

        f.GitHub("0.8.0", tamperSums: s => new string('0', 64) + s[64..]);
        var r3 = await f.Check("0.5.0");
        Assert.Equal("0.6.0", r3.Release!.Version.ToString());
        Assert.Contains("invalid signature", r3.Message);
    }

    [Fact]
    public async Task Relabelled_old_GitHub_release_cannot_pose_as_a_new_version()
    {
        await using var f = new Feeds();
        // Genuine, signed 0.4.0 files re-published under tag v9.0.0: the signed sums only vouch for UpsHub-Setup-0.4.0.exe.
        f.GitHub("9.0.0", sumsInstallerName: "UpsHub-Setup-0.4.0.exe");
        var r = await f.Check("0.5.0");
        Assert.Null(r.Release);
        Assert.Contains("not listed in the signed", r.Message);

        // Installer asset named for another version than the tag.
        await using var g = new Feeds();
        g.GitHub("9.0.0", assetName: "UpsHub-Setup-0.4.0.exe");
        Assert.Contains("has no UpsHub-Setup-9.0.0.exe", (await g.Check("0.5.0")).Message);
    }

    [Fact]
    public async Task Prereleases_and_other_channels_are_not_offered()
    {
        await using var f = new Feeds();
        var inst = Feeds.Bytes();
        f.Cloud("1.0.0-rc.1", inst, json: f.ManifestJson("1.0.0-rc.1", inst));
        f.GitHub("1.0.0-rc.1");
        var r = await f.Check("0.5.0");
        Assert.Null(r.Release);
        Assert.Contains("pre-releases", r.Message);

        await using var g = new Feeds();
        var i2 = Feeds.Bytes();
        g.Cloud("0.7.0", i2, json: g.ManifestJson("0.7.0", i2, channel: "beta"));
        g.GitHub("0.8.0", prerelease: true);
        var r2 = await g.Check("0.5.0");
        Assert.Null(r2.Release);
        Assert.Contains("stable channel", r2.Message);
        Assert.Contains(r2.Sources, s => s.Source == UpdateSource.GitHub && s.Status == SourceStatus.NoRelease);
    }

    [Fact]
    public async Task GitHub_etag_is_reused_across_restarts()
    {
        await using var f = new Feeds();
        f.GitHub("0.6.0");
        var json = f.Host.ReleaseJson("v0.6.0", new[] { "UpsHub-Setup-0.6.0.exe", "SHA256SUMS.txt", "SHA256SUMS.txt.minisig" });
        f.Host.Route(Feeds.ApiPath, req => req.Headers["If-None-Match"] == "\"abc\"" ? (304, Array.Empty<byte>(), "\"abc\"") : FakeGitHub.Ok(json, "\"abc\""));
        var state = new UpdateState();
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await f.Check("0.5.0", state)).Outcome);
        Assert.Equal("\"abc\"", state.ETag);

        var dir = Path.Combine(Path.GetTempPath(), "upshub-upd-" + Guid.NewGuid().ToString("n"));
        var store = new UpdateStateStore(dir);
        store.Save(state);
        var r2 = await f.Check("0.5.0", store.Load());
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, r2.Outcome);
        Assert.Contains(r2.Sources, s => s.Source == UpdateSource.GitHub && s.FromCache);
        Assert.Equal(2, f.Host.Hits(Feeds.ApiPath));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Skipped_version_stays_quiet_until_a_newer_one()
    {
        await using var f = new Feeds();
        f.Cloud("0.6.0");
        var state = new UpdateState { SkippedVersion = "0.6.0" };
        Assert.Equal(UpdateCheckOutcome.Skipped, (await f.Check("0.5.0", state, manual: false)).Outcome);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await f.Check("0.5.0", state, manual: true)).Outcome);
        f.Cloud("0.6.1");
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await f.Check("0.5.0", state, manual: false)).Outcome);
    }

    [Fact]
    public async Task Redirects_to_unapproved_hosts_and_oversized_answers_are_rejected()
    {
        await using var f = new Feeds();
        f.Host.Route(Feeds.ManifestPath, _ => (302, Encoding.UTF8.GetBytes($"http://localhost:{f.Host.Port}/elsewhere.json"), null));
        f.Host.Route("/elsewhere.json", _ => FakeGitHub.Ok("{}"));
        var r = await f.Check("0.5.0");
        Assert.Contains(r.Sources, s => s.Source == UpdateSource.Cloud && s.Status == SourceStatus.Rejected && s.Message.Contains("redirected"));

        f.Host.Route(Feeds.ManifestPath, _ => FakeGitHub.Ok(new string(' ', UpdateLimits.ManifestBytes + 10)));
        var r2 = await f.Check("0.5.0");
        Assert.Contains(r2.Sources, s => s.Source == UpdateSource.Cloud && s.Status == SourceStatus.Rejected && s.Message.Contains("more data"));
    }

    [Fact]
    public void Repo_is_read_from_the_project_url()
    {
        Assert.Equal("AslanCore/UPS-Hub", UpdateChecker.RepoFromUrl("https://github.com/AslanCore/UPS-Hub"));
        Assert.Null(UpdateChecker.RepoFromUrl("https://notgithub.com/AslanCore/UPS-Hub"));
    }

    [Fact]
    public void Auto_check_runs_at_most_once_a_day()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var c = new UpdateChecker(new HttpClient(), time: clock);
        var s = new UpdateState();
        Assert.True(c.IsDue(s));
        s.LastCheckUtc = clock.Now;
        clock.Now = clock.Now.AddHours(23);
        Assert.False(c.IsDue(s));
        clock.Now = clock.Now.AddHours(1);
        Assert.True(c.IsDue(s));
    }

    [Fact]
    public void Default_feeds_are_https_on_the_approved_hosts()
    {
        var o = new UpdateFeedOptions();
        Assert.True(o.CloudPolicy.Allows(o.CloudManifest));
        Assert.True(o.CloudPolicy.Allows(UpdateEndpoints.SignatureOf(o.CloudManifest!)));
        Assert.EndsWith("manifest.json.minisig", UpdateEndpoints.SignatureOf(o.CloudManifest!).AbsolutePath);
        Assert.Equal(ReleaseKey.Public!.Encoded, o.Key!.Encoded);
        Assert.Equal("AslanCore/UPS-Hub", o.GitHubRepo);
    }
}

internal sealed class Clock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class UpdatePolicyTests
{
    private static VerifiedRelease R(UpdateSource s, string v) =>
        new(s, SemVer.TryParse(v)!, $"UpsHub-Setup-{v}.exe", new Uri($"https://x/{v}"), 1, new string('a', 64), "", null, null);
    private static SourceResult Found(UpdateSource s, string v) => new(s, SourceStatus.Found, R(s, v), "");

    [Theory]
    [InlineData("0.7.0", "0.6.0", UpdateSource.Cloud)]
    [InlineData("0.6.0", "0.7.0", UpdateSource.GitHub)]
    [InlineData("0.6.0", "0.6.0", UpdateSource.Cloud)]
    [InlineData("0.9.0", "0.10.0", UpdateSource.GitHub)]
    [InlineData("1.0.0", "1.0.0-rc.2", UpdateSource.Cloud)]
    public void Select_takes_the_highest_verified_version(string cloud, string gh, UpdateSource expected)
    {
        var r = UpdateChecker.Select(SemVer.TryParse("0.5.0")!, new[] { Found(UpdateSource.Cloud, cloud), Found(UpdateSource.GitHub, gh) }, null, true);
        Assert.Equal(expected, r.Release!.Source);
    }

    [Fact]
    public void Select_ignores_rejected_and_unavailable_sources()
    {
        var r = UpdateChecker.Select(SemVer.TryParse("0.5.0")!, new[]
        {
            new SourceResult(UpdateSource.Cloud, SourceStatus.Rejected, null, "bad sig"),
            new SourceResult(UpdateSource.GitHub, SourceStatus.Unavailable, null, "down"),
        }, null, true);
        Assert.Equal(UpdateCheckOutcome.Failed, r.Outcome);
        Assert.Null(r.Release);
    }

    [Fact]
    public void Url_policy_requires_https_approved_hosts_and_no_credentials()
    {
        var p = UrlPolicy.GitHub;
        Assert.True(p.Allows(new Uri("https://github.com/AslanCore/UPS-Hub/releases/download/v1/x.exe")));
        Assert.True(p.Allows(new Uri("https://release-assets.githubusercontent.com/x")));
        Assert.False(p.Allows(new Uri("https://githubusercontent.com.evil.example/x")));
        Assert.False(p.Allows(new Uri("https://evilgithubusercontent.com/x")));
        Assert.False(p.Allows(new Uri("http://github.com/x")));
        Assert.False(p.Allows(new Uri("https://user:pw@github.com/x")));
        Assert.True(UrlPolicy.Cloud.Allows(new Uri($"https://{UpdateEndpoints.BlobHost}/upshub/0.6.0/UpsHub-Setup-0.6.0.exe")));
        Assert.False(UrlPolicy.Cloud.Allows(new Uri("https://other.public.blob.vercel-storage.com/x")));
        Assert.False(UrlPolicy.Cloud.Allows(new Uri("file:///C:/x.exe")));
    }

    [Theory]
    [InlineData("name", "UpsHub-Setup-0.6.1.exe", "name doesn't match")]
    [InlineData("url", "https://evil.example/UpsHub-Setup-0.6.0.exe", "not approved")]
    [InlineData("url", "https://" + UpdateEndpoints.BlobHost + "/upshub/0.6.0/other.exe", "doesn't match its name")]
    [InlineData("product", "Someone.Else", "different product")]
    [InlineData("size", "0", "invalid installer size")]
    [InlineData("sha", "xyz", "invalid installer SHA-256")]
    public void Manifest_fields_must_be_consistent(string field, string value, string expected)
    {
        var inst = new byte[10];
        string name = "UpsHub-Setup-0.6.0.exe", url = $"https://{UpdateEndpoints.BlobHost}/upshub/0.6.0/UpsHub-Setup-0.6.0.exe", product = "AslanCore.UpsHub", sha = Feeds.Sha(inst);
        long size = 10;
        switch (field)
        {
            case "name": name = value; break;
            case "url": url = value; break;
            case "product": product = value; break;
            case "size": size = long.Parse(value); break;
            case "sha": sha = value; break;
        }
        var json = $$"""{ "schema": 1, "product": "{{product}}", "channel": "stable", "version": "0.6.0", "installer": { "name": "{{name}}", "url": "{{url}}", "size": {{size}}, "sha256": "{{sha}}" } }""";
        var ex = Assert.Throws<UpdateVerificationException>(() => CloudFeed.ParseManifest(Encoding.UTF8.GetBytes(json), UrlPolicy.Cloud, "AslanCore.UpsHub"));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Valid_manifest_parses()
    {
        var json = $$"""{ "schema": 1, "product": "AslanCore.UpsHub", "channel": "stable", "version": "0.6.0", "published": "2026-11-07T20:00:00Z", "installer": { "name": "UpsHub-Setup-0.6.0.exe", "url": "https://{{UpdateEndpoints.BlobHost}}/upshub/0.6.0/UpsHub-Setup-0.6.0.exe", "size": 123, "sha256": "{{new string('A', 64)}}" } }""";
        var r = CloudFeed.ParseManifest(Encoding.UTF8.GetBytes(json), UrlPolicy.Cloud, "AslanCore.UpsHub");
        Assert.Equal("0.6.0", r.Version.ToString());
        Assert.Equal(123, r.InstallerSize);
        Assert.Equal(new string('a', 64), r.InstallerSha256);
        Assert.Null(r.HtmlUrl);
        Assert.Throws<UpdateVerificationException>(() => CloudFeed.ParseManifest(Encoding.UTF8.GetBytes(json.Replace("\"schema\": 1", "\"schema\": 2")), UrlPolicy.Cloud, "AslanCore.UpsHub"));
        Assert.Throws<UpdateVerificationException>(() => CloudFeed.ParseManifest("not json"u8.ToArray(), UrlPolicy.Cloud, "AslanCore.UpsHub"));
    }

    private static InstallConditions Ok(params UpsStatus[] s) =>
        new(true, InstallScope.PerUser, true, ShutdownPhase.Idle, false, false, s.Length == 0 ? new[] { UpsStatus.OL } : s);

    [Fact]
    public void Install_waits_for_mains_power_and_no_pending_shutdown()
    {
        Assert.Null(AutoInstallPolicy.BlockReason(Ok()));
        Assert.Null(AutoInstallPolicy.BlockReason(Ok(UpsStatus.OL | UpsStatus.CHRG)));
        Assert.Contains("mains", AutoInstallPolicy.BlockReason(Ok(UpsStatus.OB | UpsStatus.DISCHRG)));
        Assert.Contains("mains", AutoInstallPolicy.BlockReason(Ok(UpsStatus.OL, UpsStatus.OL | UpsStatus.LB)));
        Assert.Contains("mains", AutoInstallPolicy.BlockReason(Ok(UpsStatus.FSD | UpsStatus.OL)));
        Assert.Contains("countdown", AutoInstallPolicy.BlockReason(Ok() with { ShutdownPhase = ShutdownPhase.CountingDown }));
        Assert.Contains("countdown", AutoInstallPolicy.BlockReason(Ok() with { ShutdownPhase = ShutdownPhase.Executing }));
        Assert.Contains("countdown", AutoInstallPolicy.BlockReason(Ok() with { ShutdownTest = true }));
        Assert.Contains("countdown", AutoInstallPolicy.BlockReason(Ok() with { ShutdownActionRunning = true }));
    }

    [Fact]
    public void Portable_other_scope_and_non_Windows_copies_are_never_replaced()
    {
        Assert.Contains("portable", AutoInstallPolicy.BlockReason(Ok() with { Scope = InstallScope.Portable }));
        Assert.Contains("administrator", AutoInstallPolicy.BlockReason(Ok() with { Scope = InstallScope.Other }));
        Assert.Contains("administrator", AutoInstallPolicy.BlockReason(Ok() with { DirectoryWritable = false }));
        Assert.Contains("Windows build", AutoInstallPolicy.BlockReason(Ok() with { IsWindows = false }));
    }

    [Fact]
    public void Failed_installs_are_retried_at_most_twice_six_hours_apart()
    {
        var v = SemVer.TryParse("0.6.0")!;
        var s = new UpdateState();
        var t = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        Assert.True(AutoInstallPolicy.MayAutoAttempt(s, v, t));
        AutoInstallPolicy.RecordAttempt(s, v, t);
        Assert.False(AutoInstallPolicy.MayAutoAttempt(s, v, t.AddHours(1)));
        Assert.True(AutoInstallPolicy.MayAutoAttempt(s, v, t.AddHours(6)));
        AutoInstallPolicy.RecordAttempt(s, v, t.AddHours(6));
        Assert.False(AutoInstallPolicy.MayAutoAttempt(s, v, t.AddDays(3)));
        Assert.True(AutoInstallPolicy.MayAutoAttempt(s, SemVer.TryParse("0.6.1")!, t.AddDays(3)));   // a newer fix starts fresh
        Assert.Contains("didn't finish", AutoInstallPolicy.Reconcile(s, SemVer.TryParse("0.5.0")!));
        Assert.Equal("0.6.0", s.PendingVersion);
        Assert.Equal("Updated to 0.6.0.", AutoInstallPolicy.Reconcile(s, v));
        Assert.Null(s.PendingVersion);
        Assert.Null(AutoInstallPolicy.Reconcile(s, v));
    }

    [Fact]
    public void Installer_arguments_are_fixed_switches()
    {
        Assert.Equal("/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /RELAUNCH=tray /LOG=\"C:\\u\\install.log\"",
            UpdateManager.InstallerArguments(true, true, "C:\\u\\install.log"));
        Assert.Equal("/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /RELAUNCH=1",
            UpdateManager.InstallerArguments(false, false, "bad\"path"));
    }
}

public class UpdateDownloaderTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "upshub-dl-" + Guid.NewGuid().ToString("n"));

    private static async Task<(Feeds F, VerifiedRelease R, byte[] Inst)> Cloud(string v = "0.6.0")
    {
        var f = new Feeds();
        var inst = f.Cloud(v, Feeds.Bytes(300_000));
        var r = (await f.Check("0.5.0")).Release!;
        return (f, r, inst);
    }

    [Fact]
    public async Task Verified_installer_is_downloaded_and_rechecked()
    {
        var (f, rel, inst) = await Cloud();
        await using var _ = f;
        var dir = TempDir();
        var progress = new List<double>();
        var path = await new UpdateDownloader(new HttpClient(), f.Options).DownloadAsync(rel, dir, new SyncProgress(progress.Add));
        Assert.Equal(inst, await File.ReadAllBytesAsync(path));
        Assert.Equal("UpsHub-Setup-0.6.0.exe", Path.GetFileName(path));
        Assert.Equal(1, progress.Last());
        Assert.Single(Directory.GetFiles(dir));
        Assert.True(await UpdateDownloader.StillMatchesAsync(path, rel));
        await File.WriteAllBytesAsync(path, Feeds.Bytes(300_000));   // swapped on disk
        Assert.False(await UpdateDownloader.StillMatchesAsync(path, rel));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Swapped_or_resized_binary_is_rejected_and_nothing_is_kept()
    {
        var (f, rel, _) = await Cloud();
        await using var _f = f;
        var dir = TempDir();
        f.Host.Route("/cloud/0.6.0/UpsHub-Setup-0.6.0.exe", _ => FakeGitHub.Ok(Feeds.Bytes(300_000)));
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(new HttpClient(), f.Options).DownloadAsync(rel, dir));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));

        f.Host.Route("/cloud/0.6.0/UpsHub-Setup-0.6.0.exe", _ => FakeGitHub.Ok(Feeds.Bytes(300_001)));
        ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(new HttpClient(), f.Options).DownloadAsync(rel, dir));
        Assert.Contains("larger", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Download_redirected_off_the_approved_hosts_is_refused()
    {
        var (f, rel, inst) = await Cloud();
        await using var _f = f;
        f.Host.Route("/cloud/0.6.0/UpsHub-Setup-0.6.0.exe", _ => (302, Encoding.UTF8.GetBytes($"http://localhost:{f.Host.Port}/x/UpsHub-Setup-0.6.0.exe"), null));
        f.Host.Route("/x/UpsHub-Setup-0.6.0.exe", _ => FakeGitHub.Ok(inst));
        var dir = TempDir();
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(new HttpClient(), f.Options).DownloadAsync(rel, dir));
        Assert.Contains("redirected", ex.Message);
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
    }

    [Fact]
    public async Task Release_records_that_dont_match_are_refused_before_downloading()
    {
        var (f, rel, _) = await Cloud();
        await using var _f = f;
        var d = new UpdateDownloader(new HttpClient(), f.Options);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => d.DownloadAsync(rel with { InstallerName = "..\\evil.exe" }, TempDir()));
        await Assert.ThrowsAsync<UpdateVerificationException>(() => d.DownloadAsync(rel with { InstallerUrl = new Uri("http://localhost:1/UpsHub-Setup-0.6.0.exe") }, TempDir()));
        await Assert.ThrowsAsync<UpdateVerificationException>(() => d.DownloadAsync(rel with { Source = UpdateSource.GitHub, InstallerUrl = new Uri("https://example.com/UpsHub-Setup-0.6.0.exe") }, TempDir()));
    }

    [Fact]
    public async Task GitHub_release_downloads_against_its_signed_checksum()
    {
        await using var f = new Feeds();
        var inst = f.GitHub("0.6.0", Feeds.Bytes(120_000));
        var rel = (await f.Check("0.5.0")).Release!;
        var dir = TempDir();
        var path = await new UpdateDownloader(new HttpClient(), f.Options).DownloadAsync(rel, dir);
        Assert.Equal(inst, await File.ReadAllBytesAsync(path));
        Directory.Delete(dir, true);
    }

    private sealed class SyncProgress(Action<double> a) : IProgress<double> { public void Report(double v) => a(v); }
}

public class UpdateLifecycleTests
{
    private sealed class FakeLauncher : IInstallerLauncher
    {
        public List<(string Path, string Args)> Runs { get; } = new();
        public string? Fail { get; set; }
        public Task<string?> StartAsync(string installerPath, string arguments, CancellationToken ct)
        {
            Runs.Add((installerPath, arguments));
            return Task.FromResult(Fail);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Feeds F { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "upshub-life-" + Guid.NewGuid().ToString("n"));
        public InstallConditions Conditions = new(true, InstallScope.PerUser, true, ShutdownPhase.Idle, false, false, new[] { UpsStatus.OL });
        public Clock Clock { get; } = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        public int Quits;
        public bool AutoInstall = true;
        public bool DryRun;

        public UpdateManager Make(string current = "0.5.0")
        {
            var http = new HttpClient();
            var m = new UpdateManager(new UpdateChecker(http, F.Options, Clock), new UpdateDownloader(http, F.Options), new UpdateStateStore(Dir),
                Launcher, () => Conditions, SemVer.TryParse(current)!, Path.Combine(Dir, "updates"), Clock)
            { AutoInstallEnabled = () => AutoInstall, DryRun = DryRun };
            m.QuitForInstall = () => Quits++;
            return m;
        }

        public async ValueTask DisposeAsync()
        {
            await F.DisposeAsync();
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Newer_verified_update_installs_silently_and_quits_once()
    {
        await using var rig = new Rig();
        rig.F.Cloud("0.6.0");
        var m = rig.Make();
        await m.AutoTickAsync();
        var run = Assert.Single(rig.Launcher.Runs);
        Assert.EndsWith("UpsHub-Setup-0.6.0.exe", run.Path);
        Assert.StartsWith("/VERYSILENT ", run.Args);
        Assert.Contains("/RELAUNCH=tray", run.Args);
        Assert.Equal(1, rig.Quits);
        Assert.Equal(UpdatePhase.Installing, m.Phase);
        Assert.Equal("0.6.0", new UpdateStateStore(rig.Dir).Load().PendingVersion);

        // Next start on the new version: the record is cleared, nothing runs again.
        var after = rig.Make("0.6.0");
        Assert.Equal("Updated to 0.6.0.", after.Message);
        await after.AutoTickAsync();
        Assert.Single(rig.Launcher.Runs);
        Assert.Null(new UpdateStateStore(rig.Dir).Load().PendingVersion);
    }

    [Fact]
    public async Task On_battery_the_install_waits_and_resumes_when_mains_returns()
    {
        await using var rig = new Rig();
        rig.F.Cloud("0.6.0");
        rig.Conditions = rig.Conditions with { LastKnownStatuses = new[] { UpsStatus.OB | UpsStatus.DISCHRG } };
        var m = rig.Make();
        await m.AutoTickAsync();
        Assert.Empty(rig.Launcher.Runs);
        Assert.Equal(0, rig.Quits);
        Assert.Contains("mains", m.Message);

        rig.Conditions = rig.Conditions with { LastKnownStatuses = new[] { UpsStatus.OL } };
        await m.AutoTickAsync();
        Assert.Single(rig.Launcher.Runs);
        Assert.Equal(1, rig.Quits);
        Assert.Equal(1, rig.F.Host.Hits(Feeds.ManifestPath));   // one check a day, the retry only re-evaluates
    }

    [Fact]
    public async Task Installer_that_wont_start_keeps_the_app_running_and_is_not_retried_in_a_loop()
    {
        await using var rig = new Rig();
        rig.F.Cloud("0.6.0");
        rig.Launcher.Fail = "it closed straight away (exit code 1).";
        var m = rig.Make();
        await m.AutoTickAsync();
        Assert.Equal(UpdatePhase.Failed, m.Phase);
        Assert.Equal(0, rig.Quits);
        await m.AutoTickAsync();
        await m.AutoTickAsync();
        Assert.Single(rig.Launcher.Runs);

        rig.Clock.Now = rig.Clock.Now.AddHours(7);
        await m.AutoTickAsync();
        Assert.Equal(2, rig.Launcher.Runs.Count);
        rig.Clock.Now = rig.Clock.Now.AddDays(3);
        await m.AutoTickAsync();
        Assert.Equal(2, rig.Launcher.Runs.Count);   // gave up after two tries
        Assert.Equal(0, rig.Quits);
    }

    [Fact]
    public async Task Installer_changed_on_disk_after_verification_is_deleted_not_run()
    {
        await using var rig = new Rig();
        rig.F.Cloud("0.6.0");
        var m = rig.Make();
        await m.CheckAsync(manual: false);
        rig.Conditions = rig.Conditions with { ShutdownPhase = ShutdownPhase.CountingDown };
        await m.InstallAsync(automatic: false);
        Assert.Empty(rig.Launcher.Runs);
        Assert.Contains("countdown", m.Message);

        // Downloaded while waiting? Not yet: the gate runs before the download. Allow it, then block after download.
        rig.Conditions = rig.Conditions with { ShutdownPhase = ShutdownPhase.Idle };
        var path = Path.Combine(rig.Dir, "updates", "UpsHub-Setup-0.6.0.exe");
        rig.Launcher.Fail = "stop";
        await m.InstallAsync(automatic: false);
        Assert.True(File.Exists(path));
        await File.WriteAllBytesAsync(path, Feeds.Bytes());
        rig.Launcher.Fail = null;
        await m.InstallAsync(automatic: false);
        Assert.Single(rig.Launcher.Runs);   // only the first (failed) launch; the tampered file never ran
        Assert.False(File.Exists(path));
        Assert.Contains("no longer matches", m.Message);
    }

    [Fact]
    public async Task Older_or_same_versions_are_never_installed()
    {
        await using var rig = new Rig();
        rig.F.Cloud("0.6.0");
        rig.F.GitHub("0.5.9");
        var m = rig.Make("0.7.0");
        await m.AutoTickAsync();
        await m.InstallAsync(automatic: false);
        Assert.Empty(rig.Launcher.Runs);
        Assert.Equal(UpdatePhase.UpToDate, m.Phase);
    }

    [Fact]
    public async Task Portable_copy_and_demo_mode_never_start_an_installer()
    {
        await using var rig = new Rig();
        rig.F.Cloud("0.6.0");
        rig.Conditions = rig.Conditions with { Scope = InstallScope.Portable };
        var m = rig.Make();
        await m.AutoTickAsync();
        Assert.Empty(rig.Launcher.Runs);
        Assert.Contains("portable", m.Message);
        Assert.False(Directory.Exists(Path.Combine(rig.Dir, "updates")) && Directory.GetFiles(Path.Combine(rig.Dir, "updates")).Length > 0);

        await using var demo = new Rig { DryRun = true };
        demo.F.Cloud("0.6.0");
        var d = demo.Make();
        await d.AutoTickAsync();
        Assert.Empty(demo.Launcher.Runs);
        Assert.Equal(0, demo.Quits);
        Assert.Contains("Demo mode", d.Message);
    }

    [Fact]
    public async Task Failed_download_is_not_retried_every_tick()
    {
        await using var rig = new Rig();
        rig.F.Cloud("0.6.0");
        rig.F.Host.Route("/cloud/0.6.0/UpsHub-Setup-0.6.0.exe", _ => FakeGitHub.Ok(Feeds.Bytes()));   // wrong bytes
        var m = rig.Make();
        await m.AutoTickAsync();
        Assert.Equal(UpdatePhase.Failed, m.Phase);
        Assert.Contains("not installed", m.Message);
        await m.AutoTickAsync();
        await m.AutoTickAsync();
        Assert.Equal(1, rig.F.Host.Hits("/cloud/0.6.0/UpsHub-Setup-0.6.0.exe"));
        Assert.Empty(rig.Launcher.Runs);
    }

    [Fact]
    public async Task With_automatic_install_off_the_user_is_told_and_nothing_downloads()
    {
        await using var rig = new Rig { AutoInstall = false };
        rig.F.Cloud("0.6.0");
        var m = rig.Make();
        VerifiedRelease? told = null;
        m.FoundForUser += r => told = r;
        await m.AutoTickAsync();
        Assert.Equal("0.6.0", told?.Version.ToString());
        Assert.Equal(UpdatePhase.Available, m.Phase);
        Assert.Equal(0, rig.F.Host.Hits("/cloud/0.6.0/UpsHub-Setup-0.6.0.exe"));

        await m.InstallAsync(automatic: false);   // the user clicks Install
        var run = Assert.Single(rig.Launcher.Runs);
        Assert.StartsWith("/SILENT ", run.Args);
    }
}

public class UpdateCoverageTests
{
    [Fact]
    public async Task GitHub_release_without_checksums_or_installer_is_explained()
    {
        await using var f = new Feeds();
        f.Host.Route(Feeds.ApiPath, _ => FakeGitHub.Ok(f.Host.ReleaseJson("v0.6.0", new[] { "UpsHub-Setup-0.6.0.exe", "SHA256SUMS.txt.minisig" })));
        Assert.Contains("can't be verified", (await f.Check("0.5.0")).Message);
        f.Host.Route(Feeds.ApiPath, _ => FakeGitHub.Ok(f.Host.ReleaseJson("v0.6.0", new[] { "SHA256SUMS.txt", "SHA256SUMS.txt.minisig" })));
        Assert.Contains("has no UpsHub-Setup-0.6.0.exe", (await f.Check("0.5.0")).Message);
    }

    [Fact]
    public async Task Handover_is_postponed_and_not_counted_when_the_UPS_goes_on_battery_meanwhile()
    {
        await using var f = new Feeds();
        f.Cloud("0.6.0");
        var dir = Path.Combine(Path.GetTempPath(), "upshub-pp-" + Guid.NewGuid().ToString("n"));
        var cond = new InstallConditions(true, InstallScope.PerUser, true, ShutdownPhase.Idle, false, false, new[] { UpsStatus.OL });
        var launcher = new BatteryOnLaunch(() => cond = cond with { LastKnownStatuses = new[] { UpsStatus.OB } });
        var http = new HttpClient();
        var quits = 0;
        var m = new UpdateManager(new UpdateChecker(http, f.Options), new UpdateDownloader(http, f.Options), new UpdateStateStore(dir),
            launcher, () => cond, SemVer.TryParse("0.5.0")!, Path.Combine(dir, "updates")) { QuitForInstall = () => quits++ };
        await m.AutoTickAsync();
        Assert.Equal(1, launcher.Runs);
        Assert.Equal(0, quits);
        Assert.Contains("postponed", m.Message);
        Assert.Null(new UpdateStateStore(dir).Load().PendingVersion);
        Directory.Delete(dir, true);
    }

    private sealed class BatteryOnLaunch(Action onLaunch) : IInstallerLauncher
    {
        public int Runs;
        public Task<string?> StartAsync(string p, string a, CancellationToken ct) { Runs++; onLaunch(); return Task.FromResult<string?>(null); }
    }
}

/// <summary>The real process launcher, with real child processes (the Setup mutex probe is the only stand-in).</summary>
public class InstallerLauncherTests
{
    private static string Script(string body)
    {
        var p = Path.Combine(Path.GetTempPath(), $"upshub-fake-setup-{Guid.NewGuid():n}.sh");
        File.WriteAllText(p, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return p;
    }

    [Fact]
    public async Task Setup_that_exits_at_once_is_reported_with_its_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;
        var l = new UpsHub.App.Services.ProcessInstallerLauncher { SetupRunning = () => false, StartTimeout = TimeSpan.FromSeconds(10) };
        var err = await l.StartAsync(Script("exit 3"), "", CancellationToken.None);
        Assert.Contains("exit code 3", err);
    }

    [Fact]
    public async Task Running_setup_is_confirmed_only_while_our_process_is_alive()
    {
        if (OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(Path.GetTempPath(), $"upshub-mutex-{Guid.NewGuid():n}");
        var l = new UpsHub.App.Services.ProcessInstallerLauncher { SetupRunning = () => File.Exists(marker), StartTimeout = TimeSpan.FromSeconds(10) };
        Assert.Null(await l.StartAsync(Script($"touch '{marker}'; sleep 5"), "", CancellationToken.None));

        // Mutex appears but the process we started is already gone: not ours, not confirmed.
        File.Delete(marker);
        var err = await l.StartAsync(Script($"touch '{marker}'; exit 0"), "", CancellationToken.None);
        Assert.NotNull(err);
        File.Delete(marker);
    }

    [Fact]
    public async Task Another_setup_already_running_blocks_the_handover()
    {
        var l = new UpsHub.App.Services.ProcessInstallerLauncher { SetupRunning = () => true };
        Assert.Contains("already running", await l.StartAsync("/nonexistent", "", CancellationToken.None));
    }

    [Fact]
    public async Task Setup_that_never_shows_up_is_stopped_after_the_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var pidFile = Path.Combine(Path.GetTempPath(), $"upshub-pid-{Guid.NewGuid():n}");
        var l = new UpsHub.App.Services.ProcessInstallerLauncher { SetupRunning = () => false, StartTimeout = TimeSpan.FromSeconds(2) };
        var err = await l.StartAsync(Script($"echo $$ > '{pidFile}'; sleep 60"), "", CancellationToken.None);
        Assert.Contains("stopped", err);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        await Task.Delay(300);
        Assert.False(System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid && !p.HasExited));
        File.Delete(pidFile);
    }

    [Fact]
    public async Task Missing_installer_file_is_an_error_not_a_crash()
    {
        var l = new UpsHub.App.Services.ProcessInstallerLauncher { SetupRunning = () => false };
        Assert.NotNull(await l.StartAsync("/nonexistent/UpsHub-Setup-9.9.9.exe", "", CancellationToken.None));
    }
}
