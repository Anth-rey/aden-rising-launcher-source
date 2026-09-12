using System.Runtime.InteropServices;

namespace AdenRising.Updater.Core;

/// <summary>
/// Which monitor the player is looking at, and how fast it refreshes.
///
/// This file used to shape the game's window as well -- take its frame off,
/// size it to the monitor, answer Alt+Enter, pen the mouse inside it -- because
/// the client asked Direct3D for the screen and died on hardware its settings
/// file had never described. All of that now happens a layer lower, in the
/// graphics wrapper that sits under the client, which does it for anyone who
/// starts the game by hand as well. Two things managing one window only ever
/// fight, so the launcher stopped being one of them.
/// </summary>
public static class Display
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    private const int MonitorDefaultToNearest = 2;
    private const int CurrentSettings = -1;

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode dm);

    public readonly record struct Screen(int X, int Y, int Width, int Height, int RefreshRate);

    /// <summary>The monitor showing <paramref name="near"/> -- the launcher
    /// window, which is the screen the player is sitting in front of. Reading
    /// the primary monitor instead answers for the wrong one on any desk with
    /// two.</summary>
    public static Screen ScreenFor(IntPtr near)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(MonitorFromWindow(near, MonitorDefaultToNearest), ref info))
            return new Screen(0, 0, 1920, 1080, 60);

        var dm = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        var hz = EnumDisplaySettings(info.Device, CurrentSettings, ref dm) ? (int)dm.DisplayFrequency : 60;

        return new Screen(
            info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top,
            hz > 0 ? hz : 60);
    }
}
