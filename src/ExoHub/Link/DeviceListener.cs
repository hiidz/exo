// The one loopback port every device's localabstract:exo reaches through
// adb reverse (PROJECT_SPEC FR-4.3). Accepts, runs the handshake in
// DECISIONS 5.7, and holds the display connection and the camera
// connection, whose frames go to the camera decoder.
//
// One thread accepts and one thread serves each connection. There are only
// ever a few connections, and a blocking thread each reads top to bottom.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ExoHub.Camera;
using ExoHub.Display;
using ExoHub.Touch;

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
    readonly TouchInjector touch;   // TOUCH from the display connection (FR-3.3)
    readonly CameraDecoder camera;  // null: the camera role is refused
    readonly VirtualDisplay monitor;  // plugged for the display connection
    readonly object gate = new();
    Connection display;     // under gate
    Connection cameraConn;  // under gate

    // Random per hub start, sent in every WELCOME. A phone that logs a new
    // value is talking to a new hub instance (DECISIONS 5.7).
    public ulong SessionId { get; } = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    sealed class Connection(Socket socket, Phone phone, byte role, byte rotation)
    {
        public readonly Socket Socket = socket;
        public readonly Phone Phone = phone;
        public readonly byte Role = role;
        public readonly byte Rotation = rotation;  // clockwise quarter turns, 0 or 2
    }

    // camera is null when the hub has no virtual camera to feed, so the
    // camera role is refused.
    public DeviceListener(int port, Stopwatch clock, DisplaySender sender, TouchInjector touch, CameraDecoder camera,
                          VirtualDisplay monitor)
    {
        this.clock = clock;
        this.monitor = monitor;
        this.sender = sender;
        this.touch = touch;
        this.camera = camera;
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
            if (conn.Role == Wire.RoleCamera)
            {
                try
                {
                    ReadCamera(net, conn);
                }
                finally
                {
                    camera.EndStream(conn.Phone.ToString());
                }
                return;
            }
            s.SendBufferSize = SendBufferBytes;
            Log($"{conn.Phone} send buffer {s.SendBufferSize} bytes, nodelay {s.NoDelay}");
            monitor.Connected(conn.Phone);
            sender.Attach(net, conn.Phone.ToString());
            try
            {
                ReadMessages(net, conn);
            }
            finally
            {
                sender.Detach(net);
                monitor.Disconnected(conn.Phone);
                touch.ConnectionEnded();
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        {
            // IOException wraps the SocketException for a reset or a timeout.
            string why = ex.InnerException is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name;
            Log($"{conn?.Phone.ToString() ?? who} closed: {why}");
        }
        finally
        {
            lock (gate)
            {
                if (display == conn)
                    display = null;
                if (cameraConn == conn)
                    cameraConn = null;
            }
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
        ushort newest = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(head[6..]);
        if (length > Wire.MaxHelloBody)
            return Refuse(net, who, Wire.BadHello, $"hello body {length} bytes, limit {Wire.MaxHelloBody}");
        byte[] body = new byte[length];
        net.ReadExactly(body);

        // Its length is in the frozen prefix, so the body was read whole and
        // a refusal goes out on a clean stream. Before 4 there is no oldest:
        // the device speaks only its newest.
        ushort oldest = newest >= 4 && length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(body) : newest;
        if (newest < Wire.Oldest || oldest > Wire.Version)
            return Refuse(net, who, Wire.VersionMismatch,
                          $"device speaks protocol {oldest} to {newest}, hub {Wire.Oldest} to {Wire.Version}: " +
                          $"update {(newest < Wire.Oldest ? "the app" : "the hub")}");
        // The version both speak. Every version since the oldest the hub
        // speaks keeps 4's body as its start, so it parses the same.
        ushort version = Math.Min(newest, Wire.Version);

        int nameAt = 2 + 1 + Wire.IdSize + 1;
        if (length < nameAt || body[nameAt - 1] > Wire.MaxName || length < nameAt + body[nameAt - 1] + 1)
            return Refuse(net, who, Wire.BadHello, $"hello body malformed, {length} bytes");
        byte role = body[2], rotation = body[nameAt + body[nameAt - 1]];
        var phone = new Phone(Convert.ToHexString(body, 3, Wire.IdSize).ToLowerInvariant(),
                              Encoding.UTF8.GetString(body, nameAt, body[nameAt - 1]));
        string name = $"{phone} \"{phone.Name}\"";
        if (role != Wire.RoleDisplay && role != Wire.RoleCamera)
            return Refuse(net, name, Wire.BadHello, $"role {role} unknown");
        string roleName = role == Wire.RoleCamera ? "camera" : "display";
        if (role == Wire.RoleCamera && camera == null)
            return Refuse(net, name, Wire.CameraUnavailable, "this hub has no virtual camera");
        if (role == Wire.RoleDisplay && monitor.Unavailable is string why)
            return Refuse(net, name, Wire.DisplayUnavailable, $"ExoDisplay {why}");
        // Landscape only: a quarter turn would make the picture 720x1280,
        // which ExoCamera.dll refuses (TECHNICAL_DESIGN 3.3).
        if (rotation != 0 && rotation != 2)
            return Refuse(net, name, Wire.BadHello, $"rotation {rotation * 90} degrees, only 0 and 180 are supported");

        var conn = new Connection(s, phone, role, rotation);
        Connection replaced;
        lock (gate)
        {
            Connection held = role == Wire.RoleCamera ? cameraConn : display;
            if (held != null && held.Phone.Id != phone.Id)
            {
                Log($"{name} REFUSED status {Wire.RoleTaken}: {roleName} role held by {held.Phone} \"{held.Phone.Name}\"");
                SendWelcome(net, Wire.Version, Wire.RoleTaken, Encoding.UTF8.GetBytes(held.Phone.Name));
                return null;
            }
            // The same phone: the app restarted before this end saw the old
            // connection go. The new one wins (DECISIONS 5.7).
            replaced = held;
            if (role == Wire.RoleCamera)
                cameraConn = conn;
            else
                display = conn;
        }
        if (replaced != null)
        {
            Log($"{name} replaces its previous {roleName} connection");
            replaced.Socket.Close();
        }

        Span<byte> session = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(session, SessionId);
        SendWelcome(net, version, Wire.Accepted, session);
        Log($"{name} accepted: protocol {version} (device {oldest} to {newest}), role {roleName}, " +
            $"rotation {rotation * 90}, session {SessionId:x16}");
        return conn;
    }

    Connection Refuse(NetworkStream net, string who, byte status, string reason)
    {
        Log($"{who} REFUSED status {status}: {reason}");
        SendWelcome(net, Wire.Version, status, Encoding.UTF8.GetBytes(reason));
        return null;
    }

    // Also L7's WELCOME, which has the same layout at its own version.
    internal static void SendWelcome(NetworkStream net, ushort version, byte status, ReadOnlySpan<byte> body)
    {
        Span<byte> head = stackalloc byte[9];
        Wire.Magic.CopyTo(head);
        BinaryPrimitives.WriteUInt16BigEndian(head[4..], version);
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
                Log($"{conn.Phone} protocol error: message type {type} claims {length} bytes, dropping");
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
            if (type == Wire.TypeTouch)
            {
                if (!touch.Post(body.AsSpan(0, (int)length)))
                {
                    Log($"{conn.Phone} protocol error: malformed TOUCH, {length} bytes, dropping");
                    return;
                }
                continue;
            }
            if (skipped++ == 0)
                Log($"{conn.Phone} skipping message type {type}, {length} bytes; logged once per connection");
        }
    }

    // The camera role's connection: each FRAME into the decoder, on this
    // thread, and each PING answered with a PONG, written here since this is
    // the only thread writing this socket. Nothing from the phone is dropped
    // here (5.7): a decoder that falls behind slows this read, and TCP
    // carries that back to the phone's queue.
    void ReadCamera(NetworkStream net, Connection conn)
    {
        const int FrameFields = 4 + 3 * 8;  // seq; present, encoded, sent
        camera.BeginStream(conn.Phone.ToString(), flip: conn.Rotation == 2);
        Span<byte> head = stackalloc byte[Wire.HeaderSize];
        Span<byte> pong = stackalloc byte[Wire.HeaderSize + 24];
        byte[] body = new byte[256 * 1024];  // grows to the largest keyframe, once
        long windowStart = Stopwatch.GetTimestamp();
        int total = 0, frames = 0, keyframes = 0, gaps = 0, skipped = 0;
        long bytes = 0;
        uint lastSeq = 0;
        while (true)
        {
            net.ReadExactly(head);
            byte type = head[0], flags = head[1];
            uint length = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            if (length > Wire.MaxBody)
            {
                Log($"{conn.Phone} protocol error: message type {type} claims {length} bytes, dropping");
                return;
            }
            if (body.Length < length)
                body = new byte[length];
            net.ReadExactly(body.AsSpan(0, (int)length));

            if (type == 2 && length == 8)  // PING
            {
                long t2 = DisplaySender.Micros(Stopwatch.GetTimestamp());
                pong.Clear();
                pong[0] = 3;  // PONG
                BinaryPrimitives.WriteUInt32BigEndian(pong[4..], 24);
                body.AsSpan(0, 8).CopyTo(pong[8..]);
                BinaryPrimitives.WriteInt64BigEndian(pong[16..], t2);
                BinaryPrimitives.WriteInt64BigEndian(pong[24..], DisplaySender.Micros(Stopwatch.GetTimestamp()));
                net.Write(pong);
                continue;
            }
            if (type != Wire.TypeFrame || length < FrameFields)
            {
                if (skipped++ == 0)
                    Log($"{conn.Phone} skipping message type {type}, {length} bytes; logged once per connection");
                continue;
            }

            uint seq = BinaryPrimitives.ReadUInt32BigEndian(body);
            bool key = (flags & DisplaySender.FlagKeyframe) != 0, config = (flags & DisplaySender.FlagConfig) != 0;
            if (total++ == 0)
                Log($"{conn.Phone} camera first frame: seq {seq}, {length - FrameFields} bytes, " +
                    $"{(key ? "KEYFRAME" : "not a keyframe")}, {(config ? "CONFIG" : "no config")}");
            gaps += lastSeq != 0 && seq != lastSeq + 1 ? 1 : 0;
            lastSeq = seq;
            frames++;
            keyframes += key ? 1 : 0;
            bytes += length;
            if (!camera.Submit(body.AsSpan(FrameFields, (int)length - FrameFields), key, config))
            {
                Log($"{conn.Phone} camera decoder has failed ({camera.Error}); closing");
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (now - windowStart >= Stopwatch.Frequency * 5)
            {
                double s = (now - windowStart) / (double)Stopwatch.Frequency;
                Log($"{conn.Phone} camera last 5s: {frames} frames, {frames / s:F1}fps, {keyframes} keyframes, " +
                    $"{bytes * 8 / s / 1e6:F2} Mbps, {gaps} seq gaps");
                windowStart = now;
                frames = keyframes = gaps = 0;
                bytes = 0;
            }
        }
    }

    public void Dispose()
    {
        listener.Stop();
        lock (gate)
        {
            display?.Socket.Close();
            cameraConn?.Socket.Close();
        }
    }
}
