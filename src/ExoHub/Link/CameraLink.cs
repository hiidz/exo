// L7, the link from the hub to ExoCamera.dll (DECISIONS 5.9). Listens on a
// fixed loopback port, because the DLL reads no config and so has no way to
// learn a port Windows chose. Each client gets 5.7's handshake, then FRAMEs.
//
// A latest-frame slot (DECISIONS 5.1). One producer, the decoder or the
// test pattern, publishes each frame once, built as a whole L7 message.
// Every client thread sends the newest message it has not sent; a frame
// superseded before a client got to it is skipped, never queued. So one
// decode serves every client (FR-1.3), and a slow client holds back only
// itself. A published message is not written again until it is free, so
// client threads read it without the lock.
//
// Message buffers are pooled and reference counted by hand, as COM counts
// its objects. A new 1.4MB array per frame went on the large-object heap,
// which only a gen-2 collection reclaims: 12 to 14 of them every 5s at
// 30fps. The slot holds one reference to the newest frame and each client
// holds one while it writes; the frame that drops to 0 goes back on the
// free list, and the producer takes from that list before allocating. So
// the pool settles at about clients + 2 buffers. All of it under gate.
//
// Frames go out only while someone watches, which the virtual camera's
// SOURCE_START and SOURCE_STOP report (SetWatching). With nothing written
// for a second, a client gets IDLE instead: the hub is alive and has no
// picture for it, so the DLL shows its test card, and its no-message
// timeout only fires on a hub that has hung (DECISIONS 5.9).
//
// One thread accepts and one thread serves each client, as DeviceListener.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ExoHub.Link;

sealed class CameraLink : IDisposable
{
    const int HelloTimeoutMs = 2000;
    // A third of the DLL's 3s no-message timeout, so one late IDLE is no loss.
    const int IdleMs = 1000;
    // How often an idle client is checked for having gone. The DLL's Stop
    // shuts its socket down, but its reader's blocked recv does not wake for
    // that; it wakes when this side closes. Checked once a second, Stop
    // waited 778ms of its 1000 (2026-10-11).
    const int CloseCheckMs = 100;
    // seq u32, generated-at i64, width, height, stride u32 each.
    const int BodyPrefix = 4 + 8 + 3 * 4;

    readonly TcpListener listener;
    readonly Stopwatch clock;
    readonly object gate = new();
    readonly List<Socket> clients = new();  // under gate

    sealed class Frame(int length)
    {
        public readonly byte[] Data = new byte[length];  // a whole FRAME message
        public uint Seq;
        public int Refs;  // under gate
    }

    readonly Stack<Frame> free = new();  // under gate
    Frame latest;      // under gate: the newest, holding one reference
    uint seq;          // the producer's alone
    bool disposed;     // under gate
    int allocated;     // under gate: buffers ever made, for the log
    // Under gate. True until a virtual camera says otherwise, so a hub that
    // registered none sends frames to any client, as before.
    bool watching = true;

    public int Allocated { get { lock (gate) return allocated; } }

