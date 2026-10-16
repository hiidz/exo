// The loopback ports each USB device's localabstract:exo reaches through
// adb reverse, one a serial, so a connection's port names its device
// (PLAN.md, Transport and Wi-Fi), and the Wi-Fi port. Accepts, runs the
// handshake in DECISIONS 5.7, and holds the display connection and the
// camera connection, whose frames go to the camera decoder.
//
// One thread accepts and one thread serves each connection. There are only
// ever a few connections, and a blocking thread each reads top to bottom.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
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

    readonly Dictionary<string, TcpListener> usb = new();  // loopback, a serial's, for adb reverse; under itself
    readonly TcpListener wifi;      // every interface, for Wi-Fi (PLAN.md, Transport and Wi-Fi)
    readonly Stopwatch clock;
    readonly DisplaySender sender;
    readonly TouchInjector touch;   // TOUCH from the display connection (FR-3.3)
    readonly CameraDecoder camera;  // null: the camera role is refused
    readonly VirtualCamera vcam;    // registered again for the camera phone's format
    readonly VirtualDisplay monitor;  // plugged for the display connection
    readonly object gate = new();
    Connection display;     // under gate
    Connection cameraConn;  // under gate

    // Random per hub start, sent in every WELCOME. A phone that logs a new
    // value is talking to a new hub instance (DECISIONS 5.7).
    public ulong SessionId { get; } = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
    public int WifiPort => ((IPEndPoint)wifi.LocalEndpoint).Port;
    // Asks the PC's user whether a phone may connect over Wi-Fi; blocks until
    // answered, and remembers an allow. Null until the tray is up: no answer.
    public Func<Phone, string, bool> Ask;

    sealed class Connection(Socket socket, Phone phone, byte role, byte rotation, DisplayMode mode, string serial)
    {
        public readonly string Serial = serial;    // the USB device it came through; null over Wi-Fi
        public readonly Socket Socket = socket;
        public readonly Phone Phone = phone;
        public readonly byte Role = role;
        public readonly byte Rotation = rotation;  // clockwise quarter turns, 0 or 2
        public readonly DisplayMode Mode = mode;   // the display role's; default for the camera
        public CameraFormat Format;                // the camera role's
        public int Frames;                         // the camera role's, received so far
        public bool Wifi => Serial == null;
    }

    // One accepted connection as the window shows it. Progress moves while
    // it works: frames the display phone acked, or camera frames received.
    // Serial is the USB device's, null over Wi-Fi. Paused: the phone's display is out of view.
    public readonly record struct Link(Phone Phone, bool Camera, string Detail, int Progress, string Serial, bool Paused);

    public List<Link> Snapshot()
    {
        var links = new List<Link>();
        bool paused = sender.Paused;  // before gate, so the two locks never nest
        lock (gate)
        {
            if (display != null)
                links.Add(new Link(display.Phone, false, $"{display.Mode.Width}x{display.Mode.Height} at {display.Mode.Fps}fps",
                                   Volatile.Read(ref sender.Acked), display.Serial, paused));
            if (cameraConn != null)
                links.Add(new Link(cameraConn.Phone, true, cameraConn.Format.ToString(), Volatile.Read(ref cameraConn.Frames),
                                   cameraConn.Serial, false));
        }
        return links;
    }

    // camera is null when the hub has no virtual camera to feed, so the
    // camera role is refused.
    public DeviceListener(Stopwatch clock, DisplaySender sender, TouchInjector touch, CameraDecoder camera,
                          VirtualCamera vcam, VirtualDisplay monitor)
    {
        this.clock = clock;
        this.monitor = monitor;
        this.sender = sender;
        this.touch = touch;
        this.camera = camera;
        this.vcam = vcam;
        // Port 0, so an orphan hub cannot be mistaken for this one: the
        // phone learns the port and session from Discovery's answer. TLS on
        // every connection to it (Pairing).
        wifi = new TcpListener(IPAddress.Any, 0) { ExclusiveAddressUse = true };
        wifi.Start();
        new Thread(() => AcceptLoop(wifi, null)) { IsBackground = true, Name = "accept-wifi" }.Start();
    }

    // A port for this USB device alone, for its reverse; the same one while
    // it stays. Loopback only, so nothing on the LAN can connect. Port 0 and
    // exclusive, so with WELCOME's session ID it names this hub (5.7, 5.8).
    public int Listen(string serial)
    {
        TcpListener l;
        lock (usb)
        {
            if (usb.TryGetValue(serial, out l))
                return ((IPEndPoint)l.LocalEndpoint).Port;
            l = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
            l.Start();
            usb[serial] = l;
        }
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        Log($"{serial} listening on 127.0.0.1:{port}");
        new Thread(() => AcceptLoop(l, serial)) { IsBackground = true, Name = "accept " + serial }.Start();
        return port;
    }

    // The device left: its port closes. Its connections end as its
    // transport went, by themselves.
    public void Unlisten(string serial)
    {
        TcpListener l;
        lock (usb)
            if (!usb.Remove(serial, out l))
                return;
        l.Stop();
        Log($"{serial} port closed");
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s link {line}");

    // serial: the USB device whose port this is; null for Wi-Fi.
    void AcceptLoop(TcpListener from, string serial)
    {
        while (true)
        {
            Socket s;
            try
            {
                s = from.AcceptSocket();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;  // Dispose stopped the listener
            }
            new Thread(() => Serve(s, serial)) { IsBackground = true, Name = "device" }.Start();
        }
    }

    // The connection's stream: plain over USB, which adb already guards, and
    // TLS over Wi-Fi once the client starts it. A plain start over Wi-Fi is
    // an app from before TLS, which Handshake refuses with a sentence.
    Stream Open(Socket s, string serial, string who)
    {
        var net = new NetworkStream(s, ownsSocket: true);
        // A TLS handshake record starts 0x16; a HELLO starts with 'E'.
        Span<byte> first = stackalloc byte[1];
        if (serial != null || s.Receive(first, SocketFlags.Peek) != 1 || first[0] != 0x16)
            return net;
        var tls = new SslStream(net, leaveInnerStreamOpen: false);
        try
        {
            tls.AuthenticateAsServer(new SslServerAuthenticationOptions
            {
                ServerCertificate = Pairing.Certificate,
                ClientCertificateRequired = true,
                // Any certificate: Handshake checks it against the pairing.
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null,
            });
        }
        catch
        {
            tls.Dispose();
            throw;
        }
        Log($"{who} TLS {tls.SslProtocol} {tls.NegotiatedCipherSuite}");
        return tls;
    }

    // One connection, from accept to close.
    void Serve(Socket s, string serial)
    {
        string who = s.RemoteEndPoint?.ToString() ?? "?";
        Connection conn = null;
        try
        {
            s.ReceiveTimeout = HelloTimeoutMs;
            using Stream net = Open(s, serial, who);
            conn = Handshake(s, net, who, serial);
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
            monitor.Connected(conn.Phone, conn.Mode);
            sender.Attach(net, conn.Phone.ToString(), conn.Mode.Fps, conn.Wifi);
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
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or
                                         AuthenticationException or SocketException)
        {
            // IOException wraps the SocketException for a reset or a timeout;
            // Open's peek throws it bare. AuthenticationException is a TLS
            // handshake that failed.
            string why = (ex as SocketException ?? ex.InnerException as SocketException) is { } se
                ? se.SocketErrorCode.ToString() : ex.GetType().Name;
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
    Connection Handshake(Socket s, Stream net, string who, string serial)
    {
        bool overWifi = serial == null;
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
        if (overWifi && (net is not SslStream || version < Wire.TlsFrom))
            return Refuse(net, who, Wire.VersionMismatch,
                          $"device speaks protocol {oldest} to {newest} without TLS; Wi-Fi needs {Wire.TlsFrom}: update the app");

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
        DisplayMode mode = default;
        CameraFormat format = default;
        if (role == Wire.RoleCamera)
        {
            int at = nameAt + body[nameAt - 1] + 1;
            if (length < at + Wire.CameraFields)
                return Refuse(net, name, Wire.BadHello, $"camera hello {length} bytes, no format");
            var c = body.AsSpan(at);
            format = new CameraFormat(BinaryPrimitives.ReadUInt16BigEndian(c), BinaryPrimitives.ReadUInt16BigEndian(c[2..]),
                                      c[4]);
            // Even, for 4:2:0; landscape; and small enough that one NV12
            // frame fits an L7 FRAME, 1920x1080 at most.
            if (format.Width % 2 != 0 || format.Height % 2 != 0 || format.Width < format.Height ||
                format.Width is < 160 or > 1920 || format.Height is < 90 or > 1080 || format.Fps is not (30 or 60))
                return Refuse(net, name, Wire.BadHello, $"camera format {format} unsupported");
        }
        if (role == Wire.RoleDisplay)
        {
            int at = nameAt + body[nameAt - 1] + 1;
            if (length < at + Wire.DisplayFields)
                return Refuse(net, name, Wire.BadHello, $"display hello {length} bytes, no mode");
            var d = body.AsSpan(at);
            mode = new DisplayMode(BinaryPrimitives.ReadUInt16BigEndian(d), BinaryPrimitives.ReadUInt16BigEndian(d[2..]),
                                   d[4], BinaryPrimitives.ReadUInt16BigEndian(d[5..]),
                                   BinaryPrimitives.ReadUInt16BigEndian(d[7..]));
            // Even, for 4:2:0, and in a range every part handles.
            if (mode.Width % 2 != 0 || mode.Height % 2 != 0 || mode.Width is < 320 or > 8192 ||
                mode.Height is < 320 or > 8192 || mode.Fps is not (30 or 60))
                return Refuse(net, name, Wire.BadHello, $"display mode {mode} unsupported");
        }

        // Over Wi-Fi, anyone on the network can get here: a phone pairs once,
        // when either side holds no pin for the other, and the PC's user
        // allows it after comparing the code (Pairing). The phone hears
        // Pending and waits; nothing is held meanwhile.
        if (overWifi)
        {
            int pairAt = nameAt + body[nameAt - 1] + 1 + (role == Wire.RoleCamera ? Wire.CameraFields : Wire.DisplayFields);
            if (length < pairAt + Wire.PairFields)
                return Refuse(net, name, Wire.BadHello, $"hello {length} bytes, no pairing fields");
            var phoneCert = ((SslStream)net).RemoteCertificate;
            string fingerprint = Pairing.Fingerprint(phoneCert);
            bool phoneAsks = body[pairAt] != 0;
            if (phoneAsks || !Ui.Settings.IsAllowed(phone.Id, fingerprint))
            {
                // Before the tray is up there is nobody to ask: closed without
                // an answer, so the phone tries again.
                if (Ask is not { } ask)
                {
                    Log($"{name} over Wi-Fi from {who}: closed, nobody to ask yet");
                    return null;
                }
                if (PairingProblem != null)
                    return Refuse(net, name, Wire.Denied, "pairing is paused after abandoned pairings");
                byte[] commitment = body[(pairAt + 1)..(pairAt + Wire.PairFields)];
                string key = phone.Id + Convert.ToHexString(commitment);
                byte[] hubNonce = NonceFor(key);
                Log($"{name} over Wi-Fi from {who}: pairing, {(phoneAsks ? "the phone holds no pin" : "not allowed here")}");
                SendWelcome(net, Wire.Version, Wire.Pending, [.. hubNonce, .. Encoding.UTF8.GetBytes(Dns.GetHostName())]);
                // The phone reveals its nonce at once. One that does not has
                // seen the code this PC would show and may try again with a
                // new commitment until it matches the code on the phone it
                // sits in front of: so only a few, then no pairing until
                // the hub starts again.
                byte[] phoneNonce = new byte[Wire.NonceSize];
                bool revealed;
                try
                {
                    net.ReadExactly(phoneNonce);
                    revealed = SHA256.HashData(phoneNonce).AsSpan().SequenceEqual(commitment);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException)
                {
                    revealed = false;
                }
                if (!revealed)
                {
                    lock (nonces)
                    {
                        nonces.Remove(key);
                        if (++abandoned >= MaxAbandoned)
                            PairingProblem = "Wi-Fi pairing is paused: something on the network started pairing and " +
                                             "gave up several times. Restart Exo to pair again.";
                    }
                    Log($"{name} pairing ABANDONED ({abandoned} since the last answer)");
                    return null;
                }
                string code = Pairing.Code(Pairing.Certificate, phoneCert, phoneNonce, hubNonce);
                Log($"{name} code {code}, asking the PC's user");
                bool yes = ask(phone, code);
                lock (nonces)
                {
                    nonces.Remove(key);
                    abandoned = 0;
                }
                if (!yes)
                    return Refuse(net, name, Wire.Denied, "the PC's user did not allow this phone");
                Ui.Settings.Allow(phone.Id, fingerprint, phone.Name);
                Log($"{name} allowed by the PC's user, certificate {fingerprint[..16]}");
            }
        }

        var conn = new Connection(s, phone, role, rotation, mode, serial) { Format = format };
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
        // Before WELCOME, so the camera offers this format by the time the
        // phone's frames arrive. Nothing happens when it already does.
        if (role == Wire.RoleCamera)
            vcam.Use(format);

        Span<byte> session = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(session, SessionId);
        SendWelcome(net, version, Wire.Accepted, session);
        Log($"{name} accepted: protocol {version} (device {oldest} to {newest}), role {roleName}, " +
            (role == Wire.RoleDisplay ? $"{mode}" : $"{format}, rotation {rotation * 90}") + $", session {SessionId:x16}, " +
            (overWifi ? $"Wi-Fi from {who}" : $"USB {serial}"));
        return conn;
    }

    // The hub's nonce for one pairing, by the phone's identity and
    // commitment: the phone keeps its nonce for the whole pairing, so a
    // second role or a retry gets the same nonce here and shows the same
    // code. Dropped once the PC's user answers, or the pairing is abandoned.
    readonly Dictionary<string, byte[]> nonces = new();
    const int MaxAbandoned = 5;
    int abandoned;  // under nonces: pairings abandoned since the last answer
    // For the window: set once pairing paused, until the hub restarts.
    public volatile string PairingProblem;

    byte[] NonceFor(string key)
    {
        lock (nonces)
        {
            if (!nonces.TryGetValue(key, out var n))
                nonces[key] = n = RandomNumberGenerator.GetBytes(Wire.NonceSize);
            return n;
        }
    }

    Connection Refuse(Stream net, string who, byte status, string reason)
    {
        Log($"{who} REFUSED status {status}: {reason}");
        SendWelcome(net, Wire.Version, status, Encoding.UTF8.GetBytes(reason));
        return null;
    }

    // Also L7's WELCOME, which has the same layout at its own version.
    internal static void SendWelcome(Stream net, ushort version, byte status, ReadOnlySpan<byte> body)
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
    void ReadMessages(Stream net, Connection conn)
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
            if (type == Wire.TypeAck && length == 4)
            {
                sender.Ack(net, BinaryPrimitives.ReadUInt32BigEndian(body));
                continue;
            }
            if (type == Wire.TypePause && length == 0)
            {
                sender.Pause(net);
                continue;
            }
            if (type == Wire.TypeResume && length == 0)
            {
                sender.Resume(net);
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
    void ReadCamera(Stream net, Connection conn)
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
            Volatile.Write(ref conn.Frames, total);
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
        lock (usb)
        {
            foreach (var l in usb.Values)
                l.Stop();
            usb.Clear();
        }
        wifi.Stop();
        lock (gate)
        {
            display?.Socket.Close();
            cameraConn?.Socket.Close();
        }
    }
}
