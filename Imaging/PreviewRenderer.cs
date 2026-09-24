using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HdrCapture.Capture;
using HdrCapture.Configuration;

namespace HdrCapture.Imaging;

/// <summary>
/// Turns a frozen capture into an 8 bit sRGB bitmap for the selection overlay. The result is
/// frozen so it can be created on the capture thread and displayed by the UI thread.
/// </summary>
internal static class PreviewRenderer
{
    private const int MaxPreviewDimension = 1920;

    public static BitmapSource Create(CapturedMonitor capture, PreviewQuality quality)
    {
        var (width, height) = GetPreviewDimensions(capture.Width, capture.Height, quality);
        var downsample = GetDownsampleFactor(capture.Width, capture.Height, quality);
        var pixels = new byte[checked(width * height * 4)];

        if (capture.Format == CapturePixelFormat.Rgba16Float)
        {
            var sourceBytes = capture.Pixels;
            var scale = (float)(1.0 / LdrConverter.GetScRgbWhiteLevel(capture.Monitor.HdrEnabled, capture.Monitor.SdrWhiteNits));
            var sourceWidth = capture.Width;
            var sourceHeight = capture.Height;

            Parallel.For(0, height, row =>
            {
                var source = MemoryMarshal.Cast<byte, ushort>(sourceBytes.AsSpan());
                var destinationIndex = row * width * 4;
                var sourceTop = row * downsample;
                var sourceBottom = Math.Min(sourceHeight, sourceTop + downsample);
                for (var column = 0; column < width; column++)
                {
                    var sourceLeft = column * downsample;
                    var sourceRight = Math.Min(sourceWidth, sourceLeft + downsample);
                    var red = 0f;
                    var green = 0f;
                    var blue = 0f;
                    var count = 0;
                    for (var sourceY = sourceTop; sourceY < sourceBottom; sourceY++)
                    {
                        var sourceIndex = ((sourceY * sourceWidth) + sourceLeft) * 4;
                        for (var sourceX = sourceLeft; sourceX < sourceRight; sourceX++)
                        {
                            red += RegionComposer.FromHalfBits(source[sourceIndex]);
                            green += RegionComposer.FromHalfBits(source[sourceIndex + 1]);
                            blue += RegionComposer.FromHalfBits(source[sourceIndex + 2]);
                            sourceIndex += 4;
                            count++;
                        }
                    }

                    var divisor = Math.Max(1, count);
                    var mapped = NeutralTonemapper.Map(
                        (red / divisor) * scale,
                        (green / divisor) * scale,
                        (blue / divisor) * scale);
                    pixels[destinationIndex] = LdrConverter.EncodeSrgb(mapped.Blue);
                    pixels[destinationIndex + 1] = LdrConverter.EncodeSrgb(mapped.Green);
                    pixels[destinationIndex + 2] = LdrConverter.EncodeSrgb(mapped.Red);
                    pixels[destinationIndex + 3] = 255;
                    destinationIndex += 4;
                }
            });
        }
        else
        {
            var sourceBytes = capture.Pixels;
            var sourceWidth = capture.Width;
            var sourceHeight = capture.Height;
            Parallel.For(0, height, row =>
            {
                var destinationIndex = row * width * 4;
                var sourceTop = row * downsample;
                var sourceBottom = Math.Min(sourceHeight, sourceTop + downsample);
                for (var column = 0; column < width; column++)
                {
                    var sourceLeft = column * downsample;
                    var sourceRight = Math.Min(sourceWidth, sourceLeft + downsample);
                    uint blue = 0;
                    uint green = 0;
                    uint red = 0;
                    var count = 0;
                    for (var sourceY = sourceTop; sourceY < sourceBottom; sourceY++)
                    {
                        var sourceIndex = ((sourceY * sourceWidth) + sourceLeft) * 4;
                        for (var sourceX = sourceLeft; sourceX < sourceRight; sourceX++)
                        {
                            blue += sourceBytes[sourceIndex];
                            green += sourceBytes[sourceIndex + 1];
                            red += sourceBytes[sourceIndex + 2];
                            sourceIndex += 4;
                            count++;
                        }
                    }

                    var divisor = (uint)Math.Max(1, count);
                    pixels[destinationIndex] = (byte)(blue / divisor);
                    pixels[destinationIndex + 1] = (byte)(green / divisor);
                    pixels[destinationIndex + 2] = (byte)(red / divisor);
                    pixels[destinationIndex + 3] = 255;
                    destinationIndex += 4;
                }
            });
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static (int Width, int Height) GetPreviewDimensions(
        int width,
        int height,
        PreviewQuality quality)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "预览尺寸必须大于 0。");
        }

        var downsample = GetDownsampleFactor(width, height, quality);
        return (
            (width + downsample - 1) / downsample,
            (height + downsample - 1) / downsample);
    }

    private static int GetDownsampleFactor(int width, int height, PreviewQuality quality)
    {
        return quality switch
        {
            PreviewQuality.Full => 1,
            PreviewQuality.Half => 2,
            PreviewQuality.Low => Math.Max(
                1,
                (int)Math.Ceiling(Math.Max(width, height) / (double)MaxPreviewDimension)),
            _ => Math.Max(
                1,
                (int)Math.Ceiling(Math.Max(width, height) / (double)MaxPreviewDimension))
        };
    }
}
