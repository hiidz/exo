using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ExoHub.Link;
using Microsoft.Win32.SafeHandles;
using static ExoHub.Display.ExoDisplayDriver;

namespace ExoHub.Display;

// The hub's side of plugging ExoDisplay's monitor (ExoDisplayDriver).
//
// A display phone's connection asks for a monitor with its panel's modes;
// the monitor stays for a grace period after the connection ends, so a
// replug or a short drop does not throw windows about. One thread makes
// every driver call: the plug, a ping each second for the driver's
// watchdog, the grace timer, and the topology check after a plug. If the
// hub dies, the watchdog unplugs the monitor within 3 s.
sealed class VirtualDisplay : IDisposable
{
    // EDID manufacturer EXO, product 0001 (src/ExoDisplay/edid.h). One at a
    // time, so it names the monitor exactly.
    public const string MonitorId = "EXO0001";

    const int PingMs = 1000;           // the watchdog unplugs after 3 s without an IOCTL
    const int GraceMs = 10_000;        // a replug or a short drop keeps the monitor
    const int SettleMs = 6000;         // Windows extends onto it by itself, seen within 4 s
    const int TopologyPollMs = 250;
    const int GiveUpMs = 15_000;
    const int MaxEnforcements = 2;

    readonly Stopwatch clock;
    readonly uint panelWidth, panelHeight;
    readonly Thread thread;
    readonly AutoResetEvent wake = new(false);
    readonly object gate = new();
    Phone wanted;     // under gate: the phone whose monitor should exist, or null
    long leftQpc;     // under gate: when wanted last became null
    bool disposed;    // under gate

    // The thread's own, but for openError, which the device listener reads.
    SafeFileHandle device;
    volatile string openError;  // the last reason the driver could not be opened, logged once
    Phone plugged;              // the phone whose monitor is plugged, or null
    Guid pluggedId;
    uint luidLow, target;
    int luidHigh;
    long pluggedQpc;
    bool settled, plugFailing;
    int enforcements;

    volatile bool ready;
    // A plugged monitor is on the desktop, extended. The capture loop
    // captures nothing until then.
    public bool Ready => ready;

    // Where the plugged monitor is, for Topology: the driver's adapter LUID
    // and the target ID. False when none is plugged.
    public bool TryGetTarget(out uint luidLow, out int luidHigh, out uint target)
    {
        lock (gate)
        {
            luidLow = targetLow;
            luidHigh = targetHigh;
            target = targetId;
            return targetKnown;
        }
    }
    uint targetLow, targetId;  // under gate, copies of the thread's own
    int targetHigh;
    bool targetKnown;

    void Publish(bool known)
    {
        lock (gate)
        {
            targetKnown = known;
            targetLow = luidLow;
            targetHigh = luidHigh;
            targetId = target;
        }
    }

