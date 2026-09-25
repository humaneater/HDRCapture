using HdrCapture.Capture;

namespace HdrCapture.Core;

internal sealed class CaptureOutcome
{
    public bool Cancelled { get; init; }

    public PixelRect Region { get; init; }

    public string? ExrPath { get; set; }

    public string? ExrError { get; set; }

    public string? SaveDirectoryError { get; set; }

    public bool ClipboardCopied { get; set; }

    public string? ClipboardError { get; set; }

    public bool ExrSaved => ExrPath is not null;
}
