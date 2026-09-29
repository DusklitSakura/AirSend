using System.Buffers.Binary;
using System.Text;

namespace AirSend.Core.Rtsp;

/// <summary>
/// Binary property list (bplist00) reader and writer: the payload format AirPlay 2
/// uses for <c>GET /info</c>, SETUP and SET_PARAMETER bodies.
/// Counterpart of <c>airplay-rtsp/src/plist_codec.rs</c>.
/// </summary>
public static class BinaryPlist
{
    private const string Header = "bplist00";
    private static readonly object NullSentinel = new();

    public static byte[] Write(object? value)
    {
        var objects = new List<object?>();
        var indexOf = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var children = new Dictionary<int, int[]>();

        // Post-order walk: the bplist object table must list every object after the
        // objects it references, so children are collected before their parent.
        int Collect(object? node)
        {
            object key = node ?? NullSentinel;
            if (indexOf.TryGetValue(key, out int existing))
            {
                return existing;
            }

            int[] childIndexes = [];
            switch (node)
            {
                case Dictionary<string, object?> dictionary:
                {
                    var collected = new List<int>(dictionary.Count * 2);
                    foreach (string name in dictionary.Keys)
                    {
                        collected.Add(Collect(name));
                    }

                    foreach (object? item in dictionary.Values)
                    {
                        collected.Add(Collect(item));
                    }

                    childIndexes = collected.ToArray();
                    break;
                }

                case List<object?> list:
                {
                    var collected = new List<int>(list.Count);
                    foreach (object? item in list)
                    {
                        collected.Add(Collect(item));
                    }

                    childIndexes = collected.ToArray();
                    break;
                }
            }

            int index = objects.Count;
            indexOf[key] = index;
            objects.Add(node);
            children[index] = childIndexes;
            return index;
        }

        int rootIndex = Collect(value);
        int objectCount = objects.Count;

        int indexSize = objectCount <= 0xFF ? 1 : objectCount <= 0xFFFF ? 2 : 4;

        // Serialize the object table; children already carry lower indices.
        var table = new List<byte>();
        var offsets = new int[objectCount];
        for (int i = 0; i < objectCount; i++)
        {
            // Offsets in the trailer table are absolute file offsets, i.e. they
            // include the 8 byte "bplist00" header.
            offsets[i] = Header.Length + table.Count;
            WriteObject(table, objects[i], children.TryGetValue(i, out int[]? childIndexes) ? childIndexes : null, indexSize);
        }

        int totalSize = Header.Length + table.Count + objectCount * 4;
        int offsetSize = totalSize <= 0xFF ? 1 : totalSize <= 0xFFFF ? 2 : 4;
        int offsetTableOffset = Header.Length + table.Count;

        foreach (int offset in offsets)
        {
            WriteBigEndian(table, (ulong)offset, offsetSize);
        }

        var output = new List<byte>(table.Count + 40);
        output.AddRange(Encoding.ASCII.GetBytes(Header));
        output.AddRange(table);

        var trailer = new byte[32];
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)indexSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objectCount);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), (ulong)rootIndex);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)offsetTableOffset);
        output.AddRange(trailer);

        return output.ToArray();
    }

    private static void WriteObject(List<byte> buffer, object? value, int[]? children, int indexSize)
    {
        switch (value)
        {
            case null:
                buffer.Add(0x00);
                break;

            case bool boolean:
                buffer.Add(boolean ? (byte)0x09 : (byte)0x08);
                break;

            case long number:
                WriteInteger(buffer, number);
                break;

            case int number:
                WriteInteger(buffer, number);
                break;

            case double number:
                buffer.Add(0x23);
                WriteBigEndian(buffer, BitConverter.DoubleToUInt64Bits(number), 8);
                break;

            case byte[] data:
                WriteCount(buffer, 0x40, data.Length);
                buffer.AddRange(data);
                break;

            case string text:
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(text);
                if (utf8.Length == text.Length)
                {
                    WriteCount(buffer, 0x50, utf8.Length);
                    buffer.AddRange(utf8);
                }
                else
                {
                    byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
                    WriteCount(buffer, 0x60, utf16.Length / 2);
                    buffer.AddRange(utf16);
                }

                break;
            }

            case Dictionary<string, object?> dictionary:
            {
                WriteCount(buffer, 0xD0, dictionary.Count);
                foreach (int child in children ?? [])
                {
                    WriteBigEndian(buffer, (ulong)child, indexSize);
                }

                break;
            }

            case List<object?> list:
            {
                WriteCount(buffer, 0xA0, list.Count);
                foreach (int child in children ?? [])
                {
                    WriteBigEndian(buffer, (ulong)child, indexSize);
                }

                break;
            }

            default:
                throw new NotSupportedException($"plist type {value.GetType().Name} is not supported");
        }
    }

    private static void WriteInteger(List<byte> buffer, long value)
    {
        if (value is >= 0 and <= 0xFF)
        {
            buffer.Add(0x10);
            buffer.Add((byte)value);
        }
        else if (value is >= 0 and <= 0xFFFF)
        {
            buffer.Add(0x11);
            WriteBigEndian(buffer, (ulong)value, 2);
        }
        else if (value is >= 0 and <= 0xFFFFFFFF)
        {
            buffer.Add(0x12);
            WriteBigEndian(buffer, (ulong)value, 4);
        }
        else
        {
            buffer.Add(0x13);
            WriteBigEndian(buffer, (ulong)value, 8);
        }
    }

    private static void WriteCount(List<byte> buffer, byte marker, int count)
    {
        if (count < 15)
        {
            buffer.Add((byte)(marker | count));
            return;
        }

        buffer.Add((byte)(marker | 0x0F));
        if (count <= 0xFF)
        {
            buffer.Add(0x10);
            buffer.Add((byte)count);
        }
        else if (count <= 0xFFFF)
        {
            buffer.Add(0x11);
            WriteBigEndian(buffer, (ulong)count, 2);
        }
        else
        {
            buffer.Add(0x12);
            WriteBigEndian(buffer, (ulong)count, 4);
        }
    }

    private static void WriteBigEndian(List<byte> buffer, ulong value, int size)
    {
        for (int i = size - 1; i >= 0; i--)
        {
            buffer.Add((byte)((value >> (i * 8)) & 0xFF));
        }
    }

    public static object? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data[..8].SequenceEqual(Encoding.ASCII.GetBytes(Header)))
        {
            throw new FormatException("not a binary plist");
        }

        int offsetSize = data[^26];
        int referenceSize = data[^25];
        long objectCount = (long)BinaryPrimitives.ReadUInt64BigEndian(data[^24..]);
        long rootIndex = (long)BinaryPrimitives.ReadUInt64BigEndian(data[^16..]);
        long offsetTableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(data[^8..]);

        if (objectCount <= 0 || offsetSize is < 1 or > 8)
        {
            throw new FormatException("invalid binary plist trailer");
        }

        var offsets = new long[objectCount];
        for (int i = 0; i < objectCount; i++)
        {
            offsets[i] = ReadBigEndian(data.Slice((int)offsetTableOffset + i * offsetSize, offsetSize));
        }

        return ReadObject(data, offsets, rootIndex, referenceSize);
    }

    private static object? ReadObject(ReadOnlySpan<byte> data, long[] offsets, long index, int referenceSize)
    {
        int position = (int)offsets[index];
        byte marker = data[position];
        int type = marker >> 4;
        int info = marker & 0x0F;
        int cursor = position + 1;

        int ReadCount(ReadOnlySpan<byte> span, ref int position)
        {
            if (info < 0x0F)
            {
                return info;
            }

            int byteCount = 1 << (span[position] & 0x0F);
            position++;
            long count = ReadBigEndian(span.Slice(position, byteCount));
            position += byteCount;
            return (int)count;
        }

        switch (type)
        {
            case 0x0:
                return info switch
                {
                    0x8 => false,
                    0x9 => true,
                    _ => null,
                };

            case 0x1:
                return ReadBigEndian(data.Slice(cursor, 1 << info));

            case 0x2:
                return info switch
                {
                    0x2 => (double)BitConverter.UInt32BitsToSingle((uint)ReadBigEndian(data.Slice(cursor, 4))),
                    0x3 => BitConverter.UInt64BitsToDouble((ulong)ReadBigEndian(data.Slice(cursor, 8))),
                    _ => 0d,
                };

            case 0x4:
            {
                int length = ReadCount(data, ref cursor);
                return data.Slice(cursor, length).ToArray();
            }

            case 0x5:
            {
                int length = ReadCount(data, ref cursor);
                return Encoding.ASCII.GetString(data.Slice(cursor, length));
            }

            case 0x6:
            {
                int length = ReadCount(data, ref cursor);
                return Encoding.BigEndianUnicode.GetString(data.Slice(cursor, length * 2));
            }

            case 0xA:
            {
                int length = ReadCount(data, ref cursor);
                var list = new List<object?>(length);
                for (int i = 0; i < length; i++)
                {
                    list.Add(ReadObject(data, offsets, ReadBigEndian(data.Slice(cursor + i * referenceSize, referenceSize)), referenceSize));
                }

                return list;
            }

            case 0xD:
            {
                int length = ReadCount(data, ref cursor);
                var dictionary = new Dictionary<string, object?>(length);
                for (int i = 0; i < length; i++)
                {
                    long keyIndex = ReadBigEndian(data.Slice(cursor + i * referenceSize, referenceSize));
                    long valueIndex = ReadBigEndian(data.Slice(cursor + (length + i) * referenceSize, referenceSize));
                    var key = (string)ReadObject(data, offsets, keyIndex, referenceSize)!;
                    dictionary[key] = ReadObject(data, offsets, valueIndex, referenceSize);
                }

                return dictionary;
            }

            default:
                throw new FormatException($"unsupported plist marker 0x{marker:x2}");
        }
    }

    private static long ReadBigEndian(ReadOnlySpan<byte> data)
    {
        long value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    // Convenience accessors used by the RTSP layer.
    public static Dictionary<string, object?>? AsDictionary(object? value) => value as Dictionary<string, object?>;

    public static string? GetString(Dictionary<string, object?>? dictionary, string key) =>
        dictionary is not null && dictionary.TryGetValue(key, out object? value) && value is string text ? text : null;

    public static long? GetInteger(Dictionary<string, object?>? dictionary, string key) =>
        dictionary is not null && dictionary.TryGetValue(key, out object? value) &&
        value is long number ? number : null;

    public static byte[]? GetData(Dictionary<string, object?>? dictionary, string key) =>
        dictionary is not null && dictionary.TryGetValue(key, out object? value) && value is byte[] data ? data : null;

    public static List<object?>? GetArray(Dictionary<string, object?>? dictionary, string key) =>
        dictionary is not null && dictionary.TryGetValue(key, out object? value) && value is List<object?> list ? list : null;
}
