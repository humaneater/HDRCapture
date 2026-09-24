namespace HdrCapture.Capture;

internal sealed record MonitorInfo(
    nint Handle,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    uint Dpi,
    bool HdrEnabled,
    double SdrWhiteNits)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public double DpiScale => Dpi / 96.0;

    public bool Contains(int x, int y) =>
        x >= X && x < Right && y >= Y && y < Bottom;
}
