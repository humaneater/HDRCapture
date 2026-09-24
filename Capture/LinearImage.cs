namespace HdrCapture.Capture;

/// <summary>
/// Cropped, composed capture in linear scRGB half precision. 1.0 equals 80 nit.
/// Channels are stored as separate planes so the OpenEXR writer can emit scanlines
/// without any extra copy.
/// </summary>
internal sealed class LinearImage
{
    public LinearImage(int width, int height)
    {
        Width = width;
        Height = height;
        var count = checked(width * height);
        Red = new ushort[count];
        Green = new ushort[count];
        Blue = new ushort[count];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Red plane, half-float bits.</summary>
    public ushort[] Red { get; }

    /// <summary>Green plane, half-float bits.</summary>
    public ushort[] Green { get; }

    /// <summary>Blue plane, half-float bits.</summary>
    public ushort[] Blue { get; }

    public List<WhitePointRegion> Regions { get; } = [];

    public long ApproximateBytes => 6L * Width * Height;
}

/// <summary>Rectangle of the composed image that came from one display.</summary>
internal readonly record struct WhitePointRegion(
    int X,
    int Y,
    int Width,
    int Height,
    string MonitorDevice,
    bool HdrEnabled,
    double SdrWhiteNits)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}
