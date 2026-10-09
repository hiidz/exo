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

    // Used only to print which DPI awareness the manifest actually gave us.
    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
}
