using System.Net;
using System.Text;
using UpsHub.Core.Update;

namespace UpsHub.App.Services;

public enum DemoReleaseMode { NoReleases, Available, UpToDate, Error }

/// <summary>
/// Demo / screenshots: an in-process stand-in for the GitHub releases API with a throwaway signing
/// key, so the whole check → download → verify path runs for real without touching the network.
/// Nothing is installed (the service runs with DryRunInstall).
/// </summary>
public sealed class DemoGitHub : HttpMessageHandler
{
    private readonly Minisign.SecretKey _key = Minisign.SecretKey.Generate();
    private readonly byte[] _installer;
    private readonly string _sums, _sig;

    public DemoReleaseMode Mode { get; set; } = DemoReleaseMode.Available;
    public string Version { get; set; } = "0.5.0";
    public Minisign.PublicKey PublicKey => _key.Public;
    public static readonly Uri Base = new("https://demo.invalid/");
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(700);

    public DemoGitHub()
    {
        _installer = Encoding.UTF8.GetBytes("UPS Hub demo installer — not a real program\n" + new string('.', 4 << 20));
        _sums = Checksums.Format(new[] { (InstallerName, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_installer)).ToLowerInvariant()) });
        _sig = Minisign.Sign(_key, Encoding.UTF8.GetBytes(_sums), "UPS Hub demo release");
    }

    private string InstallerName => $"UpsHub-Setup-{Version}.exe";

    public string Notes =>
        "### What's new\n- Faster network discovery on large subnets\n- Hibernate countdown shows the remaining battery\n" +
        "- Eaton 5E: input voltage reads correctly\n\n### Fixes\n- Tray tooltip no longer truncates long UPS names\n- Settings import keeps custom ports";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Latency, ct);
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/releases/latest"))
        {
            return Mode switch
            {
                DemoReleaseMode.NoReleases => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") },
                DemoReleaseMode.Error => new HttpResponseMessage(HttpStatusCode.BadGateway) { ReasonPhrase = "Bad Gateway" },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(Mode == DemoReleaseMode.UpToDate ? Core.AppInfo.Version : Version)) },
            };
        }
        var name = Path.GetFileName(path);
        byte[]? body = name == UpdateChecker_ChecksumsName ? Encoding.UTF8.GetBytes(_sums)
            : name == UpdateChecker_SignatureName ? Encoding.UTF8.GetBytes(_sig)
            : name == InstallerName ? _installer : null;
        return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private const string UpdateChecker_ChecksumsName = ReleaseInfo.ChecksumsName;
    private const string UpdateChecker_SignatureName = ReleaseInfo.SignatureName;

    private string ReleaseJson(string version)
    {
        string Asset(string n, long size) => $$"""{ "name": "{{n}}", "size": {{size}}, "browser_download_url": "{{Base}}dl/{{n}}" }""";
        var notes = System.Text.Json.JsonSerializer.Serialize(Notes);
        return $$"""
            { "tag_name": "v{{version}}", "name": "UPS Hub {{version}}", "draft": false, "prerelease": false,
              "html_url": "https://github.com/AslanCore/UPS-Hub/releases/tag/v{{version}}", "published_at": "{{DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}}",
              "body": {{notes}},
              "assets": [ {{Asset(InstallerName, _installer.Length)}}, {{Asset(ReleaseInfo.ChecksumsName, _sums.Length)}}, {{Asset(ReleaseInfo.SignatureName, _sig.Length)}} ] }
            """;
    }
}
