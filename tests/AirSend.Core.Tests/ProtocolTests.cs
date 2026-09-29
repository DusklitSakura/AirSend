using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirSend.Core.Audio;
using AirSend.Core.Crypto;
using AirSend.Core.Discovery;
using AirSend.Core.Probe;
using AirSend.Core.Rtsp;
using AirSend.Core.Streaming;

namespace AirSend.Core.Tests;

public class ProtocolTests
{
    public class LatencyProfileTests
    {
        [Fact]
        public void MapsSliderBoundsToAirPlayFrames()
        {
            Assert.Equal((22_050u, 132_300u), LatencyProfile.ToFrames(LatencyProfile.DefaultLatencyMs));
            Assert.Equal((11_025u, 44_100u), LatencyProfile.ToFrames(1000));
            Assert.Equal((4_410u, 8_820u), LatencyProfile.ToFrames(200));

            // 100 ms is the floor: receivers accept the session below that but stay
            // silent, so the slider never goes lower.
            Assert.Equal((4_410u, 4_410u), LatencyProfile.ToFrames(LatencyProfile.MinLatencyMs));
            Assert.Throws<ArgumentOutOfRangeException>(() => LatencyProfile.ToFrames(0));
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(199u)]
        [InlineData(250u)]
        [InlineData(3100u)]
        public void RejectsOffGridValues(uint latencyMs)
        {
            Assert.False(LatencyProfile.IsValid(latencyMs));
            Assert.Throws<ArgumentOutOfRangeException>(() => LatencyProfile.ToFrames(latencyMs));
        }

        [Fact]
        public void DecodesLegacySettings()
        {
            Assert.Equal(3000u, LatencyProfile.DecodeLegacySetting("music"));
            Assert.Equal(2000u, LatencyProfile.DecodeLegacySetting("video"));
            Assert.Equal(1000u, LatencyProfile.DecodeLegacySetting("gaming"));
            Assert.Equal(700u, LatencyProfile.DecodeLegacySetting(700L));
            Assert.Null(LatencyProfile.DecodeLegacySetting(750L));
        }

        [Fact]
        public void LatencyCooldownUnlocksAfterTenSeconds()
        {
            DateTimeOffset changedAt = DateTimeOffset.UtcNow;
            Assert.Equal(TimeSpan.FromSeconds(1), LatencyProfile.CooldownRemaining(changedAt, changedAt.AddSeconds(9)));
            Assert.Equal(TimeSpan.Zero, LatencyProfile.CooldownRemaining(changedAt, changedAt.AddSeconds(10)));
        }
    }

    public class ManualEndpointTests
    {
        [Fact]
        public void ParsesBareIpv4WithDefaultPort()
        {
            Assert.Equal(
                (IPAddress.Parse("192.168.86.20"), (ushort)7000),
                ManualEndpoint.Parse("192.168.86.20"));
        }

        [Fact]
        public void EmbeddedPortWinsOverFallback()
        {
            Assert.Equal(
                (IPAddress.Parse("192.168.1.10"), (ushort)1),
                ManualEndpoint.Parse("192.168.1.10:1", ushort.MaxValue));
            Assert.Equal(
                (IPAddress.Parse("192.168.1.10"), ushort.MaxValue),
                ManualEndpoint.Parse("192.168.1.10:65535"));
        }

        [Fact]
        public void ParsesBareAndBracketedIpv6()
        {
            Assert.Equal((IPAddress.IPv6Loopback, (ushort)7000), ManualEndpoint.Parse("::1"));
            Assert.Equal((IPAddress.IPv6Loopback, (ushort)7453), ManualEndpoint.Parse("[::1]:7453"));
        }

        [Fact]
        public void RejectsInvalidEndpointsAndZeroPorts()
        {
            Assert.Equal(ManualEndpointError.Empty, Assert.Throws<ManualEndpointException>(() => ManualEndpoint.Parse("")).Error);
            Assert.Equal(ManualEndpointError.ZeroPort, Assert.Throws<ManualEndpointException>(() => ManualEndpoint.Parse("192.168.1.10:0")).Error);
            Assert.Equal(ManualEndpointError.Invalid, Assert.Throws<ManualEndpointException>(() => ManualEndpoint.Parse("192.168.1.10:65536")).Error);
            Assert.Equal(ManualEndpointError.Invalid, Assert.Throws<ManualEndpointException>(() => ManualEndpoint.Parse("homepod.local:7000")).Error);
        }
    }

