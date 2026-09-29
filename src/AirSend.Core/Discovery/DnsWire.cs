using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AirSend.Core.Discovery;

internal enum DnsRecordType : ushort
{
    A = 1,
    Ns = 2,
    Cname = 5,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Opt = 41,
    Nsec = 47,
    Any = 255,
}

internal sealed class DnsResourceRecord
{
    public required string Name { get; init; }

    public DnsRecordType Type { get; init; }

    public uint TtlSeconds { get; init; }

    public string? DomainName { get; init; }

    public ushort SrvPort { get; init; }

    public byte[]? AddressBytes { get; init; }

    public IReadOnlyList<string> TextEntries { get; init; } = Array.Empty<string>();

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Identity of the record payload, used for de-duplication inside the cache.</summary>
    public string PayloadKey => Type switch
    {
        DnsRecordType.Ptr or DnsRecordType.Cname or DnsRecordType.Ns => DomainName ?? string.Empty,
        DnsRecordType.Srv => $"{DomainName}:{SrvPort}",
        DnsRecordType.A or DnsRecordType.Aaaa => AddressBytes is null ? string.Empty : Convert.ToHexString(AddressBytes),
        DnsRecordType.Txt => string.Join('\u001f', TextEntries),
        _ => string.Empty,
    };

    public IPAddress? ToAddress() =>
        Type switch
        {
            DnsRecordType.A when AddressBytes is { Length: 4 } => new IPAddress(AddressBytes),
            DnsRecordType.Aaaa when AddressBytes is { Length: 16 } => new IPAddress(AddressBytes),
            _ => null,
        };
}

/// <summary>
/// Minimal DNS wire codec: enough of RFC 1035 and RFC 6762 (mDNS) to browse
/// DNS-SD services. Port of the record parsing that <c>mdns-sd</c> performs for
/// <c>crates/airplay-core/src/discovery.rs</c>, without the dependency.
/// </summary>
internal static class DnsWire
{
    public static IReadOnlyList<DnsResourceRecord> ParseRecords(ReadOnlySpan<byte> message)
    {
        var records = new List<DnsResourceRecord>();
        if (message.Length < 12)
        {
            return records;
        }

        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(message[8..]);
        int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(message[10..]);

        int offset = 12;
        for (int i = 0; i < questionCount; i++)
        {
            ReadName(message, ref offset);
            offset += 4;
            if (offset > message.Length)
            {
                return records;
            }
        }

        int total = answerCount + authorityCount + additionalCount;
        for (int i = 0; i < total; i++)
        {
            if (!TryReadRecord(message, ref offset, out var record) || record is null)
            {
                break;
            }

            records.Add(record);
        }

        return records;
    }

