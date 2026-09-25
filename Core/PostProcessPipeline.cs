using HdrCapture.Capture;
using HdrCapture.Imaging;

namespace HdrCapture.Core;

internal sealed record PostProcessRenderResult(
    BgraImage Ldr,
    LinearImage Hdr,
    bool UsedOidn,
    string? Warning);

internal static class PostProcessPipeline
{
    public const int PreviewMaxDimension = 1600;

    public static PostProcessRenderResult Render(
        LinearImage source,
        double denoise,
        double detailPreservation,
        double exposureEv,
        bool useOidn,
        bool preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var working = preview ? Downscale(source, PreviewMaxDimension) : source;
        var denoised = preview
            ? HdrDenoiser.DenoiseFast(working, denoise, detailPreservation)
            : HdrDenoiser.DenoiseFinal(
                working,
                denoise,
                detailPreservation,
                useOidn,
                cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var ldr = LdrConverter.ToBgra(denoised.Image, exposureEv);
        return new PostProcessRenderResult(
            new BgraImage(denoised.Image.Width, denoised.Image.Height, ldr),
            denoised.Image,
            denoised.UsedOidn,
            denoised.Warning);
    }

    public static BgraImage RenderOriginal(
        LinearImage source,
        double exposureEv,
        bool preview,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var working = preview ? Downscale(source, PreviewMaxDimension) : source;
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = LdrConverter.ToBgra(working, exposureEv);
        return new BgraImage(working.Width, working.Height, pixels);
    }

    internal static LinearImage Downscale(LinearImage source, int maxDimension)
    {
        var largest = Math.Max(source.Width, source.Height);
        if (largest <= maxDimension)
        {
            return source;
        }

        var scale = maxDimension / (double)largest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var output = new LinearImage(width, height);
        var sourceX = new int[width];
        var sourceY = new int[height];
        for (var x = 0; x < width; x++)
        {
            sourceX[x] = Math.Min(source.Width - 1, (int)((long)x * source.Width / width));
        }

        for (var y = 0; y < height; y++)
        {
            sourceY[y] = Math.Min(source.Height - 1, (int)((long)y * source.Height / height));
        }

        Parallel.For(0, height, row =>
        {
            var sourceRow = sourceY[row] * source.Width;
            var targetRow = row * width;
            for (var column = 0; column < width; column++)
            {
                var sourceIndex = sourceRow + sourceX[column];
                var targetIndex = targetRow + column;
                output.Red[targetIndex] = source.Red[sourceIndex];
                output.Green[targetIndex] = source.Green[sourceIndex];
                output.Blue[targetIndex] = source.Blue[sourceIndex];
            }
        });

        foreach (var region in source.Regions)
        {
            var left = Math.Clamp((int)Math.Floor(region.X * scale), 0, width);
            var top = Math.Clamp((int)Math.Floor(region.Y * scale), 0, height);
            var right = Math.Clamp((int)Math.Ceiling(region.Right * scale), left, width);
            var bottom = Math.Clamp((int)Math.Ceiling(region.Bottom * scale), top, height);
            if (right > left && bottom > top)
            {
                output.Regions.Add(new WhitePointRegion(
                    left,
                    top,
                    right - left,
                    bottom - top,
                    region.MonitorDevice,
                    region.HdrEnabled,
                    region.SdrWhiteNits));
            }
        }

        return output;
    }
}