    public class DeviceModelTests
    {
        [Theory]
        [InlineData("AudioAccessory5,1", DeviceKind.HomePod)]
        [InlineData("AppleTV6,2", DeviceKind.AppleTv)]
        [InlineData("AirPort10,115", DeviceKind.AirportExpress)]
        [InlineData("Sonos", DeviceKind.OtherAirPlay)]
        [InlineData(null, DeviceKind.OtherAirPlay)]
        public void ClassifiesReceiversByModel(string? model, DeviceKind expected) =>
            Assert.Equal(expected, DeviceKindExtensions.FromModel(model));

        [Theory]
        [InlineData("00:06:78:AA:BB:CC", "000678aabbcc")]
        [InlineData("000678AABBCC", "000678aabbcc")]
        [InlineData("Denon", null)]
        public void NormalisesHardwareIds(string input, string? expected) =>
            Assert.Equal(expected, AirPlayDiscovery.NormalizeHardwareId(input));

        [Theory]
        [InlineData("AABBCCDDEEFF@Salón", "Salón")]
        [InlineData("Salón", "Salón")]
        public void StripsRaopHardwarePrefix(string instance, string expected) =>
            Assert.Equal(expected, AirPlayDiscovery.StripRaopPrefix(instance));

        [Fact]
        public void GroupsAirPlayAndRaopAnnouncementsOfTheSameDevice()
        {
            var airplay = new AirPlayDevice
            {
                Id = "Salón._airplay._tcp.local",
                HardwareId = "aabbccddeeff",
                Name = "Salón",
                Host = "salon.local",
                Addresses = [IPAddress.Parse("192.168.1.50")],
                Port = 7000,
                Kind = DeviceKind.HomePod,
                Model = "AudioAccessory5,1",
                SupportsAirPlay2 = true,
            };

            var raop = new AirPlayDevice
            {
                Id = "AABBCCDDEEFF@Salón._raop._tcp.local",
                HardwareId = "aabbccddeeff",
                Name = "Salón",
                Host = "salon.local",
                Addresses = [IPAddress.Parse("192.168.1.50")],
                Port = 7000,
                Kind = DeviceKind.HomePod,
                Features = "0x4A7FCA00,0x3C354BD0",
            };

            IReadOnlyDictionary<string, AirPlayDevice> grouped = DeviceGrouping.Group([airplay, raop]);

            Assert.Single(grouped);
            AirPlayDevice merged = grouped.Values.Single();
            Assert.Equal("hardware:aabbccddeeff", merged.Id);
            Assert.Equal("Salón", merged.Name);
            Assert.True(merged.SupportsAirPlay2);
            Assert.Equal("0x4A7FCA00,0x3C354BD0", merged.Features);
        }
    }

