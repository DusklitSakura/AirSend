using System.Security.Cryptography;

namespace AirSend.Core.Crypto;

/// <summary>
/// HKDF with SHA-512 (RFC 5869), the KDF AirPlay 2 uses for pair-setup,
/// pair-verify and the RTSP control keys.
/// Port of <c>airplay-crypto/src/hkdf.rs</c>.
/// </summary>
public static class HkdfSha512
{
    public static byte[] DeriveKey(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int outputLength)
    {
        byte[] prk = Extract(inputKeyMaterial, salt);
        return Expand(prk, info, outputLength);
    }

    public static byte[] Extract(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt)
    {
        if (salt.Length == 0)
        {
            // RFC 5869: an absent salt is a string of HashLen zeros.
            salt = new byte[64];
        }

        return Hmac(salt, inputKeyMaterial);
    }

    public static byte[] Expand(ReadOnlySpan<byte> pseudorandomKey, ReadOnlySpan<byte> info, int outputLength)
    {
        const int hashLength = 64;
        if (outputLength <= 0 || outputLength > 255 * hashLength)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        var output = new byte[outputLength];
        byte[] block = [];
        int written = 0;
        byte counter = 1;

        while (written < outputLength)
        {
            var input = new byte[block.Length + info.Length + 1];
            block.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(block.Length));
            input[^1] = counter;

            block = Hmac(pseudorandomKey, input);

            int take = Math.Min(hashLength, outputLength - written);
            block.AsSpan(0, take).CopyTo(output.AsSpan(written));
            written += take;
            counter++;
        }

        return output;
    }

    private static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA512(key.ToArray());
        return hmac.ComputeHash(data.ToArray());
    }

    // Well-known AirPlay salt/info pairs (see hkdf.rs constants).
    public static readonly byte[] PairSetupEncryptSalt = "Pair-Setup-Encrypt-Salt"u8.ToArray();
    public static readonly byte[] PairSetupEncryptInfo = "Pair-Setup-Encrypt-Info"u8.ToArray();
    public static readonly byte[] PairVerifyEncryptSalt = "Pair-Verify-Encrypt-Salt"u8.ToArray();
    public static readonly byte[] PairVerifyEncryptInfo = "Pair-Verify-Encrypt-Info"u8.ToArray();
    public static readonly byte[] ControlSalt = "Control-Salt"u8.ToArray();
    public static readonly byte[] ControlWriteKeyInfo = "Control-Write-Encryption-Key"u8.ToArray();
    public static readonly byte[] ControlReadKeyInfo = "Control-Read-Encryption-Key"u8.ToArray();

    public static byte[] DerivePairSetupKey(ReadOnlySpan<byte> sharedSecret) =>
        DeriveKey(sharedSecret, PairSetupEncryptSalt, PairSetupEncryptInfo, 32);

    public static byte[] DerivePairVerifyKey(ReadOnlySpan<byte> sharedSecret) =>
        DeriveKey(sharedSecret, PairVerifyEncryptSalt, PairVerifyEncryptInfo, 32);

    public static byte[] DeriveControlWriteKey(ReadOnlySpan<byte> sharedSecret) =>
        DeriveKey(sharedSecret, ControlSalt, ControlWriteKeyInfo, 32);

    public static byte[] DeriveControlReadKey(ReadOnlySpan<byte> sharedSecret) =>
        DeriveKey(sharedSecret, ControlSalt, ControlReadKeyInfo, 32);

    /// <summary>SHA-512 of the concatenation, used for the "fruit" legacy key derivation.</summary>
    public static byte[] HashConcat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var sha = SHA512.Create();
        var buffer = new byte[first.Length + second.Length];
        first.CopyTo(buffer);
        second.CopyTo(buffer.AsSpan(first.Length));
        return sha.ComputeHash(buffer);
    }
}
