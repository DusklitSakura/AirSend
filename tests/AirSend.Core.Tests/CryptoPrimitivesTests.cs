using AirSend.Core.Crypto;

namespace AirSend.Core.Tests;

public class CryptoPrimitivesTests
{
    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", string.Empty));

    private static string ToHex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    public class Tlv8Tests
    {
        [Fact]
        public void ParsesStateAndMethod()
        {
            Tlv8 tlv = Tlv8.Parse([0x06, 0x01, 0x01, 0x00, 0x01, 0x00]);
            Assert.Equal((byte)0x01, tlv.State);
            Assert.Equal([0x00], tlv.Get(TlvType.Method));
        }

        [Fact]
        public void ReassemblesFragmentedValues()
        {
            var data = new List<byte>();
            data.AddRange(new byte[] { 0x03, 0xFF });
            data.AddRange(Enumerable.Repeat((byte)0xAA, 255));
            data.AddRange(new byte[] { 0x03, 0x2D });
            data.AddRange(Enumerable.Repeat((byte)0xBB, 45));

            Tlv8 tlv = Tlv8.Parse(data.ToArray());
            byte[] value = tlv.Get(TlvType.PublicKey)!;
            Assert.Equal(300, value.Length);
            Assert.All(value[..255], b => Assert.Equal(0xAA, b));
            Assert.All(value[255..], b => Assert.Equal(0xBB, b));
        }

        [Fact]
        public void RejectsTruncatedHeader()
        {
            Assert.Throws<FormatException>(() => Tlv8.Parse([0x06]));
        }

        [Fact]
        public void RejectsTruncatedValue()
        {
            Assert.Throws<FormatException>(() => Tlv8.Parse([0x06, 0x05, 0x01, 0x02]));
        }

        [Fact]
        public void EncodesSortedAndFragmented()
        {
            var tlv = new Tlv8();
            tlv.Set(TlvType.State, [0x01]);
            tlv.Set(TlvType.Method, [0x00]);
            Assert.Equal([0x00, 0x01, 0x00, 0x06, 0x01, 0x01], tlv.Encode());

            var longValue = new Tlv8();
            longValue.Set(TlvType.PublicKey, Enumerable.Repeat((byte)0xCD, 300).ToArray());
            byte[] encoded = longValue.Encode();
            Assert.Equal(0x03, encoded[0]);
            Assert.Equal(255, encoded[1]);
            Assert.Equal(0x03, encoded[257]);
            Assert.Equal(45, encoded[258]);
        }

        [Fact]
        public void PairSetupM1UsesTransientFlag()
        {
            Tlv8 m1 = Tlv8.PairSetupM1();
            Assert.Equal((byte)0x01, m1.State);
            Assert.Equal([0x00], m1.Get(TlvType.Method));
            Assert.Equal([0x10, 0x00, 0x00, 0x00], m1.Get(TlvType.Flags));
        }
    }

    public class HkdfTests
    {
        // Reference values produced by the independent Python implementation in
        // work/tools/gen_srp_vector.py (hashlib/hmac, OpenSSL backed).
        [Fact]
        public void MatchesRfc5869StyleVector()
        {
            byte[] ikm = Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
            byte[] salt = Hex("000102030405060708090a0b0c");
            byte[] info = Hex("f0f1f2f3f4f5f6f7f8f9");

            byte[] derived = HkdfSha512.DeriveKey(ikm, salt, info, 42);

            Assert.Equal(
                "832390086cda71fb47625bb5ceb168e4c8e26a1a16ed34d9fc7fe92c1481579338da362cb8d9f925d7cb",
                ToHex(derived));
        }

        [Fact]
        public void DerivesPairSetupEncryptionKey()
        {
            byte[] key = HkdfSha512.DerivePairSetupKey("shared-secret-64-bytes"u8);
            Assert.Equal("3c6617f0ed833e576da2460a9bb41f2e3e4d6c0c5c5cdc5068a8380125b348f5", ToHex(key));
        }
    }

    public class Curve25519Tests
    {
        // RFC 7748 §5.2
        [Fact]
        public void MatchesRfc7748KeyAgreement()
        {
            byte[] alicePrivate = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            byte[] bobPrivate = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");

            byte[] alicePublic = Curve25519.PublicKeyFromPrivateKey(alicePrivate);
            byte[] bobPublic = Curve25519.PublicKeyFromPrivateKey(bobPrivate);

            Assert.Equal("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", ToHex(alicePublic));
            Assert.Equal("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", ToHex(bobPublic));

            byte[] aliceShared = Curve25519.X25519(alicePrivate, bobPublic);
            byte[] bobShared = Curve25519.X25519(bobPrivate, alicePublic);

            Assert.Equal("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742", ToHex(aliceShared));
            Assert.Equal(ToHex(aliceShared), ToHex(bobShared));
        }

        // RFC 7748 §6.1
        [Fact]
        public void MatchesRfc7748ScalarMultiplication()
        {
            byte[] scalar = Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
            byte[] u = Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");

            Assert.Equal(
                "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552",
                ToHex(Curve25519.X25519(scalar, u)));
        }
    }

