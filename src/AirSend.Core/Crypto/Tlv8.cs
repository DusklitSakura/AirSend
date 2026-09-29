namespace AirSend.Core.Crypto;

/// <summary>HomeKit TLV8 type tags (port of <c>TlvType</c> in <c>airplay-crypto/src/tlv.rs</c>).</summary>
public enum TlvType : byte
{
    Method = 0x00,
    Identifier = 0x01,
    Salt = 0x02,
    PublicKey = 0x03,
    Proof = 0x04,
    EncryptedData = 0x05,
    State = 0x06,
    Error = 0x07,
    RetryDelay = 0x08,
    Certificate = 0x09,
    Signature = 0x0A,
    Permissions = 0x0B,
    FragmentData = 0x0C,
    FragmentLast = 0x0D,
    SessionId = 0x0E,
    Flags = 0x13,
    Separator = 0xFF,
}

/// <summary>
/// TLV8 codec for HomeKit pairing messages: <c>[type][length][value]</c> with
/// automatic fragmentation of values longer than 255 bytes.
/// Straight port of <c>Tlv8</c> in <c>airplay-crypto/src/tlv.rs</c>.
/// </summary>
public sealed class Tlv8
{
    private readonly Dictionary<byte, byte[]> _items = new();

    public static Tlv8 Parse(ReadOnlySpan<byte> data)
    {
        var tlv = new Tlv8();
        int index = 0;
        byte? lastType = null;

        while (index < data.Length)
        {
            if (index + 2 > data.Length)
            {
                throw new FormatException("TLV8: truncated header");
            }

            byte type = data[index];
            int length = data[index + 1];
            index += 2;

            if (index + length > data.Length)
            {
                throw new FormatException(
                    $"TLV8: truncated value (expected {length} bytes, got {data.Length - index})");
            }

            ReadOnlySpan<byte> value = data.Slice(index, length);
            index += length;

            // Consecutive entries with the same type are fragments of one value.
            if (lastType == type && tlv._items.TryGetValue(type, out byte[]? existing))
            {
                var merged = new byte[existing.Length + value.Length];
                existing.CopyTo(merged, 0);
                value.CopyTo(merged.AsSpan(existing.Length));
                tlv._items[type] = merged;
            }
            else if (tlv._items.TryGetValue(type, out byte[]? prior))
            {
                var merged = new byte[prior.Length + value.Length];
                prior.CopyTo(merged, 0);
                value.CopyTo(merged.AsSpan(prior.Length));
                tlv._items[type] = merged;
            }
            else
            {
                tlv._items[type] = value.ToArray();
            }

            lastType = type;
        }

        return tlv;
    }

    public byte[] Encode()
    {
        var output = new List<byte>();
        foreach (byte type in _items.Keys.OrderBy(k => k))
        {
            byte[] value = _items[type];
            if (value.Length == 0)
            {
                output.Add(type);
                output.Add(0);
                continue;
            }

            for (int offset = 0; offset < value.Length; offset += 255)
            {
                int chunk = Math.Min(255, value.Length - offset);
                output.Add(type);
                output.Add((byte)chunk);
                output.AddRange(value.AsSpan(offset, chunk).ToArray());
            }
        }

        return output.ToArray();
    }

    public byte[]? Get(TlvType type) => _items.TryGetValue((byte)type, out byte[]? value) ? value : null;

    public byte[]? GetRaw(byte type) => _items.TryGetValue(type, out byte[]? value) ? value : null;

    public void Set(TlvType type, ReadOnlySpan<byte> value) => _items[(byte)type] = value.ToArray();

    public void SetRaw(byte type, ReadOnlySpan<byte> value) => _items[type] = value.ToArray();

    public bool Contains(TlvType type) => _items.ContainsKey((byte)type);

    public byte? State => Get(TlvType.State) is { Length: > 0 } state ? state[0] : null;

    public byte? Error => Get(TlvType.Error) is { Length: > 0 } error ? error[0] : null;

    public ushort RetryDelay
    {
        get
        {
            byte[]? value = Get(TlvType.RetryDelay);
            return value switch
            {
                null or { Length: 0 } => 0,
                { Length: 1 } => value[0],
                _ => (ushort)(value[0] | (value[1] << 8)),
            };
        }
    }

    public string? ErrorDescription
    {
        get
        {
            if (Error is not { } code)
            {
                return null;
            }

            string name = code switch
            {
                0x01 => "Unknown",
                0x02 => "Authentication",
                0x03 => "Backoff (rate limited)",
                0x04 => "MaxPeers",
                0x05 => "MaxTries",
                0x06 => "Unavailable",
                0x07 => "Busy",
                _ => "Unknown error code",
            };

            string description = $"Error 0x{code:x2}: {name}";
            return RetryDelay > 0 ? $"{description} (retry after {RetryDelay} seconds)" : description;
        }
    }

    /// <summary>M1 of pair-setup: state 1, method 0 (pair-setup).</summary>
    public static Tlv8 PairSetupM1(bool transient = true)
    {
        var tlv = new Tlv8();
        tlv.Set(TlvType.State, [0x01]);
        tlv.Set(TlvType.Method, [0x00]);
        if (transient)
        {
            // kPairingFlag_Transient = 0x00000010, little-endian u32.
            tlv.Set(TlvType.Flags, [0x10, 0x00, 0x00, 0x00]);
        }

        return tlv;
    }

    /// <summary>M1 of pair-verify: state 1 plus our Curve25519 public key.</summary>
    public static Tlv8 PairVerifyM1(ReadOnlySpan<byte> publicKey)
    {
        var tlv = new Tlv8();
        tlv.Set(TlvType.State, [0x01]);
        tlv.Set(TlvType.PublicKey, publicKey);
        return tlv;
    }
}
