using System.Text;

namespace UpsHub.Core.Update;

public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// Downloads a release's installer and proves it is the one the release key signed:
/// 1) SHA256SUMS.txt.minisig must be a valid signature of SHA256SUMS.txt by the embedded public key,
/// 2) the installer's SHA-256 must equal its line in SHA256SUMS.txt.
/// Nothing is kept (or run) unless both pass.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, Minisign.PublicKey? releaseKey)
{
    /// <summary>When false (no key embedded) the checksum alone is checked. Release builds always embed a key.</summary>
    public bool RequireSignature { get; init; } = true;

    public async Task<string> DownloadAsync(ReleaseInfo release, string directory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var installer = release.Installer ?? throw new UpdateVerificationException("This release has no Windows installer (UpsHub-Setup-*.exe).");
        var sumsAsset = release.Checksums ?? throw new UpdateVerificationException($"This release has no {ReleaseInfo.ChecksumsName}, so the download can't be verified.");

        var sumsBytes = await http.GetByteArrayAsync(sumsAsset.DownloadUrl, ct);
        if (releaseKey is not null)
        {
            var sigAsset = release.Signature;
            if (sigAsset is null)
            {
                if (RequireSignature) throw new UpdateVerificationException($"This release is not signed ({ReleaseInfo.SignatureName} is missing).");
            }
            else
            {
                var sig = await http.GetStringAsync(sigAsset.DownloadUrl, ct);
                if (!Minisign.Verify(releaseKey, sumsBytes, sig, out var err))
                    throw new UpdateVerificationException("The release signature is not valid: " + err);
            }
        }
        else if (RequireSignature) throw new UpdateVerificationException("No release key is built into this copy of UPS Hub.");

        var sums = Checksums.Parse(Encoding.UTF8.GetString(sumsBytes));
        if (!sums.TryGetValue(installer.Name, out var expected))
            throw new UpdateVerificationException($"{installer.Name} is not listed in {ReleaseInfo.ChecksumsName}.");

        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, installer.Name);
        var part = final + ".part";
        try
        {
            using (var resp = await http.GetAsync(installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? installer.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(part);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }
            var actual = await Checksums.Sha256Async(part, ct);
            if (!Checksums.Matches(expected, actual))
                throw new UpdateVerificationException($"The downloaded installer's SHA-256 ({actual[..12]}…) doesn't match the release ({expected[..12]}…).");
            File.Move(part, final, overwrite: true);
            progress?.Report(1);
            return final;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }
}