    // Random per hub start, sent in every WELCOME, as on the device link.
    public ulong SessionId { get; } = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));

    // Throws SocketException (AddressAlreadyInUse) when another process
    // holds the port, which with exclusive use includes an orphaned hub.
    public CameraLink(Stopwatch clock)
    {
        this.clock = clock;
        listener = new TcpListener(IPAddress.Loopback, Wire.CameraPort) { ExclusiveAddressUse = true };
        listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "camera accept" }.Start();
        Log($"listening on 127.0.0.1:{Wire.CameraPort}, protocol {Wire.CameraVersion}, session {SessionId:x16}");
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s camera {line}");

    // Called by the one producer. y holds height rows and uv height / 2
    // rows, each stride bytes apart, as the producer's buffer has them: the
    // geometry is the producer's, sent as it is, and the DLL checks it.
    // stampQpc is when the frame came into being, from Stopwatch.
    // flip turns the picture 180 degrees on the way in (DECISIONS 6.4).
    public void Publish(long stampQpc, int width, int height, int stride, ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, bool flip)
    {
        int ySize = stride * height, uvSize = stride * height / 2;
        int length = Wire.HeaderSize + BodyPrefix + ySize + uvSize;
        Frame f = null;
        lock (gate)
        {
            // A buffer of another size, from before a geometry change, is
            // dropped here for the collector rather than reused.
            while (f == null && free.TryPop(out Frame old))
                if (old.Data.Length == length)
                    f = old;
            if (f == null)
            {
                allocated++;
                if (allocated <= 8 || allocated % 100 == 0)
                    Log($"frame buffer {allocated} allocated, {length} bytes");
            }
        }
        f ??= new Frame(length);
        byte[] m = f.Data;
        m[0] = Wire.TypeFrame;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), (uint)(BodyPrefix + ySize + uvSize));
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(8), ++seq);
        BinaryPrimitives.WriteInt64BigEndian(m.AsSpan(12), DisplaySender.Micros(stampQpc));
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(20), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(24), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(28), (uint)stride);
        Span<byte> yTo = m.AsSpan(Wire.HeaderSize + BodyPrefix, ySize);
        Span<byte> uvTo = m.AsSpan(Wire.HeaderSize + BodyPrefix + ySize, uvSize);
        if (flip)
        {
            CopyTurned(y, yTo, height, width, stride, pairs: false);
            CopyTurned(uv, uvTo, height / 2, width, stride, pairs: true);
        }
        else
        {
            y[..ySize].CopyTo(yTo);
            uv[..uvSize].CopyTo(uvTo);
        }
        f.Seq = seq;
        lock (gate)
        {
            f.Refs = 1;  // the slot's
            if (latest != null)
                ReleaseLocked(latest);
            latest = f;
            // Monitor is C#'s condition variable: Wait releases the lock and
            // sleeps, PulseAll wakes every waiter to re-check.
            Monitor.PulseAll(gate);
        }
    }

    // One NV12 plane turned 180 degrees: the last row first, each row back to
    // front. A UV row is pairs, U then V, so it is reversed in 16-bit units,
    // which keeps each pair's order. Only width bytes of a row are picture;
    // the padding out to the stride stays at the end.
    static void CopyTurned(ReadOnlySpan<byte> from, Span<byte> to, int rows, int width, int stride, bool pairs)
    {
        for (int r = 0; r < rows; r++)
        {
            Span<byte> row = to.Slice(r * stride, width);
            from.Slice((rows - 1 - r) * stride, width).CopyTo(row);
            if (pairs)
                MemoryMarshal.Cast<byte, ushort>(row).Reverse();
            else
                row.Reverse();
        }
    }

    // The producer's stream ended. The newest frame is now stale, so it goes:
    // a client connecting after this waits for a new one rather than
    // showing a picture from a camera that has gone.
    public void Clear()
    {
        lock (gate)
        {
            if (latest == null)
                return;
            ReleaseLocked(latest);
            latest = null;
        }
        Log("slot cleared: the stream ended, so the last frame is not sent again");
    }

    // Whether anyone is streaming from the camera. Frames are sent only
    // while true; the slot keeps filling either way, so the newest frame
    // goes out the moment it turns true.
    public void SetWatching(bool on, string why)
    {
        lock (gate)
        {
            if (watching == on)
                return;
            watching = on;
            Monitor.PulseAll(gate);
        }
        Log(on ? $"frames resume: {why}" : $"frames held: {why}");
    }

    // Under gate. The last reference puts the buffer back for reuse.
    void ReleaseLocked(Frame f)
    {
        if (--f.Refs == 0)
            free.Push(f);
    }

    void AcceptLoop()
    {
        while (true)
        {
            Socket s;
            try
            {
                s = listener.AcceptSocket();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;  // Dispose stopped the listener
            }
            new Thread(() => Serve(s)) { IsBackground = true, Name = "camera client" }.Start();
        }
    }

    void Serve(Socket s)
    {
        string who = s.RemoteEndPoint?.ToString() ?? "?";
        int sent = 0;
        string name = null;
        lock (gate)
            clients.Add(s);
        try
        {
            using var net = new NetworkStream(s, ownsSocket: true);
            s.ReceiveTimeout = HelloTimeoutMs;
            name = Handshake(net, who);
            if (name == null)
                return;
            s.NoDelay = true;
            SendFrames(net, name, ref sent);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        {
            // IOException wraps the SocketException for a reset or a timeout.
            string why = ex.InnerException is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name;
            Log($"{name ?? who} closed: {(ex is EndOfStreamException ? ex.Message : why)}, after {sent} frames");
        }
        finally
        {
            lock (gate)
                clients.Remove(s);
        }
    }

    // Reads the DLL's HELLO, whose body is its process ID, and answers it.
    // Returns a name for the client's log lines, or null after a refusal.
    string Handshake(NetworkStream net, string who)
    {
        Span<byte> head = stackalloc byte[8];
        net.ReadExactly(head);
        if (!head[..4].SequenceEqual(Wire.Magic))
        {
            Log($"{who} REFUSED: bad magic {Convert.ToHexString(head[..4])}, closed without reply");
            return null;
        }
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(head[6..]);
        if (length > Wire.MaxHelloBody)
            return Refuse(net, who, Wire.BadHello, $"hello body {length} bytes, limit {Wire.MaxHelloBody}");
        byte[] body = new byte[length];
        net.ReadExactly(body);
        // Before the body is parsed: another version's body may differ.
        if (version != Wire.CameraVersion)
            return Refuse(net, who, Wire.VersionMismatch, $"hub speaks camera protocol {Wire.CameraVersion}, dll sent {version}");
        if (length < 4)
            return Refuse(net, who, Wire.BadHello, $"hello body malformed, {length} bytes");

        // The PID is what the DLL says it is. The process name is looked up
        // here, so the log shows which host loaded the DLL (TECHNICAL_DESIGN R5).
        uint pid = BinaryPrimitives.ReadUInt32BigEndian(body);
        string exe;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            exe = p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            exe = "not found";
        }
        string name = $"pid {pid} ({exe})";

        Span<byte> session = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(session, SessionId);
        DeviceListener.SendWelcome(net, Wire.CameraVersion, Wire.Accepted, session);
        int count;
        lock (gate)
            count = clients.Count;
        Log($"{name} accepted from {who}: protocol {version}, {count} client(s) connected");
        return name;
    }

    string Refuse(NetworkStream net, string who, byte status, string reason)
    {
        Log($"{who} REFUSED status {status}: {reason}");
        DeviceListener.SendWelcome(net, Wire.CameraVersion, status, Encoding.UTF8.GetBytes(reason));
        return null;
    }

    // Sends the newest frame each time one is published while someone
    // watches, until a write fails, the client goes, or the link is
    // disposed. After IdleMs with nothing written it sends IDLE, so the DLL
    // knows the hub is alive. Before that it looks for a client that went:
    // the DLL never sends after HELLO, so readable means closed, EOF or a
    // reset. Seen 2026-10-11, before IDLE, with the camera phone gone: each
    // DLL gave up after 3s without frames and reconnected, and ten such
    // clients piled up.
    void SendFrames(NetworkStream net, string name, ref int sent)
    {
        Socket s = net.Socket;
        uint lastSeq = 0;
        long lastWrite = Stopwatch.GetTimestamp();
        byte idleReason = 0;  // the reason last sent; 0 once a frame follows
        byte[] idle = new byte[Wire.HeaderSize + 1];
        idle[0] = Wire.TypeIdle;
        BinaryPrimitives.WriteUInt32BigEndian(idle.AsSpan(4), 1);
        long windowStart = lastWrite;
        int windowFrames = 0, windowSkipped = 0;
        double windowMaxMs = 0;
        while (true)
        {
            Frame f = null;
            byte reason = 0;
            lock (gate)
            {
                while (true)
                {
                    if (disposed)
                        return;
                    if (watching && latest != null && latest.Seq != lastSeq)
                    {
                        f = latest;
                        f.Refs++;  // ours while we write it
                        break;
                    }
                    // Until the IDLE is due, not a fixed second: while frames
                    // are held, pulses come at the frame rate.
                    int dueMs = IdleMs - (int)Stopwatch.GetElapsedTime(lastWrite).TotalMilliseconds;
                    if (dueMs <= 0)
                    {
                        reason = watching ? Wire.IdleNoFrame : Wire.IdleNotWatching;
                        break;
                    }
                    if (!Monitor.Wait(gate, Math.Min(dueMs, CloseCheckMs)) && s.Poll(0, SelectMode.SelectRead))
                        throw new EndOfStreamException("client closed while no frames were coming");
                }
            }

            if (f == null)
            {
                if (s.Poll(0, SelectMode.SelectRead))
                    throw new EndOfStreamException("client closed while no frames were coming");
                idle[Wire.HeaderSize] = reason;
                net.Write(idle);
                lastWrite = Stopwatch.GetTimestamp();
                if (reason != idleReason)
                    Log($"{name} IDLE sent, {(reason == Wire.IdleNotWatching ? "nobody watching" : "no new frame")}; " +
                        "repeated each second, logged on change");
                idleReason = reason;
                continue;
            }

            byte[] m = f.Data;
            uint mSeq = f.Seq;
            // Read while the reference is held: once released, the producer
            // may refill this buffer.
            string geometry = sent == 0
                ? $"{BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(20))}x{BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(24))} " +
                  $"stride {BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(28))}, {m.Length} bytes"
                : null;
            // Frames published while held were never due, so not counted.
            if (lastSeq != 0 && idleReason == 0)
                windowSkipped += (int)(mSeq - lastSeq - 1);
            if (idleReason != 0)
            {
                Log($"{name} frames again after IDLE, seq {mSeq}");
                windowStart = Stopwatch.GetTimestamp();  // so "last 5s" means it
                windowFrames = windowSkipped = 0;
                windowMaxMs = 0;
            }
            idleReason = 0;
            lastSeq = mSeq;

            long now = Stopwatch.GetTimestamp();
            try
            {
                net.Write(m);
            }
            finally
            {
                lock (gate)
                    ReleaseLocked(f);
            }
            lastWrite = Stopwatch.GetTimestamp();
            double ms = (lastWrite - now) * 1000.0 / Stopwatch.Frequency;
            sent++;
            windowFrames++;
            windowMaxMs = Math.Max(windowMaxMs, ms);
            if (geometry != null)
                Log($"{name} first frame written, seq {mSeq}, {geometry}, in {ms:F2}ms");
            if (now - windowStart >= Stopwatch.Frequency * 5)
            {
                Log($"{name} last 5s: {windowFrames} frames, {windowSkipped} superseded unsent, write max {windowMaxMs:F2}ms, " +
                    $"{Allocated} frame buffers ever allocated");
                windowStart = now;
                windowFrames = windowSkipped = 0;
                windowMaxMs = 0;
            }
        }
    }

    public void Dispose()
    {
        listener.Stop();
        lock (gate)
        {
            disposed = true;
            Monitor.PulseAll(gate);
            foreach (Socket s in clients)
                s.Close();
        }
    }
}
