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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size; public RECT Monitor; public RECT Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    // DEVMODEW is 220 bytes on x86 and x64; only dmSize and dmDisplayFrequency are touched.
    private const int DevModeSize = 220;
    private const int DevModeSizeOffset = 68;
    private const int DevModeFrequencyOffset = 184;
    private const int EnumCurrentSettings = -1;

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettings(string deviceName, int modeNum, byte[] devMode);
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

    /// <summary>Current refresh rate of the panel's display in Hz, or 0 if Windows will not say.</summary>
    public static int GetRefreshRateHz(IntPtr hwnd)
    {
        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>(), DeviceName = string.Empty };
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !GetMonitorInfoEx(monitor, ref info))
            return 0;

        var devMode = new byte[DevModeSize];
        BitConverter.TryWriteBytes(devMode.AsSpan(DevModeSizeOffset, 2), (ushort)DevModeSize);
        if (!EnumDisplaySettings(info.DeviceName, EnumCurrentSettings, devMode))
            return 0;

        uint hz = BitConverter.ToUInt32(devMode, DevModeFrequencyOffset);
        return hz > 1 && hz < 1000 ? (int)hz : 0; // 0 and 1 mean "hardware default"
    }

    public static bool ExcludeFromCapture(IntPtr hwnd) =>
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture) &&
        GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == WdaExcludeFromCapture;
}
