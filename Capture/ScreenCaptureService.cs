using System.Runtime.ExceptionServices;
using HdrCapture.Configuration;
using HdrCapture.Infrastructure;
using HdrCapture.Native;

namespace HdrCapture.Capture;

internal sealed class ScreenCaptureService
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<CapturedMonitor>> CaptureAllAsync(
        IReadOnlyList<MonitorInfo> monitors,
        bool includeCursor,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => CaptureAll(monitors, includeCursor, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<CapturedMonitor>> CaptureAllAsync(
        bool includeCursor,
        CancellationToken cancellationToken = default)
    {
        return CaptureAllAsync(MonitorEnumerator.Enumerate(), includeCursor, cancellationToken);
    }

    public static void CreatePreviews(
        IReadOnlyList<CapturedMonitor> captures,
        PreviewQuality quality)
    {
        Parallel.ForEach(
            captures,
            capture => capture.Preview = Imaging.PreviewRenderer.Create(capture, quality));
    }

    private static IReadOnlyList<CapturedMonitor> CaptureAll(
        IReadOnlyList<MonitorInfo> monitors,
        bool includeCursor,
        CancellationToken cancellationToken)
    {
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("没有找到可用的显示器。");
        }

        // One device and one capture session per display: the Windows Graphics Capture frame
        // latency of all displays then overlaps instead of adding up.
        var captures = new CapturedMonitor[monitors.Count];
        Exception? failure = null;
        Parallel.For(0, monitors.Count, index =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var graphicsDevice = WinRtCaptureNative.CreateGraphicsDevice();
                captures[index] = CaptureMonitor(graphicsDevice, monitors[index], includeCursor);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        });

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return captures;
    }

    private static CapturedMonitor CaptureMonitor(
        WinRtCaptureNative.GraphicsDevice graphicsDevice,
        MonitorInfo monitor,
        bool includeCursor)
    {
        nint item = 0;
        nint framePool = 0;
        nint session = 0;
        nint frame = 0;
        nint surface = 0;
        nint texture = 0;
        nint staging = 0;

        var pixelFormat = monitor.HdrEnabled
            ? WinRtCaptureNative.DirectXPixelFormatR16G16B16A16Float
            : WinRtCaptureNative.DirectXPixelFormatB8G8R8A8UIntNormalized;

        try
        {
            item = WinRtCaptureNative.CreateGraphicsCaptureItem(monitor.Handle);
            var width = WinRtCaptureNative.GetItemWidth(item);
            var height = WinRtCaptureNative.GetItemHeight(item);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"显示器 {monitor.DeviceName} 的捕获尺寸无效。");
            }

            if (width != monitor.Width || height != monitor.Height)
            {
                Log.Info(
                    $"Capture size {width}x{height} differs from monitor rect " +
                    $"{monitor.Width}x{monitor.Height} on {monitor.DeviceName}.");
            }

            framePool = WinRtCaptureNative.CreateFramePool(
                graphicsDevice.GraphicsDevicePointer,
                pixelFormat,
                width,
                height);
            session = WinRtCaptureNative.CreateCaptureSession(framePool, item);
            WinRtCaptureNative.ConfigureSession(session, includeCursor, hideBorder: true);
            WinRtCaptureNative.StartCapture(session);

            frame = WinRtCaptureNative.TryGetNextFrame(framePool, FrameTimeout);
            surface = WinRtCaptureNative.GetFrameSurface(frame);
            texture = WinRtCaptureNative.GetD3DTextureFromSurface(surface);

            var sourceFormat = WinRtCaptureNative.GetTextureDescription(texture).Format;
            if (sourceFormat != (uint)pixelFormat)
            {
                throw new InvalidOperationException(
                    monitor.HdrEnabled
                        ? $"显示器 {monitor.DeviceName} 已启用 HDR，但捕获帧不是 FP16 " +
                          $"(DXGI format {sourceFormat})，无法取得真正的 HDR 数据。"
                        : $"显示器 {monitor.DeviceName} 的捕获帧格式为 {sourceFormat}，" +
                          $"而不是预期的 BGRA8。");
            }

            staging = WinRtCaptureNative.CreateStagingTexture(
                graphicsDevice.Device,
                pixelFormat,
                width,
                height);
            WinRtCaptureNative.CopyResource(graphicsDevice.Context, staging, texture);

            // The staging texture now owns the copied frame. Release the much larger WGC
            // frame pool before allocating the CPU-side pixel buffer.
            Com.Release(texture);
            texture = 0;
            Com.Release(surface);
            surface = 0;
            Com.Release(frame);
            frame = 0;
            Com.CloseAndRelease(ref session);
            Com.CloseAndRelease(ref framePool);
            Com.Release(item);
            item = 0;

            var pixels = WinRtCaptureNative.ReadTexture(
                graphicsDevice.Context,
                staging,
                width,
                height,
                monitor.HdrEnabled ? 8 : 4);

            return new CapturedMonitor(
                monitor,
                monitor.HdrEnabled ? CapturePixelFormat.Rgba16Float : CapturePixelFormat.Bgra8,
                width,
                height,
                pixels);
        }
        finally
        {
            Com.Release(staging);
            Com.Release(texture);
            Com.Release(surface);
            Com.Release(frame);
            Com.CloseAndRelease(ref session);
            Com.CloseAndRelease(ref framePool);
            Com.Release(item);
        }
    }
}
