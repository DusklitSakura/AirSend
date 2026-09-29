using System.Numerics;

namespace AirSend.Core.Crypto;

/// <summary>
/// Byte-order helpers shared by the managed Curve25519/Ed25519 implementations.
/// .NET has no built-in X25519/Ed25519, so both are implemented over
/// <see cref="BigInteger"/>: slow compared to a native field implementation but
/// more than fast enough for the handful of operations a pairing handshake needs.
/// </summary>
internal static class BigInts
{
    public static BigInteger FromBigEndian(ReadOnlySpan<byte> data) =>
        new(data, isUnsigned: true, isBigEndian: true);

    public static BigInteger FromLittleEndian(ReadOnlySpan<byte> data)
    {
        Span<byte> reversed = data.Length <= 128 ? stackalloc byte[data.Length] : new byte[data.Length];
        data.CopyTo(reversed);
        reversed.Reverse();
        return new BigInteger(reversed, isUnsigned: true, isBigEndian: true);
    }

    public static byte[] ToBigEndian(BigInteger value, int length)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == length)
        {
            return raw;
        }

        var result = new byte[length];
        if (raw.Length > length)
        {
            Array.Copy(raw, raw.Length - length, result, 0, length);
        }
        else
        {
            Array.Copy(raw, 0, result, length - raw.Length, raw.Length);
        }

        return result;
    }

    public static byte[] ToLittleEndian(BigInteger value, int length)
    {
        byte[] bigEndian = ToBigEndian(value, length);
        Array.Reverse(bigEndian);
        return bigEndian;
    }

    /// <summary>Always returns a value in <c>[0, modulus)</c>, even for negative inputs.</summary>
    public static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        BigInteger result = value % modulus;
        return result.Sign < 0 ? result + modulus : result;
    }

    public static BigInteger ModPow(BigInteger value, BigInteger exponent, BigInteger modulus) =>
        BigInteger.ModPow(Mod(value, modulus), exponent, modulus);

    /// <summary>Modular inverse through the extended Euclidean algorithm.</summary>
    public static BigInteger ModInverse(BigInteger value, BigInteger modulus)
    {
        BigInteger a = Mod(value, modulus);
        BigInteger t = BigInteger.Zero;
        BigInteger newT = BigInteger.One;
        BigInteger r = modulus;
        BigInteger newR = a;

        while (!newR.IsZero)
        {
            BigInteger quotient = r / newR;
            (t, newT) = (newT, t - quotient * newT);
            (r, newR) = (newR, r - quotient * newR);
        }

        if (r > BigInteger.One)
        {
            throw new InvalidOperationException("value is not invertible modulo the given modulus");
        }

        return Mod(t, modulus);
    }

    public static byte[] Concat(params byte[][] parts)
    {
        int length = parts.Sum(p => p.Length);
        var result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
