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

    // PLAN.md, Wi-Fi as built in step 4: a phone allowed once over Wi-Fi
    // connects from then on, with the certificate it paired with; a deny is
    // not kept. Each allowed phone is a value under Paired, its identity,
    // holding its certificate's fingerprint (Pairing) and its name for
    // whoever reads the registry. Not Allowed, 2.0's list, made before TLS:
    // those phones pair again.
    public static bool IsAllowed(string id, string fingerprint)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key + @"\Paired");
        return k?.GetValue(id) is string[] v && v.Length > 0 && v[0] == fingerprint;
    }

    public static void Allow(string id, string fingerprint, string name)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key + @"\Paired");
        k.SetValue(id, new[] { fingerprint, name }, RegistryValueKind.MultiString);
    }

    // The hub's TLS certificate with its key, PKCS#12, null before the first
    // Wi-Fi start. Uninstall removes it with the rest, so phones pair again.
    public static byte[] Certificate
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("Certificate") as byte[];
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("Certificate", value, RegistryValueKind.Binary);
        }
    }

    public static bool AdbEnabled
    {
        get => Read("AdbEnabled", 1) != 0;
        set => Write("AdbEnabled", value ? 1 : 0);
    }

    // Start with Windows is on from the first run. Only once, so turning it
    // off stays off. When on, every start points it at this exe, so an
    // install in another folder does not leave it starting the old one.
    public static void FirstRun()
    {
        if (Read("Started", 0) != 0)
        {
            if (StartWithWindows)
                StartWithWindows = true;
            return;
        }
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
