using System.Buffers.Binary;
using System.Text;

namespace HdrCapture.Exr;

internal sealed class ExrImage
{
    public int Width { get; init; }

    public int Height { get; init; }

    public List<string> ChannelNames { get; init; } = [];

    public Dictionary<string, int> ChannelTypes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Half-float bits per channel, row major.</summary>
    public Dictionary<string, ushort[]> Planes { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, byte[]> Attributes { get; init; } = new(StringComparer.Ordinal);

    public string GetStringAttribute(string name) =>
        Attributes.TryGetValue(name, out var value) ? Encoding.UTF8.GetString(value) : string.Empty;

    public float GetFloatAttribute(string name) =>
        Attributes.TryGetValue(name, out var value) && value.Length >= 4
            ? BitConverter.ToSingle(value)
            : 0f;

    public float MaxLinear(string channel)
    {
        if (!Planes.TryGetValue(channel, out var plane))
        {
            return float.NaN;
        }

        var maximum = float.MinValue;
        foreach (var bits in plane)
        {
            var value = (float)BitConverter.UInt16BitsToHalf(bits);
            if (float.IsFinite(value) && value > maximum)
            {
                maximum = value;
            }
        }

        return maximum;
    }
}

/// <summary>Minimal OpenEXR reader used by diagnostics and the built-in self-test.</summary>
internal static class OpenExrReader
{
    public static ExrImage Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Read(stream);
    }

    public static ExrImage Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (reader.ReadInt32() != OpenExrWriter.ExrMagic)
        {
            throw new InvalidDataException("不是有效的 OpenEXR 文件。");
        }

        var version = reader.ReadInt32();
        if ((version & 0xFF) != 2)
        {
            throw new InvalidDataException($"不支持的 OpenEXR 版本 {version}。");
        }

        var attributes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (true)
        {
            var name = ReadZString(reader);
            if (name.Length == 0)
            {
                break;
            }

            _ = ReadZString(reader);
            var size = reader.ReadInt32();
            attributes[name] = reader.ReadBytes(size);
        }

        var channelNames = new List<string>();
        var channelTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (attributes.TryGetValue("channels", out var channelData))
        {
            ParseChannels(channelData, channelNames, channelTypes);
        }

        if (!attributes.TryGetValue("dataWindow", out var window) || window.Length < 16)
        {
            throw new InvalidDataException("EXR 缺少 dataWindow 属性。");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(window.AsSpan(8)) -
                    BinaryPrimitives.ReadInt32LittleEndian(window.AsSpan(0)) + 1;
        var height = BinaryPrimitives.ReadInt32LittleEndian(window.AsSpan(12)) -
                     BinaryPrimitives.ReadInt32LittleEndian(window.AsSpan(4)) + 1;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("EXR dataWindow 无效。");
        }

        var offsets = new long[height];
        for (var index = 0; index < offsets.Length; index++)
        {
            offsets[index] = reader.ReadInt64();
        }

        var planes = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
        foreach (var channel in channelNames)
        {
            planes[channel] = new ushort[checked(width * height)];
        }

        for (var index = 0; index < offsets.Length; index++)
        {
            stream.Position = offsets[index];
            var y = reader.ReadInt32();
            var dataSize = reader.ReadInt32();
            var channelBytes = channelNames.Count == 0 ? 0 : dataSize / channelNames.Count;
            var pixelCount = channelBytes / 2;
            if (y < 0 || y >= height)
            {
                continue;
            }

            foreach (var channel in channelNames)
            {
                var row = reader.ReadBytes(channelBytes);
                var plane = planes[channel];
                var destination = y * width;
                for (var column = 0; column < Math.Min(width, pixelCount); column++)
                {
                    plane[destination + column] = BinaryPrimitives.ReadUInt16LittleEndian(
                        row.AsSpan(column * 2));
                }
            }
        }

        return new ExrImage
        {
            Width = width,
            Height = height,
            ChannelNames = channelNames,
            ChannelTypes = channelTypes,
            Planes = planes,
            Attributes = attributes
        };
    }

    private static void ParseChannels(
        byte[] data,
        List<string> names,
        Dictionary<string, int> types)
    {
        var position = 0;
        while (position < data.Length)
        {
            var end = Array.IndexOf(data, (byte)0, position);
            if (end < 0)
            {
                break;
            }

            var name = Encoding.ASCII.GetString(data, position, end - position);
            position = end + 1;
            if (name.Length == 0)
            {
                break;
            }

            var pixelType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
            position += 16;
            names.Add(name);
            types[name] = pixelType;
        }
    }

    private static string ReadZString(BinaryReader reader)
    {
        var builder = new StringBuilder(32);
        while (true)
        {
            var value = reader.ReadByte();
            if (value == 0)
            {
                return builder.ToString();
            }

            builder.Append((char)value);
        }
    }
}
