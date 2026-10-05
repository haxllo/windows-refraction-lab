namespace RefractionLab.Logic;

public static class FramePacing
{
    public const int Unlimited = 0;
    public const int DefaultFps = 30;
    public static readonly int[] Options = [15, 30, 60, Unlimited];

    /// <summary>Minimum time between processed frames. <see cref="Unlimited"/> means as fast as frames arrive.</summary>
    public static TimeSpan MinInterval(int fps) =>
        fps == Unlimited ? TimeSpan.Zero :
        fps < 0 ? MinInterval(DefaultFps) :
        TimeSpan.FromSeconds(1.0 / fps);

    public static string Label(int fps) => fps == Unlimited ? "Unlimited" : fps.ToString();
}
