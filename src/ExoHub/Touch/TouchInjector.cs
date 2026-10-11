// Touch from the display device, injected at the virtual display
// (TECHNICAL_DESIGN 2.9, PROJECT_SPEC FR-3, spike 3 FINDINGS).
//
// TOUCH body, big-endian: sent i64 (phone microseconds), picture width and
// height u16, count u8, then count entries of id u8, phase u8, x i32, y i32.
// x and y are picture pixels, which are the duplicated output's pixels, so
// the desktop point is the output's origin plus them. Signed throughout: the
// origin is negative when the VDD sits left of or above the primary.
//
// Two threads. The device thread parses each TOUCH, maps it to the desktop
// and appends it to its finger's queue. This class's own thread owns
// InitializeTouchInjection and every InjectTouchInput, and injects at most
// once per 8ms (FR-3.2): above about 125Hz updates silently vanish. Each
// injected frame lists every finger that is down, because leaving one out
// cancels the whole gesture (spike 3), and takes at most one DOWN or UP per
// finger, so a tap whose DOWN and UP arrive within one 8ms still becomes two
// frames. A run of MOVEs comes to its last position.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoHub.Display;

namespace ExoHub.Touch;

sealed class TouchInjector : IDisposable
{
    public const byte Down = 1, Move = 2, Up = 3, Cancel = 4;
    const int Fixed = 8 + 2 + 2 + 1, Entry = 1 + 1 + 4 + 4;
    const int MaxContacts = 10;
    const double PaceMs = 8;

    sealed class Contact
    {
        public int Slot;          // the contact id given to Windows, 0 to 9
        public bool Injected;     // its DOWN has gone to Windows and no UP since
        public int X, Y;          // the last point injected, desktop pixels
        public int LastX, LastY;  // the last point queued
        public readonly Queue<(byte Phase, int X, int Y, long RecvQpc)> Pending = new();
    }

    readonly Stopwatch clock;
    readonly object gate = new();
    readonly Dictionary<int, Contact> contacts = new();  // by the phone's finger id, under gate
    readonly Thread thread;
    bool disposed;  // under gate

    // The VDD's rectangle, from the capture thread at each Reopen, null
    // while the duplication is lost. Immutable, so a reader that copies the
    // reference once has one whole rectangle.
    volatile OutputInfo target;
    public OutputInfo Target { get => target; set => target = value; }

    // Device thread only: the receiving side of a gesture.
    readonly HashSet<int> receiving = new();
    readonly List<double> recvGapsMs = new();
    int recvMessages;
    long recvStart, recvLast;
    string lastDrop;

    // Injector thread only, except the totals read after Dispose.
    long lastInject;
    POINTER_TOUCH_INFO[] retry;
    int retries, gestureFrames, gestureCoalesced;
    readonly List<double> gestureIntervals = new();

    public int Messages, Gestures, Dropped, Refused, Unknown;
    public int Frames, Coalesced, InjectedGestures, Retried;
    public readonly Dictionary<int, int> Failures = new();
    public readonly List<double> IntervalMs = new();       // between injections within a gesture
    public readonly List<double> RecvToInjectMs = new();   // a TOUCH read to its event injected
    public string Broken { get; private set; }

