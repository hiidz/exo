# PROJECT_SPEC SC-10, the two disturbances a script can start. Run during a
# hub run (sc11run.sh); the hub log has the loss and the recovery, this log
# has when each disturbance began.
#
#   sc10events.ps1 -Kind res -Count 10    VDD to 1920x1080 and back
#   sc10events.ps1 -Kind uac -Count 10    a UAC prompt; answer No
#
# The resolution change is not saved (flags 0), and the VDD is put back to
# 2944x1840 even if the script is stopped part way. Only the VDD changes,
# never the primary display. If a screen goes black anyway: Win+P, arrow,
# Enter for "PC screen only".
param([ValidateSet('res', 'uac')][string]$Kind, [int]$Count = 10,
      [int]$Hold = 10, [string]$Device = '\\.\DISPLAY8')

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Vdd {
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

    // Starts from the current mode so position and refresh stay as they are.
    // Returns DISP_CHANGE_SUCCESSFUL (0) or the failure code.
    public static int Set(string dev, int w, int h) {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(dev, -1, ref dm)) return -100;   // ENUM_CURRENT_SETTINGS
        dm.dmPelsWidth = w; dm.dmPelsHeight = h;
        dm.dmFields = 0x80000 | 0x100000;                          // DM_PELSWIDTH | DM_PELSHEIGHT
        return ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }
}
'@

function Say($s) { Write-Output "$(Get-Date -Format 'HH:mm:ss.fff') $s" }

try {
    for ($i = 1; $i -le $Count; $i++) {
        if ($Kind -eq 'res') {
            Say "#$i to 1920x1080: $([Vdd]::Set($Device, 1920, 1080))"
            Start-Sleep $Hold
            Say "#$i to 2944x1840: $([Vdd]::Set($Device, 2944, 1840))"
        } else {
            Say "#$i UAC prompt"
            try { Start-Process cmd -ArgumentList '/c', 'exit' -Verb RunAs -Wait; Say "#$i answered Yes" }
            catch { Say "#$i answered No" }
        }
        Start-Sleep $Hold
    }
}
finally {
    if ($Kind -eq 'res') { Say "restore 2944x1840: $([Vdd]::Set($Device, 2944, 1840))" }
}
