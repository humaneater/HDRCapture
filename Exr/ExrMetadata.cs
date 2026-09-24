using HdrCapture.Capture;

namespace HdrCapture.Exr;

internal sealed record ExrMetadata(
    DateTimeOffset CapturedAt,
    PixelRect Region,
    string MonitorSummary,
    double PrimarySdrWhiteNits,
    double LdrExposureEv,
    string SourceDescription);
