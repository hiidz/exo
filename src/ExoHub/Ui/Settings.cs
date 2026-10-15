// The PC window's settings (PLAN.md, PC app), in HKCU: per user, written
// by the unelevated hub. Start with Windows is the hub's own Run value,
// written here rather than by an elevated installer, which would write the
// hive of whoever elevated (RESEARCH.md, Release and installer).

using Microsoft.Win32;

namespace ExoHub.Ui;

static class Settings
{
    const string Key = @"Software\Exo";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Exo";

    // At sign-in the hub starts in the tray, with no window.
    public const string TrayFlag = "--tray";

    public static bool StartWithWindows
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue(RunValue) != null;
        }
        set
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                run.SetValue(RunValue, $"\"{Environment.ProcessPath}\" {TrayFlag}");
            else
                run.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public static bool AdbEnabled
    {
        get => Read("AdbEnabled", 1) != 0;
        set => Write("AdbEnabled", value ? 1 : 0);
    }

    // Start with Windows is on from the first run. Only once, so turning it
    // off stays off.
    public static void FirstRun()
    {
        if (Read("Started", 0) != 0)
            return;
        StartWithWindows = true;
        Write("Started", 1);
    }

    static int Read(string name, int fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(name) is int v ? v : fallback;
    }

    static void Write(string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }
}
