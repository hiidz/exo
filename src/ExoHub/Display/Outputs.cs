using Vortice.DXGI;

namespace ExoHub.Display;

// One DXGI output as seen in one enumeration. Plain data, no COM objects
// held, so a list of these needs no disposing.
//
// GdiName (\\.\DISPLAY12) is only valid inside this enumeration: it changed
// to DISPLAY18 across a driver restart in spike 2. MonitorId is the EDID
// manufacturer and product code (ACR0721), which is what we match on.
record OutputInfo(uint AdapterIndex, uint OutputIndex, string AdapterName,
                  string GdiName, string MonitorId, int Left, int Top, int Width, int Height)
{
    public override string ToString() =>
        $"adapter {AdapterIndex} output {OutputIndex}  monitor={MonitorId}  {GdiName}  " +
        $"{Width}x{Height} at ({Left},{Top})  on {AdapterName}";
}

static class Outputs
{
    // Walk every adapter and every output on it. The adapter is never chosen
    // directly: two adapters on this box report the identical description,
    // and only one has outputs. The output is what we look for, and the
    // adapter is whichever one owned it.
    public static List<OutputInfo> List(IDXGIFactory1 factory)
    {
        var found = new List<OutputInfo>();

        // EnumAdapters1 and EnumOutputs return a Result rather than throwing.
        // Failure (DXGI_ERROR_NOT_FOUND) is the normal end of the list.
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            // `using` calls Dispose at the end of the block, and Dispose on a
            // Vortice wrapper is COM Release. Without it the reference leaks.
            using (adapter)
            {
                string adapterName = adapter.Description1.Description.Trim();
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        var r = d.DesktopCoordinates;
                        found.Add(new OutputInfo(a, o, adapterName, d.DeviceName, MonitorId(d.DeviceName),
                                                 r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                    }
                }
            }
        }
        return found;
    }

    // GDI name -> monitor device interface path -> the EDID segment of it.
    // Returns "?" when Windows has no monitor attached to that name.
    static string MonitorId(string gdiName)
    {
        var dd = new Win32.DISPLAY_DEVICEW();
        dd.cb = System.Runtime.InteropServices.Marshal.SizeOf<Win32.DISPLAY_DEVICEW>();
        if (!Win32.EnumDisplayDevicesW(gdiName, 0, ref dd, Win32.EDD_GET_DEVICE_INTERFACE_NAME))
            return "?";

        // \\?\DISPLAY#ACR0721#7&15a5ef1&0&UID260#{guid} -> ACR0721
        string[] parts = dd.DeviceID.Split('#');
        return parts.Length > 1 ? parts[1] : dd.DeviceID;
    }
}
