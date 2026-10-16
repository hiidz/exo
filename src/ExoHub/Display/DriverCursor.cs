using System.Buffers.Binary;
using System.Diagnostics;
using ExoHub.Link;
using Microsoft.Win32.SafeHandles;
using Vortice.DXGI;
using static ExoHub.Display.ExoDisplayDriver;

namespace ExoHub.Display;

// The pointer, as ExoDisplay forwards it. Windows keeps it out of the
// images (a hardware cursor), and the phone draws it. This
// thread wakes on the driver's event, reads the pointer, and hands shape
// and position to the sender, which sends the newest of each.
sealed class DriverCursor : IDisposable
{
    const uint NoShape = 0xFFFFFFFF;
    // The most a wait lasts: how soon a restarted driver is found again.
    const int WaitMs = 500;

    readonly Stopwatch clock;
    readonly DisplaySender display;
    readonly Func<OutputInfo> output;  // where the monitor is, for the GetCursorPos check
    readonly Thread thread;
    volatile bool stopping;

    public readonly CursorTracker Tracker = new();
    public int Disagreements, ReadFailures;

    // A wait handle around an event opened by name.
    sealed class NamedEvent : WaitHandle
    {
        public NamedEvent(SafeWaitHandle h) => SafeWaitHandle = h;
    }

    public DriverCursor(Stopwatch clock, DisplaySender display, Func<OutputInfo> output)
    {
        this.clock = clock;
        this.display = display;
        this.output = output;
        thread = new Thread(Run) { IsBackground = true, Name = "cursor" };
        thread.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s {line}");

    void Run()
    {
        SafeFileHandle driver = null;
        NamedEvent wake = null;
        uint have = NoShape, lastSeq = 0;
        var input = new byte[4];
        var buffer = new byte[CursorInfoSize + CursorMax * CursorMax * 4];

        while (!stopping)
        {
            if (driver == null)
            {
                driver = ExoDisplayDriver.Open(out _, out _);
                if (driver == null)
                {
                    Thread.Sleep(WaitMs);
                    continue;
                }
                var h = Win32.OpenEventW(Win32.SYNCHRONIZE, false, CursorEvent);
                wake = h.IsInvalid ? null : new NamedEvent(h);
                have = NoShape;
                lastSeq = 0;
            }
            if (wake != null)
                wake.WaitOne(WaitMs);
            else
                Thread.Sleep(WaitMs);

            BinaryPrimitives.WriteUInt32LittleEndian(input, have);
            if (Ioctl(driver, IoctlGetCursor, input, buffer, out _) != 0)
            {
                ReadFailures++;
                driver.Dispose();
                driver = null;
                wake?.Dispose();
                wake = null;
                continue;
            }
            var c = CursorInfo.Read(buffer);
            if (c.Seq == lastSeq && c.ShapeBytes == 0)
                continue;
            lastSeq = c.Seq;

            // The shape first: its hotspot applies to the position with it.
            if (c.ShapeBytes > 0)
            {
                have = c.ShapeId;
                var info = new OutduplPointerShapeInfo
                {
                    // IddCx's alpha (2) and masked colour (1) are DXGI's
                    // colour (2) and masked colour (4).
                    Type = c.Type == 2 ? 2u : 4u,
                    Width = c.Width,
                    Height = c.Height,
                    Pitch = c.Pitch,
                    HotSpot = new((int)c.XHot, (int)c.YHot),
                };
                if (Tracker.TakeShape(info, buffer.AsSpan(CursorInfoSize, (int)c.ShapeBytes)))
                {
                    display.CursorShape(Tracker.Shape);
                    Log($"CURSOR SHAPE #{Tracker.ShapeChanges} {Tracker.Shape}, {c.ShapeBytes} bytes raw, id {c.ShapeId}");
                }
            }

            bool wasVisible = Tracker.Known && Tracker.Visible;
            var pos = new OutduplPointerPosition { Position = new(c.X, c.Y), Visible = c.Visible };
            if (!Tracker.TakePosition(pos))
                continue;
            display.CursorPos(Tracker.Visible, Tracker.X, Tracker.Y, c.Qpc);
            if (Tracker.Visible == wasVisible)
                continue;
            if (!Tracker.Visible)
            {
                Log("CURSOR LEFT");
                continue;
            }
            // GetCursorPos is the hotspot in virtual-desktop pixels. With the
            // mouse still, it minus the monitor's origin, turned as Windows
            // turns the desktop onto the monitor, must equal what the driver's
            // top-left plus the shape's hotspot gave: the driver's pointer is
            // already in the monitor's own pixels, its shapes turned too.
            Win32.GetCursorPos(out Win32.POINT cp);
            OutputInfo o = output();
            var (gx, gy) = o == null ? (cp.X, cp.Y) : o.ToPicture(cp.X - o.Left, cp.Y - o.Top);
            bool agree = gx == Tracker.X && gy == Tracker.Y;
            if (!agree)
                Disagreements++;
            Log($"CURSOR ENTERED at picture ({Tracker.X},{Tracker.Y}); GetCursorPos ({cp.X},{cp.Y}) " +
                $"is picture ({gx},{gy}), {(agree ? "agrees" : "DIFFERS")}" +
                (Tracker.Shape == null ? "; no shape yet" : ""));
        }
        wake?.Dispose();
        driver?.Dispose();
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join(WaitMs * 2);
    }
}
