// The one loopback port every device's localabstract:exo reaches through
// adb reverse (PROJECT_SPEC FR-4.3). Accepts, runs the handshake in
// DECISIONS 5.7, and holds the display connection.
//
// One thread accepts and one thread serves each connection. There are only
// ever a few connections, and a blocking thread each reads top to bottom.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ExoHub.Link;

sealed class DeviceListener : IDisposable
{
    const int HelloTimeoutMs = 2000;
    // About one and a half frames at 40 Mbps. Set, because Windows otherwise
    // autotunes it and a fast-completing write would hide a backlog (5.7).
    const int SendBufferBytes = 256 * 1024;

    readonly TcpListener listener;
    readonly Stopwatch clock;
    readonly DisplaySender sender;
    readonly object gate = new();
    Connection display;  // under gate

    // Random per hub start, sent in every WELCOME. A phone that logs a new
    // value is talking to a new hub instance (DECISIONS 5.7).
    public ulong SessionId { get; } = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    sealed class Connection(Socket socket, string serial)
    {
        public readonly Socket Socket = socket;
        public readonly string Serial = serial;
    }

    public DeviceListener(int port, Stopwatch clock, DisplaySender sender)
    {
        this.clock = clock;
        this.sender = sender;
        // Loopback only, so nothing on the LAN can connect. Exclusive, so a
        // second hub fails here instead of sharing the port with this one.
        listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "accept" }.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s link {line}");

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
            new Thread(() => Serve(s)) { IsBackground = true, Name = "device" }.Start();
        }
    }

    // One connection, from accept to close.
    void Serve(Socket s)
    {
        string who = s.RemoteEndPoint?.ToString() ?? "?";
        Connection conn = null;
        try
        {
            using var net = new NetworkStream(s, ownsSocket: true);
            s.ReceiveTimeout = HelloTimeoutMs;
            conn = Handshake(s, net, who);
            if (conn == null)
                return;
            s.ReceiveTimeout = 0;
            // Small messages such as PONG go out at once rather than waiting
            // for Nagle's algorithm to batch them.
            s.NoDelay = true;
            s.SendBufferSize = SendBufferBytes;
            Log($"{conn.Serial} send buffer {s.SendBufferSize} bytes, nodelay {s.NoDelay}");
            sender.Attach(net, conn.Serial);
            try
            {
                ReadMessages(net, conn);
            }
            finally
            {
                sender.Detach(net);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        {
            // IOException wraps the SocketException for a reset or a timeout.
            string why = ex.InnerException is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name;
            Log($"{conn?.Serial ?? who} closed: {why}");
        }
        finally
        {
            lock (gate)
                if (display == conn)
                    display = null;
        }
    }

    // Reads a HELLO and answers it. Returns the accepted connection, or null
    // after a refusal, which has already been logged and sent.
    Connection Handshake(Socket s, NetworkStream net, string who)
    {
        Span<byte> head = stackalloc byte[8];
        net.ReadExactly(head);
        if (!head[..4].SequenceEqual(Wire.Magic))
        {
            // Not Exo, so there is nothing to say back to it.
            Log($"{who} REFUSED: bad magic {Convert.ToHexString(head[..4])}, closed without reply");
            return null;
        }
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(head[6..]);
        if (length > Wire.MaxHelloBody)
            return Refuse(net, who, Wire.BadHello, $"hello body {length} bytes, limit {Wire.MaxHelloBody}");
        byte[] body = new byte[length];
        net.ReadExactly(body);

        // Checked before the body is parsed: another version's body may not
        // have this layout. Its length is in the frozen prefix, so it was
        // read whole and the refusal goes out on a clean stream.
        if (version != Wire.Version)
            return Refuse(net, who, Wire.VersionMismatch, $"hub speaks protocol {Wire.Version}, device sent {version}");

        if (length < 3 || length < 3 + body[2] || body[2] > Wire.MaxSerial)
            return Refuse(net, who, Wire.BadHello, $"hello body malformed, {length} bytes");
        byte role = body[0], direction = body[1];
        string serial = Encoding.ASCII.GetString(body, 3, body[2]);
        string name = serial == "" ? $"{who} (serial not supplied)" : serial;
        if (role != Wire.RoleDisplay)
            return Refuse(net, name, Wire.BadHello, $"role {role} not served, only display ({Wire.RoleDisplay})");
        if (direction != Wire.Down)
            return Refuse(net, name, Wire.BadHello, $"display role must be direction down ({Wire.Down}), got {direction}");

        var conn = new Connection(s, serial);
        Connection replaced;
        lock (gate)
        {
            if (display != null && display.Serial != serial)
                return Refuse(net, name, Wire.RoleTaken, $"display role held by {display.Serial}");
            // Same serial: the app restarted before this end saw the old
            // connection go. The new one wins (DECISIONS 5.7).
            replaced = display;
            display = conn;
        }
        if (replaced != null)
        {
            Log($"{name} replaces its previous display connection");
            replaced.Socket.Close();
        }

        Span<byte> session = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(session, SessionId);
        SendWelcome(net, Wire.Accepted, session);
        Log($"{name} accepted: protocol {version}, role display, session {SessionId:x16}");
        return conn;
    }

    Connection Refuse(NetworkStream net, string who, byte status, string reason)
    {
        Log($"{who} REFUSED status {status}: {reason}");
        SendWelcome(net, status, Encoding.UTF8.GetBytes(reason));
        return null;
    }

    static void SendWelcome(NetworkStream net, byte status, ReadOnlySpan<byte> body)
    {
        Span<byte> head = stackalloc byte[9];
        Wire.Magic.CopyTo(head);
        BinaryPrimitives.WriteUInt16BigEndian(head[4..], Wire.Version);
        head[6] = status;
        BinaryPrimitives.WriteUInt16BigEndian(head[7..], (ushort)body.Length);
        net.Write(head);
        net.Write(body);
    }

    // After the handshake. PING is answered through the sender, the one
    // thread that writes this socket; anything else is skipped by its length.
    void ReadMessages(NetworkStream net, Connection conn)
    {
        Span<byte> head = stackalloc byte[Wire.HeaderSize];
        byte[] body = new byte[256];
        int skipped = 0;
        while (true)
        {
            net.ReadExactly(head);
            byte type = head[0];
            uint length = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            if (length > Wire.MaxBody)
            {
                Log($"{conn.Serial} protocol error: message type {type} claims {length} bytes, dropping");
                return;
            }
            if (body.Length < length)
                body = new byte[length];
            net.ReadExactly(body.AsSpan(0, (int)length));
            if (type == 2 && length == 8)  // PING
            {
                long t2 = DisplaySender.Micros(Stopwatch.GetTimestamp());
                sender.Pong(net, BinaryPrimitives.ReadInt64BigEndian(body), t2);
                continue;
            }
            if (skipped++ == 0)
                Log($"{conn.Serial} skipping message type {type}, {length} bytes; logged once per connection");
        }
    }

    public void Dispose()
    {
        listener.Stop();
        lock (gate)
            display?.Socket.Close();
    }
}
