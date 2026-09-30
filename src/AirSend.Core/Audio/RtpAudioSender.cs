using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirSend.Core.Crypto;
using AirSend.Core.Logging;

namespace AirSend.Core.Audio;

/// <summary>AirPlay RTP payload types (see <c>airplay-audio/src/rtp.rs</c>).</summary>
public static class RtpPayloadTypes
{
    public const byte TimingRequest = 82;
    public const byte TimingResponse = 83;
    public const byte Sync = 84;
    public const byte RetransmitRequest = 85;
    public const byte RetransmitResponse = 86;
    public const byte PtpSync = 87;
    public const byte AudioRealtime = 96;
    public const byte AudioBuffered = 103;
}

public readonly record struct RtpHeader(byte PayloadType, ushort Sequence, uint Timestamp, uint Ssrc, bool Marker)
{
    public byte[] Serialize()
    {
        var buffer = new byte[12];
        buffer[0] = 0x80; // version 2
        buffer[1] = (byte)((Marker ? 0x80 : 0x00) | (PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), Ssrc);
        return buffer;
    }

    public static RtpHeader Parse(ReadOnlySpan<byte> data) => new(
        (byte)(data[1] & 0x7F),
        BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
        (data[1] & 0x80) != 0);
}

/// <summary>
/// Sends ALAC frames as AirPlay RTP packets and answers retransmission requests.
/// Port of the packet writer in <c>airplay-audio/src/rtp.rs</c> plus the control
/// channel handling from <c>airplay-client</c>.
/// </summary>
/// <remarks>
/// Packet layout (AirPlay 2, encrypted):
/// <code>
/// [12 byte RTP header][ciphertext][16 byte Poly1305 tag][8 byte nonce]
/// </code>
/// </remarks>
public sealed class RtpAudioSender : IDisposable
{
    private const int MaxRememberedPackets = 512;

    /// <summary>
    /// A blocking UDP send can wait forever when the network stalls (a Wi-Fi hiccup, a
    /// firewall inspecting the traffic). That would stop the audio pump, and with it
    /// everything waiting on the queue, so sends are given a deadline and the packet
    /// is dropped instead: RTP tolerates loss, and the receiver asks for retransmits.
    /// </summary>
    internal const int SendTimeoutMs = 100;

    private static readonly TimeSpan SendWarningInterval = TimeSpan.FromSeconds(5);

    private readonly UdpClient _socket;
    private readonly IPEndPoint _destination;
    private readonly AudioCipher? _cipher;
    private readonly Queue<(ushort Sequence, byte[] Packet)> _sentPackets = new();
    private readonly Lock _historyLock = new();

    private ushort _sequence = (ushort)Random.Shared.Next(1, ushort.MaxValue);
    private uint _timestamp;
    private bool _firstPacketOfBurst = true;
    private DateTimeOffset _lastSendWarning = DateTimeOffset.MinValue;

    public RtpAudioSender(IPAddress destination, int dataPort, AudioCipher? cipher, uint ssrc)
    {
        _destination = new IPEndPoint(destination, dataPort);
        _socket = new UdpClient(destination.AddressFamily);
        _socket.Client.ConfigureSocket();
        _cipher = cipher;
        Ssrc = ssrc;
    }

    public uint Ssrc { get; }

    public ushort NextSequence => _sequence;

    public uint NextTimestamp => _timestamp;

    public long PacketsSent { get; private set; }

    public long PacketsDropped { get; private set; }

    public void SetInitialTimestamp(uint timestamp) => _timestamp = timestamp;

