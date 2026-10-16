// Answers each phone's discovery QUERY on the LAN with the Wi-Fi listener's
// port, the session and this PC's name, as SuperDisplay's phone finds its
// PC. The answer goes back to the query's
// sender, unicast, so the phone needs no multicast lock. Layout in Wire.cs.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExoHub.Link;

sealed class Discovery : IDisposable
{
    readonly UdpClient udp;
    readonly Stopwatch clock;
    readonly byte[] answer;

    // Null when the port is taken, by another hub in another session or
    // anything else: Wi-Fi then reaches this hub by no means, and the window
    // says why.
    public static Discovery Start(Stopwatch clock, int tcpPort, ulong session, out string problem)
    {
        try
        {
            var d = new Discovery(clock, tcpPort, session);
            problem = null;
            return d;
        }
        catch (SocketException ex)
        {
            problem = $"UDP port {Wire.DiscoveryPort} is in use ({ex.SocketErrorCode})";
            Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s discovery off: {problem}");
            return null;
        }
    }

    Discovery(Stopwatch clock, int tcpPort, ulong session)
    {
        this.clock = clock;
        byte[] name = Encoding.UTF8.GetBytes(Dns.GetHostName());
        if (name.Length > Wire.MaxName)
            name = name[..Wire.MaxName];
        answer = new byte[4 + 2 + 1 + 2 + 8 + 1 + name.Length + 2];
        var a = answer.AsSpan();
        Wire.Magic.CopyTo(a);
        BinaryPrimitives.WriteUInt16BigEndian(a[4..], Wire.DiscoveryVersion);
        a[6] = Wire.KindAnswer;
        BinaryPrimitives.WriteUInt16BigEndian(a[7..], (ushort)tcpPort);
        BinaryPrimitives.WriteUInt64BigEndian(a[9..], session);
        a[17] = (byte)name.Length;
        name.CopyTo(a[18..]);
        BinaryPrimitives.WriteUInt16BigEndian(a[(18 + name.Length)..], Wire.Version);

        // Exclusive, so a second hub fails here rather than sharing queries.
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try
        {
            s.Bind(new IPEndPoint(IPAddress.Any, Wire.DiscoveryPort));
        }
        catch
        {
            s.Dispose();
            throw;
        }
        udp = new UdpClient { Client = s };
        Log($"answering on UDP {Wire.DiscoveryPort} as \"{Dns.GetHostName()}\", TCP {tcpPort}");
        new Thread(Loop) { IsBackground = true, Name = "discovery" }.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s discovery {line}");

    void Loop()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        string lastFrom = null;
        while (true)
        {
            byte[] q;
            try
            {
                q = udp.Receive(ref from);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A reset for an earlier answer whose port was gone (ICMP
                // unreachable); nothing to do but read on.
                continue;
            }
            if (q.Length < 7 || !q.AsSpan(0, 4).SequenceEqual(Wire.Magic) ||
                BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)) != Wire.DiscoveryVersion || q[6] != Wire.KindQuery)
                continue;
            try
            {
                udp.Send(answer, from);
            }
            catch (SocketException ex)
            {
                Log($"answer to {from} failed: {ex.SocketErrorCode}");
                continue;
            }
            // A phone asks every second while it looks, so each new asker
            // is logged once.
            string who = from.Address.ToString();
            if (who != lastFrom)
                Log($"answered {from}");
            lastFrom = who;
        }
    }

    public void Dispose() => udp.Dispose();
}
