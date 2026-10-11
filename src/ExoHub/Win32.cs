using System.Runtime.InteropServices;

namespace ExoHub;

// The few user32 calls the hub needs that DXGI does not cover.
//
// A P/Invoke struct must match the C layout byte for byte. Sequential stops
// the CLR reordering fields, CharSet.Unicode makes the strings WCHAR, and
// ByValTStr with SizeConst is a fixed inline WCHAR array, not a pointer. A
// wrong layout does not fail, it silently reads the neighbouring field.
static class Win32
{
    // Ask for the monitor's device interface path in DeviceID instead of
    // the bare hardware ID. The path carries the EDID manufacturer and
    // product code, e.g. \\?\DISPLAY#ACR0721#7&15a5ef1&0&UID260#{...}.
    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum,
                                                  ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    // The hotspot in virtual-desktop pixels, physical because the manifest
    // makes this process per-monitor aware. Only to check DXGI's pointer
    // position against an independent source.
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    // cbSize must be set to this struct's size (40) before the call: it is
    // how Windows tells MONITORINFO from the larger MONITORINFOEX.
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTONULL = 0;

    // The monitor rectangle touch is injected into (CLAUDE.md, Touch), in
    // physical pixels because this process is per-monitor aware.
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    // Used only to print which DPI awareness the manifest actually gave us.
    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    // Timer resolution for this process, in milliseconds. The default tick is
    // about 15.6ms, half a frame at 30fps, and the encoder's rate gate waits
    // on a timer. Paired: every Begin needs an End with the same value.
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint ms);
}
