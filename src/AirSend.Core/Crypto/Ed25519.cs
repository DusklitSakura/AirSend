using System.Numerics;
using System.Security.Cryptography;

namespace AirSend.Core.Crypto;

/// <summary>
/// Ed25519 signatures (RFC 8032), used as the long term device identity in
/// AirPlay 2 pair-verify. Port of <c>airplay-crypto/src/ed25519.rs</c>, but
/// implemented over <see cref="BigInteger"/> instead of a field backend.
/// The implementation is verified against the RFC 8032 test vectors.
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L =
        (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = BigInts.Mod(-121665 * BigInts.ModInverse(121666, P), P);
    private static readonly BigInteger SqrtMinusOne = BigInts.ModPow(2, (P - 1) / 4, P);
    private static readonly Point Base = CreateBasePoint();

    public const int PublicKeyLength = 32;
    public const int SignatureLength = 64;
    public const int SeedLength = 32;

    /// <summary>Derives the public key from a 32 byte seed (the Ed25519 "private key").</summary>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        ExpandSeed(seed, out BigInteger scalar, out _);
        return Encode(ScalarMultiply(Base, scalar));
    }

    public static byte[] GenerateSeed() => RandomNumberGenerator.GetBytes(SeedLength);

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        if (seed.Length != SeedLength)
        {
            throw new ArgumentException("Ed25519 seed must be 32 bytes", nameof(seed));
        }

        ExpandSeed(seed, out BigInteger scalar, out byte[] prefix);
        byte[] publicKey = Encode(ScalarMultiply(Base, scalar));

        BigInteger r = BigInts.FromLittleEndian(Hash(prefix, message)) % L;
        byte[] encodedR = Encode(ScalarMultiply(Base, r));

        BigInteger k = BigInts.FromLittleEndian(Hash(encodedR, publicKey, message)) % L;
        BigInteger s = BigInts.Mod(r + k * scalar, L);

        var signature = new byte[SignatureLength];
        encodedR.CopyTo(signature, 0);
        BigInts.ToLittleEndian(s, 32).CopyTo(signature, 32);
        return signature;
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength)
        {
            return false;
        }

        Point? a = TryDecode(publicKey);
        Point? r = TryDecode(signature[..32]);
        if (a is null || r is null)
        {
            return false;
        }

        BigInteger s = BigInts.FromLittleEndian(signature[32..]);
        if (s >= L)
        {
            return false;
        }

        BigInteger k = BigInts.FromLittleEndian(Hash(signature[..32], publicKey, message)) % L;
        Point left = ScalarMultiply(Base, s);
        Point right = Add(r.Value, ScalarMultiply(a.Value, k));
        return Encode(left).AsSpan().SequenceEqual(Encode(right));
    }

    private static void ExpandSeed(ReadOnlySpan<byte> seed, out BigInteger scalar, out byte[] prefix)
    {
        if (seed.Length != SeedLength)
        {
            throw new ArgumentException("Ed25519 seed must be 32 bytes", nameof(seed));
        }

        byte[] hash = Hash(seed);
        byte[] clamped = hash[..32];
        clamped[0] &= 248;
        clamped[31] &= 127;
        clamped[31] |= 64;
        scalar = BigInts.FromLittleEndian(clamped);
        prefix = hash[32..];
    }

    private static byte[] Hash(ReadOnlySpan<byte> first) => SHA512.HashData(first);

    private static byte[] Hash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var buffer = new byte[first.Length + second.Length];
        first.CopyTo(buffer);
        second.CopyTo(buffer.AsSpan(first.Length));
        return SHA512.HashData(buffer);
    }

    private static byte[] Hash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third)
    {
        int length = first.Length + second.Length + third.Length;
        var buffer = new byte[length];
        first.CopyTo(buffer);
        second.CopyTo(buffer.AsSpan(first.Length));
        third.CopyTo(buffer.AsSpan(first.Length + second.Length));
        return SHA512.HashData(buffer);
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point CreateBasePoint()
    {
        BigInteger y = BigInts.Mod(4 * BigInts.ModInverse(5, P), P);
        BigInteger x = RecoverX(y, signBit: 0);
        return new Point(x, y, BigInteger.One, BigInts.Mod(x * y, P));
    }

    private static Point Add(Point p, Point q)
    {
        BigInteger a = BigInts.Mod((p.Y - p.X) * (q.Y - q.X), P);
        BigInteger b = BigInts.Mod((p.Y + p.X) * (q.Y + q.X), P);
        BigInteger c = BigInts.Mod(p.T * 2 * D * q.T, P);
        BigInteger d = BigInts.Mod(p.Z * 2 * q.Z, P);
        BigInteger e = BigInts.Mod(b - a, P);
        BigInteger f = BigInts.Mod(d - c, P);
        BigInteger g = BigInts.Mod(d + c, P);
        BigInteger h = BigInts.Mod(b + a, P);

        return new Point(
            BigInts.Mod(e * f, P),
            BigInts.Mod(g * h, P),
            BigInts.Mod(f * g, P),
            BigInts.Mod(e * h, P));
    }

    private static Point Double(Point p)
    {
        BigInteger a = BigInts.Mod(p.X * p.X, P);
        BigInteger b = BigInts.Mod(p.Y * p.Y, P);
        BigInteger c = BigInts.Mod(2 * p.Z * p.Z, P);
        BigInteger d = BigInts.Mod(-a, P);
        BigInteger e = BigInts.Mod(BigInts.Mod((p.X + p.Y) * (p.X + p.Y), P) - a - b, P);
        BigInteger g = BigInts.Mod(d + b, P);
        BigInteger f = BigInts.Mod(g - c, P);
        BigInteger h = BigInts.Mod(d - b, P);

        return new Point(
            BigInts.Mod(e * f, P),
            BigInts.Mod(g * h, P),
            BigInts.Mod(f * g, P),
            BigInts.Mod(e * h, P));
    }

    private static Point ScalarMultiply(Point point, BigInteger scalar)
    {
        Point result = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);
        Point addend = point;

        while (scalar > BigInteger.Zero)
        {
            if (!scalar.IsEven)
            {
                result = Add(result, addend);
            }

            addend = Double(addend);
            scalar >>= 1;
        }

        return result;
    }

    private static byte[] Encode(Point point)
    {
        BigInteger zInverse = BigInts.ModInverse(point.Z, P);
        BigInteger x = BigInts.Mod(point.X * zInverse, P);
        BigInteger y = BigInts.Mod(point.Y * zInverse, P);

        byte[] encoded = BigInts.ToLittleEndian(y, 32);
        if (!x.IsEven)
        {
            encoded[31] |= 0x80;
        }

        return encoded;
    }

    private static Point? TryDecode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 32)
        {
            return null;
        }

        byte[] copy = encoded.ToArray();
        int signBit = copy[31] >> 7;
        copy[31] &= 0x7F;
        BigInteger y = BigInts.FromLittleEndian(copy);
        if (y >= P)
        {
            return null;
        }

        try
        {
            BigInteger x = RecoverX(y, signBit);
            return new Point(x, y, BigInteger.One, BigInts.Mod(x * y, P));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static BigInteger RecoverX(BigInteger y, int signBit)
    {
        BigInteger ySquared = BigInts.Mod(y * y, P);
        BigInteger numerator = BigInts.Mod(ySquared - 1, P);
        BigInteger denominator = BigInts.Mod(D * ySquared + 1, P);
        BigInteger xSquared = BigInts.Mod(numerator * BigInts.ModInverse(denominator, P), P);

        BigInteger x = BigInts.ModPow(xSquared, (P + 3) / 8, P);
        if (BigInts.Mod(x * x - xSquared, P) != BigInteger.Zero)
        {
            x = BigInts.Mod(x * SqrtMinusOne, P);
        }

        if (BigInts.Mod(x * x - xSquared, P) != BigInteger.Zero)
        {
            throw new InvalidOperationException("point is not on the curve");
        }

        if ((int)(x & BigInteger.One) != signBit)
        {
            x = P - x;
        }

        return x;
    }
}
