using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AirSend.Core.Crypto;

/// <summary>
/// RTSP control channel encryption.
/// </summary>
/// <remarks>
/// HomeKit framing, ported from <c>ControlCipher</c> in
/// <c>airplay-crypto/src/chacha.rs</c>:
/// <code>
/// [u16_le length][ciphertext][16 byte tag]
/// </code>
/// with <c>length</c> as the AEAD associated data and a 12 byte nonce made of
/// four zero bytes followed by an 8 byte little-endian block counter.
/// </remarks>
public sealed class ControlCipher
{
    private const int MaxBlock = 0x400;

    private readonly ChaCha20Poly1305 _writeCipher;
    private readonly ChaCha20Poly1305 _readCipher;

    private ulong _encryptCounter;
    private ulong _decryptCounter;

    public ControlCipher(ReadOnlySpan<byte> writeKey, ReadOnlySpan<byte> readKey)
    {
        _writeCipher = new ChaCha20Poly1305(writeKey);
        _readCipher = new ChaCha20Poly1305(readKey);
    }

    public static ControlCipher Unidirectional(ReadOnlySpan<byte> key) => new(key, key);

    public ulong EncryptCounter => _encryptCounter;

    public ulong DecryptCounter => _decryptCounter;

    public void ResetCounters()
    {
        _encryptCounter = 0;
        _decryptCounter = 0;
    }

    /// <summary>Encrypts plaintext into one or more framed blocks.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length == 0)
        {
            throw new ArgumentException("Empty plaintext", nameof(plaintext));
        }

        var output = new List<byte>(plaintext.Length + 18);
        Span<byte> associatedData = stackalloc byte[2];
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> tag = stackalloc byte[16];
        int offset = 0;
        while (offset < plaintext.Length)
        {
            int blockLength = Math.Min(MaxBlock, plaintext.Length - offset);
            ReadOnlySpan<byte> block = plaintext.Slice(offset, blockLength);

            BinaryPrimitives.WriteUInt16LittleEndian(associatedData, (ushort)blockLength);
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _encryptCounter);

            var ciphertext = new byte[blockLength];
            _writeCipher.Encrypt(nonce, block, ciphertext, tag, associatedData);

            output.AddRange(associatedData.ToArray());
            output.AddRange(ciphertext);
            output.AddRange(tag.ToArray());

            _encryptCounter++;
            offset += blockLength;
        }

        return output.ToArray();
    }

    /// <summary>Decrypts a framed block; <paramref name="ciphertextWithTag"/> excludes the length prefix.</summary>
    public byte[] DecryptBlock(ReadOnlySpan<byte> ciphertextWithTag, ushort blockLength)
    {
        if (ciphertextWithTag.Length < blockLength + 16)
        {
            throw new CryptographicException("Ciphertext block too short");
        }

        Span<byte> associatedData = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(associatedData, blockLength);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _decryptCounter);

        var plaintext = new byte[blockLength];
        _readCipher.Decrypt(
            nonce,
            ciphertextWithTag[..blockLength],
            ciphertextWithTag.Slice(blockLength, 16),
            plaintext,
            associatedData);

        _decryptCounter++;
        return plaintext;
    }

    /// <summary>Decrypts every framed block in <paramref name="data"/>.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            throw new CryptographicException("Data too short for HomeKit frame");
        }

        var output = new List<byte>(data.Length);
        int offset = 0;
        while (offset + 2 <= data.Length)
        {
            ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
            int frameLength = blockLength + 16;
            if (offset + frameLength > data.Length)
            {
                break;
            }

            output.AddRange(DecryptBlock(data.Slice(offset, frameLength), blockLength));
            offset += frameLength;
        }

        return output.ToArray();
    }
}

/// <summary>
/// AirPlay 2 audio packet encryption.
/// </summary>
/// <remarks>
/// Ported from <c>AudioCipher</c> in <c>airplay-crypto/src/chacha.rs</c>:
/// <list type="bullet">
/// <item>nonce: 12 bytes, zero except the RTP sequence number at offset 4 (little-endian)</item>
/// <item>AAD: RTP timestamp (big-endian) followed by SSRC (big-endian)</item>
/// <item>trailer sent on the wire: 16 byte tag then the 8 byte nonce (nonce bytes 4..11)</item>
/// </list>
/// </remarks>
public sealed class AudioCipher
{
    private readonly ChaCha20Poly1305 _cipher;

    public AudioCipher(ReadOnlySpan<byte> key)
    {
        _cipher = new ChaCha20Poly1305(key);
    }

    public (byte[] CiphertextWithTag, byte[] Nonce8) EncryptWithSequence(
        ReadOnlySpan<byte> audioData,
        uint rtpTimestamp,
        uint ssrc,
        ushort sequenceNumber)
    {
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(nonce[4..], sequenceNumber);

        Span<byte> associatedData = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(associatedData, rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(associatedData[4..], ssrc);

        var ciphertext = new byte[audioData.Length];
        Span<byte> tag = stackalloc byte[16];
        _cipher.Encrypt(nonce, audioData, ciphertext, tag, associatedData);

        var ciphertextWithTag = new byte[audioData.Length + 16];
        ciphertext.CopyTo(ciphertextWithTag, 0);
        tag.CopyTo(ciphertextWithTag.AsSpan(audioData.Length));

        var nonce8 = new byte[8];
        nonce[4..12].CopyTo(nonce8);
        return (ciphertextWithTag, nonce8);
    }

    public byte[] DecryptWithSequence(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        uint rtpTimestamp,
        uint ssrc,
        ushort sequenceNumber)
    {
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(nonce[4..], sequenceNumber);

        Span<byte> associatedData = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(associatedData, rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(associatedData[4..], ssrc);

        var plaintext = new byte[ciphertext.Length];
        _cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }
}

/// <summary>Pairing frame encryption: explicit 12 byte nonce, no associated data.</summary>
public static class PairingCipher
{
    /// <summary>HomeKit nonces are ASCII strings right-padded with zeroes, e.g. "PS-Msg05".</summary>
    public static byte[] NonceFromString(string value)
    {
        var nonce = new byte[12];
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(value);
        ascii.AsSpan(0, Math.Min(12, ascii.Length)).CopyTo(nonce);
        return nonce;
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext)
    {
        using var cipher = new ChaCha20Poly1305(key);
        var ciphertext = new byte[plaintext.Length];
        Span<byte> tag = stackalloc byte[16];
        cipher.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[plaintext.Length + 16];
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result.AsSpan(plaintext.Length));
        return result;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextWithTag)
    {
        if (ciphertextWithTag.Length < 16)
        {
            throw new CryptographicException("Encrypted payload is missing its tag");
        }

        using var cipher = new ChaCha20Poly1305(key);
        int length = ciphertextWithTag.Length - 16;
        var plaintext = new byte[length];
        cipher.Decrypt(nonce, ciphertextWithTag[..length], ciphertextWithTag[length..], plaintext);
        return plaintext;
    }
}
