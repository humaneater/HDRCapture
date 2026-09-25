using System.Globalization;
using System.Text;
using HdrCapture.Capture;
using HdrCapture.ComfyUi;
using HdrCapture.Configuration;
using HdrCapture.Infrastructure;
using HdrCapture.Imaging;
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
            output.AppendLine($"OIDN: {(OidnRuntime.IsAvailable ? "available" : "unavailable")}");
            if (!OidnRuntime.IsAvailable)
            {
                output.AppendLine($"OIDN error: {OidnRuntime.Error}");
            }

            var settings = new SettingsStore().Load();
            var comfyValidation = ComfyUiService.Validate(settings.ComfyUi);
            output.AppendLine(
                $"ComfyUI configured root: " +
                $"{(string.IsNullOrWhiteSpace(settings.ComfyUi.RootPath) ? "(empty)" : settings.ComfyUi.RootPath)}");
            output.AppendLine($"ComfyUI path valid: {(comfyValidation.IsValid ? "yes" : "no")}");
            if (!comfyValidation.IsValid)
            {
                foreach (var missing in comfyValidation.Missing)
                {
                    output.AppendLine($"ComfyUI missing: {missing}");
                }
            }

            var comfyService = new ComfyUiService();
            try
            {
                var ready = await comfyService
                    .ProbeAsync(settings.ComfyUi)
                    .ConfigureAwait(false);
                var processSource = ready.IsReady
                    ? comfyService.TryGetOwnedProcess(out var ownedProcessId)
                        ? $"HDRCapture (PID {ownedProcessId})"
                        : "external"
                    : "not running";
                output.AppendLine(
                    $"ComfyUI API {settings.ComfyUi.BaseUrl}: " +
                    $"{(ready.IsReady ? "ready" : "not running")}, process source {processSource}");
                if (ready.IsReady)
                {
                    output.AppendLine(
                        $"ComfyUI nodes: FaceDetailer " +
                        $"{(ready.FaceDetailerAvailable ? "available" : "missing")}, " +
                        $"IPAdapterAdvanced " +
                        $"{(ready.IpAdapterAdvancedAvailable ? "available" : "missing")}");
                    output.AppendLine(
                        $"ComfyUI checkpoints: " +
                        $"{(ready.Checkpoints.Count == 0 ? "(none)" : string.Join(", ", ready.Checkpoints))}");
                }

                if (!string.IsNullOrWhiteSpace(ready.Error))
                {
                    output.AppendLine($"ComfyUI API error: {ready.Error}");
                }

                if (comfyValidation.Installation is not null)
                {
                    var faceModel = Path.Combine(
                        comfyValidation.Installation.UltralyticsDirectory,
                        "face_yolov8m.pt");
                    var samModel = Path.Combine(
                        comfyValidation.Installation.SamDirectory,
                        "sam_vit_b_01ec64.pth");
                    output.AppendLine($"Face detector: {(File.Exists(faceModel) ? faceModel : "missing")}");
                    output.AppendLine($"SAM model: {(File.Exists(samModel) ? samModel : "missing")}");
                    var optional = ComfyUiOptionalComponents.Inspect(
                        comfyValidation.Installation);
                    output.AppendLine(
                        $"Optional IP-Adapter Face: " +
                        $"{(optional.IsInstalled ? "available" : "not installed")}");
                    output.AppendLine(
                        $"Optional CLIP-Vision: " +
                        $"{(optional.ClipVisionPath ?? "missing")}");
                }
            }
            finally
            {
                comfyService.Dispose();
            }

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
