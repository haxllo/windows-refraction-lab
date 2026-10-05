namespace RefractionLab.Logic;

public static class FramePacing
{
    public const int DefaultFps = 30;
    public static readonly int[] Options = [15, 30, 60];

    /// <summary>Minimum time between processed frames. 60 or more means "as fast as the display updates".</summary>
    public static TimeSpan MinInterval(int fps) =>
        fps <= 0 ? MinInterval(DefaultFps) :
        fps >= 60 ? TimeSpan.Zero :
        TimeSpan.FromSeconds(1.0 / fps);
}
