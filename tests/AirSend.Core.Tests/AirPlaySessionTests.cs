using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AirSend.Core.Audio;
using AirSend.Core.Crypto;
using AirSend.Core.Discovery;
using AirSend.Core.Pairing;
using AirSend.Core.Rtsp;
using AirSend.Core.Streaming;

namespace AirSend.Core.Tests;

/// <summary>
/// Drives <see cref="AirPlayStreamSession"/> against a scripted AirPlay 2 receiver:
/// SRP pair-setup, the switch to the encrypted control channel, SETUP phase 1/2,
/// RECORD, volume and the feedback heartbeat. The audio pump is skipped because a
/// test machine has no capture device.
/// </summary>
public class AirPlaySessionTests
{
    [Fact]
    public async Task CompletesTheFullHandshakeAgainstAScriptedReceiver()
    {
        await using var receiver = new FakeAirPlayReceiver();

        var device = new AirPlayDevice
        {
            Id = "Salón._airplay._tcp.local",
            Name = "Salón",
            Host = "127.0.0.1",
            Addresses = [IPAddress.Loopback],
            Port = (ushort)receiver.Port,
            Kind = DeviceKind.HomePod,
            Model = "AudioAccessory5,1",
            SupportsAirPlay2 = true,
        };

        PairingIdentity identity = PairingIdentity.Generate();

        await using AirPlayStreamSession session = await AirPlayStreamSession.OpenAsync(
            device,
            identity,
            initialVolume: 0.25f,
            latencyMs: LatencyProfile.DefaultLatencyMs,
            captureAudio: false);

        Assert.True(receiver.PairSetupCompleted);
        Assert.True(receiver.ControlChannelEncrypted);

        // SETUP runs twice: timing/events first, then the audio stream.
        Assert.Equal(2, receiver.SetupPhaseCount);
        Dictionary<string, object?> phase1 = receiver.SetupBodies[0]!;
        Assert.True(BinaryPlist.GetInteger(phase1, "timingPort") > 0);
        Assert.Equal("NTP", BinaryPlist.GetString(phase1, "timingProtocol"));
        Assert.Equal(identity.DeviceId, BinaryPlist.GetString(phase1, "deviceID"));

        Dictionary<string, object?> phase2 = receiver.SetupBodies[1]!;
        var stream = (Dictionary<string, object?>)BinaryPlist.GetArray(phase2, "streams")![0]!;
        Assert.Equal(44_100L, BinaryPlist.GetInteger(stream, "sr"));
        Assert.Equal(352L, BinaryPlist.GetInteger(stream, "spf"));
        // The compression type has to be ALAC (2) and the audio format 0x40000,
        // otherwise the receiver decodes the frames as raw PCM and stays silent.
        Assert.Equal(2L, BinaryPlist.GetInteger(stream, "ct"));
        Assert.Equal(0x40000L, BinaryPlist.GetInteger(stream, "audioFormat"));
        Assert.Equal(96L, BinaryPlist.GetInteger(stream, "type"));
        Assert.True((bool)stream["isMedia"]!);
        Assert.Equal(22_050L, BinaryPlist.GetInteger(stream, "latencyMin"));
        Assert.Equal(132_300L, BinaryPlist.GetInteger(stream, "latencyMax"));
        Assert.Equal(32, BinaryPlist.GetData(stream, "shk")!.Length);
        Assert.Equal(
            (352, 16, 2, 44_100),
            AlacMagicCookie.Parse(BinaryPlist.GetData(stream, "asc")!));

        Assert.True(receiver.RecordReceived);
        Assert.True(receiver.LastVolumeRequest is not null);
        Assert.Equal(44_100, session.Info.SampleRate);
        Assert.Equal(2, session.Info.Channels);
        Assert.Equal(0.25f, session.Volume, 0.001f);

        // The volume request is sent as dB relative to full scale.
        double expectedDb = 20 * Math.Log10(0.25);
        Assert.Contains(expectedDb.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), receiver.LastVolumeRequest);

        await session.SetVolumeAsync(0.8f);
        Assert.Contains("volume", receiver.LastVolumeRequest);

        bool feedback = await session.SendFeedbackAsync();
        Assert.True(feedback);

