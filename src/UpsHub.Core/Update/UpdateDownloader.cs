using System.Security.Cryptography;

namespace UpsHub.Core.Update;

public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// Downloads the installer of a <see cref="VerifiedRelease"/> and keeps it only if its size and SHA-256 are
/// exactly what the release key vouched for. The file is written under a temporary name and moved into
/// place only after it checks out, so a half or wrong download is never left where it could run.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, UpdateFeedOptions options)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);

    public async Task<string> DownloadAsync(VerifiedRelease rel, string directory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var name = VerifiedRelease.InstallerNameFor(rel.Version);
        if (rel.InstallerName != name) throw new UpdateVerificationException("The installer name doesn't match the release version.");
        var policy = options.PolicyFor(rel.Source);
        if (!policy.Allows(rel.InstallerUrl)) throw new UpdateVerificationException("The installer address is not approved.");
        if (rel.InstallerSha256 is not { Length: 64 } || !rel.InstallerSha256.All(Uri.IsHexDigit)) throw new UpdateVerificationException("The release has no valid SHA-256.");
        long cap = rel.InstallerSize > 0 ? rel.InstallerSize : UpdateLimits.InstallerBytes;
        if (cap > UpdateLimits.InstallerBytes) throw new UpdateVerificationException("The installer is larger than UPS Hub accepts.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, name);
        var part = Path.Combine(directory, $"{name}.{Guid.NewGuid():n}.part");
        try
        {
            string actual;
            long done = 0;
            using (var resp = await http.GetAsync(rel.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                if (!policy.Allows(resp.RequestMessage?.RequestUri)) throw new UpdateVerificationException("The download was redirected to an address UPS Hub doesn't trust.");
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is { } len && len > cap) throw new UpdateVerificationException("The download is larger than the release says.");
                long total = rel.InstallerSize > 0 ? rel.InstallerSize : resp.Content.Headers.ContentLength ?? 0;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token))
                await using (var dst = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
                    {
                        done += n;
                        if (done > cap) throw new UpdateVerificationException("The download is larger than the release says.");
                        sha.AppendData(buf, 0, n);
                        await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                        if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                    }
                    await dst.FlushAsync(cts.Token);
                }
                actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (rel.InstallerSize > 0 && done != rel.InstallerSize)
                throw new UpdateVerificationException($"The download is {done} bytes; the release says {rel.InstallerSize}.");
            if (!Checksums.Matches(rel.InstallerSha256, actual))
                throw new UpdateVerificationException($"The downloaded installer's SHA-256 ({actual[..12]}…) doesn't match the release ({rel.InstallerSha256[..12].ToLowerInvariant()}…).");
            File.Move(part, final, overwrite: true);
            progress?.Report(1);
            return final;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateVerificationException("The download took too long and was stopped.");
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    /// <summary>Re-hash a downloaded installer right before it runs.</summary>
    public static async Task<bool> StillMatchesAsync(string path, VerifiedRelease rel, CancellationToken ct = default)
    {
        try
        {
            if (Path.GetFileName(path) != VerifiedRelease.InstallerNameFor(rel.Version)) return false;
            if (rel.InstallerSize > 0 && new FileInfo(path).Length != rel.InstallerSize) return false;
            return Checksums.Matches(rel.InstallerSha256, await Checksums.Sha256Async(path, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return false; }
    }
}
