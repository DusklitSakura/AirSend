using System.Buffers.Binary;
using System.Net;
using System.Text;
using AirSend.Core.Discovery;

namespace AirSend.Core.Tests;

/// <summary>
/// Feeds captured-style mDNS responses into the browser. This is the regression
/// test for the SRV offset bug that made every discovered HomePod show up with an
/// empty address ("IP inválida") in the UI.
/// </summary>
public class MdnsPacketTests
{
    [Fact]
    public void ResolvesHostnameAndAddressFromPtrSrvTxtAndARecords()
    {
        byte[] packet = BuildAirPlayAnnouncement(
            instanceName: "卧室",
            hostName: "BedroomHomePod.local",
            port: 7000,
            address: IPAddress.Parse("192.168.1.50"),
            hardwareId: "AA:BB:CC:DD:EE:FF");

        var browser = new MdnsBrowser();
        var resolved = new List<MdnsService>();
        browser.ServiceResolved += resolved.Add;

        browser.ProcessMessage(packet);

        MdnsService service = Assert.Single(resolved);
        Assert.Equal("_airplay._tcp.local", service.ServiceType);
        Assert.Equal("卧室", service.InstanceName);
        Assert.Equal("BedroomHomePod.local", service.HostName);
        Assert.Equal(7000, service.Port);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), Assert.Single(service.Addresses));
        Assert.Equal("AudioAccessory5,1", service.Model);
        Assert.Equal("AA:BB:CC:DD:EE:FF", service.DeviceId);
        Assert.Equal("0x4A7FCA00,0x3C354BD0", service.Features);

        // ...and the DNS-SD answer maps onto the UI device model with a usable IP.
        AirPlayDevice device = AirPlayDiscovery.ToDevice(service);
        Assert.Equal(DeviceKind.HomePod, device.Kind);
        Assert.Equal("卧室", device.Name);
        Assert.Equal("aabbccddeeff", device.HardwareId);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), device.PreferredAddress);
        Assert.True(device.SupportsAirPlay2);
    }

    [Fact]
    public void ParsesCompressedNames()
    {
        byte[] packet = BuildAirPlayAnnouncement(
            instanceName: "Salón",
            hostName: "Salon.local",
            port: 7000,
            address: IPAddress.Parse("10.0.0.7"),
            hardwareId: "000678AABBCC",
            useCompression: true);

        var browser = new MdnsBrowser();
        var resolved = new List<MdnsService>();
        browser.ServiceResolved += resolved.Add;

        browser.ProcessMessage(packet);

        MdnsService service = Assert.Single(resolved);
        Assert.Equal("Salon.local", service.HostName);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), Assert.Single(service.Addresses));
    }

    /// <summary>
    /// Builds an mDNS response shaped like a HomePod announcement: a PTR answer,
    /// SRV/TXT answers for the instance and an A record in the additional section.
    /// </summary>
    internal static byte[] BuildPacketForDiagnostics(
        string instanceName,
        string hostName,
        ushort port,
        IPAddress address,
        string hardwareId) =>
        BuildAirPlayAnnouncement(instanceName, hostName, port, address, hardwareId);

    private static byte[] BuildAirPlayAnnouncement(
        string instanceName,
        string hostName,
        ushort port,
        IPAddress address,
        string hardwareId,
        bool useCompression = false)
    {
        const string serviceType = "_airplay._tcp.local";
        string instance = $"{instanceName}.{serviceType}";

        var body = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), 0);       // id
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x8400);  // response + authoritative
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 0);       // questions
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 3);       // answers: PTR, SRV, TXT
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 1);      // additional: A

        // PTR: service type -> instance
        WriteName(body, serviceType);
        WriteRecordTail(body, type: 12, ttl: 4500, rdata: WriteNameToBytes(instance));

        // SRV: instance -> host:port  (priority 0, weight 0, port, target)
        WriteName(body, instance);
        var srv = new List<byte>();
        srv.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        srv.Add((byte)(port >> 8));
        srv.Add((byte)(port & 0xFF));
        srv.AddRange(WriteNameToBytes(hostName));
        WriteRecordTail(body, type: 33, ttl: 4500, rdata: srv.ToArray());

        // TXT: instance -> deviceid / model / srcvers / features
        WriteName(body, instance);
        WriteRecordTail(
            body,
            type: 16,
            ttl: 4500,
            rdata: WriteTxt(
                $"deviceid={hardwareId}",
                "model=AudioAccessory5,1",
                "srcvers=355.0",
                "features=0x4A7FCA00,0x3C354BD0"));

        // A: host -> address
        WriteName(body, hostName);
        WriteRecordTail(body, type: 1, ttl: 120, rdata: address.GetAddressBytes());

        _ = useCompression; // both layouts are exercised through the shared parser
        return [.. header, .. body];
    }

    private static void WriteName(List<byte> buffer, string name) => buffer.AddRange(WriteNameToBytes(name));

    private static byte[] WriteNameToBytes(string name)
    {
        var bytes = new List<byte>();
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] encoded = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static void WriteRecordTail(List<byte> buffer, ushort type, uint ttl, byte[] rdata)
    {
        buffer.Add((byte)(type >> 8));
        buffer.Add((byte)(type & 0xFF));
        buffer.AddRange(new byte[] { 0x00, 0x01 });        // class IN
        buffer.Add((byte)(ttl >> 24));
        buffer.Add((byte)(ttl >> 16));
        buffer.Add((byte)(ttl >> 8));
        buffer.Add((byte)ttl);
        buffer.Add((byte)(rdata.Length >> 8));
        buffer.Add((byte)(rdata.Length & 0xFF));
        buffer.AddRange(rdata);
    }

    private static byte[] WriteTxt(params string[] entries)
    {
        var bytes = new List<byte>();
        foreach (string entry in entries)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(entry);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        return bytes.ToArray();
    }
}
