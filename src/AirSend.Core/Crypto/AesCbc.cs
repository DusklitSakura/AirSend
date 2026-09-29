using System.Security.Cryptography;

namespace AirSend.Core.Crypto;

/// <summary>
/// AES-128-CBC helper used by the legacy ("fruit") pairing paths and by the
/// AirPlay 1 RAOP audio stream.
/// Port of <c>airplay-crypto/src/aes.rs</c>.
/// </summary>
public static class AesCbc
{
    /// <summary>Encrypts whole 16-byte blocks; trailing partial block is passed through.</summary>
    public static byte[] EncryptRaw(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using Aes aes = CreateCipher(key, iv);
        using ICryptoTransform encryptor = aes.CreateEncryptor();

        int fullLength = data.Length - (data.Length % 16);
        var output = new byte[data.Length];
        if (fullLength > 0)
        {
            encryptor.TransformBlock(data[..fullLength].ToArray(), 0, fullLength, output, 0);
        }

        data[fullLength..].CopyTo(output.AsSpan(fullLength));
        return output;
    }

    /// <summary>Decrypts whole 16-byte blocks; trailing partial block is passed through.</summary>
    public static byte[] DecryptRaw(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using Aes aes = CreateCipher(key, iv);
        using ICryptoTransform decryptor = aes.CreateDecryptor();

        int fullLength = data.Length - (data.Length % 16);
        var output = new byte[data.Length];
        if (fullLength > 0)
        {
            decryptor.TransformBlock(data[..fullLength].ToArray(), 0, fullLength, output, 0);
        }

        data[fullLength..].CopyTo(output.AsSpan(fullLength));
        return output;
    }

    /// <summary>Encrypts with PKCS#7 padding, as the HomeKit encrypted payloads expect.</summary>
    public static byte[] EncryptPkcs7(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using Aes aes = CreateCipher(key, iv);
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data.ToArray(), 0, data.Length);
    }

    /// <summary>Decrypts PKCS#7 padded data.</summary>
    public static byte[] DecryptPkcs7(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using Aes aes = CreateCipher(key, iv);
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data.ToArray(), 0, data.Length);
    }

    /// <summary>
    /// HomeKit pair-setup uses a 16 byte IV whose first 16 bytes are the salt
    /// derived key info; the callers pass the full block.
    /// </summary>
    public static byte[] DeriveIv(ReadOnlySpan<byte> ivSource)
    {
        var iv = new byte[16];
        ivSource[..Math.Min(16, ivSource.Length)].CopyTo(iv);
        return iv;
    }

    private static Aes CreateCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        return aes;
    }
}
