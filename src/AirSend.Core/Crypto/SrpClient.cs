using System.Numerics;
using System.Security.Cryptography;

namespace AirSend.Core.Crypto;

public sealed record SrpProof(byte[] ClientProof, byte[] SharedSecret, byte[] ExpectedServerProof);

/// <summary>
/// SRP-6a client for HomeKit pair-setup (3072-bit RFC 5054 group, g = 5, SHA-512).
/// Direct port of <c>SrpClient</c> in <c>airplay-crypto/src/srp.rs</c>, including the
/// Apple quirk that M1 hashes the raw generator byte instead of the padded one.
/// </summary>
public sealed class SrpClient
{
    private const int NBytes = 384;

    /// <summary>RFC 5054 3072-bit prime.</summary>
    public const string ModulusHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E08" +
        "8A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B" +
        "302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9" +
        "A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE6" +
        "49286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8" +
        "FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D" +
        "670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C" +
        "180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D" +
        "04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7D" +
        "B3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D226" +
        "1AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
        "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFC" +
        "E0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

    public static readonly BigInteger Modulus = BigInteger.Parse(
        "0" + ModulusHex,
        System.Globalization.NumberStyles.HexNumber);

    public static readonly BigInteger Generator = new(5);

    /// <summary>Identity used by every AirPlay 2 pair-setup exchange.</summary>
    public static readonly byte[] PairSetupIdentity = "Pair-Setup"u8.ToArray();

    /// <summary>HomePod transient pairing PIN (HKP = 4).</summary>
    public const string HomePodTransientPin = "3939";

    private readonly byte[] _identity;
    private readonly byte[] _password;
    private readonly BigInteger _privateKey;
    private readonly BigInteger _publicKey;

    public SrpClient(byte[]? identity = null, string? password = null, byte[]? privateKey = null)
    {
        _identity = identity ?? PairSetupIdentity;
        _password = System.Text.Encoding.UTF8.GetBytes(password ?? HomePodTransientPin);
        byte[] a = privateKey ?? RandomNumberGenerator.GetBytes(32);
        _privateKey = BigInts.FromBigEndian(a);
        _publicKey = BigInts.ModPow(Generator, _privateKey, Modulus);
    }

    /// <summary>Client public key A, padded to the modulus size.</summary>
    public byte[] PublicKey => PadToModulus(_publicKey);

    public static int ModulusSizeBytes => NBytes;

    /// <summary>Processes the server challenge (M2) and produces the client proof (M3).</summary>
    public SrpProof Proceed(ReadOnlySpan<byte> serverPublicKey, ReadOnlySpan<byte> salt)
    {
        BigInteger b = BigInts.FromBigEndian(serverPublicKey);
        if (BigInts.Mod(b, Modulus).IsZero)
        {
            throw new CryptographicException("Invalid server public key: B mod N = 0");
        }

        BigInteger u = ComputeU(_publicKey, b);
        if (u.IsZero)
        {
            throw new CryptographicException("Invalid u value: u = 0");
        }

        BigInteger x = ComputeX(salt, _identity, _password);
        BigInteger k = ComputeK();

        BigInteger gx = BigInts.ModPow(Generator, x, Modulus);
        BigInteger kgx = BigInts.Mod(k * gx, Modulus);
        BigInteger basis = BigInts.Mod(b - kgx, Modulus);

        BigInteger exponent = BigInts.Mod(_privateKey + u * x, Modulus - BigInteger.One);
        BigInteger s = BigInts.ModPow(basis, exponent, Modulus);

        byte[] sharedSecret = SHA512.HashData(PadToModulus(s));
        byte[] clientProof = ComputeM1(_identity, salt, _publicKey, b, sharedSecret);

        byte[] paddedA = PadToModulus(_publicKey);
        var serverProofInput = new byte[paddedA.Length + clientProof.Length + sharedSecret.Length];
        paddedA.CopyTo(serverProofInput, 0);
        clientProof.CopyTo(serverProofInput, paddedA.Length);
        sharedSecret.CopyTo(serverProofInput, paddedA.Length + clientProof.Length);
        byte[] expectedServerProof = SHA512.HashData(serverProofInput);

        return new SrpProof(clientProof, sharedSecret, expectedServerProof);
    }

    public static bool VerifyServerProof(ReadOnlySpan<byte> proof, ReadOnlySpan<byte> expected) =>
        CryptographicOperations.FixedTimeEquals(proof, expected);

    private static BigInteger ComputeU(BigInteger a, BigInteger b) =>
        BigInts.FromBigEndian(SHA512.HashData(BigInts.Concat(PadToModulus(a), PadToModulus(b))));

    private static BigInteger ComputeX(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> identity, ReadOnlySpan<byte> password)
    {
        var inner = new byte[identity.Length + 1 + password.Length];
        identity.CopyTo(inner);
        inner[identity.Length] = (byte)':';
        password.CopyTo(inner.AsSpan(identity.Length + 1));
        byte[] innerHash = SHA512.HashData(inner);

        var outer = new byte[salt.Length + innerHash.Length];
        salt.CopyTo(outer);
        innerHash.CopyTo(outer.AsSpan(salt.Length));
        return BigInts.FromBigEndian(SHA512.HashData(outer));
    }

    private static BigInteger ComputeK() =>
        BigInts.FromBigEndian(SHA512.HashData(
            BigInts.Concat(PadToModulus(Modulus), PadToModulus(Generator))));

    private static byte[] ComputeM1(
        ReadOnlySpan<byte> identity,
        ReadOnlySpan<byte> salt,
        BigInteger a,
        BigInteger b,
        byte[] sharedSecret)
    {
        byte[] hN = SHA512.HashData(PadToModulus(Modulus));
        byte[] hG = SHA512.HashData(Generator.ToByteArray(isUnsigned: true, isBigEndian: true));

        var xor = new byte[hN.Length];
        for (int i = 0; i < xor.Length; i++)
        {
            xor[i] = (byte)(hN[i] ^ hG[i]);
        }

        byte[] hI = SHA512.HashData(identity.ToArray());
        byte[] paddedA = PadToModulus(a);
        byte[] paddedB = PadToModulus(b);
        byte[] saltArray = salt.ToArray();

        byte[] buffer = BigInts.Concat(xor, hI, saltArray, paddedA, paddedB, sharedSecret);
        return SHA512.HashData(buffer);
    }

    private static byte[] PadToModulus(BigInteger value) => BigInts.ToBigEndian(value, NBytes);
}
