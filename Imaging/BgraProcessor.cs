namespace HdrCapture.Imaging;

/// <summary>CPU-only helpers applied to the LDR preview/result after HDR denoising.</summary>
internal static class BgraProcessor
{
    public static BgraImage ApplyDetailPreservation(
        BgraImage original,
        BgraImage generated,
        double amount)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(generated);
        if (original.Width != generated.Width || original.Height != generated.Height)
        {
            return generated;
        }

        var strength = Math.Clamp(amount, 0.0, 1.0);
        if (strength <= 0.0001)
        {
            return generated;
        }

        var width = original.Width;
        var height = original.Height;
        var count = checked(width * height);
        var originalLuma = new float[count];
        var generatedLuma = new float[count];
        Parallel.For(0, height, row =>
        {
            var index = row * width;
            var pixel = index * 4;
            for (var column = 0; column < width; column++, index++, pixel += 4)
            {
                originalLuma[index] = Luma(
                    original.Pixels[pixel + 2],
                    original.Pixels[pixel + 1],
                    original.Pixels[pixel]);
                generatedLuma[index] = Luma(
                    generated.Pixels[pixel + 2],
                    generated.Pixels[pixel + 1],
                    generated.Pixels[pixel]);
            }
        });

        var blurredOriginal = BoxBlur(originalLuma, width, height, radius: 2);
        var output = new byte[generated.Pixels.Length];
        Array.Copy(generated.Pixels, output, generated.Pixels.Length);
        var detailScale = (float)(0.75 * strength);
        Parallel.For(0, height, row =>
        {
            var index = row * width;
            var pixel = index * 4;
            for (var column = 0; column < width; column++, index++, pixel += 4)
            {
                var detail = originalLuma[index] - blurredOriginal[index];
                output[pixel + 2] = AddClamped(output[pixel + 2], detail * detailScale);
                output[pixel + 1] = AddClamped(output[pixel + 1], detail * detailScale);
                output[pixel] = AddClamped(output[pixel], detail * detailScale);
            }
        });

        return new BgraImage(width, height, output);
    }

    public static BgraImage Sharpen(BgraImage source, double amount)
    {
        var strength = Math.Clamp(amount, 0.0, 1.0);
        if (strength <= 0.0001)
        {
            return source;
        }

        var width = source.Width;
        var height = source.Height;
        var pixels = source.Pixels;
        var output = new byte[pixels.Length];
        Array.Copy(pixels, output, pixels.Length);
        var scale = (float)(0.55 * strength);

        Parallel.For(1, height - 1, row =>
        {
            var rowOffset = row * width * 4;
            for (var column = 1; column < width - 1; column++)
            {
                var pixel = rowOffset + (column * 4);
                var center = Luma(pixels[pixel + 2], pixels[pixel + 1], pixels[pixel]);
                var neighbours =
                    Luma(pixels[pixel - 4 + 2], pixels[pixel - 4 + 1], pixels[pixel - 4]) +
                    Luma(pixels[pixel + 4 + 2], pixels[pixel + 4 + 1], pixels[pixel + 4]) +
                    Luma(pixels[pixel - (width * 4) + 2], pixels[pixel - (width * 4) + 1], pixels[pixel - (width * 4)]) +
                    Luma(pixels[pixel + (width * 4) + 2], pixels[pixel + (width * 4) + 1], pixels[pixel + (width * 4)]);
                var edge = center - (neighbours * 0.25f);
                output[pixel + 2] = AddClamped(output[pixel + 2], edge * scale);
                output[pixel + 1] = AddClamped(output[pixel + 1], edge * scale);
                output[pixel] = AddClamped(output[pixel], edge * scale);
            }
        });

        return new BgraImage(width, height, output);
    }

    public static bool HasVisiblePixels(BgraImage image, byte threshold = 8)
    {
        var pixels = image.Pixels;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] > threshold ||
                pixels[index + 1] > threshold ||
                pixels[index + 2] > threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        var horizontal = new float[source.Length];
        Parallel.For(0, height, row =>
        {
            var rowStart = row * width;
            for (var column = 0; column < width; column++)
            {
                var left = Math.Max(0, column - radius);
                var right = Math.Min(width - 1, column + radius);
                var sum = 0f;
                for (var sample = left; sample <= right; sample++)
                {
                    sum += source[rowStart + sample];
                }

                horizontal[rowStart + column] = sum / (right - left + 1);
            }
        });

        var output = new float[source.Length];
        Parallel.For(0, height, row =>
        {
            var top = Math.Max(0, row - radius);
            var bottom = Math.Min(height - 1, row + radius);
            for (var column = 0; column < width; column++)
            {
                var sum = 0f;
                for (var sampleRow = top; sampleRow <= bottom; sampleRow++)
                {
                    sum += horizontal[(sampleRow * width) + column];
                }

                output[(row * width) + column] = sum / (bottom - top + 1);
            }
        });

        return output;
    }

    private static float Luma(byte red, byte green, byte blue) =>
        (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);

    private static byte AddClamped(byte value, float delta)
    {
        var result = value + delta;
        return (byte)Math.Clamp((int)MathF.Round(result), 0, 255);
    }
}
