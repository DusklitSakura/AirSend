using AirSend.Core.Crypto;
using AirSend.Core.Logging;
using AirSend.Core.Rtsp;

namespace AirSend.Core.Pairing;

/// <summary>Keys negotiated by pair-setup/pair-verify, used to encrypt RTSP traffic.</summary>
public sealed record SessionKeys(byte[] WriteKey, byte[] ReadKey, byte[] SharedSecret)
{
    /// <summary>Both directions are derived from the SRP/ECDH shared secret.</summary>
    public static SessionKeys FromSharedSecret(byte[] sharedSecret) => new(
        HkdfSha512.DeriveControlWriteKey(sharedSecret),
        HkdfSha512.DeriveControlReadKey(sharedSecret),
        sharedSecret);

    public ControlCipher CreateCipher() => new(WriteKey, ReadKey);
}

public sealed class AirPlayPairingException(string messageKey, object? parameters = null, Exception? innerException = null)
    : AirSendException(messageKey, parameters, innerException);

/// <summary>
/// AirPlay 2 pairing: transient pair-setup (SRP, HKP = 4, PIN "3939") and
/// pair-verify (Curve25519 + Ed25519), both spoken over RTSP.
/// Port of <c>airplay-client/src/connection.rs</c> plus <c>airplay-pairing</c>.
/// </summary>
public static class AirPlayPairing
{
    public const string TransientPin = "3939";

    /// <summary>
    /// Transient pair-setup: M1 to M4. No identity exchange happens, so this is
    /// what a HomePod accepts without showing a PIN dialog.
    /// </summary>
    public static async Task<SessionKeys> PairSetupTransientAsync(
        RtspClient client,
        string pin = TransientPin,
        CancellationToken cancellationToken = default)
    {
        var srp = new SrpClient(password: pin);

        byte[] m1 = Tlv8.PairSetupM1(transient: true).Encode();
        RtspResponse m2Response = await PostPairingAsync(client, "pair-setup", m1, hkp: 4, cancellationToken)
            .ConfigureAwait(false);
        Tlv8 m2 = Tlv8.Parse(RequireBody(m2Response, "pair-setup M2"));

        if (m2.Error is not null)
        {
        throw new AirPlayPairingException("error.pairing.setup_m2_failed", new { detail = m2.ErrorDescription });
        }

        byte[] serverPublicKey = m2.Get(TlvType.PublicKey)
            ?? throw new AirPlayPairingException("error.pairing.setup_m2_no_key");
        byte[] salt = m2.Get(TlvType.Salt)
            ?? throw new AirPlayPairingException("error.pairing.setup_m2_no_salt");

        SrpProof proof = srp.Proceed(serverPublicKey, salt);

        var m3 = new Tlv8();
        m3.Set(TlvType.State, [0x03]);
        m3.Set(TlvType.PublicKey, srp.PublicKey);
        m3.Set(TlvType.Proof, proof.ClientProof);

        RtspResponse m4Response = await PostPairingAsync(client, "pair-setup", m3.Encode(), hkp: 4, cancellationToken)
            .ConfigureAwait(false);
        Tlv8 m4 = Tlv8.Parse(RequireBody(m4Response, "pair-setup M4"));

        if (m4.Error is not null)
        {
        throw new AirPlayPairingException("error.pairing.setup_m4_failed", new { detail = m4.ErrorDescription });
        }

        byte[] serverProof = m4.Get(TlvType.Proof)
            ?? throw new AirPlayPairingException("error.pairing.setup_m4_no_proof");

        if (!SrpClient.VerifyServerProof(serverProof, proof.ExpectedServerProof))
        {
        throw new AirPlayPairingException("error.pairing.setup_m4_proof_mismatch");
        }

        AppLog.Info("log.pairing.setup_done");
        return SessionKeys.FromSharedSecret(proof.SharedSecret);
    }

