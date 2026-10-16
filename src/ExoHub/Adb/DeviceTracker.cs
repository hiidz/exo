// Holds host:track-devices on the hub's own adb server and turns each list
// into per-serial transitions. Never
// polls `adb devices`, never kill-server.
//
// The server sends the whole list on every change, and once at connect
// (AOSP adb transport.cpp, device_tracker_send). The tracker diffs each list
// against the last, so one device's change never touches another.
//
// When the server dies the held socket reads EOF. Every device then counts
// as departed, and the tracker starts the server again, forever, with
// backoff keyed to consecutive failed starts.

using System.Diagnostics;
using System.Net.Sockets;

namespace ExoHub.Adb;

sealed class DeviceTracker
{
    const int FirstRetryMs = 100;
    const int MaxRetryMs = 1000;

    readonly AdbServer server;
    readonly Stopwatch clock;
    // Called on the tracker thread with the serial and its new state, or
    // null for a departure.
    readonly Action<string, string> onState;
    volatile Dictionary<string, string> devices = new();

    // Set when tracking stops for good: a foreign server took 5037. adb
    // then stays off, and the window says why (AdbControl).
    public volatile string Fatal;

    // Each serial and its state, as of the last list. Replaced, never
    // changed, so a reader on another thread sees one whole list.
    public IReadOnlyDictionary<string, string> Devices => devices;

    volatile bool stopped;
    volatile NetworkStream held;  // the tracking socket, closed by Stop

    public DeviceTracker(AdbServer server, Stopwatch clock, Action<string, string> onState)
    {
        this.server = server;
        this.clock = clock;
        this.onState = onState;
        new Thread(Run) { IsBackground = true, Name = "adb-track" }.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s track {line}");

    void Run()
    {
        while (true)
        {
            try
            {
                Track();
            }
            catch (Exception ex) when (ex is IOException or SocketException or AdbFailException or InvalidDataException
                                           or ObjectDisposedException)
            {
                Log($"tracking ended: {ex.GetType().Name}: {ex.Message}");
            }
            if (stopped)
            {
                Apply(new Dictionary<string, string>());
                return;
            }

            // The socket ended, so the server has gone or is going.
            if (!server.Child.WaitForExit(1000))
                Log($"server pid {server.Child.Id} still running after the tracking socket ended; tracking again");
            else
            {
                server.Child.WaitForExit();  // drain its last output
                Log($"SERVER DIED: pid {server.Child.Id} exit {server.Child.ExitCode}, last line: {server.LastLine}");
                Apply(new Dictionary<string, string>());
                if (!Restart())
                    return;
            }
        }
    }

    // Ends tracking and, through the departures it reports, every reverse
    // setup still retrying. The server is the caller's to end.
    public void Stop()
    {
        stopped = true;
        held?.Dispose();
    }

    // One held socket, from request to EOF.
    void Track()
    {
        using var net = AdbWire.Connect();
        held = net;
        if (stopped)
            return;
        AdbWire.Send(net, "host:track-devices");
        AdbWire.ReadStatus(net);
        Log("holding host:track-devices");
        while (true)
        {
            string list = AdbWire.ReadHexString(net);
            if (list == null)
            {
                Log("server closed the tracking socket");
                return;
            }
            // Raw, escaped, so the documented format is checked against
            // what this server actually sends.
            Log($"list \"{list.Replace("\t", "\\t").Replace("\n", "\\n")}\"");
            var next = new Dictionary<string, string>();
            foreach (string line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.IndexOf('\t');
                if (tab < 0)
                    throw new InvalidDataException($"device line without a tab: \"{line}\"");
                next[line[..tab]] = line[(tab + 1)..];
            }
            Apply(next);
        }
    }

    // Logs and reports every difference between the last list and this one.
    void Apply(Dictionary<string, string> next)
    {
        foreach (var (serial, state) in next)
        {
            if (!devices.TryGetValue(serial, out string was))
                Log($"{serial} ARRIVED {state}");
            else if (was != state)
                Log($"{serial} {was} -> {state}");
            else
                continue;
            if (state == "unauthorized")
                Log($"{serial} UNAUTHORIZED: accept the USB debugging prompt on the device");
            onState(serial, state);
        }
        foreach (string serial in devices.Keys.Except(next.Keys))
        {
            Log($"{serial} DEPARTED (was {devices[serial]})");
            onState(serial, null);
        }
        devices = next;
    }

    // Starts the server again. False when tracking must end: stopped, or a
    // foreign server took 5037.
    bool Restart()
    {
        for (int failed = 0; ; failed++)
        {
            if (stopped)
                return false;
            if (failed > 0)
                Thread.Sleep(Math.Min(FirstRetryMs << Math.Min(failed - 1, 10), MaxRetryMs));
            string refused = server.CheckForeign();
            if (refused == null)
            {
                refused = server.Start();
                if (refused == null && stopped)
                    server.Child.Kill();  // Stop ended the one before it
                if (refused == null)
                    return !stopped;
                if (!refused.StartsWith("FOREIGN"))
                {
                    Log($"restart {failed + 1} failed: {refused}");
                    continue;
                }
            }
            Fatal = refused;
            return false;
        }
    }
}
