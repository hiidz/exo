// L7, the link from the hub to ExoCamera.dll (DECISIONS 5.9). Listens on a
// fixed loopback port, because the DLL reads no config and so has no way to
// learn a port Windows chose. Each client gets 5.7's handshake, then FRAMEs.
//
// A latest-frame slot (DECISIONS 5.1). One producer, the decoder or the
// test pattern, publishes each frame once, built as a whole L7 message.
// Every client thread sends the newest message it has not sent; a frame
// superseded before a client got to it is skipped, never queued. So one
// decode serves every client (FR-1.3), and a slow client holds back only
// itself. A published message is never written again, so client threads
// read it without the lock.
//
// One thread accepts and one thread serves each client, as DeviceListener.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ExoHub.Link;

sealed class CameraLink : IDisposable
{
    const int HelloTimeoutMs = 2000;
    // seq u32, generated-at i64, width, height, stride u32 each.
    const int BodyPrefix = 4 + 8 + 3 * 4;

    readonly TcpListener listener;
    readonly Stopwatch clock;
    readonly object gate = new();
    readonly List<Socket> clients = new();  // under gate
    byte[] latest;     // under gate: the newest whole FRAME message
    uint latestSeq;    // under gate: its seq
    uint seq;          // the producer's alone
    bool disposed;     // under gate

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
    public void Publish(long stampQpc, int width, int height, int stride, ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv)
    {
        int ySize = stride * height, uvSize = stride * height / 2;
        byte[] m = new byte[Wire.HeaderSize + BodyPrefix + ySize + uvSize];
        m[0] = Wire.TypeFrame;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), (uint)(BodyPrefix + ySize + uvSize));
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(8), ++seq);
        BinaryPrimitives.WriteInt64BigEndian(m.AsSpan(12), DisplaySender.Micros(stampQpc));
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(20), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(24), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(28), (uint)stride);
        y[..ySize].CopyTo(m.AsSpan(Wire.HeaderSize + BodyPrefix));
        uv[..uvSize].CopyTo(m.AsSpan(Wire.HeaderSize + BodyPrefix + ySize));
        lock (gate)
        {
            latest = m;
            latestSeq = seq;
            // Monitor is C#'s condition variable: Wait releases the lock and
            // sleeps, PulseAll wakes every waiter to re-check.
            Monitor.PulseAll(gate);
        }
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
            Log($"{name ?? who} closed: {why}, after {sent} frames");
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

    // Sends the newest frame each time one is published, until a write
    // fails or the link is disposed.
    void SendFrames(NetworkStream net, string name, ref int sent)
    {
        uint lastSeq = 0;
        long windowStart = Stopwatch.GetTimestamp();
        int windowFrames = 0, windowSkipped = 0;
        double windowMaxMs = 0;
        while (true)
        {
            byte[] m;
            uint mSeq;
            lock (gate)
            {
                while (!disposed && (latest == null || latestSeq == lastSeq))
                    Monitor.Wait(gate);
                if (disposed)
                    return;
                m = latest;
                mSeq = latestSeq;
            }
            if (lastSeq != 0)
                windowSkipped += (int)(mSeq - lastSeq - 1);
            lastSeq = mSeq;

            long now = Stopwatch.GetTimestamp();
            net.Write(m);
            double ms = (Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency;
            sent++;
            windowFrames++;
            windowMaxMs = Math.Max(windowMaxMs, ms);
            if (sent == 1)
            {
                int w = (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(20));
                int h = (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(24));
                int stride = (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(28));
                Log($"{name} first frame written, seq {mSeq}, {w}x{h} stride {stride}, {m.Length} bytes, in {ms:F2}ms");
            }
            if (now - windowStart >= Stopwatch.Frequency * 5)
            {
                Log($"{name} last 5s: {windowFrames} frames, {windowSkipped} superseded unsent, write max {windowMaxMs:F2}ms");
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