    /// <summary>Pair-verify M1 to M4 for receivers that already know our identity key.</summary>
    public static async Task<SessionKeys> PairVerifyAsync(
        RtspClient client,
        PairingIdentity identity,
        CancellationToken cancellationToken = default)
    {
        byte[] privateKey = Curve25519.GeneratePrivateKey();
        byte[] publicKey = Curve25519.PublicKeyFromPrivateKey(privateKey);

        byte[] m1 = Tlv8.PairVerifyM1(publicKey).Encode();
        RtspResponse m2Response = await PostPairingAsync(client, "pair-verify", m1, hkp: 4, cancellationToken)
            .ConfigureAwait(false);
        Tlv8 m2 = Tlv8.Parse(RequireBody(m2Response, "pair-verify M2"));

        byte[] serverPublicKey = m2.Get(TlvType.PublicKey)
            ?? throw new AirPlayPairingException("error.pairing.verify_m2_no_key");
        byte[] encrypted = m2.Get(TlvType.EncryptedData)
            ?? throw new AirPlayPairingException("error.pairing.verify_m2_no_data");

        byte[] sharedSecret = Curve25519.X25519(privateKey, serverPublicKey);
        byte[] verifyKey = HkdfSha512.DerivePairVerifyKey(sharedSecret);
        byte[] decrypted = PairingCipher.Decrypt(verifyKey, PairingCipher.NonceFromString("PV-Msg02"), encrypted);
        Tlv8 serverInfo = Tlv8.Parse(decrypted);

        byte[] serverIdentifier = serverInfo.Get(TlvType.Identifier)
            ?? throw new AirPlayPairingException("error.pairing.verify_m2_no_id");
        byte[] serverSignature = serverInfo.Get(TlvType.Signature)
            ?? throw new AirPlayPairingException("error.pairing.verify_m2_no_signature");

        // The signature covers: shared secret || our public key || their public key.
        byte[] signedData = BigInts.Concat(sharedSecret, publicKey, serverPublicKey);
        if (!Ed25519.Verify(serverIdentifier, signedData, serverSignature))
        {
        throw new AirPlayPairingException("error.pairing.verify_m2_bad_signature");
        }

        var m3 = new Tlv8();
        m3.Set(TlvType.State, [0x03]);
        var clientInfo = new Tlv8();
        clientInfo.Set(TlvType.Identifier, System.Text.Encoding.ASCII.GetBytes(identity.Identifier));
        clientInfo.Set(TlvType.Signature, identity.Sign(signedData));
        m3.Set(TlvType.EncryptedData, PairingCipher.Encrypt(
            verifyKey,
            PairingCipher.NonceFromString("PV-Msg03"),
            clientInfo.Encode()));

        RtspResponse m4Response = await PostPairingAsync(client, "pair-verify", m3.Encode(), hkp: 4, cancellationToken)
            .ConfigureAwait(false);
        Tlv8 m4 = Tlv8.Parse(RequireBody(m4Response, "pair-verify M4"));
        if (m4.Error is not null)
        {
        throw new AirPlayPairingException("error.pairing.verify_m4_failed", new { detail = m4.ErrorDescription });
        }

        AppLog.Info("log.pairing.verify_done");
        return SessionKeys.FromSharedSecret(sharedSecret);
    }

    private static Task<RtspResponse> PostPairingAsync(
        RtspClient client,
        string endpoint,
        byte[] body,
        byte hkp,
        CancellationToken cancellationToken)
    {
        var request = new RtspRequest("POST", $"/{endpoint}");
        request.WithBody(body, "application/octet-stream");
        request.WithHeader("X-Apple-HKP", hkp.ToString());
        request.WithHeader("User-Agent", "AirPlay/745.83");
        return client.SendAsync(request, cancellationToken);
    }

    private static byte[] RequireBody(RtspResponse response, string what)
    {
        if (!response.IsSuccess)
        {
            throw new AirPlayPairingException("error.pairing.status", new
            {
                what,
                status = response.StatusCode,
            });
        }

        return response.Body ?? throw new AirPlayPairingException("error.pairing.empty_body", new { what });
    }
}
