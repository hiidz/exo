# PROJECT_SPEC SC-10, the two disturbances a script can start. Run during a
# hub run (sc11run.sh); the hub log has the loss and the recovery, this log
# has when each disturbance began.
#
#   sc10events.ps1 -Kind res -Count 10    the EXO monitor to 30Hz and back
#   sc10events.ps1 -Kind uac -Count 10    a UAC prompt; answer No
#
# The EXO monitor offers only its panel's size, at 60 and 30, so the mode
# change is the refresh rate. It is not saved (flags 0), and the monitor is
# put back to 60 even if the script is stopped part way. Only the EXO
# monitor changes, never the primary display. If a screen goes black
# anyway: Win+P, arrow, Enter for "PC screen only".
param([ValidateSet('res', 'uac')][string]$Kind, [int]$Count = 10, [int]$Hold = 10)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Exo {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplayDevices(string dev, int i, ref DISPLAY_DEVICE dd, int flags);

    // The GDI name of the display whose monitor is EXO0001, or null.
    public static string Find() {
        for (int i = 0; ; i++) {
            var a = new DISPLAY_DEVICE(); a.cb = Marshal.SizeOf(a);
            if (!EnumDisplayDevices(null, i, ref a, 0)) return null;
            var m = new DISPLAY_DEVICE(); m.cb = a.cb;
            if (EnumDisplayDevices(a.DeviceName, 0, ref m, 1) && m.DeviceID.Contains("EXO0001")) return a.DeviceName;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

    // Starts from the current mode so position and size stay as they are.
    // Returns DISP_CHANGE_SUCCESSFUL (0) or the failure code.
    public static int Set(string dev, int hz) {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(dev, -1, ref dm)) return -100;   // ENUM_CURRENT_SETTINGS
        dm.dmDisplayFrequency = hz;
        dm.dmFields = 0x400000;                                    // DM_DISPLAYFREQUENCY
        return ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }
}
'@

function Say($s) { Write-Output "$(Get-Date -Format 'HH:mm:ss.fff') $s" }

$Device = [Exo]::Find()
if ($Kind -eq 'res' -and -not $Device) { Say 'no EXO0001 monitor on the desktop'; exit 1 }

try {
    for ($i = 1; $i -le $Count; $i++) {
        if ($Kind -eq 'res') {
            Say "#$i $Device to 30Hz: $([Exo]::Set($Device, 30))"
            Start-Sleep $Hold
            Say "#$i $Device to 60Hz: $([Exo]::Set($Device, 60))"
        } else {
            Say "#$i UAC prompt"
            try { Start-Process cmd -ArgumentList '/c', 'exit' -Verb RunAs -Wait; Say "#$i answered Yes" }
            catch { Say "#$i answered No" }
        }
        Start-Sleep $Hold
    }
}
finally {
    if ($Kind -eq 'res') { Say "restore 60Hz: $([Exo]::Set($Device, 60))" }
}
