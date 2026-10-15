// Sends encoder output down the display connection (TECHNICAL_DESIGN 2.8),
// bounded per DECISIONS 5.7.
//
// The encoder hands frames over on a Media Foundation thread under its own
// lock, so Put only copies into a queue. This class's thread does the
// blocking socket writes. The encoder asks Busy before each input and gets
// none while a frame is still waiting here, or while MaxInFlight frames are
// sent and not yet acked by the phone, so a slow phone or link holds back
// the encoder and the drop lands on raw frames, where its Offer already
// keeps only the newest. Encoded frames are never dropped singly.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using ExoHub.Display;

namespace ExoHub.Link;

sealed class DisplaySender : IDisposable
{
    public const byte FlagKeyframe = 1, FlagConfig = 2;
    const int FrameHeader = Wire.HeaderSize + 4 + 3 * 8;  // seq, present, encoded, sent
    // Frames sent and not yet acked, 6 as SuperDisplay's fixed window
    // (SUPERDISPLAY-RE.md, FrameDone). A frame counts from its send until
    // the phone has decoded it, about 30ms on the TB375FC at 2944x1840@60,
    // so bursts filled a cap of 3 there: 2026-10-15, 60 s over USB, 3 sent
    // 50/s and was at the cap 1135 times (t-60-strip), 6 sent 56/s, never
    // at the cap, and present to output fell from 40.1 to 35.7ms median
    // (t-60-cap6). On the S21 at 2400x1080@60 the cap barely mattered
    // (2026-10-14, 20 s: no cap 548 frames, 3 545). The cost lands on a
    // decoder that cannot keep up: up to 6 frames queue behind it.
    const int MaxInFlight = 6;
    // At the cap with no ack for this long, the frames in flight are
    // written off, so a decoder that holds frames or a lost ack cannot
    // freeze the display. Expected never to fire.
    const int StallMs = 1000;

    sealed class Frame
    {
        public byte[] Data = new byte[256 * 1024];
        public int Length;
        public uint Seq;
        public byte Flags;
        public long PresentQpc, EncodedQpc;
    }

    readonly Stopwatch clock;
    readonly object gate = new();
    readonly Queue<Frame> waiting = new();  // under gate
    readonly Queue<(long t1, long t2)> pongs = new();  // under gate
    readonly Stack<Frame> free = new();     // under gate
    // Sent and not yet acked, oldest first, with when each was taken to send.
    readonly Queue<(uint seq, long qpc, int bytes)> inFlight = new();  // under gate
    readonly Timer stallCheck;
    readonly Thread thread;
    NetworkStream stream;  // under gate; null with no device
    string serial;
    bool keyframeWanted, waitForKeyframe, disposed;
    // The phone's display is out of view (PAUSE): only PONGs go, and the
    // capture loop encodes nothing, until RESUME. Under gate.
    bool paused;
    // The next frame written is the first of its connection. For the replug
    // timings in DECISIONS 5.8.
    bool firstOfConnection;
    Action kick;  // under gate

    // The cursor (DECISIONS 11.1): state, not a queue. Each slot holds the
    // latest value and whether this connection still needs it, so a slow
    // phone gets the newest position, never a backlog. Kept across
    // connections and replayed on Attach, since a shape changes rarely and a
    // phone that connects later would otherwise never get one. All under gate.
    CursorImage shape;
    bool shapePending;
    bool cursorKnown, cursorVisible, posPending;
    int cursorX, cursorY;
    long cursorMouseQpc;
    public int ShapesSent, PositionsGiven, PositionsSent, PositionsReplaced;

    uint seq;
    int spsProfile = -1, spsLevel = -1;
    // Totals for the whole run, read at the end.
    public int Sent, DiscardedNoDevice, DiscardedBeforeKeyframe, DiscardedOtherRate, MaxWaiting, Connections, Pauses;
    public long SentBytes;
    public readonly List<double> WaitMs = new();   // encoded to sent
    public readonly List<double> WriteMs = new();  // how long Write blocked
    public readonly List<double> AckMs = new();    // taken to send, to acked
    public int Acks, Acked, AtCap, StallReleases;  // AtCap: Busy said yes for the cap alone

