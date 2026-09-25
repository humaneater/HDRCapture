using HdrCapture.Capture;
using HdrCapture.Infrastructure;

namespace HdrCapture.Imaging;

internal sealed record DenoiseResult(
    LinearImage Image,
    bool UsedOidn,
    string? Warning = null);

/// <summary>
/// HDR-aware denoising in linear scRGB. The fast path is an edge-preserving filter used for
/// interactive previews. The final path uses OIDN over overlapped, feathered tiles.
/// </summary>
internal static class HdrDenoiser
{
    private const int Radius = 2;
    private const int OidnTileSize = 1024;
    private const int OidnOverlap = 128;

    public static DenoiseResult DenoiseFast(
        LinearImage source,
        double strength,
        double detailPreservation)
    {
        strength = Math.Clamp(strength, 0.0, 1.0);
        detailPreservation = Math.Clamp(detailPreservation, 0.0, 1.0);
        if (strength <= 0.0001)
        {
            return new DenoiseResult(source, UsedOidn: false);
        }

        return new DenoiseResult(
            FilterFast(source, (float)strength, (float)detailPreservation),
            UsedOidn: false);
    }

    public static DenoiseResult DenoiseFinal(
        LinearImage source,
        double strength,
        double detailPreservation,
        bool useOidn,
        CancellationToken cancellationToken = default)
    {
        strength = Math.Clamp(strength, 0.0, 1.0);
        detailPreservation = Math.Clamp(detailPreservation, 0.0, 1.0);
        if (strength <= 0.0001)
        {
            return new DenoiseResult(source, UsedOidn: false);
        }

        if (!useOidn || !OidnRuntime.IsAvailable)
        {
            var warning = useOidn && !OidnRuntime.IsAvailable
                ? $"OIDN 不可用，已使用快速降噪：{OidnRuntime.Error}"
                : null;
            return new DenoiseResult(
                FilterFast(source, (float)strength, (float)detailPreservation),
                UsedOidn: false,
                warning);
        }

        try
        {
            return new DenoiseResult(
                FilterOidn(source, (float)strength, (float)detailPreservation, cancellationToken),
                UsedOidn: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("OIDN denoise failed; falling back to the fast filter.", ex);
            return new DenoiseResult(
                FilterFast(source, (float)strength, (float)detailPreservation),
                UsedOidn: false,
                $"OIDN 处理失败，已使用快速降噪：{ex.Message}");
        }
    }

    private static LinearImage FilterFast(LinearImage source, float strength, float detail)
    {
        var width = source.Width;
        var height = source.Height;
        var count = checked(width * height);
        var output = new LinearImage(width, height);
        output.Regions.AddRange(source.Regions);

        var regionMap = BuildRegionMap(source);
        var guide = new float[count];
        Parallel.For(0, height, row =>
        {
            var index = row * width;
            for (var column = 0; column < width; column++, index++)
            {
                if (regionMap[index] == 0)
                {
                    continue;
                }

                guide[index] = (float)Math.Log2(
                    1.0 + Math.Max(
                        0.0,
                        (0.2126 * RegionComposer.FromHalfBits(source.Red[index])) +
                        (0.7152 * RegionComposer.FromHalfBits(source.Green[index])) +
                        (0.0722 * RegionComposer.FromHalfBits(source.Blue[index]))));
            }
        });

        var horizontal = new float[checked(count * 3)];
        Parallel.For(0, height, row =>
        {
            var rowStart = row * width;
            for (var column = 0; column < width; column++)
            {
                var index = rowStart + column;
                if (regionMap[index] == 0)
                {
                    continue;
                }

                var centerGuide = guide[index];
                var region = regionMap[index];
                var red = 0f;
                var green = 0f;
                var blue = 0f;
                var weightSum = 0f;
                var left = Math.Max(0, column - Radius);
                var right = Math.Min(width - 1, column + Radius);
                for (var sample = left; sample <= right; sample++)
                {
                    var sampleIndex = rowStart + sample;
                    if (regionMap[sampleIndex] != region)
                    {
                        continue;
                    }

                    var delta = MathF.Abs(guide[sampleIndex] - centerGuide);
                    var weight = SpatialWeight(Math.Abs(sample - column)) / (1f + (delta * 8f));
                    red += RegionComposer.FromHalfBits(source.Red[sampleIndex]) * weight;
                    green += RegionComposer.FromHalfBits(source.Green[sampleIndex]) * weight;
                    blue += RegionComposer.FromHalfBits(source.Blue[sampleIndex]) * weight;
                    weightSum += weight;
                }

                var target = index * 3;
                var divisor = MathF.Max(weightSum, 0.0001f);
                horizontal[target] = red / divisor;
                horizontal[target + 1] = green / divisor;
                horizontal[target + 2] = blue / divisor;
            }
        });

        Parallel.For(0, height, row =>
        {
            var rowStart = row * width;
            var top = Math.Max(0, row - Radius);
            var bottom = Math.Min(height - 1, row + Radius);
            for (var column = 0; column < width; column++)
            {
                var index = rowStart + column;
                if (regionMap[index] == 0)
                {
                    output.Red[index] = 0;
                    output.Green[index] = 0;
                    output.Blue[index] = 0;
                    continue;
                }

                var centerGuide = guide[index];
                var region = regionMap[index];
                var red = 0f;
                var green = 0f;
                var blue = 0f;
                var weightSum = 0f;
                for (var sampleRow = top; sampleRow <= bottom; sampleRow++)
                {
                    var sampleIndex = (sampleRow * width) + column;
                    if (regionMap[sampleIndex] != region)
                    {
                        continue;
                    }

                    var delta = MathF.Abs(guide[sampleIndex] - centerGuide);
                    var weight = SpatialWeight(Math.Abs(sampleRow - row)) / (1f + (delta * 8f));
                    var sample = sampleIndex * 3;
                    red += horizontal[sample] * weight;
                    green += horizontal[sample + 1] * weight;
                    blue += horizontal[sample + 2] * weight;
                    weightSum += weight;
                }

                var divisor = MathF.Max(weightSum, 0.0001f);
                Blend(
                    RegionComposer.FromHalfBits(source.Red[index]),
                    RegionComposer.FromHalfBits(source.Green[index]),
                    RegionComposer.FromHalfBits(source.Blue[index]),
                    red / divisor,
                    green / divisor,
                    blue / divisor,
                    strength,
                    detail,
                    out var finalRed,
                    out var finalGreen,
                    out var finalBlue);
                output.Red[index] = RegionComposer.ToHalfBits(finalRed);
                output.Green[index] = RegionComposer.ToHalfBits(finalGreen);
                output.Blue[index] = RegionComposer.ToHalfBits(finalBlue);
            }
        });

        return output;
    }

    private static LinearImage FilterOidn(
        LinearImage source,
        float strength,
        float detail,
        CancellationToken cancellationToken)
    {
        var width = source.Width;
        var height = source.Height;
        var count = checked(width * height);
        var regionMap = BuildRegionMap(source);
        var accumulated = new float[checked(count * 3)];
        var accumulatedWeight = new float[count];
        using var api = new OidnApi();

        const int step = OidnTileSize - (OidnOverlap * 2);
        for (var tileTop = 0; tileTop < height; tileTop += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tileHeight = Math.Min(OidnTileSize, height - tileTop);
            for (var tileLeft = 0; tileLeft < width; tileLeft += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tileWidth = Math.Min(OidnTileSize, width - tileLeft);
                var color = new float[checked(tileWidth * tileHeight * 3)];
                var denoised = new float[color.Length];
                FillOidnTile(source, regionMap, tileLeft, tileTop, tileWidth, tileHeight, color);

                api.Execute(color, denoised, tileWidth, tileHeight);
                AccumulateTile(
                    denoised,
                    accumulated,
                    accumulatedWeight,
                    width,
                    tileLeft,
                    tileTop,
                    tileWidth,
                    tileHeight);

                if (tileLeft + tileWidth >= width)
                {
                    break;
                }
            }

            if (tileTop + tileHeight >= height)
            {
                break;
            }
        }

        var output = new LinearImage(width, height);
        output.Regions.AddRange(source.Regions);
        Parallel.For(0, height, row =>
        {
            var index = row * width;
            for (var column = 0; column < width; column++, index++)
            {
                if (regionMap[index] == 0)
                {
                    output.Red[index] = 0;
                    output.Green[index] = 0;
                    output.Blue[index] = 0;
                    continue;
                }

                var weight = accumulatedWeight[index];
                if (weight <= 0.0001f)
                {
                    output.Red[index] = source.Red[index];
                    output.Green[index] = source.Green[index];
                    output.Blue[index] = source.Blue[index];
                    continue;
                }

                var sample = index * 3;
                var divisor = MathF.Max(weight, 0.0001f);
                Blend(
                    RegionComposer.FromHalfBits(source.Red[index]),
                    RegionComposer.FromHalfBits(source.Green[index]),
                    RegionComposer.FromHalfBits(source.Blue[index]),
                    accumulated[sample] / divisor,
                    accumulated[sample + 1] / divisor,
                    accumulated[sample + 2] / divisor,
                    strength,
                    detail,
                    out var red,
                    out var green,
                    out var blue);
                output.Red[index] = RegionComposer.ToHalfBits(red);
                output.Green[index] = RegionComposer.ToHalfBits(green);
                output.Blue[index] = RegionComposer.ToHalfBits(blue);
            }
        });

        return output;
    }

    private static void FillOidnTile(
        LinearImage source,
        byte[] regionMap,
        int left,
        int top,
        int width,
        int height,
        float[] destination)
    {
        var sourceWidth = source.Width;
        Parallel.For(0, height, localRow =>
        {
            var sourceIndex = ((top + localRow) * sourceWidth) + left;
            var destinationIndex = localRow * width * 3;
            for (var column = 0; column < width; column++, sourceIndex++, destinationIndex += 3)
            {
                if (regionMap[sourceIndex] == 0)
                {
                    destination[destinationIndex] = float.NaN;
                    destination[destinationIndex + 1] = float.NaN;
                    destination[destinationIndex + 2] = float.NaN;
                    continue;
                }

                destination[destinationIndex] = RegionComposer.FromHalfBits(source.Red[sourceIndex]);
                destination[destinationIndex + 1] = RegionComposer.FromHalfBits(source.Green[sourceIndex]);
                destination[destinationIndex + 2] = RegionComposer.FromHalfBits(source.Blue[sourceIndex]);
            }
        });
    }

    private static void AccumulateTile(
        float[] tile,
        float[] accumulated,
        float[] accumulatedWeight,
        int imageWidth,
        int left,
        int top,
        int width,
        int height)
    {
        var xWeights = new float[width];
        for (var column = 0; column < width; column++)
        {
            xWeights[column] = EdgeWeight(column, width);
        }

        var yWeights = new float[height];
        for (var row = 0; row < height; row++)
        {
            yWeights[row] = EdgeWeight(row, height);
        }

        for (var row = 0; row < height; row++)
        {
            var targetRow = (top + row) * imageWidth;
            var tileRow = row * width * 3;
            for (var column = 0; column < width; column++)
            {
                var source = tileRow + (column * 3);
                if (!float.IsFinite(tile[source]) ||
                    !float.IsFinite(tile[source + 1]) ||
                    !float.IsFinite(tile[source + 2]))
                {
                    continue;
                }

                var weight = xWeights[column] * yWeights[row];
                var target = targetRow + left + column;
                var destination = target * 3;
                accumulated[destination] += tile[source] * weight;
                accumulated[destination + 1] += tile[source + 1] * weight;
                accumulated[destination + 2] += tile[source + 2] * weight;
                accumulatedWeight[target] += weight;
            }
        }
    }

    private static float EdgeWeight(int position, int length)
    {
        if (length <= 1)
        {
            return 1f;
        }

        var edge = Math.Min(position, length - 1 - position);
        if (edge >= OidnOverlap)
        {
            return 1f;
        }

        return (edge + 1f) / (OidnOverlap + 1f);
    }

    private static byte[] BuildRegionMap(LinearImage image)
    {
        var map = new byte[checked(image.Width * image.Height)];
        if (image.Regions.Count == 0)
        {
            Array.Fill(map, (byte)1);
            return map;
        }

        byte regionId = 0;
        foreach (var region in image.Regions)
        {
            regionId = regionId == byte.MaxValue ? byte.MaxValue : (byte)(regionId + 1);
            var left = Math.Clamp(region.X, 0, image.Width);
            var right = Math.Clamp(region.Right, 0, image.Width);
            var top = Math.Clamp(region.Y, 0, image.Height);
            var bottom = Math.Clamp(region.Bottom, 0, image.Height);
            for (var row = top; row < bottom; row++)
            {
                var start = (row * image.Width) + left;
                Array.Fill(map, regionId, start, Math.Max(0, right - left));
            }
        }

        return map;
    }

    private static float SpatialWeight(int distance) => distance switch
    {
        0 => 3f,
        1 => 2f,
        _ => 1f
    };

    private static void Blend(
        float originalRed,
        float originalGreen,
        float originalBlue,
        float filteredRed,
        float filteredGreen,
        float filteredBlue,
        float strength,
        float detail,
        out float red,
        out float green,
        out float blue)
    {
        var detailScale = strength * detail * 0.5f;
        red = originalRed + ((filteredRed - originalRed) * strength) +
              ((originalRed - filteredRed) * detailScale);
        green = originalGreen + ((filteredGreen - originalGreen) * strength) +
                ((originalGreen - filteredGreen) * detailScale);
        blue = originalBlue + ((filteredBlue - originalBlue) * strength) +
               ((originalBlue - filteredBlue) * detailScale);

        red = Sanitize(red);
        green = Sanitize(green);
        blue = Sanitize(blue);
    }

    private static float Sanitize(float value) =>
        !float.IsFinite(value) ? 0f : Math.Max(0f, value);
}
