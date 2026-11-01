namespace UpsHub.Core.Update;

/// <summary>
/// Public half of the UPS Hub release signing key (minisign format). Updates are installed only if
/// SHA256SUMS.txt carries a valid signature from this key. The private half is held by the maintainer
/// and, for CI, in the UPSHUB_SIGNING_KEY repository secret; it is never in this repository.
/// Verify a release by hand with: minisign -Vm SHA256SUMS.txt -P RWTkuntIbxaEgIdipWngZd/lNqKd4Bvp2H+uqxGQy6/fA2sgF+XcJxZ7
/// </summary>
public static class ReleaseKey
{
    public const string Encoded = "RWTkuntIbxaEgIdipWngZd/lNqKd4Bvp2H+uqxGQy6/fA2sgF+XcJxZ7";
    public static Minisign.PublicKey? Public { get; } = Parse();

    private static Minisign.PublicKey? Parse()
    {
        try { return Minisign.ParsePublicKey(Encoded); } catch (FormatException) { return null; }
    }
}
