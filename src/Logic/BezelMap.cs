namespace RefractionLab.Logic;

/// <summary>
/// Displacement map for a rounded glass pane. Inside the bezel each pixel samples the backdrop from
/// further toward the pane's center (a convex rim); the interior is neutral, so it stays undistorted.
/// Channel encoding follows D2D/Win2D: result[p] = Source[p + Amount * (channel - 0.5)].
/// </summary>
public static class BezelMap
{
    public const byte Neutral = 128;

    /// <summary>Unit-range displacement (toward the sample position) for pixel (x, y); zero outside the bezel.</summary>
    public static (float X, float Y) Displacement(int x, int y, int width, int height, float radius, float bezel)
    {
        float px = x + 0.5f - width / 2f;
        float py = y + 0.5f - height / 2f;
        float qx = MathF.Abs(px) - (width / 2f - radius);
        float qy = MathF.Abs(py) - (height / 2f - radius);
        float outside = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0));
        float depth = -(outside + MathF.Min(MathF.Max(qx, qy), 0) - radius);

        float t = Math.Clamp(depth / bezel, 0f, 1f);
        if (t >= 1f)
            return (0f, 0f);

        float sx = px < 0 ? -1f : 1f;
        float sy = py < 0 ? -1f : 1f;
        float nx, ny;
        if (qx > 0 && qy > 0)
        {
            nx = qx / outside * sx;
            ny = qy / outside * sy;
        }
        else if (qx > qy)
        {
            nx = sx;
            ny = 0;
        }
        else
        {
            nx = 0;
            ny = sy;
        }

        float strength = (1f - t) * (1f - t);
        return (-nx * strength, -ny * strength);
    }

    /// <summary>BGRA map; red carries X, green carries Y, alpha is opaque.</summary>
    public static byte[] Generate(int width, int height, float radius, float bezel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            (float dx, float dy) = Displacement(x, y, width, height, radius, bezel);
            int i = (y * width + x) * 4;
            bytes[i] = Neutral;
            bytes[i + 1] = Encode(dy);
            bytes[i + 2] = Encode(dx);
            bytes[i + 3] = 255;
        }
        return bytes;
    }

    private static byte Encode(float v) => (byte)(Neutral + (int)MathF.Round(Math.Clamp(v, -1f, 1f) * 127f));
}
