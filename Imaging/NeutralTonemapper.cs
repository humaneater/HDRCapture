namespace HdrCapture.Imaging;

/// <summary>
/// Khronos PBR Neutral tone mapping. Input is scene linear where 1.0 is SDR white,
/// output is display linear in the 0..1 range.
/// </summary>
internal static class NeutralTonemapper
{
    private const float StartCompression = 0.8f - 0.04f;
    private const float Desaturation = 0.15f;

    public static (float Red, float Green, float Blue) Map(float red, float green, float blue)
    {
        red = MathF.Max(red, 0f);
        green = MathF.Max(green, 0f);
        blue = MathF.Max(blue, 0f);

        var minimum = MathF.Min(red, MathF.Min(green, blue));
        var offset = minimum < 0.08f
            ? minimum - (6.25f * minimum * minimum)
            : 0.04f;
        red -= offset;
        green -= offset;
        blue -= offset;

        var peak = MathF.Max(red, MathF.Max(green, blue));
        if (peak < StartCompression || peak <= 0f)
        {
            return (red, green, blue);
        }

        const float Distance = 1f - StartCompression;
        var newPeak = 1f - (Distance * Distance / (peak + Distance - StartCompression));
        var scale = newPeak / peak;
        red *= scale;
        green *= scale;
        blue *= scale;

        var blend = 1f - (1f / ((Desaturation * (peak - newPeak)) + 1f));
        return (
            red + ((newPeak - red) * blend),
            green + ((newPeak - green) * blend),
            blue + ((newPeak - blue) * blend));
    }
}