    public DisplaySender(Stopwatch clock)
    {
        this.clock = clock;
        thread = new Thread(WriteLoop) { IsBackground = true, Name = "display sender" };
        thread.Start();
        stallCheck = new Timer(_ => CheckStall(), null, 250, 250);
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s send {line}");

    // The rate the attached phone asked for, which the capture loop builds
    // the encoder at; 0 before the first connection. An encoder at another
    // rate gets no keyframe request and none of its frames are sent, so a
    // phone that changed rate sees parameter sets from the rebuilt encoder
    // only.
    public uint Fps { get { lock (gate) return fps; } }
    uint fps;  // under gate

    // CBR for the attached phone, which the capture loop builds the encoder
    // at, matched as the rate is. NFR-3: 40 Mbps at 30fps, not to be tuned
    // down, and at 60 the same bits a frame. Over Wi-Fi it starts at 40 Mbps,
    // which got through here (PLAN.md, Wi-Fi as built in step 4), and moves.
    public const uint BitrateAt30 = 40_000_000, WifiStart = 40_000_000;
    // --bitrate, for measurements: per 30fps, whatever the transport.
    public uint BitrateOverride;
    public uint Bitrate { get { lock (gate) return bitrate; } }
    uint bitrate;  // under gate

    public uint BitrateFor(uint rate, bool wifi) =>
        BitrateOverride != 0 ? BitrateOverride * rate / 30 : Math.Min(BitrateAt30 * rate / 30, wifi ? WifiStart : uint.MaxValue);

    // Over Wi-Fi the CBR target follows the network (PLAN.md, Transport
    // and Wi-Fi): the encoder is built at BitrateFor, which is where the
    // target starts, and takes each new target on the running encoder
    // (TargetFor). Bitrate stays the encoder's identity. A queue is the
    // median ack time of a 250ms tick more than two frame intervals over
    // the lowest tick median of the last 5s; one ack is too noisy, 12 to
    // 92ms on an idle USB link (s5-ratetest-r1). Or the 6-frame cap hit in
    // the tick, which a link slow from the start shows when its floor is
    // already high. On a queue the target drops to three quarters of what
    // was acked in the last second, then waits a second for the queue to
    // drain; after 2s with none it rises 8% a second, up to USB's rate.
    public const uint WifiFloor = 10_000_000;
    const double RiseFactor = 1.08, CutFactor = 0.75;
    bool adaptive;    // under gate, as all below
    uint target, ceiling;
    long holdUntilQpc, calmSinceQpc, riseAtQpc;
    readonly List<double> tickAcks = new();
    readonly Queue<(long qpc, double median)> tickMedians = new();
    readonly Queue<(long qpc, int bytes)> ackedBytes = new();
    bool tickAtCap;
    public int TargetCuts, TargetRises;

    // The target the encoder is to run at, or 0 for an encoder not built
    // for this connection.
    public uint TargetFor(uint encoderFps, uint encoderBitrate)
    {
        lock (gate)
            return encoderFps == fps && encoderBitrate == bitrate ? target : 0;
    }

    // The display connection, once its handshake is done. Discards until a
    // keyframe carrying SPS and PPS, and asks the encoder for one (5.3).
    public void Attach(NetworkStream net, string who, uint rate, bool wifi)
    {
        lock (gate)
        {
            stream = net;
            serial = who;
            fps = rate;
            bitrate = target = BitrateFor(rate, wifi);
            adaptive = wifi && BitrateOverride == 0;
            ceiling = BitrateAt30 * rate / 30;
            ResetSignal();
            keyframeWanted = waitForKeyframe = true;
            paused = false;
            Connections++;
            ReturnAll();
            shapePending = shape != null;
            posPending = cursorKnown;
        }
        Log($"{who} attached, waiting for a forced IDR");
    }

    // From the capture loop, whenever the shape or the position changed.
    // Never blocks on the socket: the write thread sends them, ahead of any
    // frame, and they never count towards Busy.
    public void CursorShape(CursorImage s)
    {
        lock (gate)
        {
            shape = s;
            shapePending = true;
            Monitor.PulseAll(gate);
        }
    }

    public void CursorPos(bool visible, int x, int y, long mouseQpc)
    {
        lock (gate)
        {
            PositionsGiven++;
            if (posPending && stream != null)
                PositionsReplaced++;
            cursorKnown = true;
            cursorVisible = visible;
            cursorX = x;
            cursorY = y;
            cursorMouseQpc = mouseQpc;
            posPending = true;
            Monitor.PulseAll(gate);
        }
    }

    public void Detach(NetworkStream net)
    {
        lock (gate)
        {
            if (stream != net)
                return;  // already replaced
            stream = null;
            ReturnAll();
            Monitor.PulseAll(gate);
        }
    }

    // The listener's reader calls these for PAUSE and RESUME (protocol 9):
    // the phone left its display and came back unchanged. Paused, frames
    // in flight are written off, since the phone acks what still arrives
    // but the stall check must not count them. Resumed, as a new
    // connection: a forced IDR first, and the cursor replayed.
    public void Pause(NetworkStream net)
    {
        lock (gate)
        {
            if (stream != net || paused)
                return;
            paused = true;
            Pauses++;
            ReturnAll();
        }
        Log($"{serial} paused, its display out of view");
    }

    public void Resume(NetworkStream net)
    {
        lock (gate)
        {
            if (stream != net || !paused)
                return;
            paused = false;
            ResetSignal();
            keyframeWanted = waitForKeyframe = true;
            shapePending = shape != null;
            posPending = cursorKnown;
            Monitor.PulseAll(gate);
        }
        Log($"{serial} resumed, waiting for a forced IDR");
    }

    // The capture loop reads these to decide on a resend (DECISIONS 5.4),
    // and encodes nothing without a device.
    public bool HasDevice { get { lock (gate) return stream != null && !paused; } }
    public bool KeyframeWanted { get { lock (gate) return keyframeWanted; } }

    // The encoder's TrySubmit calls these under its own lock.
    public bool Busy
    {
        get
        {
            lock (gate)
            {
                if (stream == null || paused)
                    return false;
                if (waiting.Count > 0)
                    return true;
                if (inFlight.Count < MaxInFlight)
                    return false;
                AtCap++;
                tickAtCap = true;
                return true;
            }
        }
    }

    // The listener's reader calls this for each ACK on the display
    // connection: every frame up to seq is done. One from a connection since
    // replaced, or for frames already written off, changes nothing.
    public void Ack(NetworkStream net, uint seq)
    {
        Action k = null;
        lock (gate)
        {
            if (stream != net)
                return;
            Acks++;
            long now = Stopwatch.GetTimestamp();
            while (inFlight.Count > 0 && (int)(seq - inFlight.Peek().seq) >= 0)
            {
                var done = inFlight.Dequeue();
                double ms = Ms(now - done.qpc);
                AckMs.Add(ms);
                tickAcks.Add(ms);
                ackedBytes.Enqueue((now, done.bytes));
                Acked++;
                k = kick;
            }
        }
        // Outside the lock: Kick takes the encoder's lock, and the encoder
        // takes this one inside its own.
        k?.Invoke();
    }

    // On a timer thread. At the cap with the oldest frame unacked for
    // StallMs, writes them all off and lets the encoder go on.
    void CheckStall()
    {
        Action k = null;
        string why = null;
        string change = null;
        lock (gate)
            change = Adapt();
        if (change != null)
            Log(change);
        lock (gate)
        {
            if (stream == null || paused || inFlight.Count < MaxInFlight)
                return;
            double ms = Ms(Stopwatch.GetTimestamp() - inFlight.Peek().qpc);
            if (ms < StallMs)
                return;
            why = $"{serial} NO ACK for {ms:F0}ms with {inFlight.Count} frames in flight, seq {inFlight.Peek().seq} " +
                  "the oldest; writing them off";
            inFlight.Clear();
            StallReleases++;
            k = kick;
        }
        Log(why);
        k?.Invoke();
    }

    // Under gate. A new connection or a resume starts the signal again,
    // since the old acks say nothing about the link now. No cut for the
    // first 3s: the first keyframe and a cold decoder ack slowly, and read
    // as a queue cut the S21 to the floor 1.5s in, 30s from 80 again
    // (s5-s21-wifi60). A link slow from the start hits the cap after it.
    void ResetSignal()
    {
        tickAcks.Clear();
        tickMedians.Clear();
        ackedBytes.Clear();
        holdUntilQpc = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
        tickAtCap = false;
        calmSinceQpc = riseAtQpc = Stopwatch.GetTimestamp();
    }

    // Under gate, each 250ms tick: the target over Wi-Fi. Returns a line to
    // log when it changed.
    string Adapt()
    {
        if (!adaptive || stream == null || paused || tickAcks.Count == 0)
            return null;
        long now = Stopwatch.GetTimestamp();
        tickAcks.Sort();
        double median = tickAcks[tickAcks.Count / 2];
        tickAcks.Clear();
        tickMedians.Enqueue((now, median));
        while (now - tickMedians.Peek().qpc > 5 * Stopwatch.Frequency)
            tickMedians.Dequeue();
        while (now - ackedBytes.Peek().qpc > Stopwatch.Frequency)
            ackedBytes.Dequeue();
        double floorMs = tickMedians.Min(m => m.median);
        double frameMs = 1000.0 / fps;
        uint was = target;
        bool atCap = tickAtCap;
        tickAtCap = false;
        if (median > floorMs + 2 * frameMs || atCap)
        {
            calmSinceQpc = now;
            if (now < holdUntilQpc)
                return null;
            long acked = ackedBytes.Sum(a => (long)a.bytes) * 8;
            target = (uint)Math.Clamp(acked * CutFactor, WifiFloor, was);
            holdUntilQpc = now + Stopwatch.Frequency;
            if (target == was)
                return null;
            TargetCuts++;
            return $"{serial} Wi-Fi target {was / 1e6:F1} -> {target / 1e6:F1} Mbps: ack median {median:F0}ms{(atCap ? ", at the cap" : "")}, " +
                   $"floor {floorMs:F0}ms, acked {acked / 1e6:F1} Mbps in the last second";
        }
        if (target >= ceiling || now - calmSinceQpc < 2 * Stopwatch.Frequency || now < riseAtQpc)
            return null;
        riseAtQpc = now + Stopwatch.Frequency;
        target = (uint)Math.Min(target * RiseFactor, ceiling);
        TargetRises++;
        return $"{serial} Wi-Fi target {was / 1e6:F1} -> {target / 1e6:F1} Mbps, ack median {median:F0}ms";
    }

    public bool TakeKeyframeRequest(uint encoderFps, uint encoderBitrate)
    {
        lock (gate)
        {
            if (encoderFps != fps || encoderBitrate != bitrate)
                return false;
            bool r = keyframeWanted;
            keyframeWanted = false;
            return r;
        }
    }
    // How the sender gets the encoder going again once a frame is taken.
    public void SetKick(Action action) { lock (gate) kick = action; }

    // On a Media Foundation thread, under the encoder's lock. Never blocks.
    public void Put(byte[] data, int length, bool keyframe, long presentQpc, long encodedQpc, uint encoderFps,
                    uint encoderBitrate)
    {
        byte flags = (byte)((keyframe ? FlagKeyframe : 0) |
                            (HasParameterSets(data, length, out int profile, out int level) ? FlagConfig : 0));
        lock (gate)
        {
            // What the encoder wrote, which its media type leaves unset.
            if (level >= 0 && (profile != spsProfile || level != spsLevel))
            {
                spsProfile = profile;
                spsLevel = level;
                Log($"SPS profile_idc {profile} level_idc {level}");
            }
            uint n = seq++;
            if (stream == null || paused)
            {
                DiscardedNoDevice++;
                return;
            }
            if (encoderFps != fps || encoderBitrate != bitrate)
            {
                DiscardedOtherRate++;
                return;
            }
            if (waitForKeyframe)
            {
                if (flags != (FlagKeyframe | FlagConfig))
                {
                    DiscardedBeforeKeyframe++;
                    return;
                }
                waitForKeyframe = false;
                firstOfConnection = true;
            }
            Frame f = free.Count > 0 ? free.Pop() : new Frame();
            if (f.Data.Length < length)
                f.Data = new byte[length * 2];
            Buffer.BlockCopy(data, 0, f.Data, 0, length);
            f.Length = length;
            f.Seq = n;
            f.Flags = flags;
            f.PresentQpc = presentQpc;
            f.EncodedQpc = encodedQpc;
            waiting.Enqueue(f);
            MaxWaiting = Math.Max(MaxWaiting, waiting.Count);
            Monitor.PulseAll(gate);
        }
    }

    void WriteLoop()
    {
        byte[] head = new byte[FrameHeader];
        while (true)
        {
            Frame f = null;
            NetworkStream net;
            Action k = null;
            bool first = false;
            bool isPong = false, isPos = false;
            CursorImage s = null;
            (long t1, long t2) pong = default;
            (bool visible, int x, int y, long mouse) pos = default;
            lock (gate)
            {
                while (!disposed && (stream == null ||
                                     (waiting.Count == 0 && pongs.Count == 0 && (paused || (!shapePending && !posPending)))))
                    Monitor.Wait(gate);
                if (disposed)
                    return;
                net = stream;
                // A PONG goes first: it is 32 bytes, and holding it behind a
                // frame would put that frame's write time into the clock
                // measurement. The cursor next, for the same reason: a
                // position held behind a 166KB frame arrives late.
                if (pongs.Count > 0)
                {
                    pong = pongs.Dequeue();
                    isPong = true;
                }
                else if (shapePending && !paused)
                {
                    s = shape;
                    shapePending = false;
                }
                else if (posPending && !paused)
                {
                    pos = (cursorVisible, cursorX, cursorY, cursorMouseQpc);
                    posPending = false;
                    isPos = true;
                }
                else
                {
                    f = waiting.Dequeue();
                    // Counted before the write, so an ack can never come
                    // for a frame not yet in the queue.
                    inFlight.Enqueue((f.Seq, Stopwatch.GetTimestamp(), f.Length));
                    k = kick;
                    first = firstOfConnection;
                    firstOfConnection = false;
                }
            }
            if (isPong)
            {
                WritePong(net, pong.t1, pong.t2);
                continue;
            }
            if (s != null)
            {
                WriteCursorShape(net, s);
                continue;
            }
            if (isPos)
            {
                WriteCursorPos(net, pos.visible, pos.x, pos.y, pos.mouse);
                continue;
            }
            // Outside this lock: Kick takes the encoder's lock, and the
            // encoder takes this one inside its own.
            k?.Invoke();

            long sentQpc = Stopwatch.GetTimestamp();
            head[0] = 1;  // FRAME
            head[1] = f.Flags;
            head[2] = head[3] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)(FrameHeader - Wire.HeaderSize + f.Length));
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), f.Seq);
            BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(12), Micros(f.PresentQpc));
            BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(20), Micros(f.EncodedQpc));
            BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(28), Micros(sentQpc));
            try
            {
                net.Write(head);
                net.Write(f.Data, 0, f.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The listener's reader sees the close and detaches.
                Log($"{serial} write failed: {ex.InnerException?.Message ?? ex.Message}");
                net.Close();
            }
            long doneQpc = Stopwatch.GetTimestamp();
            if (first)
                Log($"{serial} first frame sent, seq {f.Seq}, {f.Length} bytes");
            lock (gate)
            {
                Sent++;
                SentBytes += f.Length;
                WaitMs.Add(Ms(sentQpc - f.EncodedQpc));
                WriteMs.Add(Ms(doneQpc - sentQpc));
                free.Push(f);
            }
        }
    }

    // The listener's reader calls this for each PING: t1 is the phone's
    // time from the PING, t2 the hub's when it was read (DECISIONS 5.7).
    public void Pong(NetworkStream net, long t1, long t2)
    {
        lock (gate)
        {
            if (stream != net)
                return;
            pongs.Enqueue((t1, t2));
            Monitor.PulseAll(gate);
        }
    }

    void WritePong(NetworkStream net, long t1, long t2)
    {
        Span<byte> m = stackalloc byte[Wire.HeaderSize + 24];
        m.Clear();
        m[0] = 3;  // PONG
        BinaryPrimitives.WriteUInt32BigEndian(m[4..], 24);
        BinaryPrimitives.WriteInt64BigEndian(m[8..], t1);
        BinaryPrimitives.WriteInt64BigEndian(m[16..], t2);
        // t3, immediately before the write.
        BinaryPrimitives.WriteInt64BigEndian(m[24..], Micros(Stopwatch.GetTimestamp()));
        try
        {
            net.Write(m);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            net.Close();
        }
    }

    void WriteCursorShape(NetworkStream net, CursorImage s)
    {
        Span<byte> m = stackalloc byte[Wire.HeaderSize + 8];
        m.Clear();
        m[0] = Wire.TypeCursorShape;
        BinaryPrimitives.WriteUInt32BigEndian(m[4..], (uint)(8 + s.Bgra.Length));
        BinaryPrimitives.WriteUInt16BigEndian(m[8..], (ushort)s.Width);
        BinaryPrimitives.WriteUInt16BigEndian(m[10..], (ushort)s.Height);
        BinaryPrimitives.WriteUInt16BigEndian(m[12..], (ushort)s.HotX);
        BinaryPrimitives.WriteUInt16BigEndian(m[14..], (ushort)s.HotY);
        try
        {
            net.Write(m);
            net.Write(s.Bgra);
            lock (gate) ShapesSent++;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            net.Close();
        }
    }

    void WriteCursorPos(NetworkStream net, bool visible, int x, int y, long mouseQpc)
    {
        Span<byte> m = stackalloc byte[Wire.HeaderSize + 16];
        m.Clear();
        m[0] = Wire.TypeCursorPos;
        m[1] = visible ? Wire.FlagVisible : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(m[4..], 16);
        BinaryPrimitives.WriteInt32BigEndian(m[8..], x);
        BinaryPrimitives.WriteInt32BigEndian(m[12..], y);
        BinaryPrimitives.WriteInt64BigEndian(m[16..], Micros(mouseQpc));
        try
        {
            net.Write(m);
            lock (gate) PositionsSent++;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            net.Close();
        }
    }

    // Under gate.
    void ReturnAll()
    {
        while (waiting.Count > 0)
            free.Push(waiting.Dequeue());
        pongs.Clear();
        inFlight.Clear();
    }

    // Whether SPS (7) and PPS (8) come before the first slice (1 or 5).
    // Reads only NAL headers, which are at the front of the unit, and the
    // SPS's profile_idc and level_idc (level 5.2 is 52), which follow its
    // header; -1 without an SPS.
    static bool HasParameterSets(byte[] d, int length, out int profile, out int level)
    {
        bool sps = false, pps = false;
        profile = level = -1;
        for (int i = 0; i + 3 < length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1)
                continue;
            int type = d[i + 3] & 0x1F;
            if (type == 1 || type == 5)
                break;
            if (type == 7 && i + 6 < length)
            {
                profile = d[i + 4];
                level = d[i + 6];
            }
            sps |= type == 7;
            pps |= type == 8;
            i += 3;
        }
        return sps && pps;
    }

    // QPC ticks to microseconds without overflowing: ticks * 1e6 overflows
    // a long after about 10 days of uptime at 10MHz.
    public static long Micros(long qpc) =>
        qpc / Stopwatch.Frequency * 1_000_000 + qpc % Stopwatch.Frequency * 1_000_000 / Stopwatch.Frequency;

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        stallCheck.Dispose();
        lock (gate)
        {
            disposed = true;
            Monitor.PulseAll(gate);
        }
    }
}
