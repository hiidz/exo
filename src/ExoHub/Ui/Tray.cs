// The tray icon and the settings window: connected phones, each one's role
// and whether it works, and a few PC settings; the rest live on the phone.
// WinForms, on its own STA thread, so the capture
// loop keeps the main thread. Quit cancels the hub's stop token, and the
// hub's usual shutdown runs. Closing the window only hides it.
//
// No ApplicationConfiguration.Initialize or SetHighDpiMode: the manifest
// sets DPI awareness, and touch geometry depends on it (app.manifest).

using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using ExoHub.Link;

namespace ExoHub.Ui;

// What the window shows, read once a second on the UI thread.
sealed record HubState(List<DeviceListener.Link> Links, IReadOnlyDictionary<string, string> Usb, string AdbProblem,
                       string DisplayProblem, string CameraProblem, string WifiProblem, string PairingProblem);

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
    Icon small;
    SectionTitle phonesTitle, settingsTitle;
    PhoneTable phones;     // a row a phone; hidden while there is none
    Steps steps;           // the first-run steps, in the list's place
    ProblemLines problems; // only while there is one
    CardPanel settingsCard;
    CheckRow start, adb;
    Hyperlink displaySettings;
    string shown;          // the rows last put in the list, so a still list is left alone
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

    // One question per pairing, however many of its connections wait on it:
    // both roles, or a retry after the phone gave up waiting, each showing
    // the same code. Any thread; blocks until the PC's user answers.
    readonly Dictionary<(string, string), Task<bool>> asks = new();

    public bool Ask(Phone phone, string code)
    {
        Task<bool> answer;
        lock (asks)
        {
            if (!asks.TryGetValue((phone.Id, code), out answer))
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                asks[(phone.Id, code)] = answer = tcs.Task;
                form.BeginInvoke(() =>
                {
                    // Its own top-level window, on top of whatever has
                    // focus, since this one may be hidden in the tray.
                    using var d = new PairingDialog(phone.Name, code, small);
                    var r = d.ShowDialog();
                    lock (asks)
                        asks.Remove((phone.Id, code));
                    tcs.SetResult(r == DialogResult.Yes);
                });
            }
        }
        return answer.Result;
    }

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
        using var appIcon = LoadIcon("Exo.ico", SystemInformation.SmallIconSize);
        using var trayIcon = LoadIcon("Exo-tray.ico", SystemInformation.SmallIconSize);
        small = appIcon;
        BuildForm();
        var menu = new ContextMenuStrip { Items = { { "Open Exo", null, (_, _) => Open() }, { "Quit", null, (_, _) => quit() } } };
        // Bold, as the default item: what a left click does.
        menu.Items[0].Font = new Font(menu.Items[0].Font, FontStyle.Bold);
        icon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "Exo",
            Visible = true,
            ContextMenuStrip = menu,
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

    static Icon LoadIcon(string name, Size size)
    {
        using var s = typeof(Tray).Assembly.GetManifestResourceStream("ExoHub." + name);
        return new Icon(s, size);
    }

    void BuildForm()
    {
        form = new Form
        {
            Text = "Exo",
            Icon = LoadIcon("Exo.ico", SystemInformation.IconSize),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            // Arrange sizes everything, in the design's pixels at the
            // window's own DPI, again whenever that changes.
            AutoScaleMode = AutoScaleMode.None,
        };
        phonesTitle = new SectionTitle("Phones");
        settingsTitle = new SectionTitle("Settings");
        steps = new Steps();
        phones = new PhoneTable();
        problems = new ProblemLines();
        settingsCard = new CardPanel();
        start = new CheckRow("Start with Windows", Settings.StartWithWindows);
        start.CheckedChanged += (_, _) => Settings.StartWithWindows = start.Checked;
        adb = new CheckRow("Enable adb connections (USB)", Settings.AdbEnabled);
        adb.CheckedChanged += (_, _) =>
        {
            bool on = adb.Checked;
            Settings.AdbEnabled = on;
            adb.Busy = true;
            // Starting adb takes seconds, so not on this thread.
            Task.Run(() => setAdb(on)).ContinueWith(_ => form.BeginInvoke(() =>
            {
                adb.Busy = false;
                Refresh();
            }));
        };
        displaySettings = new Hyperlink("Windows display settings ↗");
        displaySettings.Clicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        settingsCard.Controls.AddRange([start, adb, displaySettings]);
        form.Controls.AddRange([phonesTitle, steps, phones, problems, settingsTitle, settingsCard]);
        ApplyTheme();
        // Now, so Show and Close can reach this thread before the window
        // was ever shown.
        _ = form.Handle;
        Theme.TitleBar(form.Handle);
        Arrange();
        form.DpiChanged += (_, _) => Arrange();
        // Light or dark follows Windows' app theme as it changes.
        SystemEvents.UserPreferenceChanged += (_, _) => form.BeginInvoke(() =>
        {
            if (Theme.Update())
                ApplyTheme();
        });
        // Every close hides it, whoever sent it: a WM_CLOSE from another
        // process (CloseMainWindow, TaskManagerClosing) once destroyed the
        // window, which Show and Close invoke on. Only signing out and the
        // hub's own exit close it; refusing a sign-out would hold up Windows.
        form.FormClosing += (_, e) =>
        {
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall)
                return;
            e.Cancel = true;
            form.Hide();
        };
    }

    void ApplyTheme()
    {
        var p = Theme.P;
        form.BackColor = p.Window;
        foreach (Control c in form.Controls)
            c.BackColor = p.Window;
        foreach (Control c in settingsCard.Controls)
            c.BackColor = p.Card;
        if (form.IsHandleCreated)
            Theme.TitleBar(form.Handle);
        form.Invalidate(true);
    }

    // Shown: the table or the steps, and the problem lines only while there
    // is one. Not Control.Visible, which is false while the window is hidden.
    bool listShown, problemsShown;

    // The blocks top to bottom: 4px above, 24 at the sides and below, 14
    // between; the settings card 16 inside, 14 between its rows.
    void Arrange()
    {
        int dpi = form.DeviceDpi;
        int Px(float v) => Theme.Px(v, dpi);
        int x = Px(24), w = Px(620) - Px(48), y = Px(4);
        phones.Visible = listShown;
        steps.Visible = !listShown;
        problems.Visible = problemsShown;
        var blocks = new List<Control> { phonesTitle, listShown ? phones : steps };
        if (problemsShown)
            blocks.Add(problems);
        blocks.Add(settingsTitle);
        foreach (var c in blocks)
        {
            c.Bounds = new Rectangle(x, y, w, ((Painted)c).Measure(w));
            y += c.Height + Px(14);
        }
        int cy = Px(16), cw = w - Px(32);
        foreach (var r in new[] { start, adb })
        {
            r.Bounds = new Rectangle(Px(16), cy, cw, r.Measure(cw));
            cy += r.Height + Px(14);
        }
        displaySettings.Bounds = new Rectangle(Px(16), cy, displaySettings.TextWidth, displaySettings.Measure(cw));
        cy += displaySettings.Height + Px(16);
        settingsCard.Bounds = new Rectangle(x, y, w, cy);
        form.ClientSize = new Size(Px(620), y + cy + Px(24));
        form.Invalidate(true);
    }

    void Open()
    {
        Refresh();
        form.Show();
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    // A role's or a row's state, as its dot shows it.
    const int Working = 0, Waiting = 1, Idle = 2;

    void Refresh()
    {
        HubState s = state();
        long now = Environment.TickCount64;
        // A row a phone, its roles together; then each USB device no
        // connection came through, by its serial.
        var rows = new List<(int Dot, string[] Cells)>();
        foreach (var g in s.Links.GroupBy(l => l.Phone.Id))
        {
            // Each role's state: paused while out of view on the phone,
            // working while its count moves, else waiting on frames.
            var ls = g.OrderBy(l => l.Camera).ToList();
            var states = new List<int>();
            foreach (var l in ls)
            {
                var key = (l.Phone.Id, l.Camera);
                if (!seen.TryGetValue(key, out var was) || was.Progress != l.Progress)
                    seen[key] = was = (l.Progress, now);
                states.Add(l.Paused ? Idle : now - was.At < WorkingMs && l.Progress > 0 ? Working : Waiting);
            }
            string Words(int st) => st switch { Working => "Working", Idle => "Paused", _ => "No frames yet" };
            rows.Add((states.Contains(Waiting) ? Waiting : states.Contains(Working) ? Working : Idle, [
                ls[0].Phone.Name,
                string.Join(", ", ls.Select(l => l.Camera ? "Webcam" : "Display")),
                string.Join(", ", ls.Select(l => l.Serial == null ? "Wi-Fi" : "USB").Distinct()),
                string.Join(", ", ls.Select(l => l.Detail)),
                states.Distinct().Count() == 1 ? Words(states[0])
                    : string.Join(", ", ls.Select((l, i) => (l.Camera ? "webcam " : "display ") + Words(states[i]).ToLowerInvariant())),
            ]));
        }
        foreach (var key in seen.Keys.Where(k => !s.Links.Any(l => (l.Phone.Id, l.Camera) == k)).ToList())
            seen.Remove(key);
        if (s.AdbProblem == null)
            foreach (var (serial, st) in s.Usb.Where(u => !s.Links.Any(l => l.Serial == u.Key)))
                rows.Add(st switch
                {
                    // Exo may be open on it over Wi-Fi, which no serial ties to.
                    "device" => (Idle, [serial, "", "USB", "", "No role over USB"]),
                    "unauthorized" => (Waiting, [serial, "", "USB", "", "Allow USB debugging on it"]),
                    _ => (Waiting, [serial, "", "USB", "", st]),
                });

        var lines = new List<string>();
        if (s.DisplayProblem != null)
            lines.Add($"Display unavailable: {s.DisplayProblem}");
        if (s.CameraProblem != null)
            lines.Add($"Webcam unavailable: {s.CameraProblem}");
        if (s.WifiProblem != null)
            lines.Add($"Wi-Fi unavailable: {s.WifiProblem}. Is another Exo running?");
        if (s.PairingProblem != null)
            lines.Add(s.PairingProblem);
        if (s.AdbProblem == "off")
            lines.Add("USB connections are off.");
        else if (s.AdbProblem?.StartsWith("FOREIGN") == true)
            lines.Add("USB connections are off: another adb is using port 5037 (Android Studio, for one). " +
                      "Close it, then turn adb connections off and on.");
        else if (s.AdbProblem != null)
            lines.Add($"USB connections are off: adb failed: {s.AdbProblem}");

        // Laid out again only when something shown changed.
        bool changed = problems.SetLines(lines) | listShown != rows.Count > 0 | problemsShown != lines.Count > 0;
        string rowsKey = string.Join("\n", rows.Select(r => r.Dot + "|" + string.Join("|", r.Cells)));
        if (rowsKey != shown)
        {
            shown = rowsKey;
            phones.SetRows(rows.Select(r => (r.Dot switch { Working => Theme.Working, Waiting => Theme.Waiting, _ => Theme.Off }, r.Cells)).ToList());
            changed = true;
        }
        listShown = rows.Count > 0;
        problemsShown = lines.Count > 0;
        if (changed)
            Arrange();
        icon.Text = s.Links.Count == 0 ? "Exo: no phone" : $"Exo: {string.Join(", ", s.Links.Select(l => l.Camera ? "webcam" : "display"))}";
    }
}
