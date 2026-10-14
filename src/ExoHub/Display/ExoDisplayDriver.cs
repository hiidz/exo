using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExoHub.Display;

// The hub's mirror of src/ExoDisplay/exo_display.h: IOCTL codes, version
// and struct layouts, little-endian at the C offsets. Change both and bump
// the version.
//
// Each user (VirtualDisplay, DriverFrameSource, DriverCursor) opens its own
// handle, so one reopening after a driver restart never pulls a handle out
// from under another.
static class ExoDisplayDriver
{
    public const uint Version = 3;

    public const uint IoctlAdd = 0x222000, IoctlRemove = 0x222004, IoctlPing = 0x22200C,
                      IoctlVersion = 0x222010, IoctlGetFrame = 0x222014, IoctlGetCursor = 0x222018;

    public const int AddSize = 156, MaxModes = 8, EdidString = 13;
    public const int FrameInfoSize = 176, CursorInfoSize = 56, CursorMax = 256;
    public const string CursorEvent = @"Global\ExoDisplayCursor";
    static readonly Guid Interface = new("55599bd1-bd1f-4aec-b857-87bcd97f2f8f");

    // The driver's device interface, opened and its version checked. Null
    // with the reason when it cannot be used.
    public static SafeFileHandle Open(out string why, out string path)
    {
        why = null;
        Guid g = Interface;
        int cr = Win32.CM_Get_Device_Interface_List_SizeW(out uint length, ref g, null, 0);
        var list = new char[Math.Max(length, 1)];
        if (cr == 0)
            cr = Win32.CM_Get_Device_Interface_ListW(ref g, null, list, (uint)list.Length, 0);
        path = cr == 0 ? new string(list).Split('\0')[0] : "";
        if (cr != 0)
        {
            why = $"listing its interface failed, CR {cr}";
            return null;
        }
        if (path == "")
        {
            why = "not installed (src/ExoDisplay/install.ps1); the display role cannot work";
            return null;
        }

        var device = Win32.CreateFileW(path, Win32.GENERIC_READ_WRITE, Win32.FILE_SHARE_READ_WRITE, IntPtr.Zero,
                                       Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (device.IsInvalid)
        {
            why = $"opening {path} failed, error {Marshal.GetLastWin32Error()}";
            device.Dispose();
            return null;
        }
        var v = new byte[4];
        int e = Ioctl(device, IoctlVersion, null, v, out _);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(v);
        if (e != 0 || version != Version)
        {
            why = e != 0 ? $"its version query failed, error {e}"
                         : $"driver version {version}, the hub speaks {Version}: rebuild and reinstall both";
            device.Dispose();
            return null;
        }
        return device;
    }

    // 0 on success, else the Win32 error.
    public static int Ioctl(SafeFileHandle device, uint code, byte[] input, byte[] output, out int returned) =>
        Win32.DeviceIoControl(device, code, input, input?.Length ?? 0, output, output?.Length ?? 0, out returned, IntPtr.Zero)
            ? 0 : Marshal.GetLastWin32Error();

    // ExoFrameInfo, read from IOCTL_EXO_GET_FRAME's output.
    public readonly record struct FrameInfo(uint Generation, Vortice.Luid RenderAdapter, uint Width, uint Height,
                                            uint Format, uint FrameNumber, long PresentQpc, uint Copied, uint Waited,
                                            string Name)
    {
        public static FrameInfo Read(ReadOnlySpan<byte> b) => new(
            BinaryPrimitives.ReadUInt32LittleEndian(b),
            new Vortice.Luid(BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), BinaryPrimitives.ReadInt32LittleEndian(b[8..])),
            BinaryPrimitives.ReadUInt32LittleEndian(b[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[44..]),
            MemoryMarshal.Cast<byte, char>(b.Slice(48, 128)).ToString().Split('\0')[0]);
    }

    // ExoCursorInfo, read from IOCTL_EXO_GET_CURSOR's output. The shape's
    // pixels, when sent, follow it.
    public readonly record struct CursorInfo(uint Seq, bool Visible, int X, int Y, long Qpc, uint ShapeId, uint Type,
                                             uint Width, uint Height, uint Pitch, uint XHot, uint YHot, uint ShapeBytes)
    {
        public static CursorInfo Read(ReadOnlySpan<byte> b) => new(
            BinaryPrimitives.ReadUInt32LittleEndian(b),
            BinaryPrimitives.ReadInt32LittleEndian(b[4..]) != 0,
            BinaryPrimitives.ReadInt32LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[36..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[44..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[52..]));
    }
}
