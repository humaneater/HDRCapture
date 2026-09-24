namespace HdrCapture.Capture;

internal enum CapturePixelFormat
{
    Rgba16Float,
    Bgra8
}

internal sealed class CapturedMonitor
{
    public CapturedMonitor(
        MonitorInfo monitor,
        CapturePixelFormat format,
        int width,
        int height,
        byte[] pixels)
    {
        Monitor = monitor;
        Format = format;
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public MonitorInfo Monitor { get; }

    public CapturePixelFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Raw frame bytes: RGBA half-float, or BGRA8 for SDR displays.</summary>
    public byte[] Pixels { get; private set; }

    /// <summary>Frozen sRGB preview for the selection overlay, created during capture.</summary>
    public System.Windows.Media.Imaging.BitmapSource? Preview { get; set; }

    public int BytesPerPixel => Format == CapturePixelFormat.Rgba16Float ? 8 : 4;

    public void ReleasePreview()
    {
        Preview = null;
    }

    public void ReleasePixels()
    {
        Pixels = [];
        ReleasePreview();
    }
}
