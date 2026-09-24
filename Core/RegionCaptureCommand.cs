using System.Globalization;
using System.Text;
using HdrCapture.Capture;
using HdrCapture.Configuration;
using HdrCapture.Exr;
using HdrCapture.Infrastructure;

namespace HdrCapture.Core;

/// <summary>
/// Command line capture without the overlay: used for verification runs.
/// HDRCapture.exe --capture-region 100,100,800,600 [--out file.exr] [--no-clipboard]
/// </summary>
internal static class RegionCaptureCommand
{
    public static async Task<int> RunAsync(ConsoleHost console, string[] args)
    {
        var output = new StringBuilder();
        try
        {
            if (!TryParse(args, out var region, out var outputPath, out var copyToClipboard, out var error))
            {
                output.AppendLine($"ERROR: {error}");
                Write(console, output.ToString());
                return 2;
            }

            var settings = new SettingsStore().Load();
            var workflow = new CaptureWorkflow();
            var outcome = await workflow
                .CaptureRegionAsync(settings, region, copyToClipboard, outputPath)
                .ConfigureAwait(false);

            output.AppendLine($"region: {outcome.Region}");
            if (outcome.ExrPath is { } path)
            {
                output.AppendLine($"exr: {path}");
                AppendExrSummary(output, path);
            }
            else if (outcome.ExrError is { Length: > 0 })
            {
                output.AppendLine($"exr failed: {outcome.ExrError}");
            }
            else
            {
                output.AppendLine("exr: skipped (clipboard only)");
            }

            output.AppendLine($"clipboard: {(outcome.ClipboardCopied ? "ok" : outcome.ClipboardError ?? "skipped")}");
            Write(console, output.ToString());
            return outcome.ExrSaved || (outputPath is null && outcome.ClipboardCopied) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("Region capture command failed.", ex);
            output.AppendLine($"ERROR: {ex}");
            Write(console, output.ToString());
            return 1;
        }
    }

    private static void AppendExrSummary(StringBuilder output, string path)
    {
        try
        {
            var image = OpenExrReader.Read(path);
            output.AppendLine(
                $"exr header: {image.Width}x{image.Height}, channels={string.Join(",", image.ChannelNames)}, " +
                $"compression={image.Attributes["compression"][0]}, " +
                $"timestamp={image.GetStringAttribute("captureTimestamp")}");
            foreach (var channel in image.ChannelNames)
            {
                var maximum = image.MaxLinear(channel);
                output.AppendLine(
                    $"  {channel}: max linear {maximum.ToString("0.######", CultureInfo.InvariantCulture)} " +
                    $"({(maximum * 80.0).ToString("0.#", CultureInfo.InvariantCulture)} nit)");
            }
        }
        catch (Exception ex)
        {
            output.AppendLine($"exr verify failed: {ex.Message}");
        }
    }

    private static bool TryParse(
        string[] args,
        out PixelRect region,
        out string? outputPath,
        out bool copyToClipboard,
        out string error)
    {
        region = default;
        outputPath = null;
        copyToClipboard = true;
        error = "缺少 --capture-region 参数。";

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--capture-region", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length ||
                    !TryParseRect(args[index + 1], out region))
                {
                    error = "--capture-region 需要 x,y,width,height 形式的参数。";
                    return false;
                }

                index++;
            }
            else if (string.Equals(argument, "--out", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    error = "--out 需要一个文件路径。";
                    return false;
                }

                outputPath = args[++index];
            }
            else if (string.Equals(argument, "--no-clipboard", StringComparison.OrdinalIgnoreCase))
            {
                copyToClipboard = false;
            }
        }

        if (region.IsEmpty)
        {
            error = "--capture-region 的宽高必须大于 0。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseRect(string text, out PixelRect region)
    {
        region = default;
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return false;
        }

        region = new PixelRect(x, y, width, height);
        return true;
    }

    private static void Write(ConsoleHost console, string text)
    {
        if (console.IsAttached)
        {
            console.WriteLine(text.TrimEnd());
            return;
        }

        System.Windows.MessageBox.Show(
            text,
            "HDRCapture",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }
}
