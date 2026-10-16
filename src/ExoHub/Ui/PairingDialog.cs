// P5: the Wi-Fi pairing question. On top of everything, even with the
// window hidden in the tray. No is the default: Enter, Esc and Alt+F4 all
// answer No, and only Yes allows the phone.

using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ExoHub.Ui;

sealed class PairingDialog : Form
{
    readonly string title, code;
    readonly Icon small;
    readonly DialogButton yes, no;

    public PairingDialog(string phone, string code, Icon small)
    {
        title = $"Allow “{phone}” to connect to this PC over Wi-Fi?";
        this.code = code;
        this.small = small;
        Text = "Exo";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        // The body drags it as a caption would, so a double-click there
        // would otherwise maximize it.
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        yes = new DialogButton("Yes", primary: false) { DialogResult = DialogResult.Yes };
        no = new DialogButton("No", primary: true) { DialogResult = DialogResult.No };
        Controls.AddRange([yes, no]);
        AcceptButton = no;
        CancelButton = no;
        ActiveControl = no;
        Arrange();
        // After WinForms has applied Windows' suggested size, which runs
        // after this event, so the measured size is the one kept.
        DpiChanged += (_, _) => BeginInvoke(Arrange);
    }

    int Px(float v) => Theme.Px(v, DeviceDpi);
    int Inner => Px(440) - Px(48);
    Font Title => Theme.Font(18, DeviceDpi, true);
    Font Body => Theme.Font(14, DeviceDpi);
    Font Code => Theme.Mono(36, DeviceDpi);

    const string Line1 = "The phone shows a code. Allow only if it is";
    const string Line2 = "Once allowed, it connects without asking.";

    int BodyHeight => Px(22) + Px(16) + Px(12) + Theme.Height(title, Title, Inner) + Px(12) + Theme.Height(Line1, Body, Inner)
                      + Px(12) + TextRenderer.MeasureText(code, Code, Size.Empty, Theme.Line).Height
                      + Px(12) + Theme.Height(Line2, Body, Inner) + Px(22);

    void Arrange()
    {
        int bh = Px(32), footer = Px(18) * 2 + bh, ring = Px(4);
        ClientSize = new Size(Px(440), BodyHeight + 1 + footer);
        int w = (Inner - Px(8)) / 2, y = BodyHeight + 1 + Px(18);
        yes.Bounds = new Rectangle(Px(24) - ring, y - ring, w + ring * 2, bh + ring * 2);
        no.Bounds = new Rectangle(Px(24) + w + Px(8) - ring, y - ring, w + ring * 2, bh + ring * 2);
        yes.Margin = no.Margin = new Padding(ring);
        BackColor = Theme.P.Footer;
        Invalidate();
    }

    // Dragged by anywhere that is not a button, as there is no title bar.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x84 && m.Result == 1)  // WM_NCHITTEST, HTCLIENT
            m.Result = 2;  // HTCAPTION
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Popup(Handle);
    }

    // Shown from a hub that is not in front: Windows lets a window take
    // the foreground only from the thread that has it, so join that
    // thread's input for the call.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        IntPtr fg = GetForegroundWindow();
        uint them = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
        bool joined = them != me && AttachThreadInput(me, them, true);
        SetForegroundWindow(Handle);
        Activate();
        if (joined)
            AttachThreadInput(me, them, false);
        no.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.Clear(p.Footer);
        using (var b = new SolidBrush(p.Body))
            g.FillRectangle(b, 0, 0, Width, BodyHeight);
        using (var line = new Pen(p.Border))
            g.DrawLine(line, 0, BodyHeight, Width, BodyHeight);
        int x = Px(24), y = Px(22);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawIcon(small, new Rectangle(x, y, Px(16), Px(16)));
        var exo = Theme.Font(12, DeviceDpi);
        int eh = TextRenderer.MeasureText("Exo", exo, Size.Empty, Theme.Line).Height;
        TextRenderer.DrawText(g, "Exo", exo, new Point(x + Px(24), y + (Px(16) - eh) / 2), p.Secondary, Theme.Line);
        y += Px(16) + Px(12);
        y = Draw(g, title, Title, p.Text, x, y);
        y = Draw(g, Line1, Body, p.Text, x, y);
        // The code, spaced .14em a character.
        int cx = x, ch = TextRenderer.MeasureText(code, Code, Size.Empty, Theme.Line).Height;
        foreach (char c in code)
        {
            string s = c.ToString();
            TextRenderer.DrawText(g, s, Code, new Point(cx, y), p.Code, Theme.Line);
            cx += TextRenderer.MeasureText(g, s, Code, Size.Empty, Theme.Line).Width + (int)(Code.Size * .14f);
        }
        y += ch + Px(12);
        Draw(g, Line2, Body, p.Secondary, x, y);
    }

    int Draw(Graphics g, string text, Font f, Color c, int x, int y)
    {
        int h = Theme.Height(text, f, Inner);
        TextRenderer.DrawText(g, text, f, new Rectangle(x, y, Inner, h), c, Theme.Wrap);
        return y + h + Px(12);
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}

// A Windows 11 dialog button: filled with the accent when primary, else
// the card colour with a border; the focus ring outside it, so its bounds
// leave Margin all round for the ring.
sealed class DialogButton : Button
{
    readonly bool primary;

    public DialogButton(string text, bool primary)
    {
        Text = text;
        this.primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.Clear(p.Footer);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int m = Margin.Left, r = Theme.Px(4, DeviceDpi);
        var face = new RectangleF(m + 0.5f, m + 0.5f, Width - m * 2 - 1, Height - m * 2 - 1);
        using (var path = Theme.Round(face, r))
        {
            using var b = new SolidBrush(primary ? p.Accent : p.Card);
            g.FillPath(b, path);
            if (!primary)
            {
                using var pen = new Pen(p.ButtonBorder);
                g.DrawPath(pen, path);
            }
        }
        if (Focused)
        {
            float w = Theme.Px(2, DeviceDpi);
            using var ring = new Pen(p.Text, w);
            using var path = Theme.Round(new RectangleF(w / 2, w / 2, Width - w, Height - w), r + m / 2f);
            g.DrawPath(ring, path);
        }
        TextRenderer.DrawText(g, Text, Theme.Font(14, DeviceDpi), Rectangle.Round(face), primary ? p.OnAccent : p.Text,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
