using System.Buffers.Binary;
using System.IO;
using System.Text;
using ServerLauncher.Models;

namespace ServerLauncher.Services;

public sealed class MinecraftServerListService
{
    public const string ServerName = "minivibe";
    public const string ServerAddress = "213.152.43.44:25697";

    public async Task EnsureMinivibeServerAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.InstallDirectory);
        var serversPath = Path.Combine(settings.InstallDirectory, "servers.dat");
        var root = File.Exists(serversPath)
            ? await ReadRootAsync(serversPath, cancellationToken) ?? CreateRoot()
            : CreateRoot();

        var servers = GetOrCreateServersList(root);
        if (servers.Items.OfType<NbtCompound>().Any(ServerMatches))
        {
            return;
        }

        servers.Items.Add(new NbtCompound
        {
            Tags =
            {
                ["name"] = new NbtString(ServerName),
                ["ip"] = new NbtString(ServerAddress)
            }
        });

        var tempPath = serversPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            WriteNamedTag(stream, "", root);
        }

        File.Move(tempPath, serversPath, overwrite: true);
    }

    private static bool ServerMatches(NbtCompound server)
    {
        return server.Tags.TryGetValue("ip", out var tag)
            && tag is NbtString ip
            && string.Equals(ip.Value, ServerAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<NbtCompound?> ReadRootAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            using var stream = new MemoryStream(bytes);
            var type = stream.ReadByte();
            if (type != (int)NbtType.Compound)
            {
                return null;
            }

            _ = ReadString(stream);
            return ReadCompoundPayload(stream);
        }
        catch
        {
            return null;
        }
    }

    private static NbtCompound CreateRoot()
    {
        return new NbtCompound
        {
            Tags =
            {
                ["servers"] = new NbtList(NbtType.Compound, [])
            }
        };
    }

    private static NbtList GetOrCreateServersList(NbtCompound root)
    {
        if (root.Tags.TryGetValue("servers", out var existing) && existing is NbtList list)
        {
            if (list.ElementType == NbtType.Compound)
            {
                return list;
            }
        }

        var created = new NbtList(NbtType.Compound, []);
        root.Tags["servers"] = created;
        return created;
    }

    private static NbtCompound ReadCompoundPayload(Stream stream)
    {
        var compound = new NbtCompound();
        while (true)
        {
            var rawType = stream.ReadByte();
            if (rawType < 0 || rawType == (int)NbtType.End)
            {
                break;
            }

            var name = ReadString(stream);
            compound.Tags[name] = ReadPayload(stream, (NbtType)rawType);
        }

        return compound;
    }

    private static NbtTag ReadPayload(Stream stream, NbtType type)
    {
        return type switch
        {
            NbtType.Byte => new NbtByte((byte)stream.ReadByte()),
            NbtType.Short => new NbtShort(ReadInt16(stream)),
            NbtType.Int => new NbtInt(ReadInt32(stream)),
            NbtType.Long => new NbtLong(ReadInt64(stream)),
            NbtType.Float => new NbtFloat(BitConverter.Int32BitsToSingle(ReadInt32(stream))),
            NbtType.Double => new NbtDouble(BitConverter.Int64BitsToDouble(ReadInt64(stream))),
            NbtType.ByteArray => ReadByteArray(stream),
            NbtType.String => new NbtString(ReadString(stream)),
            NbtType.List => ReadList(stream),
            NbtType.Compound => ReadCompoundPayload(stream),
            NbtType.IntArray => ReadIntArray(stream),
            NbtType.LongArray => ReadLongArray(stream),
            _ => throw new InvalidDataException($"Unsupported NBT tag type: {type}")
        };
    }

    private static NbtByteArray ReadByteArray(Stream stream)
    {
        var length = ReadInt32(stream);
        var bytes = new byte[length];
        ReadExactly(stream, bytes);
        return new NbtByteArray(bytes);
    }

    private static NbtList ReadList(Stream stream)
    {
        var elementType = (NbtType)stream.ReadByte();
        var count = ReadInt32(stream);
        var items = new List<NbtTag>(Math.Max(0, count));
        for (var index = 0; index < count; index += 1)
        {
            items.Add(ReadPayload(stream, elementType));
        }

        return new NbtList(elementType, items);
    }

    private static NbtIntArray ReadIntArray(Stream stream)
    {
        var length = ReadInt32(stream);
        var values = new int[length];
        for (var index = 0; index < length; index += 1)
        {
            values[index] = ReadInt32(stream);
        }

        return new NbtIntArray(values);
    }

    private static NbtLongArray ReadLongArray(Stream stream)
    {
        var length = ReadInt32(stream);
        var values = new long[length];
        for (var index = 0; index < length; index += 1)
        {
            values[index] = ReadInt64(stream);
        }

        return new NbtLongArray(values);
    }

    private static void WriteNamedTag(Stream stream, string name, NbtTag tag)
    {
        stream.WriteByte((byte)tag.Type);
        if (tag.Type == NbtType.End)
        {
            return;
        }

        WriteString(stream, name);
        WritePayload(stream, tag);
    }

    private static void WritePayload(Stream stream, NbtTag tag)
    {
        switch (tag)
        {
            case NbtByte value:
                stream.WriteByte(value.Value);
                break;
            case NbtShort value:
                WriteInt16(stream, value.Value);
                break;
            case NbtInt value:
                WriteInt32(stream, value.Value);
                break;
            case NbtLong value:
                WriteInt64(stream, value.Value);
                break;
            case NbtFloat value:
                WriteInt32(stream, BitConverter.SingleToInt32Bits(value.Value));
                break;
            case NbtDouble value:
                WriteInt64(stream, BitConverter.DoubleToInt64Bits(value.Value));
                break;
            case NbtByteArray value:
                WriteInt32(stream, value.Value.Length);
                stream.Write(value.Value);
                break;
            case NbtString value:
                WriteString(stream, value.Value);
                break;
            case NbtList value:
                stream.WriteByte((byte)value.ElementType);
                WriteInt32(stream, value.Items.Count);
                foreach (var item in value.Items)
                {
                    WritePayload(stream, item);
                }
                break;
            case NbtCompound value:
                foreach (var item in value.Tags)
                {
                    WriteNamedTag(stream, item.Key, item.Value);
                }

                stream.WriteByte((byte)NbtType.End);
                break;
            case NbtIntArray value:
                WriteInt32(stream, value.Value.Length);
                foreach (var item in value.Value)
                {
                    WriteInt32(stream, item);
                }

                break;
            case NbtLongArray value:
                WriteInt32(stream, value.Value.Length);
                foreach (var item in value.Value)
                {
                    WriteInt64(stream, item);
                }

                break;
        }
    }

    private static string ReadString(Stream stream)
    {
        var length = ReadUInt16(stream);
        var bytes = new byte[length];
        ReadExactly(stream, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static short ReadInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadInt16BigEndian(buffer);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadInt64BigEndian(buffer);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private enum NbtType : byte
    {
        End = 0,
        Byte = 1,
        Short = 2,
        Int = 3,
        Long = 4,
        Float = 5,
        Double = 6,
        ByteArray = 7,
        String = 8,
        List = 9,
        Compound = 10,
        IntArray = 11,
        LongArray = 12
    }

    private abstract record NbtTag(NbtType Type);
    private sealed record NbtByte(byte Value) : NbtTag(NbtType.Byte);
    private sealed record NbtShort(short Value) : NbtTag(NbtType.Short);
    private sealed record NbtInt(int Value) : NbtTag(NbtType.Int);
    private sealed record NbtLong(long Value) : NbtTag(NbtType.Long);
    private sealed record NbtFloat(float Value) : NbtTag(NbtType.Float);
    private sealed record NbtDouble(double Value) : NbtTag(NbtType.Double);
    private sealed record NbtByteArray(byte[] Value) : NbtTag(NbtType.ByteArray);
    private sealed record NbtString(string Value) : NbtTag(NbtType.String);
    private sealed record NbtList(NbtType ElementType, List<NbtTag> Items) : NbtTag(NbtType.List);
    private sealed record NbtIntArray(int[] Value) : NbtTag(NbtType.IntArray);
    private sealed record NbtLongArray(long[] Value) : NbtTag(NbtType.LongArray);
    private sealed record NbtCompound : NbtTag
    {
        public NbtCompound() : base(NbtType.Compound)
        {
        }

        public Dictionary<string, NbtTag> Tags { get; } = new(StringComparer.Ordinal);
    }
}
