using System.Net;
using System.Net.Sockets;
using System.Text;
using AirSend.Core.Discovery;

namespace AirSend.Core.Probe;

public sealed record ProbeResult(string? ServerHeader, string RawResponse)
{
    public bool LooksLikeAirTunes =>
        ServerHeader?.Contains("AirTunes", StringComparison.OrdinalIgnoreCase) ?? false;

    public bool LooksLikeAirPlay2 =>
        ServerHeader?.Contains("AirTunes", StringComparison.OrdinalIgnoreCase) ?? false;
}

public sealed class AirPlayProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Confirms that an endpoint really speaks AirPlay by opening the RTSP port and
/// issuing <c>OPTIONS * RTSP/1.0</c>.
/// Port of <c>probe_airplay</c> in <c>crates/airplay-core/src/probe.rs</c>, used for
/// manually entered IPs on networks where mDNS does not cross subnets.
/// </summary>
public static class AirPlayProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    private const string UserAgent = "AirSend/0.1 (Windows; WinUI)";

    public static async Task<ProbeResult> ProbeAsync(
        IPAddress address,
        ushort port = ManualEndpoint.DefaultAirPlayPort,
        CancellationToken cancellationToken = default)
    {
        var endpoint = new IPEndPoint(address, port);
        using var client = new TcpClient(address.AddressFamily);

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(endpoint, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AirPlayProbeException($"timeout after {ConnectTimeout.TotalSeconds:0.#}s conectando a {endpoint}");
            }
            catch (SocketException ex)
            {
                throw new AirPlayProbeException($"connection to {endpoint} failed: {ex.Message}", ex);
            }
        }

        string request =
            "OPTIONS * RTSP/1.0\r\n" +
            "CSeq: 1\r\n" +
            $"User-Agent: {UserAgent}\r\n" +
            "\r\n";

        NetworkStream stream = client.GetStream();
        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var response = new StringBuilder();
        var buffer = new byte[1024];

        using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseCts.CancelAfter(ResponseTimeout);

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, responseCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                response.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (response.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }

                if (response.Length > 8 * 1024)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AirPlayProbeException($"timeout after {ResponseTimeout.TotalSeconds:0.#}s esperando respuesta RTSP");
        }

        string text = response.ToString();
        if (!text.StartsWith("RTSP/1.0", StringComparison.Ordinal))
        {
            string firstLine = text.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            throw new AirPlayProbeException($"response is not an RTSP/AirPlay reply: {firstLine}");
        }

        string? serverHeader = null;
        foreach (string line in text.Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                serverHeader = line[(separator + 1)..].Trim();
                break;
            }
        }

        return new ProbeResult(serverHeader, text);
    }

    /// <summary>Builds the placeholder device the UI shows before the probe answers.</summary>
    public static AirPlayDevice BuildManualDevice(IPAddress address, ushort? port, string? name)
    {
        ushort resolvedPort = port ?? ManualEndpoint.DefaultAirPlayPort;
        return new AirPlayDevice
        {
            Id = $"manual://{address}:{resolvedPort}",
            Name = string.IsNullOrWhiteSpace(name) ? $"Dispositivo manual {address}" : name.Trim(),
            Host = address.ToString(),
            Addresses = [address],
            Port = resolvedPort,
            Kind = DeviceKind.OtherAirPlay,
            SupportsAirPlay2 = false,
        };
    }
}