    private static bool TryReadRecord(ReadOnlySpan<byte> message, ref int offset, out DnsResourceRecord? record)
    {
        record = null;
        try
        {
            string name = ReadName(message, ref offset);
            if (offset + 10 > message.Length)
            {
                return false;
            }

            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            // Skip the class: mDNS uses the top bit of the class field as the
            // "cache flush" flag, which does not affect parsing.
            ushort recordClass = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            offset += 10;

            if (offset + length > message.Length)
            {
                return false;
            }

            int payloadStart = offset;
            int payloadEnd = offset + length;
            bool isMdns = (recordClass & 0x8000) != 0;

            var result = new DnsResourceRecord
            {
                Name = name,
                Type = type,
                TtlSeconds = ttl == 0 ? 1 : ttl,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl == 0 ? 1 : Math.Min(ttl, 4500)),
            };

            switch (type)
            {
                case DnsRecordType.Ptr:
                case DnsRecordType.Cname:
                case DnsRecordType.Ns:
                {
                    int ptrOffset = payloadStart;
                    string target = ReadName(message, ref ptrOffset);
                    result = new DnsResourceRecord
                    {
                        Name = name,
                        Type = type,
                        TtlSeconds = result.TtlSeconds,
                        ExpiresAt = result.ExpiresAt,
                        DomainName = target,
                    };
                    break;
                }

                case DnsRecordType.Srv:
                {
                    // SRV rdata: priority(2) weight(2) port(2) target-name.
                    // The name therefore starts six bytes into the rdata, not at
                    // its beginning — parsing from offset 0 reads the numeric
                    // fields as label lengths and yields a garbage hostname.
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(message[(payloadStart + 4)..]);
                    int nameOffset = payloadStart + 6;
                    string target = ReadName(message, ref nameOffset);
                    result = new DnsResourceRecord
                    {
                        Name = name,
                        Type = type,
                        TtlSeconds = result.TtlSeconds,
                        ExpiresAt = result.ExpiresAt,
                        SrvPort = port,
                        DomainName = target,
                    };
                    break;
                }

                case DnsRecordType.Txt:
                {
                    var entries = new List<string>();
                    int cursor = payloadStart;
                    while (cursor < payloadEnd)
                    {
                        int entryLength = message[cursor];
                        cursor++;
                        if (cursor + entryLength > payloadEnd)
                        {
                            break;
                        }

                        entries.Add(Encoding.UTF8.GetString(message.Slice(cursor, entryLength)));
                        cursor += entryLength;
                    }

                    result = new DnsResourceRecord
                    {
                        Name = name,
                        Type = type,
                        TtlSeconds = result.TtlSeconds,
                        ExpiresAt = result.ExpiresAt,
                        TextEntries = entries,
                    };
                    break;
                }

                case DnsRecordType.A:
                case DnsRecordType.Aaaa:
                {
                    result = new DnsResourceRecord
                    {
                        Name = name,
                        Type = type,
                        TtlSeconds = result.TtlSeconds,
                        ExpiresAt = result.ExpiresAt,
                        AddressBytes = message.Slice(payloadStart, length).ToArray(),
                    };
                    break;
                }

                default:
                    // NSEC/OPT/unknown records are ignored, but still need to be
                    // skipped so the caller keeps the right offset.
                    break;
            }

            _ = isMdns;
            offset = payloadEnd;
            record = result;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    public static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var builder = new StringBuilder();
        int position = offset;
        int jumps = 0;
        bool jumped = false;

        while (true)
        {
            if (position < 0 || position >= message.Length)
            {
                break;
            }

            byte labelLength = message[position];
            if (labelLength == 0)
            {
                position++;
                break;
            }

            if ((labelLength & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length)
                {
                    break;
                }

                int pointer = ((labelLength & 0x3F) << 8) | message[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                position = pointer;
                if (++jumps > 64)
                {
                    break;
                }

                continue;
            }

            position++;
            if (position + labelLength > message.Length)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(message.Slice(position, labelLength)));
            position += labelLength;
        }

        if (!jumped)
        {
            offset = position;
        }

        return builder.ToString();
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0);
    }

    /// <summary>
    /// Builds a DNS-SD PTR query. <paramref name="requestUnicastResponse"/> sets the QU
    /// bit (RFC 6762 §5.4) so that sleepy receivers answer us directly instead of
    /// multicasting to the whole link, which is what <c>mdns-sd</c> does as well.
    /// </summary>
    public static byte[] BuildPtrQuery(string serviceType, ushort transactionId, bool requestUnicastResponse = true)
    {
        var buffer = new List<byte>(64);
        buffer.Add((byte)(transactionId >> 8));
        buffer.Add((byte)(transactionId & 0xFF));
        buffer.AddRange(new byte[] { 0x00, 0x00 }); // flags: standard query
        buffer.AddRange(new byte[] { 0x00, 0x01 }); // QDCOUNT
        buffer.AddRange(new byte[] { 0x00, 0x00 }); // ANCOUNT
        buffer.AddRange(new byte[] { 0x00, 0x00 }); // NSCOUNT
        buffer.AddRange(new byte[] { 0x00, 0x00 }); // ARCOUNT

        WriteName(buffer, serviceType);
        buffer.AddRange(new byte[] { 0x00, 0x0C }); // PTR
        ushort qclass = requestUnicastResponse ? (ushort)0x8001 : (ushort)0x0001;
        buffer.Add((byte)(qclass >> 8));
        buffer.Add((byte)(qclass & 0xFF));

        return buffer.ToArray();
    }

    /// <summary>Builds a query for a concrete instance name (SRV/TXT/ANY lookups).</summary>
    public static byte[] BuildAnyQuery(string name, ushort transactionId, bool requestUnicastResponse = true)
    {
        var buffer = new List<byte>(64);
        buffer.Add((byte)(transactionId >> 8));
        buffer.Add((byte)(transactionId & 0xFF));
        buffer.AddRange(new byte[] { 0x00, 0x00 });
        buffer.AddRange(new byte[] { 0x00, 0x01 });
        buffer.AddRange(new byte[] { 0x00, 0x00 });
        buffer.AddRange(new byte[] { 0x00, 0x00 });
        buffer.AddRange(new byte[] { 0x00, 0x00 });

        WriteName(buffer, name);
        buffer.AddRange(new byte[] { 0x00, 0xFF }); // ANY
        ushort qclass = requestUnicastResponse ? (ushort)0x8001 : (ushort)0x0001;
        buffer.Add((byte)(qclass >> 8));
        buffer.Add((byte)(qclass & 0xFF));

        return buffer.ToArray();
    }
}
