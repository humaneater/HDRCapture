using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HdrCapture.Imaging;

/// <summary>Top-down 8-bit BGRA image used by the post-processing window and clipboard.</summary>
internal sealed class BgraImage
{
    public BgraImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        var expectedLength = checked(width * height * 4);
        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"BGRA 数据长度应为 {expectedLength}，实际为 {pixels.Length}。",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public long ApproximateBytes => Pixels.LongLength;

    public static BgraImage FromPng(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        using var stream = new MemoryStream(encoded, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("PNG 中没有可读取的图像帧。");
        }

        var source = decoder.Frames[0];
        BitmapSource bitmap = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        return new BgraImage(width, height, pixels);
    }

    public byte[] EncodePng()
    {
        using var output = new MemoryStream();
        unsafe
        {
            fixed (byte* pointer = Pixels)
            {
                PngEncoder.Encode(
                    output,
                    (nint)pointer,
                    Width,
                    Height,
                    bottomUpSource: false);
            }
        }

        return output.ToArray();
    }

    public BitmapSource ToBitmapSource()
    {
        var bitmap = BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            Pixels,
            checked(Width * 4));
        bitmap.Freeze();
        return bitmap;
    }
}
