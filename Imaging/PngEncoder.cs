using System.Buffers.Binary;
using System.IO.Compression;

namespace HdrCapture.Imaging;

/// <summary>Dependency free PNG encoder for 8 bit RGBA images.</summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Encodes BGRA pixels that live in unmanaged memory into a PNG byte array. The source is
    /// read with a single pass and never copied.
    /// </summary>
    public static unsafe void Encode(
        Stream output,
        nint source,
        int width,
        int height,
        bool bottomUpSource)
    {
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR", header, null);

        WriteChunk(
            output,
            "IDAT",
            default,
            zlib =>
            {
                var rowBuffer = new byte[(width * 4) + 1];
                for (var row = 0; row < height; row++)
                {
                    var sourceRow = bottomUpSource ? height - 1 - row : row;
                    var rowSpan = new ReadOnlySpan<byte>(
                        (void*)(source + (sourceRow * width * 4)),
                        width * 4);
                    rowBuffer[0] = 0;
                    for (var column = 0; column < width; column++)
                    {
                        var target = 1 + (column * 4);
                        rowBuffer[target + 0] = rowSpan[(column * 4) + 2];
                        rowBuffer[target + 1] = rowSpan[(column * 4) + 1];
                        rowBuffer[target + 2] = rowSpan[column * 4];
                        rowBuffer[target + 3] = rowSpan[(column * 4) + 3];
                    }

                    zlib.Write(rowBuffer);
                }
            });

        WriteChunk(output, "IEND", default, null);
    }

    private static void WriteChunk(
        Stream output,
        string type,
        ReadOnlySpan<byte> data,
        Action<ZLibStream>? content)
    {
        if (!output.CanSeek)
        {
            throw new ArgumentException("PNG 输出流必须支持定位。", nameof(output));
        }

        Span<byte> buffer = stackalloc byte[4];
        var lengthPosition = output.Position;
        output.Write(buffer);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var index = 0; index < 4; index++)
        {
            typeBytes[index] = (byte)type[index];
        }

        output.Write(typeBytes);
        var crc = Crc32.Begin(typeBytes);
        var contentStart = output.Position;

        if (content is null)
        {
            output.Write(data);
            crc = Crc32.Update(crc, data);
        }
        else
        {
            var crcStream = new Crc32Stream(output, crc);
            try
            {
                using var zlib = new ZLibStream(crcStream, CompressionLevel.Fastest, leaveOpen: true);
                content(zlib);
            }
            finally
            {
                crc = crcStream.Crc;
            }
        }

        var length = (int)(output.Position - contentStart);
        var end = output.Position;
        output.Position = lengthPosition;
        BinaryPrimitives.WriteInt32BigEndian(buffer, length);
        output.Write(buffer);
        output.Position = end;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32.End(crc));
        output.Write(buffer);
    }

    private sealed class Crc32Stream : Stream
    {
        private readonly Stream _inner;
        private uint _crc;

        public Crc32Stream(Stream inner, uint crc)
        {
            _inner = inner;
            _crc = crc;
        }

        public uint Crc => _crc;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _crc = Crc32.Update(_crc, buffer.AsSpan(offset, count));
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _crc = Crc32.Update(_crc, buffer);
            _inner.Write(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Begin(ReadOnlySpan<byte> type) => Update(0xFFFFFFFFu, type);

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint End(uint crc) => crc ^ 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var index = 0u; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
