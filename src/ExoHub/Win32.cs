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

    // The monitor rectangle touch is injected into (traps-touch), in
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

    // PROCESS_POWER_THROTTLING_STATE, for SetProcessInformation's
    // ProcessPowerThrottling class. The IGNORE_TIMER_RESOLUTION bit in
    // ControlMask, clear in StateMask, keeps timeBeginPeriod honoured while
    // the process shows no window.
    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(nint process, int infoClass, ref PowerThrottling info, int size);

    // Null, or INVALID_HANDLE_VALUE, when the hub was started with no
    // stdout: a WinExe from the shortcut or at sign-in (HubLog).
    public const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32.dll")]
    public static extern nint GetStdHandle(int which);

    // ExoDisplay's device interface, found by its GUID and opened like a
    // file. The list is a multi-string: paths separated by NUL, ending in two.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceGuid, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceGuid, string deviceId, char[] buffer, uint length, uint flags);

    public const uint GENERIC_READ_WRITE = 0xC0000000, FILE_SHARE_READ_WRITE = 3, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
                                                                               uint disposition, uint flags, IntPtr template);

    // The driver's cursor event, opened only to wait on.
    public const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeWaitHandle OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[] inBuffer, int inLength,
                                              byte[] outBuffer, int outLength, out int returned, IntPtr overlapped);

    // The display topology (CCD). Explicit layouts, at the offsets of the C
    // structs on x64, with only the fields the hub reads or writes named.
    public const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80,
                      SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 1, DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        [FieldOffset(0)] public uint sourceAdapterLow;
        [FieldOffset(4)] public int sourceAdapterHigh;
        [FieldOffset(8)] public uint sourceId;
        [FieldOffset(12)] public uint sourceModeIdx;
        [FieldOffset(20)] public uint targetAdapterLow;
        [FieldOffset(24)] public int targetAdapterHigh;
        [FieldOffset(28)] public uint targetId;
        [FieldOffset(32)] public uint targetModeIdx;
        [FieldOffset(48)] public uint refreshNumerator;
        [FieldOffset(52)] public uint refreshDenominator;
        [FieldOffset(60)] public int targetAvailable;
        [FieldOffset(68)] public uint flags;
    }

    // The union at 16 read as DISPLAYCONFIG_SOURCE_MODE, which is all the
    // hub uses: a source's size and its position on the desktop.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public uint adapterLow;
        [FieldOffset(12)] public int adapterHigh;
        [FieldOffset(16)] public uint width;
        [FieldOffset(20)] public uint height;
        [FieldOffset(28)] public int x;
        [FieldOffset(32)] public int y;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
                                                ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(uint pathCount, [In] DISPLAYCONFIG_PATH_INFO[] paths,
                                              uint modeCount, [In] DISPLAYCONFIG_MODE_INFO[] modes, uint flags);
}
