// Each time any device reaches `device`, sets up the reverse from its
// localabstract:exo to the hub's port (DECISIONS 5.8), so the app on it can
// connect whenever its user picks a role. Nothing is launched or granted:
// the role is picked on the phone, and the app asks for its own
// permissions. The handshakes that follow are DeviceListener's.

using System.Diagnostics;
using System.Net.Sockets;

namespace ExoHub.Adb;

sealed class ReverseSetup(int port, Stopwatch clock)
{
    const int RetryMs = 1000;

    // Per serial, bumped on every state change of that device. A setup
    // checks it before each attempt, so one for a device that has since
    // changed or left stops instead of acting on a stale transport.
    readonly Dictionary<string, int> generations = new();  // under itself

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s reverse {line}");

    // The tracker's callback, on its thread. Never blocks it.
    public void OnState(string serial, string state)
    {
        int g;
        lock (generations)
            generations[serial] = g = generations.GetValueOrDefault(serial) + 1;
        if (state == "device")
            Task.Run(() => SetUp(serial, g));
    }

    bool Current(string serial, int g)
    {
        lock (generations)
            return generations[serial] == g;
    }

    // Retried every second while the device stays in `device`, forever.
    void SetUp(string serial, int g)
    {
        for (int attempt = 1; Current(serial, g); attempt++)
        {
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                string reply = AdbWire.Reverse(serial, "localabstract:exo", $"tcp:{port}");
                Log($"{serial} localabstract:exo -> tcp:{port} set in " +
                    $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1}ms, reply {reply}");
                return;
            }
            catch (Exception ex) when (ex is AdbFailException or IOException or SocketException or InvalidDataException)
            {
                Log($"{serial} attempt {attempt} failed: {ex.Message}; retry in {RetryMs}ms");
                Thread.Sleep(RetryMs);
            }
        }
    }
}
