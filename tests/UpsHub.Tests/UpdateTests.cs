using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UpsHub.Core.Update;

namespace UpsHub.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("0.4.0", "0.3.0", 1)]
    [InlineData("v0.4.0", "0.4.0", 0)]
    [InlineData("0.4.0+build.7", "0.4.0", 0)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1", -1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("2", "1.9.9", 1)]
    public void Precedence_follows_semver(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(SemVer.TryParse(a)!.CompareTo(SemVer.TryParse(b)!)));
        Assert.Equal(-sign, Math.Sign(SemVer.TryParse(b)!.CompareTo(SemVer.TryParse(a)!)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    public void Rejects_non_versions(string s) => Assert.Null(SemVer.TryParse(s));
}

public class SignatureTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Ed25519_matches_rfc8032_test_vectors()
    {
        // RFC 8032 §7.1, TEST 1 (empty message) and TEST 2 (one byte).
        var seed1 = H("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        Assert.Equal(H("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), Ed25519.PublicKey(seed1));
        var sig1 = Ed25519.Sign(seed1, ReadOnlySpan<byte>.Empty);
        Assert.Equal(H("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"), sig1);
        Assert.True(Ed25519.Verify(Ed25519.PublicKey(seed1), ReadOnlySpan<byte>.Empty, sig1));

        var seed2 = H("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        var sig2 = Ed25519.Sign(seed2, new byte[] { 0x72 });
        Assert.Equal(H("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"), sig2);
    }

    [Fact]
    public void Ed25519_verifies_an_openssl_signature()
    {
        // Throwaway key made with `openssl genpkey -algorithm ed25519`, signed with `openssl pkeyutl -sign -rawin`.
        var pub = H("03b819260bbc99b2db30f17591a5357f78bd5a7fe8cf0f2f24ecab7542f9f211");
        var sig = H("efd2a2bd8303037abcdd1a7333f8b4db0bb1b6d07f50f8e65e256ffbeb8e8fda72ce091296f0475fad9dd61e7d6ebe835336a4540a1b0566bae1f8a0e13d9204");
        var msg = Encoding.ASCII.GetBytes("hello ups hub");
        Assert.True(Ed25519.Verify(pub, msg, sig));
        Assert.Equal(pub, Ed25519.PublicKey(H("3126aa7c153fc198f2bb7e4c185a243e1eb3a1258e99c14f9fb115e41a573820")));
        msg[0] ^= 1;
        Assert.False(Ed25519.Verify(pub, msg, sig));
    }

    [Fact]
    public void Ed25519_rejects_malformed_input()
    {
        var seed = new byte[32];
        var pub = Ed25519.PublicKey(seed);
        var sig = Ed25519.Sign(seed, "x"u8);
        Assert.False(Ed25519.Verify(pub, "x"u8, sig[..63]));
        var badS = sig.ToArray(); badS[63] = 0xff;               // s >= q
        Assert.False(Ed25519.Verify(pub, "x"u8, badS));
        Assert.False(Ed25519.Verify(new byte[31], "x"u8, sig));
    }

    [Fact]
    public void Blake2b_512_matches_rfc7693()
    {
        Assert.Equal(H("ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923"),
            Blake2b.Hash("abc"u8));
        Assert.Equal(H("786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce"),
            Blake2b.Hash(ReadOnlySpan<byte>.Empty));
        // Multi-block input, streamed in odd-sized pieces, equals the one-shot hash.
        var big = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var b = new Blake2b();
        foreach (var chunk in big.Chunk(37)) b.Update(chunk);
        Assert.Equal(Blake2b.Hash(big), b.Final());
        Assert.Equal(Blake2b.Hash(big), Blake2b.Hash(new MemoryStream(big)));
    }

    [Fact]
    public void Minisign_round_trip_and_tamper_detection()
    {
        var key = Minisign.SecretKey.Generate();
        var pub = Minisign.ParsePublicKey(Minisign.PublicKeyFileText(key.Public));
        Assert.Equal(key.Public.Encoded, pub.Encoded);
        Assert.StartsWith("RW", pub.Encoded);
        var data = Encoding.UTF8.GetBytes("0123  UpsHub-Setup-0.5.0.exe\n");
        var sig = Minisign.Sign(key, data, "timestamp:1\tfile:SHA256SUMS.txt\thashed");
        Assert.True(Minisign.Verify(pub, data, sig, out var err), err);

        var other = Minisign.SecretKey.Generate().Public;
        Assert.False(Minisign.Verify(other, data, sig, out err));
        Assert.Contains("different key", err);

        var changed = data.ToArray(); changed[0] = (byte)'9';
        Assert.False(Minisign.Verify(pub, changed, sig, out err));

        var forgedComment = sig.Replace("timestamp:1", "timestamp:2");
        Assert.False(Minisign.Verify(pub, data, forgedComment, out err));
        Assert.Contains("trusted comment", err);

        Assert.False(Minisign.Verify(pub, data, "not a signature", out _));
        Assert.Equal(key.Encoded, Minisign.SecretKey.Parse(key.ToFileText()).Encoded);
    }

    [Fact]
    public void Verifies_signatures_made_by_the_minisign_cli()
    {
        // Throwaway key and signatures produced by minisign 0.12 (`-GW`, `-S`, and legacy `-S -l`).
        var pub = Minisign.ParsePublicKey("untrusted comment: minisign public key 18AE2CFCB4606CA5\nRWSlbGC0/CyuGNPfdT6ptGgA8y8CFmJ69IV/wqVLfe2IHDRxNmM+nkgI\n");
        Assert.Equal("18AE2CFCB4606CA5", pub.KeyIdHex);
        var msg = Encoding.UTF8.GetBytes("abc123  UpsHub-Setup-9.9.9.exe\n");
        const string hashed = """
            untrusted comment: signature from minisign secret key
            RUSlbGC0/CyuGE0RRr8s1Z+/DejfKI8KiyYZOpgGcj33987swEvxuBTh3hE+Q0RmbvlSLmUsDkpkQDLM38Bm2Mkfv8wjO6jshAQ=
            trusted comment: minisign cli interop
            MI5stLeFsaPk02jT9zOOhwoWRbduBajEv1dPIQszTtD2bprmYap3jpxULwBFq7Jgk131A8Pkzlym9u7V6NYHDQ==
            """;
        const string legacy = """
            untrusted comment: signature from minisign secret key
            RWSlbGC0/CyuGINngrPy3Qxr3crZYHL/9uhNUi7YSiDdn/CxP0TCg+KI0suJKzEiSZL6olZ+71xT6Eet83AKBzF4u0JDGYD5VAk=
            trusted comment: legacy
            X/dmnnwlU3+wng6oSGB6s0tCsA2SoSrP7SVibssID+HwnW6UmebMZXTAkrb5XwrP728YI5hZExHUTEvAroDICA==
            """;
        Assert.True(Minisign.Verify(pub, msg, hashed, out var e1), e1);
        Assert.True(Minisign.Verify(pub, msg, legacy, out var e2), e2);
        Assert.False(Minisign.Verify(pub, Encoding.UTF8.GetBytes("abc124  UpsHub-Setup-9.9.9.exe\n"), hashed, out _));
    }

    [Fact]
    public void Embedded_release_key_parses()
    {
        var k = ReleaseKey.Public;
        Assert.NotNull(k);
        Assert.Equal(8, k!.KeyId.Length);
        Assert.Equal(32, k.Key.Length);
    }

    [Fact]
    public void Checksums_parse_sha256sum_output()
    {
        var d = Checksums.Parse("""
            # comment
            9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  UpsHub-Setup-0.5.0.exe
            E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855 *dist/UpsHub-0.5.0-win-x64.zip
            garbage line
            """);
        Assert.Equal(2, d.Count);
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", d["upshub-setup-0.5.0.exe"]);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", d["UpsHub-0.5.0-win-x64.zip"]);
        Assert.True(Checksums.Matches(d["UpsHub-0.5.0-win-x64.zip"], "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
    }
}

/// <summary>A tiny HTTP server standing in for api.github.com and the release download host.</summary>
internal sealed class FakeGitHub : IAsyncDisposable
{
    private readonly HttpListener _l = new();
    private readonly CancellationTokenSource _cts = new();
    public Uri Base { get; }
    public Dictionary<string, Func<HttpListenerRequest, (int Status, byte[] Body, string? ETag)>> Routes { get; } = new();
    public List<string> Log { get; } = new();

    public FakeGitHub()
    {
        int port;
        using (var s = new TcpListener(IPAddress.Loopback, 0)) { s.Start(); port = ((IPEndPoint)s.LocalEndpoint).Port; }
        Base = new Uri($"http://127.0.0.1:{port}/");
        _l.Prefixes.Add(Base.ToString());
        _l.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            lock (Log) Log.Add($"{path} inm={ctx.Request.Headers["If-None-Match"]}");
            var (status, body, etag) = Routes.TryGetValue(path, out var h) ? h(ctx.Request) : (404, "{\"message\":\"Not Found\"}"u8.ToArray(), null);
            ctx.Response.StatusCode = status;
            if (etag is not null) ctx.Response.Headers["ETag"] = etag;
            if (status == 403) ctx.Response.Headers["x-ratelimit-remaining"] = "0";
            if (status != 304) { ctx.Response.ContentLength64 = body.Length; await ctx.Response.OutputStream.WriteAsync(body); }
            ctx.Response.Close();
        }
    }

    public static (int, byte[], string?) Ok(string s, string? etag = null) => (200, Encoding.UTF8.GetBytes(s), etag);
    public static (int, byte[], string?) Ok(byte[] b) => (200, b, null);

    public string ReleaseJson(string tag, params string[] assets) => $$"""
        { "tag_name": "{{tag}}", "name": "UPS Hub {{tag}}", "draft": false, "prerelease": false,
          "html_url": "https://github.com/cyberwill01/UPS-Hub/releases/tag/{{tag}}", "published_at": "2026-11-20T10:00:00Z",
          "body": "### Changes\n- Faster discovery\n- Fixes",
          "assets": [ {{string.Join(",", assets.Select(a => $$"""{ "name": "{{a}}", "size": 0, "browser_download_url": "{{Base}}dl/{{a}}" }"""))}} ] }
        """;

    public async ValueTask DisposeAsync() { _cts.Cancel(); _l.Stop(); _l.Close(); await Task.CompletedTask; }
}

public class UpdateCheckerTests
{
    private const string Repo = "cyberwill01/UPS-Hub";
    private static readonly SemVer Current = SemVer.TryParse("0.4.0")!;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task No_releases_yet_is_reported_gracefully()
    {
        await using var gh = new FakeGitHub();   // every path 404s, like a repo with no releases
        using var http = new HttpClient();
        var state = new UpdateState();
        var r = await new UpdateChecker(http, Repo, gh.Base).CheckAsync(Current, state, manual: true);
        Assert.Equal(UpdateCheckOutcome.NoReleases, r.Outcome);
        Assert.Null(r.Release);
        Assert.Contains("No releases", r.Message);
        Assert.NotNull(state.LastCheckUtc);
        Assert.Contains("/repos/cyberwill01/UPS-Hub/releases/latest", gh.Log[0]);
    }

    [Fact]
    public async Task Newer_release_is_found_and_etag_is_reused()
    {
        await using var gh = new FakeGitHub();
        int hits = 0;
        gh.Routes["/repos/cyberwill01/UPS-Hub/releases/latest"] = req =>
        {
            hits++;
            if (req.Headers["If-None-Match"] == "\"abc\"") return (304, Array.Empty<byte>(), "\"abc\"");
            return FakeGitHub.Ok(gh.ReleaseJson("v0.5.0", "UpsHub-Setup-0.5.0.exe", "SHA256SUMS.txt", "SHA256SUMS.txt.minisig"), "\"abc\"");
        };
        using var http = new HttpClient();
        var state = new UpdateState();
        var checker = new UpdateChecker(http, Repo, gh.Base);

        var r1 = await checker.CheckAsync(Current, state, manual: false);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, r1.Outcome);
        Assert.Equal("0.5.0", r1.Release!.Version.ToString());
        Assert.Equal("UpsHub-Setup-0.5.0.exe", r1.Release.Installer!.Name);
        Assert.Contains("Faster discovery", r1.Release.Notes);
        Assert.Equal("\"abc\"", state.ETag);

        // Round-trip the state through its JSON store, like a restart.
        var dir = Path.Combine(Path.GetTempPath(), "upshub-upd-" + Guid.NewGuid().ToString("n"));
        var store = new UpdateStateStore(dir);
        store.Save(state);
        var reloaded = store.Load();

        var r2 = await checker.CheckAsync(Current, reloaded, manual: true);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, r2.Outcome);
        Assert.True(r2.FromCache);
        Assert.Equal(2, hits);
        Assert.Contains("inm=\"abc\"", gh.Log[1]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Same_or_older_release_is_up_to_date_and_skipped_versions_stay_quiet()
    {
        await using var gh = new FakeGitHub();
        string tag = "v0.4.0";
        gh.Routes["/repos/cyberwill01/UPS-Hub/releases/latest"] = _ => FakeGitHub.Ok(gh.ReleaseJson(tag));
        using var http = new HttpClient();
        var checker = new UpdateChecker(http, Repo, gh.Base);
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckAsync(Current, new UpdateState(), false)).Outcome);

        tag = "v0.5.0";
        var state = new UpdateState { SkippedVersion = "0.5.0" };
        Assert.Equal(UpdateCheckOutcome.Skipped, (await checker.CheckAsync(Current, state, manual: false)).Outcome);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await checker.CheckAsync(Current, state, manual: true)).Outcome);
        tag = "v0.5.1";
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await checker.CheckAsync(Current, state, manual: false)).Outcome);
    }

    [Fact]
    public async Task Rate_limit_and_unreachable_server_fail_softly()
    {
        await using var gh = new FakeGitHub();
        gh.Routes["/repos/cyberwill01/UPS-Hub/releases/latest"] = _ => (403, "{}"u8.ToArray(), null);
        using var http = new HttpClient();
        var r = await new UpdateChecker(http, Repo, gh.Base).CheckAsync(Current, new UpdateState(), true);
        Assert.Equal(UpdateCheckOutcome.Failed, r.Outcome);
        Assert.Contains("rate limit", r.Message);

        var dead = new Uri("http://127.0.0.1:1/");
        var r2 = await new UpdateChecker(http, Repo, dead).CheckAsync(Current, new UpdateState(), true);
        Assert.Equal(UpdateCheckOutcome.Failed, r2.Outcome);
    }

    [Fact]
    public void Auto_check_runs_at_most_once_a_day()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var c = new UpdateChecker(new HttpClient(), Repo, time: clock);
        var s = new UpdateState();
        Assert.True(c.IsDue(s));
        s.LastCheckUtc = clock.Now;
        clock.Now = clock.Now.AddHours(23);
        Assert.False(c.IsDue(s));
        clock.Now = clock.Now.AddHours(1);
        Assert.True(c.IsDue(s));
    }

    [Fact]
    public void Repo_is_read_from_the_project_url() =>
        Assert.Equal("cyberwill01/UPS-Hub", UpdateChecker.RepoFromUrl("https://github.com/cyberwill01/UPS-Hub"));
}

