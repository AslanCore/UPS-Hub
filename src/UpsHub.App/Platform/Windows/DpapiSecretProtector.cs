using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using UpsHub.Core.Platform;

namespace UpsHub.App.Platform.Windows;

/// <summary>DPAPI (CurrentUser). A value copied from another PC/user can't be decrypted: we return null and the UI asks to re-enter it.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UpsHub.v1");

    public string Protect(string plaintext) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
