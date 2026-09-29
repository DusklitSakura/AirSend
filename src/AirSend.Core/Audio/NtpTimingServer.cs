using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirSend.Core.Logging;

namespace AirSend.Core.Audio;

/// <summary>
/// Minimal NTP style timing server: the receiver asks "what time is it?" and uses
/// the answer to align its playback clock with ours.
/// Port of <c>NtpTimingServer</c> from the <c>airplay-timing</c> crate.
/// </summary>
public sealed class NtpTimingServer : IDisposable
{
    private const int PacketSize = 32;

    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public NtpTimingServer()
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public int Port { get; }

    public long RequestsAnswered { get; private set; }

    /// <summary>Timing requests received from the receiver (UDP payload type 82).</summary>
    public long RequestsReceived { get; private set; }

    /// <summary>Raw datagrams received, whatever their payload type.</summary>
    public long DatagramsReceived { get; private set; }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                AppLog.Warn($"NTP timing socket error: {ex.Message}");
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                DatagramsReceived++;
                if (result.Buffer.Length >= 2)
                {
                    byte payloadType = (byte)(result.Buffer[1] & 0x7F);
                    if (payloadType == RtpPayloadTypes.TimingRequest)
                    {
                        RequestsReceived++;
                    }

                    if (DatagramsReceived <= 3)
                    {
                        AppLog.Debug(
                            $"NTP timing: {result.Buffer.Length} bytes de {result.RemoteEndPoint}, tipo {payloadType}");
                    }
                }

                byte[]? response = BuildResponse(result.Buffer);
                if (response is null)
                {
                    continue;
                }

                await _socket.SendAsync(response, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                RequestsAnswered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Debug($"NTP timing: petición descartada ({ex.Message})");
            }
        }
    }

    internal static byte[]? BuildResponse(ReadOnlySpan<byte> request)
    {
        if (request.Length < PacketSize)
        {
            return null;
        }

        byte payloadType = (byte)(request[1] & 0x7F);
        if (payloadType != RtpPayloadTypes.TimingRequest)
        {
            return null;
        }

        var response = new byte[PacketSize];
        response[0] = request[0];
        response[1] = (byte)((request[1] & 0x80) | RtpPayloadTypes.TimingResponse);
        request[2..4].CopyTo(response.AsSpan(2));

        // Origin timestamp: echo of the client's transmit timestamp (bytes 24..32).
        request[24..32].CopyTo(response.AsSpan(8));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        WriteTimestamp(response.AsSpan(16), now);
        WriteTimestamp(response.AsSpan(24), now);
        return response;
    }

    /// <summary>64 bit NTP timestamp: seconds since 1900 in the high word.</summary>
    internal static void WriteTimestamp(Span<byte> destination, DateTimeOffset time)
    {
        const double ntpEpochOffsetSeconds = 2208988800d;
        double seconds = time.ToUnixTimeMilliseconds() / 1000d + ntpEpochOffsetSeconds;
        uint whole = (uint)Math.Floor(seconds);
        uint fraction = (uint)((seconds - whole) * 4294967296d);
        BinaryPrimitives.WriteUInt32BigEndian(destination, whole);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], fraction);
    }

    /// <summary>Current time as a 64 bit NTP timestamp, the format sync packets use.</summary>
    public static ulong NowNtpTimestamp()
    {
        const double ntpEpochOffsetSeconds = 2208988800d;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        double seconds = now.ToUnixTimeMilliseconds() / 1000d + ntpEpochOffsetSeconds;
        ulong whole = (ulong)Math.Floor(seconds);
        ulong fraction = (ulong)((seconds - whole) * 4294967296d);
        return (whole << 32) | (fraction & 0xFFFFFFFF);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
        _ = _loop;
    }
}
