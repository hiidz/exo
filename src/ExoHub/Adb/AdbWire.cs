// The adb host protocol on TCP 5037, client side (DECISIONS 5.8). Every
// request is a 4 digit hex length then ASCII; the reply is OKAY, or FAIL
// then a hex-length reason (AOSP adb docs/dev/overview.md). One connection
// per request, as adb's own client does.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExoHub.Adb;

// A FAIL from the server, with its reason as the message.
sealed class AdbFailException(string reason) : Exception(reason);

static class AdbWire
{
    public const int ServerPort = 5037;

    // SIO_TCP_INITIAL_RTO, with TCP_INITIAL_RTO_PARAMETERS { Rtt unspecified
    // (0xFFFF), MaxSynRetransmissions none (0xFE) } as its 4 byte input.
    // Windows retransmits a SYN answered by a reset before it reports the
    // refusal: measured 2062ms against a free loopback port, paid on every
    // probe at startup and on every server restart.
    const int SIO_TCP_INITIAL_RTO = unchecked((int)0x98000011);
    static readonly byte[] NoSynRetransmissions = [0xFF, 0xFF, 0xFE, 0x00];

    // Throws SocketException (ConnectionRefused) when nothing listens.
    public static NetworkStream Connect()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            s.IOControl(SIO_TCP_INITIAL_RTO, NoSynRetransmissions, null);
            s.Connect(IPAddress.Loopback, ServerPort);
        }
        catch
        {
            s.Dispose();
            throw;
        }
        return new NetworkStream(s, ownsSocket: true);
    }

    public static void Send(Stream net, string request)
    {
        byte[] payload = Encoding.ASCII.GetBytes(request);
        net.Write(Encoding.ASCII.GetBytes(payload.Length.ToString("x4")));
        net.Write(payload);
    }

    // Returns normally on OKAY. Throws AdbFailException on FAIL, and
    // InvalidDataException on anything else, with the raw bytes.
    public static void ReadStatus(Stream net)
    {
        Span<byte> status = stackalloc byte[4];
        net.ReadExactly(status);
        string s = Encoding.ASCII.GetString(status);
        if (s == "OKAY")
            return;
        if (s == "FAIL")
            throw new AdbFailException(ReadHexString(net) ?? "(no reason)");
        throw new InvalidDataException($"adb status neither OKAY nor FAIL: {Convert.ToHexString(status)}");
    }

    // A hex length then that many bytes. Null on a clean EOF before the
    // length, which is how a held socket reports the server going away.
    public static string ReadHexString(Stream net)
    {
        Span<byte> hex = stackalloc byte[4];
        int got = 0;
        while (got < 4)
        {
            int n = net.Read(hex[got..]);
            if (n == 0)
                return got == 0 ? null : throw new EndOfStreamException("adb closed inside a length prefix");
            got += n;
        }
        int length = Convert.ToInt32(Encoding.ASCII.GetString(hex), 16);
        byte[] body = new byte[length];
        net.ReadExactly(body);
        return Encoding.ASCII.GetString(body);
    }

    // host:version. The server answers its protocol version as 4 hex
    // digits, "0029" for 41 (adb.cpp handle_host_request, "%04x").
    public static int Version()
    {
        using var net = Connect();
        Send(net, "host:version");
        ReadStatus(net);
        return Convert.ToInt32(ReadHexString(net), 16);
    }

    // A connection switched to one device's adbd (host:transport, services.md).
    static NetworkStream ConnectTo(string serial)
    {
        var net = Connect();
        try
        {
            Send(net, $"host:transport:{serial}");
            ReadStatus(net);
            return net;
        }
        catch
        {
            net.Dispose();
            throw;
        }
    }

    // reverse:forward, plain rather than norebind, so a stale mapping for
    // the same name is replaced. Returns the raw reply after the request,
    // escaped, so DECISIONS 5.8's "two statuses" is checked on the wire.
    // Throws AdbFailException on a FAIL in either place.
    public static string Reverse(string serial, string device, string host)
    {
        using var net = ConnectTo(serial);
        net.ReadTimeout = 5000;
        Send(net, $"reverse:forward:{device};{host}");
        byte[] raw = ReadToEnd(net);
        string text = Encoding.ASCII.GetString(raw);
        string shown = Convert.ToHexString(raw) + $" \"{text}\"";
        // Expected OKAY (service opened) then OKAY or FAIL (the forward).
        if (text.StartsWith("OKAYOKAY"))
            return shown;
        if (text.Contains("FAIL"))
            throw new AdbFailException($"reverse failed, reply {shown}");
        throw new InvalidDataException($"reverse reply not understood: {shown}");
    }

    // shell:, the v1 form, which has no exit code (services.md). Returns
    // the command's output; the device closes the stream when it ends.
    public static string Shell(string serial, string command)
    {
        using var net = ConnectTo(serial);
        net.ReadTimeout = 10000;
        Send(net, $"shell:{command}");
        ReadStatus(net);
        return Encoding.UTF8.GetString(ReadToEnd(net));
    }

    static byte[] ReadToEnd(Stream net)
    {
        using var all = new MemoryStream();
        net.CopyTo(all);
        return all.ToArray();
    }

    // Version if anything answers on 5037, null if nothing listens.
    public static int? Probe()
    {
        try
        {
            return Version();
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return null;
        }
    }
}
