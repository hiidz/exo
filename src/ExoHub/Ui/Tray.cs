// The tray icon and the settings window (PLAN.md, Goal and PC app):
// connected phones, each one's role and whether it works, and only the
// settings PLAN.md lists. WinForms, on its own STA thread, so the capture
// loop keeps the main thread. Quit cancels the hub's stop token, and the
// hub's usual shutdown runs. Closing the window only hides it.
//
// No ApplicationConfiguration.Initialize or SetHighDpiMode: the manifest
// sets DPI awareness, and touch geometry depends on it (app.manifest).

using System.Drawing;
using System.Windows.Forms;
using ExoHub.Link;

namespace ExoHub.Ui;

// What the window shows, read once a second on the UI thread.
sealed record HubState(List<DeviceListener.Link> Links, IReadOnlyDictionary<string, string> Usb, string AdbProblem,
                       string DisplayProblem, string CameraProblem);

sealed class Tray
{
    // A role works while its count moved within this long. The display
    // phone acks the heartbeat's resend at least every 500ms.
    const int WorkingMs = 2000;

    readonly Func<HubState> state;
    readonly Action quit;
    readonly Func<bool, string> setAdb;  // returns why adb did not start, or null
    readonly ManualResetEventSlim ready = new();
    Form form;
    NotifyIcon icon;
    Label status;
    readonly Dictionary<(string, bool), (int Progress, long At)> seen = new();

    public Tray(Func<HubState> state, Action quit, Func<bool, string> setAdb, bool show)
    {
        this.state = state;
        this.quit = quit;
        this.setAdb = setAdb;
        var t = new Thread(() => Run(show)) { IsBackground = true, Name = "ui" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
    }

    public void Show() => form.BeginInvoke(Open);

    // From the hub's shutdown, so the icon leaves the tray with the hub.
    public void Close() => form.Invoke(() =>
    {
        icon.Visible = false;
        icon.Dispose();
        Application.ExitThread();
    });

    void Run(bool show)
    {
        // A failure here reaches the hub's handler and log, not WinForms'
        // own dialog, which would leave the hub running half broken.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var appIcon = LoadIcon(SystemInformation.SmallIconSize);
        BuildForm(appIcon);
        icon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "Exo",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip { Items = { { "Open Exo", null, (_, _) => Open() }, { "Quit", null, (_, _) => quit() } } },
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                Open();
        };
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
        if (show)
            Open();
        ready.Set();
        Application.Run();
    }

    static Icon LoadIcon(Size size)
    {
        using var s = typeof(Tray).Assembly.GetManifestResourceStream("ExoHub.Exo.ico");
        return new Icon(s, size);
    }

    void BuildForm(Icon small)
    {
        form = new Form
        {
            Text = "Exo",
            Icon = LoadIcon(SystemInformation.IconSize),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = SystemFonts.MessageBoxFont,
        };
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            WrapContents = false,
        };
        status = new Label { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(3, 3, 3, 12) };
        var start = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = Settings.StartWithWindows };
        start.CheckedChanged += (_, _) => Settings.StartWithWindows = start.Checked;
        var adb = new CheckBox { Text = "Enable adb connections (USB)", AutoSize = true, Checked = Settings.AdbEnabled };
        adb.CheckedChanged += (_, _) =>
        {
            bool on = adb.Checked;
            Settings.AdbEnabled = on;
            adb.Enabled = false;
            // Starting adb takes seconds, so not on this thread.
            Task.Run(() => setAdb(on)).ContinueWith(_ => form.BeginInvoke(() =>
            {
                adb.Enabled = true;
                Refresh();
            }));
        };
        var link = new LinkLabel { Text = "Windows display settings", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
        link.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        panel.Controls.AddRange([status, start, adb, link]);
        form.Controls.Add(panel);
        // Now, so Show and Close can reach this thread before the window
        // was ever shown.
        _ = form.Handle;
        form.FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing)
                return;
            e.Cancel = true;
            form.Hide();
        };
    }

    void Open()
    {
        Refresh();
        form.Show();
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    void Refresh()
    {
        HubState s = state();
        long now = Environment.TickCount64;
        var lines = new List<string>();
        foreach (var l in s.Links)
        {
            var key = (l.Phone.Id, l.Camera);
            if (!seen.TryGetValue(key, out var was) || was.Progress != l.Progress)
                seen[key] = was = (l.Progress, now);
            bool works = now - was.At < WorkingMs && l.Progress > 0;
            lines.Add($"{l.Phone.Name}: {(l.Camera ? "webcam" : "display")}, {l.Detail}, " +
                      (works ? "working" : "connected, no frames yet"));
        }
        foreach (var key in seen.Keys.Where(k => !s.Links.Any(l => (l.Phone.Id, l.Camera) == k)).ToList())
            seen.Remove(key);
        if (s.Links.Count == 0)
            lines.Add("No phone connected. Open Exo on the phone and pick a role.");
        if (s.DisplayProblem != null)
            lines.Add($"Display unavailable: {s.DisplayProblem}");
        if (s.CameraProblem != null)
            lines.Add($"Webcam unavailable: {s.CameraProblem}");

        lines.Add("");
        if (s.AdbProblem == null)
        {
            if (s.Usb.Count == 0)
                lines.Add("USB: no phone. Plug one in with USB debugging on.");
            foreach (var (serial, st) in s.Usb)
                lines.Add($"USB {serial}: " + st switch
                {
                    "device" => "ready",
                    "unauthorized" => "allow USB debugging in the prompt on the phone",
                    _ => st,
                });
        }
        else if (s.AdbProblem == "off")
            lines.Add("USB connections are off.");
        else if (s.AdbProblem.StartsWith("FOREIGN"))
            lines.Add("USB connections are off: another adb is using port 5037 (Android Studio, for one). " +
                      "Close it, then turn adb connections off and on.");
        else
            lines.Add($"USB connections are off: adb failed: {s.AdbProblem}");

        string text = string.Join("\n", lines);
        if (status.Text != text)
            status.Text = text;
        icon.Text = s.Links.Count == 0 ? "Exo: no phone" : $"Exo: {string.Join(", ", s.Links.Select(l => l.Camera ? "webcam" : "display"))}";
    }
}
