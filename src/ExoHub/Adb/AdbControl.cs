// Exo's adb, on or off from the window's "Enable adb connections". Off ends
// tracking, then the hub's own server through its process handle, never
// kill-server, so 5037 is free for another adb. A foreign
// server on 5037 leaves it off, with the reason shown, until the user turns
// it on again.

using System.Diagnostics;

namespace ExoHub.Adb;

sealed class AdbControl(Stopwatch clock, Action<string, string> onState)
{
    static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    readonly object gate = new();
    AdbServer server;        // under gate, null while off
    DeviceTracker tracker;   // under gate
    string refused;          // why the last Start failed

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s adb {line}");

    // Null while adb runs; otherwise why it does not.
    public string Problem
    {
        get
        {
            lock (gate)
                return tracker?.Fatal ?? (server == null ? refused ?? "off" : null);
        }
    }

    // Every device adb lists, and its state. Empty while off.
    public IReadOnlyDictionary<string, string> Devices
    {
        get
        {
            lock (gate)
                return tracker?.Fatal == null ? tracker?.Devices ?? None : None;
        }
    }

    // Blocks for about 3s while the server comes up (AdbServer.Start).
    // Returns why it was refused, or null.
    public string Start()
    {
        lock (gate)
        {
            if (tracker?.Fatal != null)
                StopLocked();
            if (server != null)
                return null;
            var s = new AdbServer(clock);
            refused = s.CheckForeign() ?? s.Start();
            if (refused != null)
            {
                if (s.Child is { HasExited: false })
                    s.Child.Kill();
                Log($"REFUSED: {refused}");
                return refused;
            }
            server = s;
            tracker = new DeviceTracker(s, clock, onState);
            return null;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            refused = null;
            StopLocked();
        }
    }

    void StopLocked()
    {
        if (server == null)
            return;
        tracker.Stop();
        Process child = server.Child;
        if (!child.HasExited)
            child.Kill();
        child.WaitForExit();
        Log($"stopped: server pid {child.Id} exit {child.ExitCode}");
        server = null;
        tracker = null;
    }
}
