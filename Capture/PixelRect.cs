namespace HdrCapture.Capture;

/// <summary>Axis aligned rectangle in physical desktop pixels. Supports negative origins.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromCorners(int x0, int y0, int x1, int y1)
    {
        var left = Math.Min(x0, x1);
        var top = Math.Min(y0, y1);
        return new PixelRect(left, top, Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(left, top, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
