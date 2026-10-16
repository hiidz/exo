// Starts and owns the pinned adb server: the adb shipped beside the hub,
// never one found on PATH.
//
// The server runs as `adb.exe -L tcp:localhost:5037 server nodaemon`, a
// direct child, rather than through adb's own fork-server launch, which
// detaches it on purpose. The hub's job object (Job.cs) holds it, so it
// dies with the hub however the hub ends. Never kill-server.

using System.Diagnostics;

namespace ExoHub.Adb;

sealed class AdbServer
{
    // The protocol version of the pinned binary, platform-tools 37.0.1. The
    // build refuses any other revision (ExoHub.csproj).
    public const int PinnedProtocol = 41;
    const int ReadyPollMs = 50;

    readonly Stopwatch clock;
    public Process Child { get; private set; }

    // The last line the server wrote, where its real error is.
    public string LastLine { get; private set; } = "";

    public AdbServer(Stopwatch clock) => this.clock = clock;

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s adb {line}");

    // Something already answering on 5037 is foreign whatever version it
    // reports: the hub has not started its own yet. Returns
    // the reason to refuse, or null when 5037 is free.
    public string CheckForeign()
    {
        long t0 = Stopwatch.GetTimestamp();
        int? foreign = AdbWire.Probe();
        Log($"probe of {AdbWire.ServerPort} took {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1}ms: " +
            (foreign == null ? "free" : $"answered protocol {foreign}"));
        return foreign == null ? null
            : $"FOREIGN adb server on {AdbWire.ServerPort}, protocol {foreign}; Exo does not share adb. " +
              "Stop it, then turn adb connections off and on in the window.";
    }

    // Spawns the server and returns once it answers host:version. Returns
    // the reason to refuse if what answers is not this hub's server.
    public string Start()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");
        var psi = new ProcessStartInfo(exe, $"-L tcp:localhost:{AdbWire.ServerPort} server nodaemon")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // No console, so Ctrl+C reaches only the hub, and the hub's
            // shutdown never sees its server die first.
            CreateNoWindow = true,
        };
        long t0 = Stopwatch.GetTimestamp();
        Child = Process.Start(psi);
        Child.ErrorDataReceived += (_, e) => Pump("stderr", e.Data);
        Child.OutputDataReceived += (_, e) => Pump("stdout", e.Data);
        Child.BeginErrorReadLine();
        Child.BeginOutputReadLine();
        Log($"started {exe} pid {Child.Id}, in hub job: {Job.Contains(Child)}");

        // nodaemon gives no ready signal, so this polls host:version, the
        // hub's one poll, bounded. The wait is on the child's process
        // handle, so a server that exits ends it at once.
        int attempts = 0;
        while (true)
        {
            attempts++;
            // Measured: the server accepts within about 0.1s of starting but
            // holds this answer about 3s, so one probe carries most of it.
            int? version = AdbWire.Probe();
            if (version != null)
            {
                // A nodaemon server that lost the bind exits, and a foreign
                // one keeps answering, so an answer with our child gone is
                // not ours.
                if (Child.HasExited)
                    return $"FOREIGN adb server answered protocol {version} on {AdbWire.ServerPort} while ours exited " +
                           $"{Child.ExitCode}: {LastLine}";
                if (version != PinnedProtocol)
                    return $"adb server pid {Child.Id} answered protocol {version}, pinned is {PinnedProtocol}";
                Log($"ready: protocol {version}, after {attempts} probes, " +
                    $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0}ms from start");
                return null;
            }
            if (Child.WaitForExit(ReadyPollMs))
            {
                Child.WaitForExit();  // without a timeout it also waits for the pumped output to drain
                return $"adb server exited {Child.ExitCode} before answering: {LastLine}";
            }
        }
    }

    void Pump(string stream, string line)
    {
        if (line == null)
            return;  // the stream closed
        LastLine = line;
        Log($"{stream}: {line}");
    }
}
