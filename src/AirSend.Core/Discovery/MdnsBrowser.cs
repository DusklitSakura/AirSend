using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AirSend.Core.Logging;

namespace AirSend.Core.Discovery;

/// <summary>A DNS-SD service instance resolved from the local link.</summary>
public sealed record MdnsService(
    string ServiceType,
    string InstanceName,
    string FullName,
    string HostName,
    ushort Port,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyDictionary<string, string> Text)
{
    /// <summary>
    /// The <c>features</c> TXT key is announced as either <c>features</c> or the
    /// legacy <c>ft</c> alias; AirSend accepts both (see upstream discovery.rs).
    /// </summary>
    public string? Features =>
        Text.TryGetValue("features", out var features) ? features
        : Text.TryGetValue("ft", out var ft) ? ft
        : null;

    public string? Model => Text.TryGetValue("model", out var model) ? model : null;

    public string? DeviceId => Text.TryGetValue("deviceid", out var id) ? id : null;

    public string? SourceVersion => Text.TryGetValue("srcvers", out var version) ? version : null;
}

/// <summary>
/// Multicast DNS-SD browser written against <see cref="Socket"/> directly: the C#
/// port of what <c>mdns-sd</c> provides to
/// <c>crates/airplay-core/src/discovery.rs</c>.
/// </summary>
/// <remarks>
/// The browser keeps a record cache because mDNS responders only answer queries
/// with new information: after the first round of announcements the link goes
/// quiet, so a rescan must be served from the cache (upstream keeps a cache for
/// exactly the same reason).
/// </remarks>
public sealed class MdnsBrowser : IAsyncDisposable
{
    private const int MdnsPort = 5353;
    private static readonly string[] KnownServiceTypes = [AirPlayDiscovery.AirPlayService, AirPlayDiscovery.RaopService];
    private static readonly IPAddress MdnsMulticastV4 = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress MdnsMulticastV6 = IPAddress.Parse("ff02::fb");