    public VirtualDisplay(Stopwatch clock, uint panelWidth, uint panelHeight)
    {
        this.clock = clock;
        this.panelWidth = panelWidth;
        this.panelHeight = panelHeight;
        thread = new Thread(Run) { IsBackground = true, Name = "virtual display" };
        thread.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s vdisp {line}");

    // Why the driver cannot be opened, or null. The device listener refuses
    // the display role while it is set, so the phone says so.
    public string Unavailable => openError;

    // From the device listener, when a display connection is accepted and
    // when it ends.
    public void Connected(Phone phone)
    {
        lock (gate)
            wanted = phone;
        wake.Set();
    }

    public void Disconnected(Phone phone)
    {
        lock (gate)
        {
            if (wanted?.Id != phone.Id)
                return;
            wanted = null;
            leftQpc = Stopwatch.GetTimestamp();
        }
        wake.Set();
    }

    void Run()
    {
        while (true)
        {
            Phone want;
            long left;
            lock (gate)
            {
                if (disposed)
                    break;
                want = wanted;
                left = leftQpc;
            }
            Step(want, left);
            wake.WaitOne(plugged != null && !settled ? TopologyPollMs : PingMs);
        }
        if (plugged != null)
            Unplug("the hub is stopping");
        device?.Dispose();
    }

    void Step(Phone want, long left)
    {
        if (device == null && !Open())
            return;
        if (plugged != null && want != null && want.Id != plugged.Id)
            Unplug($"{want} took the display role");
        if (plugged != null && want == null && Stopwatch.GetElapsedTime(left).TotalMilliseconds >= GraceMs)
            Unplug($"{GraceMs / 1000} s after its connection ended");
        if (plugged == null && want != null)
            Plug(want);
        if (plugged == null)
            return;
        if (Ioctl(IoctlPing, null, null) is int e and not 0)
        {
            Lost($"ping failed, error {e}");
            return;
        }
        if (!settled)
            Settle();
    }

    bool Open()
    {
        device = ExoDisplayDriver.Open(out string why, out string path);
        if (why != null)
        {
            if (why != openError)
                Log($"ExoDisplay {why}");
            openError = why;
            return false;
        }
        openError = null;
        // Reopened each tick while a plug keeps failing, which says so once.
        if (!plugFailing)
            Log($"ExoDisplay opened, version {ExoDisplayDriver.Version}, {path}");
        return true;
    }

    // Plugs the phone's monitor: the panel at 60 and 30, 60 preferred. Its Id
    // comes from its identity, so Windows sees the same monitor each time and
    // keeps its position and scale. Retried each tick on failure, logged
    // once: an orphaned monitor from a hub that died is gone within 3 s.
    void Plug(Phone phone)
    {
        Guid id = new(MD5.HashData(Encoding.UTF8.GetBytes("exo.display:" + phone.Id)));
        var output = new byte[12];
        int e = Ioctl(IoctlAdd, AddParams(id, phone.Id), output);
        if (e != 0)
        {
            if (!plugFailing)
                Log($"{phone} plug FAILED, error {e}; retrying each second");
            plugFailing = true;
            CloseDevice();
            return;
        }
        plugFailing = false;
        luidLow = BinaryPrimitives.ReadUInt32LittleEndian(output);
        luidHigh = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(4));
        target = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8));
        Publish(true);
        plugged = phone;
        pluggedId = id;
        pluggedQpc = Stopwatch.GetTimestamp();
        settled = false;
        enforcements = 0;
        Log($"{phone} plugged {MonitorId} {panelWidth}x{panelHeight} at 60 and 30, " +
            $"target {target} on adapter {luidHigh:X8}:{luidLow:X8}");
    }

    void Unplug(string why)
    {
        int e = Ioctl(IoctlRemove, pluggedId.ToByteArray(), null);
        Log($"{plugged} unplugged {MonitorId}, {why}{(e != 0 ? $"; REMOVE failed, error {e}" : "")}");
        plugged = null;
        ready = false;
        Publish(false);
        if (e != 0)
            CloseDevice();
    }

    // The driver went away under the hub, a reinstall or a restart, and the
    // monitor with it. The next tick reopens it and plugs again.
    void Lost(string why)
    {
        Log($"{plugged} lost {MonitorId}: {why}");
        plugged = null;
        ready = false;
        Publish(false);
        CloseDevice();
    }

    void CloseDevice()
    {
        device?.Dispose();
        device = null;
    }

    // Waits for Windows to put the monitor on the desktop, and corrects the
    // layout if it is not extended (Topology). Ready once it is, or once the
    // corrections are spent and it is on the desktop all the same.
    void Settle()
    {
        Topology.State state;
        string what;
        try
        {
            state = Topology.Check(luidLow, luidHigh, target, out what);
        }
        catch (InvalidOperationException ex)
        {
            state = Topology.State.Missing;
            what = ex.Message;
        }
        double ms = Stopwatch.GetElapsedTime(pluggedQpc).TotalMilliseconds;

        bool correct = state == Topology.State.Bad ||
                       (state == Topology.State.Inactive && ms >= SettleMs);
        if (state == Topology.State.Fine || (state == Topology.State.Bad && enforcements >= MaxEnforcements))
        {
            settled = true;
            ready = true;
            Log($"{MonitorId} on the desktop {what}, {ms:F0}ms after the plug" +
                (state == Topology.State.Bad ? ", NOT corrected" : ""));
        }
        else if (correct && enforcements < MaxEnforcements)
        {
            enforcements++;
            string r;
            try
            {
                r = Topology.Enforce(luidLow, luidHigh, target).ToString();
            }
            catch (InvalidOperationException ex)
            {
                r = ex.Message;
            }
            Log($"{MonitorId} {what} after {ms:F0}ms: set extended, SetDisplayConfig {r}");
        }
        else if (ms >= GiveUpMs)
        {
            settled = true;
            Log($"{MonitorId} NOT on the desktop {GiveUpMs / 1000} s after the plug: {what}");
        }
    }

    // ExoAddMonitor, little-endian, laid out as in exo_display.h.
    byte[] AddParams(Guid id, string serial)
    {
        var b = new byte[AddSize];
        id.TryWriteBytes(b);
        // WidthMm and HeightMm stay 0, unknown, until the phone sends them.
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 2);
        (uint hz, int at)[] modes = [(60, 28), (30, 40)];
        foreach (var (hz, at) in modes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), panelWidth);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), panelHeight);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), hz);
        }
        int name = 28 + 12 * MaxModes;
        Encoding.ASCII.GetBytes("Exo", b.AsSpan(name));
        Encoding.ASCII.GetBytes(serial.Length > EdidString ? serial[..EdidString] : serial, b.AsSpan(name + 16));
        return b;
    }

    int Ioctl(uint code, byte[] input, byte[] output) => ExoDisplayDriver.Ioctl(device, code, input, output, out _);

    // Unplugs at once rather than leaving it to the watchdog.
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }
        wake.Set();
        thread.Join(3000);
    }
}