    public class BinaryPlistTests
    {
        [Fact]
        public void RoundTripsTheSetupPayload()
        {
            var stream = new Dictionary<string, object?>
            {
                ["audioMode"] = "default",
                ["controlPort"] = 51234L,
                ["ct"] = 1L,
                ["shk"] = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
                ["ssrc"] = 12345L,
            };
            var body = new Dictionary<string, object?>
            {
                ["streams"] = new List<object?> { stream },
                ["deviceID"] = "aa:bb:cc:dd:ee:ff",
            };

            byte[] encoded = BinaryPlist.Write(body);
            Assert.Equal("bplist00", Encoding.ASCII.GetString(encoded, 0, 8));

            var decoded = (Dictionary<string, object?>)BinaryPlist.Read(encoded)!;
            Assert.Equal("aa:bb:cc:dd:ee:ff", BinaryPlist.GetString(decoded, "deviceID"));

            List<object?> streams = BinaryPlist.GetArray(decoded, "streams")!;
            var first = (Dictionary<string, object?>)streams[0]!;
            Assert.Equal("default", BinaryPlist.GetString(first, "audioMode"));
            Assert.Equal(51234L, BinaryPlist.GetInteger(first, "controlPort"));
            Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), BinaryPlist.GetData(first, "shk"));
        }

        [Fact]
        public void RoundTripsNestedArraysAndBooleans()
        {
            var value = new Dictionary<string, object?>
            {
                ["flag"] = true,
                ["nothing"] = null,
                ["items"] = new List<object?> { "a", 2L, false, new List<object?> { "deep" } },
            };

            var decoded = (Dictionary<string, object?>)BinaryPlist.Read(BinaryPlist.Write(value))!;
            Assert.True((bool)decoded["flag"]!);
            Assert.Null(decoded["nothing"]);
            var items = (List<object?>)decoded["items"]!;
            Assert.Equal(4, items.Count);
            Assert.Equal("a", items[0]);
            Assert.Equal(2L, items[1]);
            Assert.False((bool)items[2]!);
            Assert.Equal("deep", ((List<object?>)items[3]!)[0]);
        }
    }

    public class AlacEncoderTests
    {
        [Fact]
        public void MagicCookieMatchesTheAlacStreamInfo()
        {
            byte[] cookie = AlacMagicCookie.BuildStreamInfo(352, 16, 2, 44_100);

            Assert.Equal(24, cookie.Length);
            Assert.Equal(352u, BinaryPrimitives.ReadUInt32BigEndian(cookie));
            Assert.Equal(16, cookie[5]);
            Assert.Equal((byte)40, cookie[6]);
            Assert.Equal((byte)10, cookie[7]);
            Assert.Equal((byte)14, cookie[8]);
            Assert.Equal(2, cookie[9]);
            Assert.Equal(44_100u, BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(20)));

            Assert.Equal((352, 16, 2, 44_100), AlacMagicCookie.Parse(cookie));
            Assert.Equal(32, AlacMagicCookie.BuildAtom(352, 16, 2, 44_100).Length);
        }

        [Fact]
        public void EncodedFramesRoundTripThroughTheVerbatimDecoder()
        {
            var format = AudioFormatDescription.AirPlayDefault;
            var encoder = new AlacEncoder(format);

            var samples = new short[format.FramesPerPacket * format.Channels];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(Math.Sin(i / 12.0) * 12000);
            }

            byte[] frame = encoder.EncodeFrame(samples);
            DecodedFrame decoded = AlacVerbatimDecoder.Decode(frame);

            Assert.True(decoded.IsUncompressed);
            Assert.Equal(samples.Length, decoded.Samples.Length);
            Assert.Equal(samples, decoded.Samples);
        }

        [Fact]
        public void SplitsLongBuffersIntoTenMillisecondFrames()
        {
            var format = AudioFormatDescription.AirPlayDefault;
            var encoder = new AlacEncoder(format);
            var samples = new short[format.FramesPerPacket * format.Channels * 3];

            IReadOnlyList<byte[]> frames = encoder.Encode(samples);

            Assert.Equal(3, frames.Count);
            Assert.All(frames, frame => Assert.True(AlacVerbatimDecoder.Decode(frame).IsUncompressed));
        }
    }

    public class RtspClientTests
    {
        [Fact]
        public async Task ReadsAPlaintextInfoResponse()
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = "AudioAccessory5,1",
                ["srcvers"] = "355.0",
            };
            byte[] plist = BinaryPlist.Write(body);

            await using var server = new FakeRtspServer(async stream =>
            {
                byte[] request = await ReadRequestAsync(stream);
                string requestText = Encoding.ASCII.GetString(request);
                Assert.StartsWith("GET /info RTSP/1.0", requestText);
                await WriteResponseAsync(stream, 200, "OK", plist, "application/x-apple-binary-plist");
            });

            await using var client = new RtspClient(IPAddress.Loopback, (ushort)server.Port);
            await client.ConnectAsync(TimeSpan.FromSeconds(5));
            RtspResponse response = await client.SendAsync("GET", "/info");

            Assert.True(response.IsSuccess);
            var decoded = BinaryPlist.AsDictionary(BinaryPlist.Read(response.Body!));
            Assert.Equal("AudioAccessory5,1", BinaryPlist.GetString(decoded, "model"));
        }

        [Fact]
        public async Task DecryptsEncryptedResponsesUsingTheControlCipher()
        {
            byte[] writeKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] readKey = writeKey.Reverse().ToArray();

            // From the receiver's point of view our write key is its read key.
            var serverCipher = new ControlCipher(readKey, writeKey);

            await using var server = new FakeRtspServer(async stream =>
            {
                await ReadEncryptedRequestAsync(stream, serverCipher);
                byte[] payload = Encoding.ASCII.GetBytes(
                    "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 2\r\n\r\nOK");
                byte[] frame = serverCipher.Encrypt(payload);
                await stream.WriteAsync(frame);
                await stream.FlushAsync();
            });

            await using var client = new RtspClient(IPAddress.Loopback, (ushort)server.Port);
            await client.ConnectAsync(TimeSpan.FromSeconds(5));
            client.SetCipher(new ControlCipher(writeKey, readKey));

            RtspResponse response = await client.SendAsync("OPTIONS", "*");

            Assert.Equal(200, response.StatusCode);
            Assert.Equal("OK", response.BodyText);
        }
    }

    public class ProbeTests
    {
        [Fact]
        public async Task DiscoveryStartsAndStopsWithoutThrowing()
        {
            // A real browse on a machine that may have zero AirPlay receivers:
            // this guards against multicast join/bind regressions, which would
            // otherwise only show up as a silent "no devices found" in the UI.
            await using var discovery = new AirPlayDiscovery();
            var seen = new List<AirPlayDevice>();
            discovery.DeviceDiscovered += device => seen.Add(device);

            discovery.StartBrowsing();
            Assert.True(discovery.IsBrowsing);

            await Task.Delay(TimeSpan.FromMilliseconds(750));

            discovery.StopBrowsing();
            Assert.False(discovery.IsBrowsing);
            Assert.NotNull(seen);
        }

        [Fact]
        public async Task BrowseOnceReturnsAfterTheTimeout()
        {
            IReadOnlyList<AirPlayDevice> devices = await AirPlayDiscovery.BrowseOnceAsync(
                TimeSpan.FromMilliseconds(500));

            Assert.NotNull(devices);
        }

        [Fact]
        public async Task RecognisesAnAirTunesServer()
        {
            await using var server = new FakeRtspServer(async stream =>
            {
                await ReadRequestAsync(stream);
                byte[] payload = Encoding.ASCII.GetBytes(
                    "RTSP/1.0 200 OK\r\nCSeq: 1\r\nServer: AirTunes/745.83\r\n\r\n");
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            });

            ProbeResult result = await AirPlayProbe.ProbeAsync(IPAddress.Loopback, (ushort)server.Port);

            Assert.Equal("AirTunes/745.83", result.ServerHeader);
            Assert.True(result.LooksLikeAirPlay2);
        }

        [Fact]
        public async Task RejectsAnEndpointThatIsNotAirPlay()
        {
            await using var server = new FakeRtspServer(async stream =>
            {
                await ReadRequestAsync(stream);
                byte[] payload = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\n\r\n");
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            });

            await Assert.ThrowsAsync<AirPlayProbeException>(
                () => AirPlayProbe.ProbeAsync(IPAddress.Loopback, (ushort)server.Port));
        }
    }

    // ── test doubles ────────────────────────────────────────────────────────

    /// <summary>Single connection RTSP server used to exercise the client paths.</summary>
    private sealed class FakeRtspServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _task;

        public FakeRtspServer(Func<NetworkStream, Task> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _task = Task.Run(async () =>
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                await handler(client.GetStream());
            });
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
            {
                // Test teardown only.
            }
        }
    }

    private static async Task<byte[]> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(buffer, cts.Token);
        return buffer[..read];
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

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        byte[] body,
        string contentType)
    {
        string header =
            $"RTSP/1.0 {status} {reason}\r\n" +
            "CSeq: 1\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
