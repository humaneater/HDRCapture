using HdrCapture.Capture;

namespace HdrCapture.Core;

internal static class CaptureFileNaming
{
    public static string BuildFileName(
        DateTimeOffset capturedAt,
        PixelRect region,
        IReadOnlyList<CapturedMonitor> captures)
    {
        var label = DescribeRegion(region, captures);
        return $"HDR_{capturedAt:yyyyMMdd_HHmmss_fff}_{label}.exr";
    }

    public static string DescribeRegion(PixelRect region, IReadOnlyList<CapturedMonitor> captures)
    {
        foreach (var capture in captures)
        {
            var monitor = capture.Monitor;
            var bounds = new PixelRect(monitor.X, monitor.Y, capture.Width, capture.Height);
            if (region.X >= bounds.X && region.Y >= bounds.Y &&
                region.Right <= bounds.Right && region.Bottom <= bounds.Bottom)
            {
                return Sanitize(monitor.DeviceName);
            }
        }

        return "span";
    }

    public static string EnsureUniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 100000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为截图生成唯一的文件名。");
    }

    private static string Sanitize(string deviceName)
    {
        var name = deviceName.Replace(@"\\.\", string.Empty, StringComparison.Ordinal);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(character => !invalid.Contains(character)).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "display" : cleaned;
    }
}
