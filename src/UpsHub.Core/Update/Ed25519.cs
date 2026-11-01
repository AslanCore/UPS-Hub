using System.Numerics;
using System.Security.Cryptography;

namespace UpsHub.Core.Update;

/// <summary>
/// Ed25519 (RFC 8032, pure variant), written from the RFC's reference algorithm. Only used to verify
/// one release signature per update (and by the release tool to sign), so clarity beats speed.
/// Verification is not constant-time, which is fine: it handles only public data.
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger Q = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point G;

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);
    private static readonly Point Identity = new(0, 1, 1, 0);

    static Ed25519()
    {
        var gy = Mod(4 * Inv(5));
        var gx = RecoverX(gy, 0)!.Value;
        G = new Point(gx, gy, 1, Mod(gx * gy));
    }

    private static BigInteger Mod(BigInteger x) { var r = x % P; return r.Sign < 0 ? r + P : r; }
    private static BigInteger Inv(BigInteger x) => BigInteger.ModPow(x, P - 2, P);

    private static Point Add(Point a, Point b)
    {
        var A = Mod((a.Y - a.X) * (b.Y - b.X));
        var B = Mod((a.Y + a.X) * (b.Y + b.X));
        var C = Mod(a.T * 2 * D * b.T);
        var Dd = Mod(a.Z * 2 * b.Z);
        BigInteger E = B - A, F = Dd - C, Gg = Dd + C, H = B + A;
        return new Point(Mod(E * F), Mod(Gg * H), Mod(F * Gg), Mod(E * H));
    }

    private static Point Mul(BigInteger s, Point p)
    {
        var q = Identity;
        while (s > 0)
        {
            if (!s.IsEven) q = Add(q, p);
            p = Add(p, p);
            s >>= 1;
        }
        return q;
    }

    private static bool Equal(Point a, Point b) =>
        Mod(a.X * b.Z - b.X * a.Z).IsZero && Mod(a.Y * b.Z - b.Y * a.Z).IsZero;

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2.IsZero) return sign != 0 ? null : BigInteger.Zero;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtM1);
        if (!Mod(x * x - x2).IsZero) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    private static byte[] Encode(Point p)
    {
        var zi = Inv(p.Z);
        var x = Mod(p.X * zi);
        var y = Mod(p.Y * zi);
        var v = y | ((x & 1) << 255);
        return LittleEndian(v);
    }

    private static Point? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != 32) return null;
        var y = new BigInteger(s, isUnsigned: true, isBigEndian: false);
        int sign = (int)(y >> 255);
        y &= (BigInteger.One << 255) - 1;
        var x = RecoverX(y, sign);
        if (x is null) return null;
        return new Point(x.Value, y, 1, Mod(x.Value * y));
    }

    private static byte[] LittleEndian(BigInteger v)
    {
        var b = new byte[32];
        v.TryWriteBytes(b, out _, isUnsigned: true, isBigEndian: false);
        return b;
    }

    private static BigInteger HashModQ(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) h.AppendData(p);
        return new BigInteger(h.GetHashAndReset(), isUnsigned: true, isBigEndian: false) % Q;
    }

    private static (BigInteger A, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("An Ed25519 private key seed is 32 bytes.");
        var h = SHA512.HashData(seed);
        var a = new BigInteger(h.AsSpan(0, 32), isUnsigned: true, isBigEndian: false);
        a &= (BigInteger.One << 254) - 8;
        a |= BigInteger.One << 254;
        return (a, h[32..]);
    }

    public static byte[] PublicKey(ReadOnlySpan<byte> seed) => Encode(Mul(Expand(seed).A, G));

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = Expand(seed);
        var pub = Encode(Mul(a, G));
        var msg = message.ToArray();
        var r = HashModQ(prefix, msg);
        var rs = Encode(Mul(r, G));
        var h = HashModQ(rs, pub, msg);
        var s = (r + h * a) % Q;
        return [.. rs, .. LittleEndian(s)];
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        var A = Decode(publicKey);
        if (A is null) return false;
        var rs = signature[..32].ToArray();
        var R = Decode(rs);
        if (R is null) return false;
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= Q) return false;
        var h = HashModQ(rs, publicKey.ToArray(), message.ToArray());   // SHA-512(R || A || M)
        return Equal(Mul(s, G), Add(R.Value, Mul(h, A.Value)));
    }
}
