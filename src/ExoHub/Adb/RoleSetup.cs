// One device's roles, display, camera or both, as the user chose with
// --display and --camera until a roles UI exists (DECISIONS 5.8, PROJECT_SPEC
// FR-4.4). Each time that device reaches `device`, sets up the reverse to
// the hub's port and launches ExoPhone once with its serial and every role it
// holds. For the camera it first grants the camera permission over adb, so
// nothing asks on the phone. The handshakes that follow, one connection per
// role, are DeviceListener's.
//
// One launch per device, not per role: each launch force-stops the app with
// -S, so a second launch would end the role the first one started
// (DECISIONS 10 item 4, D2b).

using System.Diagnostics;
using System.Net.Sockets;

namespace ExoHub.Adb;

sealed class RoleSetup
{
    const int RetryMs = 1000;
    const string Package = "exo.phone";
    const string Activity = Package + "/.MainActivity";

    readonly string serial;
    readonly bool camera;
    readonly int port;
    readonly Stopwatch clock;
    readonly string roles;  // for the log and the launch: "display", "camera" or "camera,display"

    // Bumped on every state change of the chosen device. A setup checks it
    // before each step, so one for a device that has since changed or left
    // stops instead of acting on a stale transport.
    int generation;

    public RoleSetup(string serial, bool display, bool camera, int port, Stopwatch clock)
    {
        this.serial = serial;
        this.camera = camera;
        this.port = port;
        this.clock = clock;
        roles = display && camera ? "camera,display" : camera ? "camera" : "display";
    }

    void Log(string line) =>
        Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s role {roles} {line}");

    // The tracker's callback, on its thread. Never blocks it.
    public void OnState(string who, string state)
    {
        if (who != serial)
            return;
        int g = Interlocked.Increment(ref generation);
        if (state == "device")
            Task.Run(() => SetUp(g));
    }

    // Reverse first, then launch (DECISIONS 5.8). Retried every second while
    // the device stays in `device`, forever.
    void SetUp(int g)
    {
        for (int attempt = 1; Volatile.Read(ref generation) == g; attempt++)
        {
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                string reply = AdbWire.Reverse(serial, "localabstract:exo", $"tcp:{port}");
                Log($"{serial} reverse localabstract:exo -> tcp:{port} set in " +
                    $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1}ms, reply {reply}");

                if (Volatile.Read(ref generation) != g)
                    return;
                if (camera)
                {
                    // Silent on success; anything printed is the failure. The
                    // notification lets CameraService's ongoing notice show.
                    foreach (string p in new[] { "CAMERA", "POST_NOTIFICATIONS" })
                    {
                        string granted = AdbWire.Shell(serial, $"pm grant {Package} android.permission.{p}").Trim();
                        Log($"{serial} {p} granted{(granted == "" ? "" : ": " + granted.Replace("\n", " | "))}");
                    }
                }

                if (Volatile.Read(ref generation) != g)
                    return;
                // -S force-stops a running copy, so the app always holds a
                // serial this hub supplied through this transport (5.7).
                // Without a role extra the app takes the display role.
                t0 = Stopwatch.GetTimestamp();
                string extra = camera ? $" --es role {roles}" : "";
                string output = AdbWire.Shell(serial, $"am start -S -n {Activity} --es serial {serial}{extra}").Trim();
                Log($"{serial} launch returned in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1}ms: " +
                    output.Replace("\n", " | "));
                if (output.Contains("Error"))
                    throw new AdbFailException("am start reported an error");
                return;
            }
            catch (Exception ex) when (ex is AdbFailException or IOException or SocketException or InvalidDataException)
            {
                Log($"{serial} setup attempt {attempt} failed: {ex.Message}; retry in {RetryMs}ms");
                Thread.Sleep(RetryMs);
            }
        }
    }
}