    public TouchInjector(Stopwatch clock)
    {
        this.clock = clock;
        thread = new Thread(Run) { IsBackground = true, Name = "touch injector" };
        thread.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s touch {line}");

    // The device thread, for each TOUCH. False for a malformed body, which
    // the caller treats as a protocol error.
    public bool Post(ReadOnlySpan<byte> body)
    {
        if (body.Length < Fixed || body.Length != Fixed + body[12] * Entry)
            return false;
        int picW = BinaryPrimitives.ReadUInt16BigEndian(body[8..]);
        int picH = BinaryPrimitives.ReadUInt16BigEndian(body[10..]);
        int count = body[12];
        for (int i = 0; i < count; i++)
            if (body[Fixed + i * Entry + 1] is < Down or > Cancel)
                return false;
        long now = Stopwatch.GetTimestamp();
        Messages++;

        // One read of the rectangle for the whole message.
        OutputInfo o = target;
        string drop = Broken != null ? $"injector not running: {Broken}"
            : o == null ? "no duplication, so no rectangle"
            : picW != o.Width || picH != o.Height ? $"phone's picture {picW}x{picH} is not the output's {o.Width}x{o.Height}"
            : null;
        if (drop != null)
        {
            Dropped++;
            if (drop != lastDrop)
                Log($"DROPPED: {drop}; logged once until the reason changes");
            lastDrop = drop;
            // Fingers already down would never see their UP.
            CancelAll();
            return true;
        }
        lastDrop = null;

        if (receiving.Count == 0)
        {
            recvStart = now;
            recvMessages = 0;
            recvGapsMs.Clear();
        }
        else
        {
            recvGapsMs.Add(Stopwatch.GetElapsedTime(recvLast, now).TotalMilliseconds);
        }
        recvLast = now;
        recvMessages++;

        lock (gate)
        {
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = body.Slice(Fixed + i * Entry, Entry);
                int id = e[0];
                byte phase = e[1];
                int dx = o.Left + BinaryPrimitives.ReadInt32BigEndian(e[2..]);
                int dy = o.Top + BinaryPrimitives.ReadInt32BigEndian(e[6..]);
                if (phase == Down) receiving.Add(id);
                if (phase is Up or Cancel) receiving.Remove(id);

                if (!contacts.TryGetValue(id, out Contact c))
                {
                    if (phase != Down)
                    {
                        Unknown++;  // a finger refused, or cancelled by Windows
                        continue;
                    }
                    int slot = FreeSlot();
                    if (slot < 0)
                    {
                        Refused++;
                        Log($"REFUSED finger {id}: all {MaxContacts} contacts in use");
                        continue;
                    }
                    c = new Contact { Slot = slot };
                    contacts[id] = c;
                }
                c.Pending.Enqueue((phase, dx, dy, now));
                c.LastX = dx;
                c.LastY = dy;
            }
            Monitor.PulseAll(gate);
        }

        if (receiving.Count == 0)
        {
            double[] g = recvGapsMs.Order().ToArray();
            Log($"received gesture: {recvMessages} messages in {Stopwatch.GetElapsedTime(recvStart, now).TotalMilliseconds:F0}ms, " +
                $"gaps ms {(g.Length == 0 ? "none" : $"min {g[0]:F1} median {g[g.Length / 2]:F1} max {g[^1]:F1}")}");
            Gestures++;
        }
        return true;
    }

    // The display connection ended: every finger still down is cancelled,
    // so none is left down in Windows.
    public void ConnectionEnded()
    {
        receiving.Clear();
        CancelAll();
    }

    void CancelAll()
    {
        lock (gate)
        {
            foreach (Contact c in contacts.Values)
                if (c.Injected || c.Pending.Count > 0)
                    c.Pending.Enqueue((Cancel, c.LastX, c.LastY, Stopwatch.GetTimestamp()));
            Monitor.PulseAll(gate);
        }
    }

    // Under gate.
    int FreeSlot()
    {
        for (int s = 0; s < MaxContacts; s++)
            if (!contacts.Values.Any(c => c.Slot == s))
                return s;
        return -1;
    }

    void Run()
    {
        string layout = CheckLayout();
        if (layout != null)
        {
            Broken = layout;
            Log($"REFUSED to inject: {layout}");
            return;
        }
        if (!InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_DEFAULT))
        {
            Broken = $"InitializeTouchInjection failed, error {Marshal.GetLastWin32Error()}";
            Log($"REFUSED to inject: {Broken}");
            return;
        }
        Log($"injector ready: {MaxContacts} contacts, paced at {PaceMs}ms or slower");

