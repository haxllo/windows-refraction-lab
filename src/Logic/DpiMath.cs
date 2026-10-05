namespace RefractionLab.Logic;

public static class DpiMath
{
    /// <summary>
    /// A size in DIPs that a DIP-based API turns into exactly <paramref name="pixels"/> device pixels at
    /// <paramref name="scale"/>, whether it rounds or truncates the product (the quarter-pixel nudge keeps
    /// float error and fractional scales such as 1.25 from losing a pixel).
    /// </summary>
    public static float DipsForPixels(int pixels, double scale) =>
        pixels <= 0 || scale <= 0 ? 0f : (float)((pixels + 0.25) / scale);
}