        await session.StopAsync();
        Assert.True(receiver.TeardownReceived);
    }

    /// <summary>
    /// Minimal AirPlay 2 receiver: SRP server for transient pair-setup, then the
    /// HomeKit encrypted framing for every later request.
    /// </summary>
    private sealed class FakeAirPlayReceiver : IAsyncDisposable
    {
        private const string Identity = "Pair-Setup";
        private const string Password = "3939";

        private readonly TcpListener _listener;
        private readonly Task _task;
        private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);

        public FakeAirPlayReceiver()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _task = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public bool PairSetupCompleted { get; private set; }

        public bool ControlChannelEncrypted { get; private set; }

        public int SetupPhaseCount => SetupBodies.Count;

        public List<Dictionary<string, object?>?> SetupBodies { get; } = [];

        public bool RecordReceived { get; private set; }

        public bool TeardownReceived { get; private set; }

        public string? LastVolumeRequest { get; private set; }

        private async Task ServeAsync()
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            ControlCipher? cipher = null;

            while (true)
            {
                byte[] request = cipher is null
                    ? await ReadPlainRequestAsync(stream)
                    : await ReadEncryptedRequestAsync(stream, cipher);

                if (request.Length == 0)
                {
                    return;
                }

                (string method, string uri, Dictionary<string, string> headers, byte[] body) =
                    ParseRequest(request);

                if (method == "GET" && uri == "/info")
                {
                    byte[] plist = BinaryPlist.Write(new Dictionary<string, object?>
                    {
                        ["model"] = "AudioAccessory5,1",
                        ["srcvers"] = "355.0",
                        ["features"] = "0x4A7FCA00,0x3C354BD0",
                    });
                    await WritePlainResponseAsync(stream, 200, plist);
                    continue;
                }

                if (method == "POST" && uri == "/pair-setup")
                {
                    Tlv8 message = Tlv8.Parse(body);
                    if (message.State == 1)
                    {
                        byte[] serverPublicKey = StartSrp();
                        var m2 = new Tlv8();
                        m2.Set(TlvType.State, [0x02]);
                        m2.Set(TlvType.PublicKey, serverPublicKey);
                        m2.Set(TlvType.Salt, _salt);
                        await WritePlainResponseAsync(stream, 200, m2.Encode());
                    }
                    else if (message.State == 3)
                    {
                        byte[] clientProof = message.Get(TlvType.Proof)!;
                        byte[] serverProof = CompleteSrp(message.Get(TlvType.PublicKey)!, clientProof);

                        var m4 = new Tlv8();
                        m4.Set(TlvType.State, [0x04]);
                        m4.Set(TlvType.Proof, serverProof);

                        PairSetupCompleted = true;

                        // The receiver reads with the key the sender writes with, and
                        // writes with the key the sender reads with.
                        SessionKeys keys = SessionKeys.FromSharedSecret(SessionKey!);
                        cipher = new ControlCipher(keys.ReadKey, keys.WriteKey);
                        await WritePlainResponseAsync(stream, 200, m4.Encode());
                    }

                    continue;
                }

                // Everything below happens on the encrypted channel.
                ControlChannelEncrypted = true;
                byte[] responseBody = [];
                switch (method)
                {
                    case "OPTIONS":
                        await WriteEncryptedResponseAsync(stream, cipher!, 200, [], "Public: ANNOUNCE, SETUP, RECORD");
                        break;

                    case "SETUP":
                    {
                        SetupBodies.Add(BinaryPlist.AsDictionary(BinaryPlist.Read(body)));
                        responseBody = BinaryPlist.Write(new Dictionary<string, object?>
                        {
                            ["streams"] = new List<object?>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["dataPort"] = 6000L,
                                    ["controlPort"] = 6001L,
                                    ["type"] = 96L,
                                },
                            },
                        });
                        await WriteEncryptedResponseAsync(stream, cipher!, 200, responseBody);
                        break;
                    }

                    case "RECORD":
                        RecordReceived = true;
                        await WriteEncryptedResponseAsync(stream, cipher!, 200, []);
                        break;

                    case "SET_PARAMETER":
                        LastVolumeRequest = Encoding.ASCII.GetString(body);
                        await WriteEncryptedResponseAsync(stream, cipher!, 200, []);
                        break;

                    case "GET_PARAMETER":
                        await WriteEncryptedResponseAsync(stream, cipher!, 200, []);
                        break;

                    case "TEARDOWN":
                        TeardownReceived = true;
                        await WriteEncryptedResponseAsync(stream, cipher!, 200, []);
                        return;

                    default:
                        await WriteEncryptedResponseAsync(stream, cipher!, 200, []);
                        break;
                }

                _ = headers;
            }
        }

        private byte[]? SessionKey { get; set; }

        private BigInteger _serverPrivate;
        private BigInteger _verifier;
        private BigInteger _serverPublicKey;

        private byte[] StartSrp()
        {
            _serverPrivate = BigInts.FromBigEndian(RandomNumberGenerator.GetBytes(32));

            // x = H(salt | H(identity ":" password)), exactly like the client side.
            byte[] innerHash = SHA512.HashData(Encoding.UTF8.GetBytes(Identity + ":" + Password));
            BigInteger x = BigInts.FromBigEndian(SHA512.HashData(BigInts.Concat(_salt, innerHash)));
            _verifier = BigInts.ModPow(SrpClient.Generator, x, SrpClient.Modulus);

            // B = k*v + g^b (mod N)
            BigInteger k = BigInts.FromBigEndian(SHA512.HashData(BigInts.Concat(
                BigInts.ToBigEndian(SrpClient.Modulus, 384),
                BigInts.ToBigEndian(SrpClient.Generator, 384))));
            _serverPublicKey = BigInts.Mod(
                k * _verifier + BigInts.ModPow(SrpClient.Generator, _serverPrivate, SrpClient.Modulus),
                SrpClient.Modulus);

            return BigInts.ToBigEndian(_serverPublicKey, 384);
        }

        private byte[] CompleteSrp(byte[] clientPublicKey, byte[] clientProof)
        {
            BigInteger a = BigInts.FromBigEndian(clientPublicKey);
            BigInteger serverPublicKey = _serverPublicKey;
            BigInteger u = BigInts.FromBigEndian(SHA512.HashData(BigInts.Concat(
                BigInts.ToBigEndian(a, 384),
                BigInts.ToBigEndian(serverPublicKey, 384))));

            BigInteger shared = BigInts.ModPow(
                BigInts.Mod(a * BigInts.ModPow(_verifier, u, SrpClient.Modulus), SrpClient.Modulus),
                _serverPrivate,
                SrpClient.Modulus);
            byte[] sessionKey = SHA512.HashData(BigInts.ToBigEndian(shared, 384));

            byte[] hN = SHA512.HashData(BigInts.ToBigEndian(SrpClient.Modulus, 384));
            byte[] hG = SHA512.HashData(SrpClient.Generator.ToByteArray(isUnsigned: true, isBigEndian: true));
            var xor = new byte[64];
            for (int i = 0; i < xor.Length; i++)
            {
                xor[i] = (byte)(hN[i] ^ hG[i]);
            }

            byte[] expectedM1 = SHA512.HashData(BigInts.Concat(
                xor,
                SHA512.HashData(Encoding.UTF8.GetBytes(Identity)),
                _salt,
                BigInts.ToBigEndian(a, 384),
                BigInts.ToBigEndian(serverPublicKey, 384),
                sessionKey));

            Assert.Equal(Convert.ToHexString(expectedM1), Convert.ToHexString(clientProof));

            SessionKey = sessionKey;

            return SHA512.HashData(BigInts.Concat(
                BigInts.ToBigEndian(a, 384),
                clientProof,
                sessionKey));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
            {
                // Test teardown only.
            }
        }

        private static async Task<byte[]> ReadPlainRequestAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            int contentLength = -1;
            int headerEnd = -1;

            while (true)
            {
                if (headerEnd < 0)
                {
                    headerEnd = FindHeaderEnd(buffer);
                }

                if (headerEnd >= 0)
                {
                    if (contentLength < 0)
                    {
                        string header = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
                        contentLength = ParseContentLength(header);
                    }

                    if (buffer.Count >= headerEnd + 4 + contentLength)
                    {
                        return buffer.ToArray();
                    }
                }

                int read = await stream.ReadAsync(chunk);
                if (read == 0)
                {
                    return [];
                }

                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }
        }

        private static async Task<byte[]> ReadEncryptedRequestAsync(NetworkStream stream, ControlCipher cipher)
        {
            var header = new byte[2];
            await stream.ReadExactlyAsync(header);
            int blockLength = header[0] | (header[1] << 8);
            var frame = new byte[blockLength + 16];
            await stream.ReadExactlyAsync(frame);
            return cipher.DecryptBlock(frame, (ushort)blockLength);
        }

        private static (string Method, string Uri, Dictionary<string, string> Headers, byte[] Body) ParseRequest(byte[] request)
        {
            string text = Encoding.ASCII.GetString(request);
            int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string[] lines = text[..headerEnd].Split("\r\n");
            string[] first = lines[0].Split(' ');

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            byte[] body = request[(headerEnd + 4)..];
            return (first[0], first[1], headers, body);
        }

        private static int ParseContentLength(string header)
        {
            foreach (string line in header.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    return int.Parse(line["Content-Length:".Length..].Trim());
                }
            }

            return 0;
        }

        private static int FindHeaderEnd(List<byte> buffer)
        {
            for (int i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static Task WritePlainResponseAsync(NetworkStream stream, int status, byte[] body, string? extraHeader = null)
        {
            _ = extraHeader;
            string header =
                $"RTSP/1.0 {status} OK\r\nCSeq: 1\r\nContent-Type: application/octet-stream\r\n" +
                $"Content-Length: {body.Length}\r\n\r\n";
            return WriteAsync(stream, Encoding.ASCII.GetBytes(header), body);
        }

        private static Task WriteEncryptedResponseAsync(
            NetworkStream stream,
            ControlCipher cipher,
            int status,
            byte[] body,
            string? extraHeader = null)
        {
            string header =
                $"RTSP/1.0 {status} OK\r\nCSeq: 2\r\nContent-Type: application/x-apple-binary-plist\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                (extraHeader is null ? string.Empty : extraHeader + "\r\n") +
                "\r\n";

            byte[] plaintext = new byte[header.Length + body.Length];
            Encoding.ASCII.GetBytes(header).CopyTo(plaintext, 0);
            body.CopyTo(plaintext, header.Length);

            return WriteAsync(stream, cipher.Encrypt(plaintext));
        }

        private static async Task WriteAsync(NetworkStream stream, params byte[][] parts)
        {
            foreach (byte[] part in parts)
            {
                if (part.Length > 0)
                {
                    await stream.WriteAsync(part);
                }
            }

            await stream.FlushAsync();
        }
    }
}