public class UpdateDownloaderTests
{
    private static async Task<(FakeGitHub Gh, ReleaseInfo Release, Minisign.SecretKey Key)> Setup(
        byte[] installer, bool sign = true, Func<string, string>? tamperSums = null, Minisign.SecretKey? signWith = null)
    {
        var gh = new FakeGitHub();
        var key = Minisign.SecretKey.Generate();
        var sums = Checksums.Format(new[] { ("UpsHub-Setup-0.5.0.exe", Convert.ToHexStringLower(SHA256.HashData(installer))) });
        sums = tamperSums?.Invoke(sums) ?? sums;
        var sumBytes = Encoding.UTF8.GetBytes(sums);
        gh.Routes["/dl/UpsHub-Setup-0.5.0.exe"] = _ => FakeGitHub.Ok(installer);
        gh.Routes["/dl/SHA256SUMS.txt"] = _ => FakeGitHub.Ok(sumBytes);
        var assets = new List<string> { "UpsHub-Setup-0.5.0.exe", "SHA256SUMS.txt" };
        if (sign)
        {
            var sig = Minisign.Sign(signWith ?? key, Encoding.UTF8.GetBytes(Checksums.Format(new[] { ("UpsHub-Setup-0.5.0.exe", Convert.ToHexStringLower(SHA256.HashData(installer))) })), "timestamp:1\tfile:SHA256SUMS.txt");
            gh.Routes["/dl/SHA256SUMS.txt.minisig"] = _ => FakeGitHub.Ok(sig);
            assets.Add("SHA256SUMS.txt.minisig");
        }
        var rel = UpdateChecker.ParseRelease(gh.ReleaseJson("v0.5.0", assets.ToArray()))!;
        await Task.CompletedTask;
        return (gh, rel, key);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "upshub-dl-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task Verified_installer_is_downloaded()
    {
        var installer = RandomNumberGenerator.GetBytes(300_000);
        var (gh, rel, key) = await Setup(installer);
        await using var _ = gh;
        using var http = new HttpClient();
        var dir = TempDir();
        var progress = new List<double>();
        var path = await new UpdateDownloader(http, key.Public).DownloadAsync(rel, dir, new SyncProgress(progress.Add));
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, progress.Last());
        Assert.False(File.Exists(path + ".part"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Checksum_mismatch_is_rejected_and_nothing_is_kept()
    {
        var installer = RandomNumberGenerator.GetBytes(50_000);
        var (gh, rel, key) = await Setup(installer, sign: false);
        await using var _ = gh;
        gh.Routes["/dl/UpsHub-Setup-0.5.0.exe"] = _ => FakeGitHub.Ok(RandomNumberGenerator.GetBytes(50_000));   // swapped binary
        using var http = new HttpClient();
        var dir = TempDir();
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            new UpdateDownloader(http, key.Public) { RequireSignature = false }.DownloadAsync(rel, dir));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Tampered_checksums_fail_the_signature()
    {
        var installer = RandomNumberGenerator.GetBytes(10_000);
        var (gh, rel, key) = await Setup(installer, tamperSums: s => "0000000000000000000000000000000000000000000000000000000000000000" + s[64..]);
        await using var _ = gh;
        using var http = new HttpClient();
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(http, key.Public).DownloadAsync(rel, TempDir()));
        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public async Task Signature_from_another_key_or_missing_signature_is_rejected()
    {
        var installer = RandomNumberGenerator.GetBytes(10_000);
        var (gh, rel, key) = await Setup(installer, signWith: Minisign.SecretKey.Generate());
        await using (gh)
        {
            using var http = new HttpClient();
            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(http, key.Public).DownloadAsync(rel, TempDir()));
            Assert.Contains("different key", ex.Message);
        }
        var (gh2, rel2, key2) = await Setup(installer, sign: false);
        await using (gh2)
        {
            using var http = new HttpClient();
            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(http, key2.Public).DownloadAsync(rel2, TempDir()));
            Assert.Contains("not signed", ex.Message);
        }
    }

    [Fact]
    public async Task Release_without_installer_or_checksums_is_explained()
    {
        await using var gh = new FakeGitHub();
        using var http = new HttpClient();
        var key = Minisign.SecretKey.Generate();
        var noInstaller = UpdateChecker.ParseRelease(gh.ReleaseJson("v0.5.0", "SHA256SUMS.txt"))!;
        Assert.Contains("no Windows installer", (await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(http, key.Public).DownloadAsync(noInstaller, TempDir()))).Message);
        var noSums = UpdateChecker.ParseRelease(gh.ReleaseJson("v0.5.0", "UpsHub-Setup-0.5.0.exe"))!;
        Assert.Contains("SHA256SUMS", (await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(http, key.Public).DownloadAsync(noSums, TempDir()))).Message);
    }

    private sealed class SyncProgress(Action<double> a) : IProgress<double> { public void Report(double v) => a(v); }
}
