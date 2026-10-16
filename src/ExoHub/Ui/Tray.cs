// The tray icon and the settings window (PLAN.md, PC app):
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
    ListView phones;   // a row a phone; hidden while there is none
    Label steps;       // the first-run steps, in the list's place
    Label problems;    // only while there is one
    string shown;      // the rows last put in the list, so a still list is left alone
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
                    // DefaultDesktopOnly: on top of whatever has focus, since
                    // the window may be hidden in the tray.
                    var r = MessageBox.Show($"Allow \"{phone.Name}\" to connect to this PC over Wi-Fi?\n\n" +
                                            $"The phone shows a code. Allow only if it is {code}.\n\n" +
                                            "Once allowed, it connects without asking.", "Exo",
                                            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                            MessageBoxDefaultButton.Button2, MessageBoxOptions.DefaultDesktopOnly);
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
        form.BackColor = SystemColors.Window;
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            WrapContents = false,
        };
        var heading = new Font(form.Font.FontFamily, form.Font.Size * 1.25f, FontStyle.Bold);
        Label Heading(string text, int top) =>
            new() { Text = text, AutoSize = true, Font = heading, Margin = new Padding(0, top, 3, 6) };

        // The list: one row a phone, its state as a coloured dot (PLAN.md,
        // Decide, PC app). Sized by the DPI-scaled font, not in pixels.
        int em = TextRenderer.MeasureText("M", form.Font).Width;
        var dots = new ImageList { ImageSize = new Size(em, em), ColorDepth = ColorDepth.Depth32Bit };
        foreach (var c in new[] { Color.FromArgb(61, 176, 96), Color.FromArgb(224, 160, 48), Color.Gray })
            dots.Images.Add(Dot(c, em));
        phones = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = dots,
            Size = new Size(em * 46, em * 7),
            Margin = new Padding(0, 0, 3, 6),
            BorderStyle = BorderStyle.FixedSingle,
        };
        foreach (var (name, width) in new[] { ("Phone", 10), ("Role", 8), ("Via", 4), ("Mode", 15), ("State", 9) })
            phones.Columns.Add(name, em * width);
        steps = new Label
        {
            AutoSize = true,
            MinimumSize = new Size(em * 46, 0),  // the list's width, so the window keeps its size
            Margin = new Padding(0, 0, 3, 6),
            Text = "No phone yet.\n\n" +
                   "1.  Install Exo on the phone.\n" +
                   "2.  USB: turn on USB debugging on the phone and plug it in.\n" +
                   "     Wi-Fi: join the same network as this PC.\n" +
                   "3.  Open Exo on the phone, then Use as display or Use as webcam.",
        };
        problems = new Label { AutoSize = true, MaximumSize = new Size(em * 46, 0), ForeColor = Color.FromArgb(176, 96, 0),
                               Margin = new Padding(0, 0, 3, 6) };

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
        panel.Controls.AddRange([Heading("Phones", 0), phones, steps, problems, Heading("Settings", 12), start, adb, link]);
        form.Controls.Add(panel);
        // Now, so Show and Close can reach this thread before the window
        // was ever shown.
        _ = form.Handle;
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

    static Bitmap Dot(Color c, int size)
    {
        var b = new Bitmap(size, size);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(c);
        int d = size * 3 / 5, at = (size - d) / 2;
        g.FillEllipse(brush, at, at, d, d);
        return b;
    }

    void Open()
    {
        Refresh();
        form.Show();
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    // The list's dots, in the ImageList's order.
    const int Working = 0, Waiting = 1, Idle = 2;

    void Refresh()
    {
        HubState s = state();
        long now = Environment.TickCount64;
        // A row a phone, its roles together; then each USB device no
        // connection came through, by its serial (PLAN.md, Decide, PC app).
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

        string rowsKey = string.Join("\n", rows.Select(r => r.Dot + "|" + string.Join("|", r.Cells)));
        if (rowsKey != shown)
        {
            shown = rowsKey;
            phones.BeginUpdate();
            phones.Items.Clear();
            foreach (var (dot, cells) in rows)
                phones.Items.Add(new ListViewItem(cells, dot));
            phones.EndUpdate();
        }
        phones.Visible = rows.Count > 0;
        steps.Visible = rows.Count == 0;
        string text = string.Join("\n", lines);
        if (problems.Text != text)
            problems.Text = text;
        problems.Visible = lines.Count > 0;
        icon.Text = s.Links.Count == 0 ? "Exo: no phone" : $"Exo: {string.Join(", ", s.Links.Select(l => l.Camera ? "webcam" : "display"))}";
    }
}