    private readonly List<Socket> _sockets = new();
    /// <summary>
    /// Extra socket bound to an ephemeral port. mDNS queries sent from here carry
    /// the QU bit, so responders answer straight back to us instead of to port
    /// 5353 — necessary because a single unicast datagram is only delivered to one
    /// socket on that port, and security suites (Kaspersky) also listen on 5353.
    /// </summary>
    private Socket? _unicastSocket;
    private readonly List<Task> _receiveLoops = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, DnsResourceRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastSignature = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _browsedTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingInstanceQueries = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private ushort _transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);

    /// <summary>Raised whenever an instance is resolved, and again if its records change.</summary>
    public event Action<MdnsService>? ServiceResolved;

    /// <summary>
    /// Every datagram received on the mDNS port, for diagnostics and packet
    /// capture. Not part of the app contract — the UI never subscribes to it.
    /// </summary>
    public event Action<byte[]>? RawPacketReceived;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start(IEnumerable<string> serviceTypes)
    {
        Stop();

        foreach (string type in serviceTypes)
        {
            _browsedTypes.Add(type);
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        OpenSockets();

        foreach (Socket socket in _sockets)
        {
            _receiveLoops.Add(Task.Run(() => ReceiveLoopAsync(socket, token), CancellationToken.None));
        }

        if (_unicastSocket is not null)
        {
            _receiveLoops.Add(Task.Run(() => ReceiveLoopAsync(_unicastSocket, token), CancellationToken.None));
        }

        _receiveLoops.Add(Task.Run(() => QueryLoopAsync(token), CancellationToken.None));
    }

    public void Stop()
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }

        foreach (Socket socket in _sockets)
        {
            try
            {
                socket.Close();
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }
        }

        _sockets.Clear();

        try
        {
            _unicastSocket?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Ignore.
        }

        _unicastSocket = null;
        _receiveLoops.Clear();

        _cts?.Dispose();
        _cts = null;

        lock (_cacheLock)
        {
            _records.Clear();
            _lastSignature.Clear();
            _pendingInstanceQueries.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask;
    }

    private void OpenSockets()
    {
        Socket? v4 = TryOpenSocket(AddressFamily.InterNetwork);
        if (v4 is not null)
        {
            _sockets.Add(v4);
        }

        Socket? v6 = TryOpenSocket(AddressFamily.InterNetworkV6);
        if (v6 is not null)
        {
            _sockets.Add(v6);
        }

        _unicastSocket = TryOpenUnicastSocket(AddressFamily.InterNetwork);
    }

    private static Socket? TryOpenUnicastSocket(AddressFamily family)
    {
        try
        {
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    AppLog.Debug("log.mdns.unicast_socket", new
                    {
                        port = ((IPEndPoint)socket.LocalEndPoint!).Port,
                    });
            return socket;
        }
        catch (SocketException ex)
        {
                    AppLog.Debug("log.mdns.no_unicast_socket", new { error = ex.SocketErrorCode });
            return null;
        }
    }

    private static Socket? TryOpenSocket(AddressFamily family)
    {
        try
        {
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
            socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, MdnsPort));
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 64 * 1024);
            if (family == AddressFamily.InterNetworkV6)
            {
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            }

            JoinMulticastGroups(socket, family);
                    AppLog.Debug("log.mdns.socket_ready", new { family });
            return socket;
        }
        catch (SocketException ex)
        {
                    AppLog.Debug("log.mdns.socket_unavailable", new { family, error = ex.SocketErrorCode });
            return null;
        }
    }

    private static void JoinMulticastGroups(Socket socket, AddressFamily family)
    {
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    !nic.SupportsMulticast ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != family)
                    {
                        continue;
                    }

                    try
                    {
                        if (family == AddressFamily.InterNetwork)
                        {
                            socket.SetSocketOption(
                                SocketOptionLevel.IP,
                                SocketOptionName.AddMembership,
                                new MulticastOption(MdnsMulticastV4, info.Address));
                        }
                        else
                        {
                            socket.SetSocketOption(
                                SocketOptionLevel.IPv6,
                                SocketOptionName.AddMembership,
                                new IPv6MulticastOption(MdnsMulticastV6, info.Address.ScopeId));
                        }
                    }
                    catch (SocketException)
                    {
                        // Interface refuses the group (e.g. VPN adapters): skip it.
                        AppLog.Debug("log.mdns.multicast_rejected", new { nic = nic.Name });
                        continue;
                    }

                        AppLog.Debug("log.mdns.joined", new { nic = nic.Name, address = info.Address });
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No adapter information available: queries still go out via the default route.
        }
    }

    private async Task QueryLoopAsync(CancellationToken token)
    {
        // Burst at the start so the device list fills quickly, then back off: the
        // initial burst mirrors the "scan devices" button behaviour of the app.
        int[] scheduleSeconds = [0, 1, 2, 4, 8, 16, 32];
        int index = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                SendQuery();

                int delay = index < scheduleSeconds.Length ? scheduleSeconds[index] : 60;
                index++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(delay, 1)), token).ConfigureAwait(false);
                PurgeExpired();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void SendQuery(string? specificName = null)
    {
        byte[] payload = specificName is null
            ? BuildBrowserQuery(out ushort _)
            : DnsWire.BuildAnyQuery(specificName, NextTransactionId());

        // Ask for unicast answers from the ephemeral port: this is the path that
        // survives sharing port 5353 with other mDNS listeners.
        if (_unicastSocket is not null)
        {
            try
            {
                _unicastSocket.SendTo(payload, new IPEndPoint(MdnsMulticastV4, MdnsPort));
            }
            catch (SocketException)
            {
                AppLog.Debug("log.mdns.unicast_send_failed");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        foreach (Socket socket in _sockets)
        {
            try
            {
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    socket.SendTo(payload, new IPEndPoint(MdnsMulticastV4, MdnsPort));
                }
                else
                {
                    socket.SendTo(payload, new IPEndPoint(MdnsMulticastV6, MdnsPort));
                }
            }
            catch (SocketException)
            {
                // Adapter went away; the next query will retry.
                    AppLog.Debug("log.mdns.send_failed");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private byte[] BuildBrowserQuery(out ushort transactionId)
    {
        // One PTR question per browsed service type, packed into a single message
        // by concatenating questions after a shared header.
        var body = new List<byte>();
        var types = _browsedTypes.ToList();
        foreach (string type in types)
        {
            byte[] single = DnsWire.BuildPtrQuery(type, 0);
            body.AddRange(single.AsSpan(12).ToArray());
        }

        transactionId = NextTransactionId();
        var message = new List<byte>();
        message.Add((byte)(transactionId >> 8));
        message.Add((byte)(transactionId & 0xFF));
        message.AddRange(new byte[] { 0x00, 0x00 });
        message.Add((byte)(types.Count >> 8));
        message.Add((byte)(types.Count & 0xFF));
        message.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        message.AddRange(body);
        return message.ToArray();
    }

    private ushort NextTransactionId() => unchecked(_transactionId++);

    /// <summary>
    /// True for the AirPlay service types this browser cares about, whether or not
    /// <see cref="Start"/> has run yet (tests and cache replays rely on this).
    /// </summary>
    private bool IsBrowsedService(string name) =>
        _browsedTypes.Contains(name) ||
        KnownServiceTypes.Contains(name, StringComparer.OrdinalIgnoreCase);

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[9000];

        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (received <= 0)
            {
                continue;
            }

                AppLog.Debug("log.mdns.received", new { bytes = received, family = socket.AddressFamily });

            if (RawPacketReceived is { } rawHandler)
            {
                rawHandler(buffer.AsSpan(0, received).ToArray());
            }

            try
            {
                ProcessMessage(buffer.AsSpan(0, received));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            Logging.AppLog.Debug("log.mdns.discarded", new { type = ex.GetType().Name, err = AirSendError.Describe(ex) });
            }
        }
    }

    internal void ProcessMessage(ReadOnlySpan<byte> message)
    {
        IReadOnlyList<DnsResourceRecord> records = DnsWire.ParseRecords(message);
        if (records.Count == 0)
        {
            return;
        }

        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_cacheLock)
        {
            foreach (DnsResourceRecord record in records)
            {
                _records[$"{record.Name}|{record.Type}|{record.PayloadKey}"] = record;

                if (record.Type == DnsRecordType.Ptr && record.DomainName is not null &&
                    IsBrowsedService(record.Name))
                {
                    instances.Add(record.DomainName);
                }
            }
        }

        // Any SRV record we just heard about can also be resolved, even when the
        // matching PTR arrived in an earlier packet.
        foreach (DnsResourceRecord record in records)
        {
            if (record.Type == DnsRecordType.Srv)
            {
                instances.Add(record.Name);
            }
        }

        foreach (string instance in instances)
        {
            if (!TryResolve(instance, out MdnsService? service) || service is null)
            {
                RequestInstanceDetails(instance);
                continue;
            }

            string signature = string.Join(
                '|',
                service.HostName,
                service.Port,
                string.Join(',', service.Addresses.Select(a => a.ToString())),
                string.Join(',', service.Text.Select(kv => $"{kv.Key}={kv.Value}")));

            bool changed;
            lock (_cacheLock)
            {
                changed = !_lastSignature.TryGetValue(instance, out string? previous) || previous != signature;
                if (changed)
                {
                    _lastSignature[instance] = signature;
                }
            }

            if (changed)
            {
                ServiceResolved?.Invoke(service);
            }
        }
    }

    private void RequestInstanceDetails(string instance)
    {
        bool shouldQuery;
        lock (_cacheLock)
        {
            shouldQuery = _pendingInstanceQueries.Add(instance);
        }

        if (shouldQuery)
        {
            SendQuery(instance);
        }
    }

    private bool TryResolve(string instance, out MdnsService? service)
    {
        service = null;
        DnsResourceRecord? srv = null;
        IReadOnlyList<string> text = Array.Empty<string>();
        string? serviceType = null;

        lock (_cacheLock)
        {
            foreach (DnsResourceRecord record in _records.Values)
            {
                if (!string.Equals(record.Name, instance, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (record.Type)
                {
                    case DnsRecordType.Srv:
                        srv = record;
                        break;
                    case DnsRecordType.Txt:
                        text = record.TextEntries;
                        break;
                }
            }

            // The instance name carries its service type ("X._airplay._tcp.local"),
            // so resolution does not depend on the browse list having been filled
            // in first.
            serviceType = KnownServiceTypes
                .Concat(_browsedTypes)
                .FirstOrDefault(type => instance.EndsWith("." + type, StringComparison.OrdinalIgnoreCase));
        }

        if (srv?.DomainName is null || serviceType is null)
        {
            return false;
        }

        var addresses = new List<IPAddress>();
        lock (_cacheLock)
        {
            foreach (DnsResourceRecord record in _records.Values)
            {
                if (!string.Equals(record.Name, srv.DomainName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IPAddress? address = record.ToAddress();
                if (address is not null && !addresses.Contains(address))
                {
                    addresses.Add(address);
                }
            }
        }

        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in text)
        {
            int separator = entry.IndexOf('=');
            if (separator > 0)
            {
                dictionary[entry[..separator]] = entry[(separator + 1)..];
            }
            else if (entry.Length > 0)
            {
                dictionary[entry] = string.Empty;
            }
        }

        string instanceName = instance.Split('.')[0];
        service = new MdnsService(
            serviceType,
            instanceName,
            instance,
            srv.DomainName.TrimEnd('.'),
            srv.SrvPort,
            addresses,
            dictionary);
        return true;
    }

    private void PurgeExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_cacheLock)
        {
            foreach (string key in _records
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _records.Remove(key);
            }

            _pendingInstanceQueries.Clear();
        }
    }
}