    /// <summary>Sends one encoded frame; <paramref name="framesInPacket"/> advances the RTP clock.</summary>
    public void SendFrame(ReadOnlySpan<byte> encodedFrame, int framesInPacket)
    {
        var header = new RtpHeader(
            RtpPayloadTypes.AudioRealtime,
            _sequence,
            _timestamp,
            Ssrc,
            Marker: _firstPacketOfBurst);

        byte[] packet = BuildPacket(header, encodedFrame);

        try
        {
            _socket.Send(packet, packet.Length, _destination);
            PacketsSent++;
            Remember(header.Sequence, packet);
        }
        catch (SocketException ex)
        {
            PacketsDropped++;

            // A stalled link drops every packet: one warning every few seconds is
            // enough to explain the gaps without flooding the log.
            if (DateTimeOffset.UtcNow - _lastSendWarning >= SendWarningInterval)
            {
                _lastSendWarning = DateTimeOffset.UtcNow;
                AppLog.Warn("log.rtp.send_failed", new { dropped = PacketsDropped, err = AirSendError.Describe(ex) });
            }
        }

        _firstPacketOfBurst = false;
        _sequence++;
        _timestamp += (uint)framesInPacket;
    }

    private byte[] BuildPacket(RtpHeader header, ReadOnlySpan<byte> encodedFrame)
    {
        byte[] headerBytes = header.Serialize();

        if (_cipher is null)
        {
            var plain = new byte[headerBytes.Length + encodedFrame.Length];
            headerBytes.CopyTo(plain, 0);
            encodedFrame.CopyTo(plain.AsSpan(headerBytes.Length));
            return plain;
        }

        (byte[] ciphertextWithTag, byte[] nonce) = _cipher.EncryptWithSequence(
            encodedFrame,
            header.Timestamp,
            Ssrc,
            header.Sequence);

        var packet = new byte[headerBytes.Length + ciphertextWithTag.Length + nonce.Length];
        headerBytes.CopyTo(packet, 0);
        ciphertextWithTag.CopyTo(packet, headerBytes.Length);
        nonce.CopyTo(packet, headerBytes.Length + ciphertextWithTag.Length);
        return packet;
    }

    private void Remember(ushort sequence, byte[] packet)
    {
        lock (_historyLock)
        {
            _sentPackets.Enqueue((sequence, packet));
            while (_sentPackets.Count > MaxRememberedPackets)
            {
                _sentPackets.Dequeue();
            }
        }
    }

    /// <summary>
    /// Answers an AirPlay retransmission request (payload type 85): the receiver
    /// asks for a lost sequence number and expects the original packet back with
    /// payload type 86.
    /// </summary>
    public void HandleRetransmitRequest(ReadOnlySpan<byte> request)
    {
        if (request.Length < 16)
        {
            return;
        }

        RtpHeader header = RtpHeader.Parse(request);
        if (header.PayloadType != RtpPayloadTypes.RetransmitRequest)
        {
            return;
        }

        ushort requestedSequence = BinaryPrimitives.ReadUInt16BigEndian(request[12..]);
        byte[]? original = null;

        lock (_historyLock)
        {
            foreach ((ushort sequence, byte[] packet) in _sentPackets)
            {
                if (sequence == requestedSequence)
                {
                    original = packet;
                    break;
                }
            }
        }

        if (original is null)
        {
            AppLog.Debug("log.rtp.retransmit_requested", new { sequence = requestedSequence });
            return;
        }

        try
        {
            var response = new byte[original.Length];
            original.CopyTo(response, 0);
            response[1] = (byte)((response[1] & 0x80) | RtpPayloadTypes.RetransmitResponse);
            _socket.Send(response, response.Length, _destination);
            AppLog.Debug("log.rtp.retransmit_sent", new { sequence = requestedSequence });
        }
        catch (SocketException ex)
        {
            // Same reasoning as in SendFrame: never block the control loop on a send.
            AppLog.Warn("log.rtp.retransmit_failed", new { sequence = requestedSequence, err = AirSendError.Describe(ex) });
        }
    }

    public void Dispose() => _socket.Dispose();

    private static class SocketOptions
    {
    }
}

internal static class UdpClientExtensions
{
    /// <summary>Small send buffer tuning so the audio pump is not the jitter source.</summary>
    public static void ConfigureSocket(this Socket socket)
    {
        try
        {
            socket.SendBufferSize = 256 * 1024;
            socket.SendTimeout = RtpAudioSender.SendTimeoutMs;
        }
        catch (SocketException)
        {
            // Not fatal: some adapters cap the buffer.
        }
    }
}
