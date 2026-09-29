using System.Net;

namespace AirSend.Core.Probe;

public enum ManualEndpointError
{
    Empty,
    ZeroPort,
    Invalid,
}

public sealed class ManualEndpointException(ManualEndpointError error, string input)
    : Exception(Describe(error, input))
{
    public ManualEndpointError Error { get; } = error;

    public string Input { get; } = input;

    private static string Describe(ManualEndpointError error, string input) => error switch
    {
        ManualEndpointError.Empty => "the endpoint is empty",
        ManualEndpointError.ZeroPort => "port 0 is not valid",
        _ => $"'{input}' is not an IP address or IP:port endpoint",
    };
}

public static class ManualEndpoint
{
    public const ushort DefaultAirPlayPort = 7000;

    /// <summary>
    /// Parses <c>192.168.1.50</c>, <c>192.168.1.50:7453</c>, <c>::1</c> or
    /// <c>[::1]:7453</c>. A port embedded in the endpoint wins over the fallback.
    /// Direct port of <c>parse_manual_endpoint</c> in
    /// <c>crates/airplay-core/src/probe.rs</c>.
    /// </summary>
    public static (IPAddress Address, ushort Port) Parse(string input, ushort? fallbackPort = null)
    {
        string trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            throw new ManualEndpointException(ManualEndpointError.Empty, input);
        }

        // The bracketed form has to win over the bare-address parse: .NET accepts
        // "[::1]:7453" as an IPv6 literal and would silently drop the port.
        bool bracketed = trimmed.StartsWith('[');

        if (!bracketed && IPAddress.TryParse(trimmed, out IPAddress? bare))
        {
            ushort port = fallbackPort ?? DefaultAirPlayPort;
            if (port == 0)
            {
                throw new ManualEndpointException(ManualEndpointError.ZeroPort, input);
            }

            return (bare, port);
        }

        if (!TryParseEndpoint(trimmed, out IPAddress? address, out ushort parsedPort))
        {
            throw new ManualEndpointException(ManualEndpointError.Invalid, input);
        }

        if (parsedPort == 0)
        {
            throw new ManualEndpointException(ManualEndpointError.ZeroPort, input);
        }

        return (address!, parsedPort);
    }

    private static bool TryParseEndpoint(string input, out IPAddress? address, out ushort port)
    {
        address = null;
        port = 0;

        string hostPart;
        string portPart;

        if (input.StartsWith('['))
        {
            int closing = input.IndexOf(']');
            if (closing < 0 || closing + 1 >= input.Length || input[closing + 1] != ':')
            {
                return false;
            }

            hostPart = input[1..closing];
            portPart = input[(closing + 2)..];
        }
        else
        {
            int separator = input.LastIndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            hostPart = input[..separator];
            portPart = input[(separator + 1)..];

            // A bare IPv6 address without brackets and without a port is already
            // handled by IPAddress.TryParse; anything else with several colons is
            // ambiguous, so reject it like the Rust implementation does.
            if (hostPart.Contains(':'))
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(hostPart, out IPAddress? parsedAddress) ||
            !ushort.TryParse(portPart, out ushort parsedPort))
        {
            return false;
        }

        address = parsedAddress;
        port = parsedPort;
        return true;
    }

    public static AirPlayDeviceBuilder CreateManualDevice(IPAddress address, ushort? port, string? name)
    {
        ushort resolvedPort = port ?? DefaultAirPlayPort;
        string display = string.IsNullOrWhiteSpace(name) ? $"Dispositivo manual {address}" : name.Trim();
        return new AirPlayDeviceBuilder(address, resolvedPort, display);
    }

    public readonly record struct AirPlayDeviceBuilder(IPAddress Address, ushort Port, string DisplayName);
}
