using System.Security.Cryptography;
using AirSend.Core.Crypto;

namespace AirSend.Core.Pairing;

/// <summary>
/// Long term sender identity: an Ed25519 key pair plus the identifier the receiver
/// sees. Port of <c>ControllerIdentity</c> from <c>airplay-pairing</c>.
/// </summary>
public sealed class PairingIdentity
{
    private PairingIdentity(byte[] seed, byte[] publicKey, string identifier)
    {
        Seed = seed;
        PublicKey = publicKey;
        Identifier = identifier;
    }

    public byte[] Seed { get; }

    public byte[] PublicKey { get; }

    /// <summary>Human readable identity, formatted like a MAC address.</summary>
    public string Identifier { get; }

    /// <summary>MAC style device id sent in <c>X-Apple-Device-ID</c>.</summary>
    public string DeviceId => Identifier;

    /// <summary>Device id without separators (used for DACP-ID / Client-Instance).</summary>
    public string CompactDeviceId => Identifier.Replace(":", string.Empty);

    public static PairingIdentity Generate() => FromSeed(Ed25519.GenerateSeed());

    public static PairingIdentity Import(byte[] seed) => FromSeed(seed);

    public byte[] Sign(ReadOnlySpan<byte> message) => Ed25519.Sign(Seed, message);

    private static PairingIdentity FromSeed(byte[] seed)
    {
        byte[] publicKey = Ed25519.PublicKeyFromSeed(seed);
        byte[] digest = SHA256.HashData(publicKey);
        string identifier = string.Join(':', digest[..6].Select(b => b.ToString("x2")));
        return new PairingIdentity(seed, publicKey, identifier);
    }
}
