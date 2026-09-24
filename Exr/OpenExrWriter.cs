using System.Buffers.Binary;
using System.Text;
using HdrCapture.Capture;

namespace HdrCapture.Exr;

/// <summary>
/// Minimal OpenEXR writer: uncompressed, scanline, three HALF channels (B, G, R).
/// Values are linear scRGB where 1.0 equals 80 nit.
/// </summary>
internal static class OpenExrWriter
{
    public const int ExrMagic = 20000630;
    private const int ExrVersion = 2;
    private const int HalfPixelType = 1;
    private const int NoCompression = 0;
    private const int IncreasingY = 0;

    private static readonly (string Name, float X, float Y)[] Chromaticities =
    [
        ("red", 0.6400f, 0.3300f),
        ("green", 0.3000f, 0.6000f),
        ("blue", 0.1500f, 0.0600f),
        ("white", 0.3127f, 0.3290f)
    ];

    /// <summary>Writes the image to a temporary file and then moves it into place.</summary>
    public static void WriteAtomic(string path, LinearImage image, ExrMetadata metadata)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       1 << 20,
                       FileOptions.SequentialScan))
            {
                Write(stream, image, metadata);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static void Write(Stream stream, LinearImage image, ExrMetadata metadata)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("EXR 图像尺寸无效。", nameof(image));
        }

        var header = BuildHeader(image, metadata);
        var rowBytes = checked(image.Width * 3 * 2);
        var blockBytes = 8 + rowBytes;
        var offsetTableBytes = checked((long)image.Height * 8);
        var dataStart = (long)header.Length + offsetTableBytes;

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(header);

        for (var row = 0; row < image.Height; row++)
        {
            writer.Write(dataStart + ((long)row * blockBytes));
        }

        var rowBuffer = new byte[rowBytes];
        var halfRow = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(rowBuffer);
        for (var row = 0; row < image.Height; row++)
        {
            var offset = row * image.Width;
            image.Blue.AsSpan(offset, image.Width).CopyTo(halfRow[..image.Width]);
            image.Green.AsSpan(offset, image.Width).CopyTo(halfRow[image.Width..(image.Width * 2)]);
            image.Red.AsSpan(offset, image.Width).CopyTo(halfRow[(image.Width * 2)..]);

            writer.Write(row);
            writer.Write(rowBytes);
            writer.Write(rowBuffer);
        }
    }

    private static byte[] BuildHeader(LinearImage image, ExrMetadata metadata)
    {
        var attributes = new List<(string Name, string Type, byte[] Data)>
        {
            ("channels", "chlist", BuildChannelList()),
            ("chromaticities", "chromaticities", BuildChromaticities()),
            ("colorSpace", "string", Encode("scRGB linear, sRGB/Rec.709 primaries, 1.0 = 80 nit")),
            ("compression", "compression", [(byte)NoCompression]),
            ("dataWindow", "box2i", BuildBox2I(image.Width, image.Height)),
            ("displayWindow", "box2i", BuildBox2I(image.Width, image.Height)),
            ("lineOrder", "lineOrder", [(byte)IncreasingY]),
            ("pixelAspectRatio", "float", BuildFloat(1.0f)),
            ("screenWindowCenter", "v2f", BuildVector2(0f, 0f)),
            ("screenWindowWidth", "float", BuildFloat(1.0f)),
            ("software", "string", Encode("HDRCapture 1.0")),
            ("captureRegion", "string", Encode(metadata.Region.ToString())),
            ("captureMonitors", "string", Encode(metadata.MonitorSummary)),
            ("captureSource", "string", Encode(metadata.SourceDescription)),
            ("captureTimestamp", "string", Encode(metadata.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"))),
            ("ldrExposureEv", "float", BuildFloat((float)metadata.LdrExposureEv)),
            ("sdrWhiteNits", "float", BuildFloat((float)metadata.PrimarySdrWhiteNits))
        };

        attributes.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        using var buffer = new MemoryStream(512);
        using var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true);
        writer.Write(ExrMagic);
        writer.Write(ExrVersion);
        foreach (var attribute in attributes)
        {
            WriteZString(writer, attribute.Name);
            WriteZString(writer, attribute.Type);
            writer.Write(attribute.Data.Length);
            writer.Write(attribute.Data);
        }

        writer.Write((byte)0);
        writer.Flush();
        return buffer.ToArray();
    }

    private static byte[] BuildChannelList()
    {
        using var buffer = new MemoryStream(64);
        using var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true);
        foreach (var name in new[] { "B", "G", "R" })
        {
            WriteZString(writer, name);
            writer.Write(HalfPixelType);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(1);
            writer.Write(1);
        }

        writer.Write((byte)0);
        writer.Flush();
        return buffer.ToArray();
    }

    private static byte[] BuildChromaticities()
    {
        var data = new byte[32];
        for (var index = 0; index < Chromaticities.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index * 8), Chromaticities[index].X);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan((index * 8) + 4), Chromaticities[index].Y);
        }

        return data;
    }

    private static byte[] BuildBox2I(int width, int height)
    {
        var data = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), width - 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), height - 1);
        return data;
    }

    private static byte[] BuildFloat(float value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(data, value);
        return data;
    }

    private static byte[] BuildVector2(float x, float y)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0), x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), y);
        return data;
    }

    private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

    private static void WriteZString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
