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

