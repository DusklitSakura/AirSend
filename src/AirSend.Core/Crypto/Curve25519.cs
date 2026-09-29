using System.Numerics;
using System.Security.Cryptography;

namespace AirSend.Core.Crypto;

/// <summary>
/// X25519 (RFC 7748) key agreement, the ECDH half of HomeKit pair-verify.
/// Port of <c>airplay-crypto/src/curve25519.rs</c>; the Rust version uses the
/// <c>x25519-dalek</c> field arithmetic, this one uses <see cref="BigInteger"/>.
/// </summary>
public static class Curve25519
{
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private const int A24 = 121665;

    /// <summary>The u-coordinate of the curve base point (9).</summary>
    public static readonly byte[] BasePoint = BuildBasePoint();

    public static byte[] GeneratePrivateKey() => RandomNumberGenerator.GetBytes(32);

    public static byte[] PublicKeyFromPrivateKey(ReadOnlySpan<byte> privateKey) =>
        X25519(privateKey, BasePoint);

    /// <summary>
    /// Computes the shared secret of <paramref name="scalar"/> and the peer's
    /// u-coordinate using the Montgomery ladder.
    /// </summary>
    public static byte[] X25519(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uCoordinate)
    {
        if (scalar.Length != 32)
        {
            throw new ArgumentException("X25519 scalar must be 32 bytes", nameof(scalar));
        }

        if (uCoordinate.Length != 32)
        {
            throw new ArgumentException("X25519 u-coordinate must be 32 bytes", nameof(uCoordinate));
        }

        byte[] k = scalar.ToArray();
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;

        byte[] u = uCoordinate.ToArray();
        u[31] &= 127;

        BigInteger x1 = BigInts.FromLittleEndian(u);
        BigInteger x2 = 1;
        BigInteger z2 = 0;
        BigInteger x3 = x1;
        BigInteger z3 = 1;
        int swap = 0;

        for (int t = 254; t >= 0; t--)
        {
            int bit = (k[t >> 3] >> (t & 7)) & 1;
            swap ^= bit;
            if (swap == 1)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }

            swap = bit;

            BigInteger a = BigInts.Mod(x2 + z2, P);
            BigInteger aa = BigInts.Mod(a * a, P);
            BigInteger b = BigInts.Mod(x2 - z2, P);
            BigInteger bb = BigInts.Mod(b * b, P);
            BigInteger e = BigInts.Mod(aa - bb, P);
            BigInteger c = BigInts.Mod(x3 + z3, P);
            BigInteger d = BigInts.Mod(x3 - z3, P);
            BigInteger da = BigInts.Mod(d * a, P);
            BigInteger cb = BigInts.Mod(c * b, P);

            x3 = BigInts.Mod((da + cb) * (da + cb), P);
            z3 = BigInts.Mod(x1 * BigInts.Mod((da - cb) * (da - cb), P), P);
            x2 = BigInts.Mod(aa * bb, P);
            z2 = BigInts.Mod(e * BigInts.Mod(aa + A24 * e, P), P);
        }

        if (swap == 1)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }

        if (z2.IsZero)
        {
            return new byte[32];
        }

        BigInteger result = BigInts.Mod(x2 * BigInts.ModInverse(z2, P), P);
        return BigInts.ToLittleEndian(result, 32);
    }

    /// <summary>Fills <paramref name="privateKey"/> with a random scalar and returns its public key.</summary>
    public static byte[] GenerateKeyPair(out byte[] privateKey)
    {
        privateKey = GeneratePrivateKey();
        return PublicKeyFromPrivateKey(privateKey);
    }

    private static byte[] BuildBasePoint()
    {
        var point = new byte[32];
        point[0] = 9;
        return point;
    }
}
