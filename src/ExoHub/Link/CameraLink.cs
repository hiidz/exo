// L7, the link from the hub to ExoCamera.dll (DECISIONS 5.9). Listens on a
// fixed loopback port, because the DLL reads no config and so has no way to
// learn a port Windows chose. Each client gets 5.7's handshake, then FRAMEs.
//
// The frames are synthetic for now (DECISIONS 10 item 4, increment B), and
// built to make a geometry bug visible rather than only logged: rows are
// wider than the picture, with the padding bright, so a reader that ignores
// the stride shows a diagonal shear; and a red marker sweeps across, whose
// colour comes out right only if the UV plane is found at stride * height.
// Increment C replaces them with frames decoded from the phone.
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
    const int Fps = 30;
    // seq u32, generated-at i64, width, height, stride u32 each.
    const int BodyPrefix = 4 + 8 + 3 * 4;

    // Studio-range NV12. The background is not the test card's, so which
    // one Chrome shows says at a glance whether the link is up.
    const byte BackY = 64, PadY = 235, Neutral = 128;
    const byte MarkY = 81, MarkU = 90, MarkV = 240;  // red, as the card's red bar
    const int MarkSize = 96;
    const double SweepSeconds = 2;

    readonly TcpListener listener;
    readonly Stopwatch clock;
    readonly int width, height, stride;
    readonly object gate = new();
    readonly List<Socket> clients = new();  // under gate

    // Random per hub start, sent in every WELCOME, as on the device link.
    public ulong SessionId { get; } = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));

    // Throws SocketException (AddressAlreadyInUse) when another process
    // holds the port, which with exclusive use includes an orphaned hub.
    public CameraLink(Stopwatch clock, int width, int height, int stride)
    {
        this.clock = clock;
        this.width = width;
        this.height = height;
        this.stride = stride;
        listener = new TcpListener(IPAddress.Loopback, Wire.CameraPort) { ExclusiveAddressUse = true };
        listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "camera accept" }.Start();
        Log($"listening on 127.0.0.1:{Wire.CameraPort}, protocol {Wire.CameraVersion}, session {SessionId:x16}, " +
            $"sending {width}x{height} stride {stride} at {Fps}fps");
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s camera {line}");

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

    // Writes a frame every 1/30s until a write fails. Paced against a
    // deadline that advances by one interval per frame, so a late wake does
    // not push every later frame back.
    void SendFrames(NetworkStream net, string name, ref int sent)
    {
        int planes = stride * height * 3 / 2;
        byte[] frame = new byte[Wire.HeaderSize + BodyPrefix + planes];
        frame[0] = Wire.TypeFrame;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)(BodyPrefix + planes));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(28), (uint)stride);
        Span<byte> y = frame.AsSpan(Wire.HeaderSize + BodyPrefix, stride * height);
        Span<byte> uv = frame.AsSpan(Wire.HeaderSize + BodyPrefix + stride * height, stride * height / 2);

        long interval = Stopwatch.Frequency / Fps;
        long due = Stopwatch.GetTimestamp();
        long windowStart = due;
        int windowFrames = 0;
        double windowMaxMs = 0;
        uint seq = 0;
        while (true)
        {
            long wait = (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (wait > 0)
                Thread.Sleep((int)wait);
            due += interval;

            Draw(y, uv);
            long now = Stopwatch.GetTimestamp();
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), seq++);
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(12), DisplaySender.Micros(now));
            net.Write(frame);
            double ms = (Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency;
            sent++;
            windowFrames++;
            windowMaxMs = Math.Max(windowMaxMs, ms);
            if (sent == 1)
                Log($"{name} first frame written, {frame.Length} bytes, in {ms:F2}ms");
            if (now - windowStart >= Stopwatch.Frequency * 5)
            {
                Log($"{name} last 5s: {windowFrames} frames, write max {windowMaxMs:F2}ms");
                windowStart = now;
                windowFrames = 0;
                windowMaxMs = 0;
            }
        }
    }

    // The whole picture, every frame: 1.4MB of fills is far cheaper than
    // tracking what moved. The marker's position is from the hub's clock,
    // so every client sees the same picture.
    void Draw(Span<byte> y, Span<byte> uv)
    {
        for (int row = 0; row < height; row++)
        {
            y.Slice(row * stride, width).Fill(BackY);
            y.Slice(row * stride + width, stride - width).Fill(PadY);
        }
        for (int row = 0; row < height / 2; row++)
        {
            uv.Slice(row * stride, width).Fill(Neutral);
            uv.Slice(row * stride + width, stride - width).Fill(PadY);
        }
        // Even coordinates, so the marker covers whole U,V pairs.
        double phase = clock.Elapsed.TotalSeconds % SweepSeconds / SweepSeconds;
        int x0 = (int)(phase * (width - MarkSize)) & ~1;
        int y0 = (height - MarkSize) / 2 & ~1;
        for (int row = y0; row < y0 + MarkSize; row++)
            y.Slice(row * stride + x0, MarkSize).Fill(MarkY);
        for (int row = y0 / 2; row < (y0 + MarkSize) / 2; row++)
            for (int col = x0; col < x0 + MarkSize; col += 2)
            {
                uv[row * stride + col] = MarkU;
                uv[row * stride + col + 1] = MarkV;
            }
    }

    public void Dispose()
    {
        listener.Stop();
        lock (gate)
            foreach (Socket s in clients)
                s.Close();
    }
}
