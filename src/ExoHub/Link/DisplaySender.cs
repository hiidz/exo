// Sends encoder output down the display connection (TECHNICAL_DESIGN 2.8),
// bounded per DECISIONS 5.7.
//
// The encoder hands frames over on a Media Foundation thread under its own
// lock, so Put only copies into a queue. This class's thread does the
// blocking socket writes. The encoder asks Busy before each input and gets
// none while a frame is still waiting here, so a slow phone holds back the
// encoder and the drop lands on raw frames, where its Offer already keeps
// only the newest. Encoded frames are never dropped singly.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

namespace ExoHub.Link;

sealed class DisplaySender : IDisposable
{
    public const byte FlagKeyframe = 1, FlagConfig = 2;
    const int FrameHeader = Wire.HeaderSize + 4 + 3 * 8;  // seq, present, encoded, sent

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
    readonly Thread thread;
    NetworkStream stream;  // under gate; null with no device
    string serial;
    bool keyframeWanted, waitForKeyframe, disposed;
    // The next frame written is the first of its connection. For the replug
    // timings in DECISIONS 5.8.
    bool firstOfConnection;
    Action kick;  // under gate

    uint seq;
    // Totals for the whole run, read at the end.
    public int Sent, DiscardedNoDevice, DiscardedBeforeKeyframe, MaxWaiting, Connections;
    public long SentBytes;
    public readonly List<double> WaitMs = new();   // encoded to sent
    public readonly List<double> WriteMs = new();  // how long Write blocked

    public DisplaySender(Stopwatch clock)
    {
        this.clock = clock;
        thread = new Thread(WriteLoop) { IsBackground = true, Name = "display sender" };
        thread.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s send {line}");

    // The display connection, once its handshake is done. Discards until a
    // keyframe carrying SPS and PPS, and asks the encoder for one (5.3).
    public void Attach(NetworkStream net, string who)
    {
        lock (gate)
        {
            stream = net;
            serial = who;
            keyframeWanted = waitForKeyframe = true;
            Connections++;
            ReturnAll();
        }
        Log($"{who} attached, waiting for a forced IDR");
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

    // The encoder's TrySubmit calls these under its own lock.
    public bool Busy { get { lock (gate) return stream != null && waiting.Count > 0; } }
    public bool TakeKeyframeRequest()
    {
        lock (gate)
        {
            bool r = keyframeWanted;
            keyframeWanted = false;
            return r;
        }
    }
    // How the sender gets the encoder going again once a frame is taken.
    public void SetKick(Action action) { lock (gate) kick = action; }

    // On a Media Foundation thread, under the encoder's lock. Never blocks.
    public void Put(byte[] data, int length, bool keyframe, long presentQpc, long encodedQpc)
    {
        byte flags = (byte)((keyframe ? FlagKeyframe : 0) | (HasParameterSets(data, length) ? FlagConfig : 0));
        lock (gate)
        {
            uint n = seq++;
            if (stream == null)
            {
                DiscardedNoDevice++;
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
            (long t1, long t2) pong = default;
            lock (gate)
            {
                while (!disposed && (stream == null || (waiting.Count == 0 && pongs.Count == 0)))
                    Monitor.Wait(gate);
                if (disposed)
                    return;
                net = stream;
                // A PONG goes first: it is 32 bytes, and holding it behind a
                // frame would put that frame's write time into the clock
                // measurement.
                if (pongs.Count > 0)
                    pong = pongs.Dequeue();
                else
                {
                    f = waiting.Dequeue();
                    k = kick;
                    first = firstOfConnection;
                    firstOfConnection = false;
                }
            }
            if (f == null)
            {
                WritePong(net, pong.t1, pong.t2);
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

    // Under gate.
    void ReturnAll()
    {
        while (waiting.Count > 0)
            free.Push(waiting.Dequeue());
        pongs.Clear();
    }

    // Whether SPS (7) and PPS (8) come before the first slice (1 or 5).
    // Reads only NAL headers, which are at the front of the unit.
    static bool HasParameterSets(byte[] d, int length)
    {
        bool sps = false, pps = false;
        for (int i = 0; i + 3 < length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1)
                continue;
            int type = d[i + 3] & 0x1F;
            if (type == 1 || type == 5)
                break;
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
        lock (gate)
        {
            disposed = true;
            Monitor.PulseAll(gate);
        }
    }
}
