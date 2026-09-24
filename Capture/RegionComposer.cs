using System.Runtime.InteropServices;

namespace HdrCapture.Capture;

/// <summary>
/// Crops the frozen per-display captures into a single linear image. Pixels inside the
/// requested rectangle that no display covers (gaps between monitors) stay black.
/// </summary>
internal static class RegionComposer
{
    private static readonly float[] SrgbToLinearLookup = BuildSrgbToLinearLookup();

    public static LinearImage Compose(
        IReadOnlyList<CapturedMonitor> captures,
        PixelRect region,
        bool releaseSources = true)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("截图区域为空。", nameof(region));
        }

        var image = new LinearImage(region.Width, region.Height);
        foreach (var capture in captures)
        {
            if (capture.Pixels.Length == 0)
            {
                continue;
            }

            var monitorBounds = new PixelRect(
                capture.Monitor.X,
                capture.Monitor.Y,
                capture.Width,
                capture.Height);
            var intersection = monitorBounds.Intersect(region);
            if (intersection.IsEmpty)
            {
                continue;
            }

            Copy(
                image,
                capture,
                intersection.X - region.X,
                intersection.Y - region.Y,
                intersection.X - monitorBounds.X,
                intersection.Y - monitorBounds.Y,
                intersection.Width,
                intersection.Height);

            image.Regions.Add(new WhitePointRegion(
                intersection.X - region.X,
                intersection.Y - region.Y,
                intersection.Width,
                intersection.Height,
                capture.Monitor.DeviceName,
                capture.Monitor.HdrEnabled,
                capture.Monitor.SdrWhiteNits));

            if (releaseSources)
            {
                capture.ReleasePixels();
            }
        }

        return image;
    }

    /// <summary>Bounding box of the captured desktop in physical pixels.</summary>
    public static PixelRect GetDesktopBounds(IReadOnlyList<CapturedMonitor> captures)
    {
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        foreach (var capture in captures)
        {
            left = Math.Min(left, capture.Monitor.X);
            top = Math.Min(top, capture.Monitor.Y);
            right = Math.Max(right, capture.Monitor.X + capture.Width);
            bottom = Math.Max(bottom, capture.Monitor.Y + capture.Height);
        }

        return right <= left || bottom <= top
            ? default
            : new PixelRect(left, top, right - left, bottom - top);
    }

    private static void Copy(
        LinearImage image,
        CapturedMonitor capture,
        int destinationX,
        int destinationY,
        int sourceX,
        int sourceY,
        int width,
        int height)
    {
        if (capture.Format == CapturePixelFormat.Rgba16Float)
        {
            CopyHdr(image, capture, destinationX, destinationY, sourceX, sourceY, width, height);
        }
        else
        {
            CopySdr(image, capture, destinationX, destinationY, sourceX, sourceY, width, height);
        }
    }

    private static void CopyHdr(
        LinearImage image,
        CapturedMonitor capture,
        int destinationX,
        int destinationY,
        int sourceX,
        int sourceY,
        int width,
        int height)
    {
        var sourceBytes = capture.Pixels;
        var sourceStride = capture.Width * 4;
        var destinationStride = image.Width;
        var red = image.Red;
        var green = image.Green;
        var blue = image.Blue;

        Parallel.For(0, height, row =>
        {
            var source = MemoryMarshal.Cast<byte, ushort>(sourceBytes.AsSpan());
            var sourceIndex = ((sourceY + row) * sourceStride) + (sourceX * 4);
            var destinationIndex = ((destinationY + row) * destinationStride) + destinationX;
            for (var column = 0; column < width; column++)
            {
                var pixel = sourceIndex + (column * 4);
                var target = destinationIndex + column;
                red[target] = source[pixel];
                green[target] = source[pixel + 1];
                blue[target] = source[pixel + 2];
            }
        });
    }

    private static void CopySdr(
        LinearImage image,
        CapturedMonitor capture,
        int destinationX,
        int destinationY,
        int sourceX,
        int sourceY,
        int width,
        int height)
    {
        var source = capture.Pixels;
        var sourceStride = capture.Width * 4;
        var destinationStride = image.Width;
        var red = image.Red;
        var green = image.Green;
        var blue = image.Blue;
        var lookup = SrgbToLinearLookup;

        Parallel.For(0, height, row =>
        {
            var sourceIndex = ((sourceY + row) * sourceStride) + (sourceX * 4);
            var destinationIndex = ((destinationY + row) * destinationStride) + destinationX;
            for (var column = 0; column < width; column++)
            {
                var pixel = sourceIndex + (column * 4);
                var target = destinationIndex + column;
                red[target] = ToHalfBits(lookup[source[pixel + 2]]);
                green[target] = ToHalfBits(lookup[source[pixel + 1]]);
                blue[target] = ToHalfBits(lookup[source[pixel]]);
            }
        });
    }

    public static ushort ToHalfBits(float value) =>
        BitConverter.HalfToUInt16Bits((Half)value);

    public static float FromHalfBits(ushort bits) =>
        (float)BitConverter.UInt16BitsToHalf(bits);

    private static float[] BuildSrgbToLinearLookup()
    {
        var lookup = new float[256];
        for (var index = 0; index < lookup.Length; index++)
        {
            var value = index / 255.0f;
            lookup[index] = value <= 0.04045f
                ? value / 12.92f
                : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
        }

        return lookup;
    }
}
