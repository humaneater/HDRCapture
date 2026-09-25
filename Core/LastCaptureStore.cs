using HdrCapture.Capture;

namespace HdrCapture.Core;

/// <summary>
/// Keeps only the most recent composed HDR frame. Replacing or clearing the store releases
/// the previous image so repeated captures cannot accumulate full-resolution buffers.
/// </summary>
internal sealed class LastCaptureStore : IDisposable
{
    private CapturedImageSnapshot? _current;

    public CapturedImageSnapshot? Current => _current;

    public bool HasCapture => _current is not null;

    public void Store(CapturedImageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previous = Interlocked.Exchange(ref _current, snapshot);
        previous?.Dispose();
    }

    public void Clear()
    {
        Interlocked.Exchange(ref _current, null)?.Dispose();
    }

    public void Dispose() => Clear();
}

internal sealed class CapturedImageSnapshot : IDisposable
{
    private LinearImage? _image;

    public CapturedImageSnapshot(
        LinearImage image,
        PixelRect region,
        DateTimeOffset capturedAt,
        string monitorSummary,
        double primarySdrWhiteNits)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        Region = region;
        CapturedAt = capturedAt;
        MonitorSummary = monitorSummary;
        PrimarySdrWhiteNits = primarySdrWhiteNits;
    }

    public LinearImage Image =>
        _image ?? throw new ObjectDisposedException(nameof(CapturedImageSnapshot));

    public PixelRect Region { get; }

    public DateTimeOffset CapturedAt { get; }

    public string MonitorSummary { get; }

    public double PrimarySdrWhiteNits { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _image, null);
    }
}