        while (true)
        {
            POINTER_TOUCH_INFO[] frame;
            bool gestureOver;
            lock (gate)
            {
                while (!disposed && retry == null && !contacts.Values.Any(c => c.Pending.Count > 0))
                    Monitor.Wait(gate);
                if (disposed)
                    return;
                double wait = PaceMs - Stopwatch.GetElapsedTime(lastInject).TotalMilliseconds;
                if (lastInject != 0 && wait > 0)
                {
                    // Woken early by a new TOUCH, the loop works the wait out again.
                    Monitor.Wait(gate, TimeSpan.FromMilliseconds(wait));
                    continue;
                }
                frame = retry ?? Build();
                retry = null;
                gestureOver = contacts.Count == 0;
            }

            long t = Stopwatch.GetTimestamp();
            bool ok = InjectTouchInput((uint)frame.Length, frame);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (lastInject != 0 && gestureFrames > 0)
                gestureIntervals.Add(Stopwatch.GetElapsedTime(lastInject, t).TotalMilliseconds);
            lastInject = t;
            gestureFrames++;
            Frames++;

            if (!ok)
            {
                Failures[error] = Failures.GetValueOrDefault(error) + 1;
                // Backpressure, intermittent (spike 3): the same frame again
                // next tick, a few times.
                if (error == ERROR_NOT_READY && retries < 3)
                {
                    retries++;
                    Retried++;
                    lock (gate) retry = frame;
                    continue;
                }
                // Anything else, 87 above all, means Windows has lifted every
                // contact itself (spike 3). Start clean; the next DOWN works.
                Log($"InjectTouchInput FAILED, error {error}, {frame.Length} contacts; every contact dropped");
                lock (gate) contacts.Clear();
                gestureOver = true;
            }
            retries = 0;

            if (gestureOver)
                EndGesture();
        }
    }

    // Under gate. One frame: every finger that is down, each with at most
    // one edge, and moves collapsed to the newest.
    POINTER_TOUCH_INFO[] Build()
    {
        var frame = new List<POINTER_TOUCH_INFO>();
        var gone = new List<int>();
        long now = Stopwatch.GetTimestamp();
        foreach ((int id, Contact c) in contacts.OrderBy(kv => kv.Value.Slot))
        {
            uint flags;
            if (c.Pending.Count == 0)
            {
                if (!c.Injected)
                {
                    gone.Add(id);
                    continue;
                }
                flags = UpdateFlags;  // unchanged, but still listed
            }
            else
            {
                var head = c.Pending.Peek();
                if (!c.Injected && head.Phase != Down)
                {
                    // Events for a finger whose DOWN never went in: only a
                    // cancel of a DOWN still queued gets here.
                    c.Pending.Clear();
                    gone.Add(id);
                    continue;
                }
                if (head.Phase == Down && !c.Injected)
                {
                    c.Pending.Dequeue();
                    flags = DownFlags;
                    c.Injected = true;
                    Log($"inject DOWN slot {c.Slot} (finger {id}) at desktop ({head.X},{head.Y})");
                }
                else if (head.Phase == Up && (head.X != c.X || head.Y != c.Y))
                {
                    // An UP that moves the contact is refused with error 87,
                    // and Windows lifts the contact itself (measured
                    // 2026-10-12: input swipe ends its last MOVE 3px past the
                    // UP). So the move goes first, and the UP, still queued,
                    // follows at the same point a tick later.
                    flags = UpdateFlags;
                }
                else if (head.Phase is Up or Cancel)
                {
                    // A cancel's position means nothing: it stays where the
                    // contact last was, so it never moves it either.
                    if (head.Phase == Cancel)
                        head = (head.Phase, c.X, c.Y, head.RecvQpc);
                    c.Pending.Dequeue();
                    flags = head.Phase == Up ? POINTER_FLAG_UP : POINTER_FLAG_UP | POINTER_FLAG_CANCELED;
                    c.Injected = false;
                    if (c.Pending.Count == 0)
                        gone.Add(id);
                    Log($"inject {(head.Phase == Up ? "UP" : "CANCEL")} slot {c.Slot} (finger {id}) at desktop ({head.X},{head.Y})");
                }
                else
                {
                    // MOVEs, or a DOWN for a finger already down, taken as a
                    // move: collapse the run to its newest point.
                    int n = 0;
                    while (c.Pending.Count > 0 && (c.Pending.Peek().Phase == Move ||
                                                   (c.Pending.Peek().Phase == Down && c.Injected)))
                    {
                        head = c.Pending.Dequeue();
                        n++;
                    }
                    gestureCoalesced += n - 1;
                    Coalesced += n - 1;
                    flags = UpdateFlags;
                }
                c.X = head.X;
                c.Y = head.Y;
                RecvToInjectMs.Add(Stopwatch.GetElapsedTime(head.RecvQpc, now).TotalMilliseconds);
            }
            frame.Add(Info(c.Slot, flags, c.X, c.Y));
        }
        foreach (int id in gone)
            contacts.Remove(id);
        return frame.ToArray();
    }

    void EndGesture()
    {
        InjectedGestures++;
        IntervalMs.AddRange(gestureIntervals);
        double[] g = gestureIntervals.Order().ToArray();
        Log($"injected gesture: {gestureFrames} frames, {gestureCoalesced} moves coalesced, " +
            $"intervals ms {(g.Length == 0 ? "none" : $"min {g[0]:F2} median {g[g.Length / 2]:F2} max {g[^1]:F2}")}");
        gestureFrames = gestureCoalesced = 0;
        gestureIntervals.Clear();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Monitor.PulseAll(gate);
        }
        thread.Join(1000);
    }

    // The contact as spike 3 injected it, with zero pixel error: a small
    // contact area following the point, a fixed orientation and pressure.
    static POINTER_TOUCH_INFO Info(int slot, uint flags, int x, int y) => new()
    {
        pointerInfo = new POINTER_INFO
        {
            pointerType = PT_TOUCH,
            pointerId = (uint)slot,
            pointerFlags = flags,
            ptPixelLocation = new Win32.POINT { X = x, Y = y },
        },
        touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE,
        rcContact = new Win32.RECT { Left = x - 2, Top = y - 2, Right = x + 2, Bottom = y + 2 },
        orientation = 90,
        pressure = 512,
    };

    // The x64 layout, worked by hand: two IntPtr fields put ptPixelLocation
    // at 32, PerformanceCount lands at 80, and POINTER_INFO pads to 96. A
    // wrong width does not fail; it shifts every later field and gives
    // plausible wrong coordinates. So it is checked, not assumed.
    static string CheckLayout()
    {
        var checks = new (string What, long Got, long Want)[]
        {
            ("sizeof POINTER_INFO", Marshal.SizeOf<POINTER_INFO>(), 96),
            ("sizeof POINTER_TOUCH_INFO", Marshal.SizeOf<POINTER_TOUCH_INFO>(), 144),
            ("offset ptPixelLocation", Marshal.OffsetOf<POINTER_INFO>(nameof(POINTER_INFO.ptPixelLocation)), 32),
            ("offset PerformanceCount", Marshal.OffsetOf<POINTER_INFO>(nameof(POINTER_INFO.PerformanceCount)), 80),
            ("offset touchFlags", Marshal.OffsetOf<POINTER_TOUCH_INFO>(nameof(POINTER_TOUCH_INFO.touchFlags)), 96),
        };
        foreach (var c in checks)
            if (c.Got != c.Want)
                return $"{c.What} is {c.Got}, expected {c.Want}";
        return null;
    }

    const uint PT_TOUCH = 2;
    const uint TOUCH_FEEDBACK_DEFAULT = 1;  // Windows draws its touch visuals, so a landed touch shows on the tablet
    const uint POINTER_FLAG_INRANGE = 0x2, POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_CANCELED = 0x8000;
    const uint POINTER_FLAG_DOWN = 0x10000, POINTER_FLAG_UPDATE = 0x20000, POINTER_FLAG_UP = 0x40000;
    const uint DownFlags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
    const uint UpdateFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
    const uint TOUCH_MASK_CONTACTAREA = 0x1, TOUCH_MASK_ORIENTATION = 0x2, TOUCH_MASK_PRESSURE = 0x4;
    const int ERROR_NOT_READY = 21;

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public Win32.POINT ptPixelLocation;
        public Win32.POINT ptHimetricLocation;
        public Win32.POINT ptPixelLocationRaw;
        public Win32.POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public Win32.RECT rcContact;
        public Win32.RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    // Never mixed with the synthetic-pointer pair (FR-3.1).
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InitializeTouchInjection(uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);
}
