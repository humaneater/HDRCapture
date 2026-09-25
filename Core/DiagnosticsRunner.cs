using System.Globalization;
using System.Text;
using HdrCapture.Capture;
using HdrCapture.Infrastructure;
using HdrCapture.Native;

namespace HdrCapture.Core;

internal static class DiagnosticsRunner
{
    public static async Task<int> RunAsync(ConsoleHost console)
    {
        var output = new StringBuilder();
        try
        {
            var monitors = MonitorEnumerator.Enumerate();
            output.AppendLine($"OS: {Environment.OSVersion.VersionString}");
            output.AppendLine($"Monitors: {monitors.Count}");
            foreach (var monitor in monitors)
            {
                output.AppendLine(
                    $"{monitor.DeviceName}: {monitor.Width}x{monitor.Height} at ({monitor.X},{monitor.Y}), " +
                    $"DPI {monitor.Dpi}, HDR {(monitor.HdrEnabled ? "on" : "off")}, " +
                    $"SDR white {Text(monitor.SdrWhiteNits)} nit " +
                    $"({Text(monitor.SdrWhiteNits / 80.0)} in scRGB)");
            }

            output.AppendLine($"D3D11 staging-map probe: 0x{WinRtCaptureNative.ProbeMapRead():X8}");

            var capture = new ScreenCaptureService();
            var captures = await capture
                .CaptureAllAsync(monitors, includeCursor: false)
                .ConfigureAwait(false);
            foreach (var item in captures)
            {
                var range = GetRange(item);
                output.AppendLine(
                    $"{item.Monitor.DeviceName}: WGC {item.Format} {item.Width}x{item.Height}, " +
                    $"linear min {Text(range.Minimum)} max {Text(range.Maximum)} " +
                    $"({Text(range.Maximum * 80.0)} nit), " +
                    $"above 1.0: {(range.Maximum > 1.0001f ? "yes" : "no")}");
            }

            Write(console, output.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostics failed.", ex);
            output.AppendLine($"ERROR: {ex}");
            Write(console, output.ToString());
            return 1;
        }
    }

    private static (float Minimum, float Maximum) GetRange(CapturedMonitor capture)
    {
        var minimum = float.MaxValue;
        var maximum = float.MinValue;

        if (capture.Format == CapturePixelFormat.Rgba16Float)
        {
            for (var index = 0; index < capture.Pixels.Length; index += 8)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = (float)BitConverter.ToHalf(
                        capture.Pixels.AsSpan(index + (channel * 2), 2));
                    if (!float.IsFinite(value))
                    {
                        continue;
                    }

                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                }
            }
        }
        else
        {
            for (var index = 0; index < capture.Pixels.Length; index += 4)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var linear = SrgbToLinear(capture.Pixels[index + channel] / 255.0f);
                    minimum = Math.Min(minimum, linear);
                    maximum = Math.Max(maximum, linear);
                }
            }
        }

        return minimum == float.MaxValue ? (0, 0) : (minimum, maximum);
    }

    private static float SrgbToLinear(float value) =>
        value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static string Text(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void Write(ConsoleHost console, string text)
    {
        if (console.IsAttached)
        {
            console.WriteLine(text.TrimEnd());
            return;
        }

        System.Windows.MessageBox.Show(
            text,
            "HDRCapture 诊断",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }
}