    public class Ed25519Tests
    {
        // RFC 8032 §7.1 test vectors.
        [Theory]
        [InlineData(
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
        [InlineData(
            "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
        [InlineData(
            "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
        public void MatchesRfc8032Vectors(string seed, string publicKey, string message, string signature)
        {
            byte[] seedBytes = Hex(seed);
            byte[] messageBytes = Hex(message);

            Assert.Equal(publicKey, ToHex(Ed25519.PublicKeyFromSeed(seedBytes)));

            byte[] produced = Ed25519.Sign(seedBytes, messageBytes);
            Assert.Equal(signature, ToHex(produced));
            Assert.True(Ed25519.Verify(Hex(publicKey), messageBytes, produced));
        }

        [Fact]
        public void RejectsTamperedSignature()
        {
            byte[] seed = Ed25519.GenerateSeed();
            byte[] publicKey = Ed25519.PublicKeyFromSeed(seed);
            byte[] signature = Ed25519.Sign(seed, "hola"u8);
            signature[0] ^= 0xFF;

            Assert.False(Ed25519.Verify(publicKey, "hola"u8, signature));
        }
    }

    public class SrpTests
    {
        // Reference transcript generated by work/tools/gen_srp_vector.py: a fixed
        // client private key and a fixed server private key, salt = 24 bytes.
        private const string ClientPrivateKeyHex = "0123232323232323232323232323232323232323232323232323232323232323";
        private const string SaltHex = "000000000000000011111111111111112222222222222222";
        private const string ExpectedA =
            "70698bba110ea343a612c6b70a43a270684412967eafde3627268fc6de3470f87c4cd0f962cd9e36d78a78f6ce944caa91d14f1b6855b22226844fc30a4000c1af0725b8a104629187c60de5777bc3c8edf39ea0c129a4765754395264f6244f77b90996a27e4b2c8cc5edd300a562a4603f1a2e7bc0786bb23d5f11b2693c572b0e74b075fc73767c2431c50e9fd02ffff13a0611cf7b2caca277221ddf36f4d28bfbf1277e7d3d01b72ad97065888643339963afc9121552616ad6cd5d2536fecf7989898b9b4299c4ecf2a066daa911b5feac07cf1514b596bdf760dc5051216f8d2827dc5b18c03f5140289cae895aff8ce2047fd6cbeaa10e10b6c9be2464104323292ca04f5690fad09912dacae0e301a5a478572a9233001111f8e957e091726e167d74599e2d5e92bf79287544a257e14073f71133b8a491bd2202343b042e5602dd5436a672fea2941fc138c6c05e67de7452d53447610dd07e9e4025ced62808c18393d53309b342bbd07f5b380bb71941c067fb2aa42225c81179";
        private const string ServerPublicKeyHex =
            "082b425f8a7abcb799990ef01b55731a5f54a3cd8cecb1cb380da78721bc7df358bd53d64aab4b73f0e17080d04a66c769864ff25a618d0afa641e943fc69c3b453d8be1f18153f208a64545aa65b6a8f4591a98a6050b51f10233e3bbd62e14bc2a3ad74fbf749ed6bfefe1687ee989604ccf8e99aea6de3485a05d4e37ed0600678d336fd539e4301b5d6ca1ff8575848ad49cc99cd7328cc4cfe899e9ec376b20419aba6c9b18355e9952007a4c89af58edff440df68c2188dbcfb17d94233713e4fba1ae9fac0a6f2c0af387b0b2a818c14fef4f19e0081b409660662edf92a4a44512138a852d0c1f8a4a8ce219323e8690579754ac853e00489190eac478d54b77dbbc2749f12a4be47db78ac76609c98ff03d36b10f9e0037120ad639e20b1dfd153d0ea482da4535c63e4ee5ae661a57151bb8c164b0433de1fe5845bcb2833600dbcdbe68f889b678f15ec4850327dad959f928b8c83410926ccb634fb34e988a3edbcc7e2567f47bb6d2f378ce49181df7a3e4a3b5179af1c60345";
        private const string ExpectedSessionKey =
            "75c11d10553dd2b7207cb1053d0f98f66b6df3d842b7ae0c50b8e525e03549cdc07c3e787c1cd33cd392b79ead996156b6f7cc35b230e596cd65a8c229bb89f5";
        private const string ExpectedClientProof =
            "07b7edb2ced4b0dfd42ad206307c219b923e1df2b5e2736695dcd1d614f232c2dad3c92601b94329fdc1900abab34efbe3848dc806636dd00e85853f35cfde8a";
        private const string ExpectedServerProof =
            "cb46bb96a3be09369bce2c993a455d8a1573dd94419db625429f73535a13c0f775acc85503431a2eb5bac58d4c407a4c41d6ac81fd6d033a35df55b57d40add8";

        [Fact]
        public void MatchesIndependentReferenceTranscript()
        {
            var client = new SrpClient(privateKey: Hex(ClientPrivateKeyHex));
            byte[] publicKey = client.PublicKey;

            Assert.Equal(384, publicKey.Length);
            Assert.Equal(ExpectedA, ToHex(publicKey));

            SrpProof proof = client.Proceed(Hex(ServerPublicKeyHex), Hex(SaltHex));

            Assert.Equal(ExpectedSessionKey, ToHex(proof.SharedSecret));
            Assert.Equal(ExpectedClientProof, ToHex(proof.ClientProof));
            Assert.Equal(ExpectedServerProof, ToHex(proof.ExpectedServerProof));
            Assert.True(SrpClient.VerifyServerProof(proof.ExpectedServerProof, Hex(ExpectedServerProof)));
        }

        [Fact]
        public void RejectsZeroServerKey()
        {
            var client = new SrpClient(privateKey: Hex(ClientPrivateKeyHex));
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
                () => client.Proceed(new byte[384], Hex(SaltHex)));
        }

        [Fact]
        public void RandomClientsProduceDistinctPublicKeys()
        {
            Assert.NotEqual(
                ToHex(new SrpClient().PublicKey),
                ToHex(new SrpClient().PublicKey));
        }
    }

    public class CipherTests
    {
        [Fact]
        public void ControlCipherRoundTripsAndAdvancesCounters()
        {
            byte[] writeKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] readKey = writeKey.Reverse().ToArray();
            var writer = new ControlCipher(writeKey, readKey);
            var reader = new ControlCipher(readKey, writeKey);

            byte[] plaintext = "GET /info RTSP/1.0\r\nCSeq: 1\r\n\r\n"u8.ToArray();
            byte[] frame = writer.Encrypt(plaintext);

            // HomeKit framing: u16 length prefix + ciphertext + 16 byte tag.
            Assert.Equal(plaintext.Length + 2 + 16, frame.Length);
            Assert.Equal(plaintext.Length, frame[0] | (frame[1] << 8));
            Assert.Equal(1UL, writer.EncryptCounter);

            byte[] decrypted = reader.Decrypt(frame);
            Assert.Equal(plaintext, decrypted);
            Assert.Equal(1UL, reader.DecryptCounter);
        }

        [Fact]
        public void ControlCipherSplitsLargePayloadsIntoBlocks()
        {
            byte[] key = Enumerable.Repeat((byte)0x42, 32).ToArray();
            var writer = new ControlCipher(key, key);
            var reader = new ControlCipher(key, key);

            byte[] plaintext = new byte[0x400 + 10];
            Random.Shared.NextBytes(plaintext);

            byte[] frame = writer.Encrypt(plaintext);
            Assert.Equal(2UL, writer.EncryptCounter);
            Assert.Equal(plaintext, reader.Decrypt(frame));
        }

        [Fact]
        public void AudioCipherRoundTripsWithAadAndSequenceNonce()
        {
            byte[] key = Enumerable.Repeat((byte)0x24, 32).ToArray();
            var cipher = new AudioCipher(key);
            byte[] payload = new byte[352 * 2 * 2];
            Random.Shared.NextBytes(payload);

            (byte[] ciphertextWithTag, byte[] nonce) = cipher.EncryptWithSequence(payload, 1000, 0x12345678, 42);

            Assert.Equal(payload.Length + 16, ciphertextWithTag.Length);
            Assert.Equal(8, nonce.Length);
            Assert.Equal(42, nonce[0] | (nonce[1] << 8));
            Assert.NotEqual(ToHex(payload), ToHex(ciphertextWithTag[..payload.Length]));

            byte[] decrypted = cipher.DecryptWithSequence(
                ciphertextWithTag[..payload.Length],
                ciphertextWithTag[payload.Length..],
                1000,
                0x12345678,
                42);
            Assert.Equal(payload, decrypted);
        }

        [Fact]
        public void PairingCipherUsesAsciiNonceString()
        {
            byte[] nonce = PairingCipher.NonceFromString("PS-Msg05");
            Assert.Equal((byte)'P', nonce[0]);
            Assert.Equal((byte)'5', nonce[7]);
            Assert.Equal(0, nonce[8]);

            byte[] key = Enumerable.Repeat((byte)0x11, 32).ToArray();
            byte[] encrypted = PairingCipher.Encrypt(key, nonce, "hola"u8);
            Assert.Equal("hola"u8.ToArray(), PairingCipher.Decrypt(key, nonce, encrypted));
        }

        [Fact]
        public void AesCbcPassesTrailingPartialBlockThrough()
        {
            byte[] key = Enumerable.Repeat((byte)0x42, 16).ToArray();
            byte[] iv = Enumerable.Repeat((byte)0x24, 16).ToArray();
            byte[] payload = Enumerable.Repeat((byte)0xAB, 20).ToArray();

            byte[] encrypted = AesCbc.EncryptRaw(key, iv, payload);
            Assert.Equal(20, encrypted.Length);
            Assert.Equal(payload[16..], encrypted[16..]);
            Assert.Equal(payload, AesCbc.DecryptRaw(key, iv, encrypted));
        }
    }
}
