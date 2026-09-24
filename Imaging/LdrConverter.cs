using HdrCapture.Capture;

namespace HdrCapture.Imaging;

/// <summary>
/// Converts the linear scRGB capture into 8 bit sRGB pixels: each pixel is normalized by
/// the SDR white point of the display it came from, exposure compensation is applied and
/// bright highlights are rolled off with the Khronos PBR Neutral curve.
/// </summary>
internal static class LdrConverter
{
    private const int SrgbLookupSize = 4096;
    private static readonly byte[] SrgbEncodeLookup = BuildSrgbEncodeLookup();

    public static byte[] ToBgra(LinearImage image, double exposureEv)
    {
        var destination = new byte[checked(image.Width * image.Height * 4)];
        unsafe
        {
            fixed (byte* pointer = destination)
            {
                ToBgra(image, exposureEv, (nint)pointer, bottomUp: false);
            }
        }

        return destination;
    }

    /// <summary>
    /// Writes BGRA pixels into unmanaged memory so the clipboard DIB can be filled in place.
    /// </summary>
    public static unsafe void ToBgra(LinearImage image, double exposureEv, nint destination, bool bottomUp)
    {
        var width = image.Width;
        var height = image.Height;
        var exposure = MathF.Pow(2f, (float)exposureEv);
        var regions = image.Regions;
        var red = image.Red;
        var green = image.Green;
        var blue = image.Blue;

        Parallel.For(0, height, row =>
        {
            var segments = BuildRowSegments(regions, row, width, exposure);
            var rowStart = row * width;
            var targetRow = bottomUp ? height - 1 - row : row;
            var destinationIndex = targetRow * width * 4;
            var rowSpan = new Span<byte>(
                (void*)(destination + destinationIndex),
                width * 4);
            var segmentIndex = 0;
            var scale = 1f;

            for (var column = 0; column < width; column++)
            {
                while (segmentIndex < segments.Count && column >= segments[segmentIndex].End)
                {
                    segmentIndex++;
                }

                if (segmentIndex < segments.Count &&
                    column >= segments[segmentIndex].Start &&
                    column < segments[segmentIndex].End)
                {
                    scale = segments[segmentIndex].Scale;
                }

                var index = rowStart + column;
                var r = RegionComposer.FromHalfBits(red[index]) * scale;
                var g = RegionComposer.FromHalfBits(green[index]) * scale;
                var b = RegionComposer.FromHalfBits(blue[index]) * scale;
                var mapped = NeutralTonemapper.Map(r, g, b);

                var offset = column * 4;
                rowSpan[offset + 0] = EncodeSrgb(mapped.Blue);
                rowSpan[offset + 1] = EncodeSrgb(mapped.Green);
                rowSpan[offset + 2] = EncodeSrgb(mapped.Red);
                rowSpan[offset + 3] = 255;
            }
        });
    }

    public static byte EncodeSrgb(float linear)
    {
        if (!float.IsFinite(linear) || linear <= 0f)
        {
            return 0;
        }

        if (linear >= 1f)
        {
            return 255;
        }

        // Table lookup instead of a pow() per channel: the error is far below one 8 bit step.
        return SrgbEncodeLookup[(int)(linear * SrgbLookupSize)];
    }

    /// <summary>scRGB value that corresponds to the SDR white of a display.</summary>
    public static double GetScRgbWhiteLevel(WhitePointRegion region) =>
        GetScRgbWhiteLevel(region.HdrEnabled, region.SdrWhiteNits);

    /// <summary>
    /// SDR displays already deliver normalized pixels, so only HDR displays are scaled by the
    /// reported SDR white level (scRGB fixes 1.0 at 80 nit).
    /// </summary>
    public static double GetScRgbWhiteLevel(bool hdrEnabled, double sdrWhiteNits) =>
        hdrEnabled && sdrWhiteNits > 0 ? sdrWhiteNits / 80.0 : 1.0;

    private static List<RowSegment> BuildRowSegments(
        IReadOnlyList<WhitePointRegion> regions,
        int row,
        int width,
        float exposure)
    {
        var segments = new List<RowSegment>(4);
        foreach (var region in regions)
        {
            if (row < region.Y || row >= region.Bottom)
            {
                continue;
            }

            var start = Math.Max(0, region.X);
            var end = Math.Min(width, region.Right);
            if (end <= start)
            {
                continue;
            }

            var white = GetScRgbWhiteLevel(region);
            segments.Add(new RowSegment(start, end, (float)(exposure / white)));
        }

        segments.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return segments;
    }

    private readonly record struct RowSegment(int Start, int End, float Scale);

    private static byte[] BuildSrgbEncodeLookup()
    {
        var lookup = new byte[SrgbLookupSize + 1];
        for (var index = 0; index <= SrgbLookupSize; index++)
        {
            var linear = index / (float)SrgbLookupSize;
            var encoded = linear <= 0.0031308f
                ? linear * 12.92f
                : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
            lookup[index] = (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
        }

        return lookup;
    }
}
