using System.Diagnostics;
using HdrCapture.Capture;
using HdrCapture.Configuration;
using HdrCapture.Exr;
using HdrCapture.Imaging;
using HdrCapture.Infrastructure;
using HdrCapture.Native;
using HdrCapture.Ui;

namespace HdrCapture.Core;

/// <summary>Freeze, select, compose, save and copy. Only one capture may run at a time.</summary>
internal sealed class CaptureWorkflow
{
    private readonly ScreenCaptureService _captureService = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Must be called from the UI thread: it shows the frozen selection overlay.</summary>
    public async Task<CaptureOutcome> RunInteractiveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            return new CaptureOutcome { Cancelled = true };
        }

        try
        {
            if (ShouldSaveExr(settings))
            {
                try
                {
                    EnsureSaveDirectory(settings);
                }
                catch (Exception ex)
                {
                    Log.Error("Save directory is not writable.", ex);
                    return new CaptureOutcome
                    {
                        Cancelled = true,
                        SaveDirectoryError = ex.Message
                    };
                }
            }

            var capturedAt = DateTimeOffset.Now;
            var stopwatch = Stopwatch.StartNew();
            MemoryTrimmer.CollectAndTrim("before freeze");
            var captures = await _captureService
                .CaptureAllAsync(
                    MonitorEnumerator.Enumerate(),
                    settings.IncludeCursor,
                    cancellationToken)
                .ConfigureAwait(true);
            Log.Info($"[timing] freeze completed in {stopwatch.ElapsedMilliseconds} ms");
            MemoryTrimmer.CollectAndTrim("after freeze");
            await Task.Run(
                    () => ScreenCaptureService.CreatePreviews(captures, settings.PreviewQuality),
                    cancellationToken)
                .ConfigureAwait(true);

            stopwatch.Restart();
            var overlay = new SelectionOverlay(captures);
            Log.Info($"[timing] overlay prepared in {stopwatch.ElapsedMilliseconds} ms");
            var region = await overlay.RunAsync().ConfigureAwait(true);
            if (region is null)
            {
                overlay.Dispose();
                return new CaptureOutcome { Cancelled = true };
            }

            // Release the full-screen previews before allocating and saving the selected image.
            overlay.Dispose();
            foreach (var capture in captures)
            {
                capture.ReleasePreview();
            }

            MemoryTrimmer.CollectAndTrim("after selection");
            return await ProcessAsync(
                    captures,
                    region.Value,
                    settings,
                    capturedAt,
                    copyToClipboard: true,
                    outputPath: null,
                    saveExr: settings.SaveExr,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CaptureOutcome> CaptureRegionAsync(
        AppSettings settings,
        PixelRect region,
        bool copyToClipboard,
        string? outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("已有截图任务正在运行。");
        }

        try
        {
            var saveExr = ShouldSaveExr(settings, outputPath);
            if (saveExr)
            {
                EnsureSaveDirectory(settings);
            }

            var capturedAt = DateTimeOffset.Now;
            MemoryTrimmer.CollectAndTrim("before freeze");
            var captures = await _captureService
                .CaptureAllAsync(MonitorEnumerator.Enumerate(), settings.IncludeCursor, cancellationToken)
                .ConfigureAwait(false);
            return await ProcessAsync(
                    captures,
                    region,
                    settings,
                    capturedAt,
                    copyToClipboard,
                    outputPath,
                    saveExr,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static void EnsureSaveDirectory(AppSettings settings)
    {
        var directory = AppSettings.ResolveSaveDirectory(settings.SaveDirectory);
        Directory.CreateDirectory(directory);

        var probePath = Path.Combine(directory, ".hdrcapture-write-test");
        File.WriteAllBytes(probePath, [0]);
        File.Delete(probePath);
    }

    public static bool ShouldSaveExr(AppSettings settings, string? outputPath = null) =>
        outputPath is not null || settings.SaveExr;

    private static async Task<CaptureOutcome> ProcessAsync(
        IReadOnlyList<CapturedMonitor> captures,
        PixelRect region,
        AppSettings settings,
        DateTimeOffset capturedAt,
        bool copyToClipboard,
        string? outputPath,
        bool saveExr,
        CancellationToken cancellationToken)
    {
        var outcome = new CaptureOutcome { Region = region };
        var image = RegionComposer.Compose(captures, region);
        MemoryTrimmer.CollectAndTrim("after compose");

        if (saveExr)
        {
            var directory = AppSettings.ResolveSaveDirectory(settings.SaveDirectory);
            var path = outputPath ?? CaptureFileNaming.EnsureUniquePath(
                directory,
                CaptureFileNaming.BuildFileName(capturedAt, region, captures));

            var metadata = new ExrMetadata(
                capturedAt,
                region,
                DescribeMonitors(captures),
                GetPrimaryWhiteNits(image),
                settings.ExposureEv,
                "Windows.Graphics.Capture (DXGI, FP16 scRGB)");

            try
            {
                await Task.Run(
                        () => OpenExrWriter.WriteAtomic(path, image, metadata),
                        cancellationToken)
                    .ConfigureAwait(true);
                outcome.ExrPath = path;
                Log.Info($"Saved EXR: {path}");
            }
            catch (Exception ex)
            {
                Log.Error("Failed to write the EXR file.", ex);
                outcome.ExrError = ex.Message;
            }
        }

        if (!copyToClipboard)
        {
            return outcome;
        }

        try
        {
            await Task.Run(
                    () => ClipboardWriter.SetImage(image, settings.ExposureEv),
                    cancellationToken)
                .ConfigureAwait(true);
            outcome.ClipboardCopied = true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to copy the PNG to the clipboard.", ex);
            outcome.ClipboardError = ex.Message;
        }

        return outcome;
    }

    private static double GetPrimaryWhiteNits(LinearImage image) =>
        image.Regions.Count == 0 ? 200.0 : image.Regions[0].SdrWhiteNits;

    private static string DescribeMonitors(IReadOnlyList<CapturedMonitor> captures)
    {
        return string.Join(
            "; ",
            captures.Select(capture =>
                $"{capture.Monitor.DeviceName} {capture.Width}x{capture.Height} " +
                $"at ({capture.Monitor.X},{capture.Monitor.Y}) " +
                $"{(capture.Monitor.HdrEnabled ? "HDR" : "SDR")} " +
                $"{capture.Monitor.SdrWhiteNits:0.#}nit"));
    }
}
