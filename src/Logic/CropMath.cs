namespace RefractionLab.Logic;

public readonly record struct PxRect(int X, int Y, int Width, int Height);

public static class CropMath
{
    public const int ProbeMarginDip = 14;
    public const int ProbeCellDip = 6;

    public static PxRect Inflate(PxRect rect, int pad) =>
        new(rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2);

    public static bool Intersects(PxRect a, PxRect b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 &&
        a.X < b.X + b.Width && b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    public static bool AnyIntersects(ReadOnlySpan<PxRect> rects, PxRect crop)
    {
        foreach (PxRect rect in rects)
            if (Intersects(rect, crop))
                return true;
        return false;
    }

    /// <summary>Probe grid location inside the panel's client area (top-right), in physical pixels.</summary>
    public static PxRect ProbeRect(int clientWidth, double scale, out int cellPx)
    {
        cellPx = Math.Max(4, (int)Math.Round(ProbeCellDip * scale));
        int margin = (int)Math.Round(ProbeMarginDip * scale);
        int size = ProbePattern.Cells * cellPx;
        return new PxRect(clientWidth - size - margin, margin, size, size);
    }

    /// <summary>
    /// Maps a panel-local rectangle to captured-frame pixels. Fails if any part falls outside the frame,
    /// so callers never read or copy pixels beyond the captured display.
    /// </summary>
    public static bool TryToFrame(PxRect local, int clientScreenX, int clientScreenY, PxRect monitor, out PxRect frame)
    {
        frame = new PxRect(
            local.X + clientScreenX - monitor.X,
            local.Y + clientScreenY - monitor.Y,
            local.Width,
            local.Height);

        return local.Width > 0 && local.Height > 0 &&
            frame.X >= 0 && frame.Y >= 0 &&
            frame.X + frame.Width <= monitor.Width &&
            frame.Y + frame.Height <= monitor.Height;
    }
}
