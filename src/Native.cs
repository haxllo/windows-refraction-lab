using System.Runtime.InteropServices;

namespace RefractionLab;

internal static class Native
{
    public const uint WdaNone = 0;
    public const uint WdaExcludeFromCapture = 0x11;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

    public static bool TryGetMonitor(IntPtr hwnd, out IntPtr monitor, out RECT rect)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        bool ok = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
        rect = info.Monitor;
        return ok;
    }

    public static bool TryGetMonitorRect(IntPtr hwnd, out RECT rect) => TryGetMonitor(hwnd, out _, out rect);

    public static bool ExcludeFromCapture(IntPtr hwnd) =>
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture) &&
        GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == WdaExcludeFromCapture;
}
