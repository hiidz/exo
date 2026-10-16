// The window's parts, each painted to the design: section titles, the
// first-run steps, the phone table, the problem lines, and the settings
// card with its checkboxes and link. Each says how tall it is at a width
// (Measure), and the window stacks them (Tray.Arrange). Text wraps, never
// cut.

using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Drawing2D;

namespace ExoHub.Ui;

abstract class Painted : Control
{
    protected Painted()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    protected int Px(float v) => Theme.Px(v, DeviceDpi);

    public abstract int Measure(int width);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Draw(e.Graphics);
    }

    protected abstract void Draw(Graphics g);
}

sealed class SectionTitle : Painted
{
    public SectionTitle(string text) => Text = text;

    public override int Measure(int width) => Px(6) + Theme.Height(Text, Theme.Font(15, DeviceDpi, true), width);

    protected override void Draw(Graphics g) =>
        TextRenderer.DrawText(g, Text, Theme.Font(15, DeviceDpi, true), new Point(0, Px(6)), Theme.P.Text, Theme.Line);
}

// P1: the first-run steps, in a card, in the phone table's place.
sealed class Steps : Painted
{
    // Each step: its lines, each an optional bold lead and the rest.
    static readonly (string Bold, string Tail)[][] steps =
    [
        [(null, "Install Exo on the phone.")],
        [("USB:", " turn on USB debugging on the phone and plug it in."), ("Wi-Fi:", " join the same network as this PC.")],
        [(null, "Open Exo on the phone, then Use as display or Use as webcam.")],
    ];

    Font Body => Theme.Font(14, DeviceDpi);
    Font Bold => Theme.Font(14, DeviceDpi, true);
    int TextWidth(int width) => width - Px(40) - Px(34);

    int LineHeight(string text, int width) => Math.Max(Px(20), Theme.Height(text, Body, width));

    public override int Measure(int width)
    {
        int h = Px(18) + Theme.Height("No phone yet.", Bold, width - Px(40));
        foreach (var step in steps)
            h += Px(12) + Math.Max(Px(22), step.Sum(l => LineHeight(l.Bold + l.Tail, TextWidth(width))));
        return h + Px(18);
    }

    protected override void Draw(Graphics g)
    {
        Theme.Card(g, ClientRectangle, DeviceDpi, Theme.P.Card);
        int x = Px(20), y = Px(18), w = TextWidth(Width);
        TextRenderer.DrawText(g, "No phone yet.", Bold, new Point(x, y), Theme.P.Text, Theme.Line);
        y += Theme.Height("No phone yet.", Bold, Width - Px(40));
        int n = 0;
        foreach (var step in steps)
        {
            y += Px(12);
            // The number in a circle the card's border colour.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.P.Border))
                g.FillEllipse(b, x, y, Px(22), Px(22));
            TextRenderer.DrawText(g, (++n).ToString(), Theme.Font(12, DeviceDpi, true), new Rectangle(x, y, Px(22), Px(22)),
                                  Theme.P.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int ly = y, tx = x + Px(34);
            foreach (var (bold, rest) in step)
            {
                int lead = 0;
                if (bold != null)
                {
                    TextRenderer.DrawText(g, bold, Bold, new Point(tx, ly), Theme.P.Text, Theme.Line);
                    lead = TextRenderer.MeasureText(g, bold, Bold, Size.Empty, Theme.Line).Width;
                }
                var r = new Rectangle(tx + lead, ly, w - lead, int.MaxValue / 2);
                TextRenderer.DrawText(g, rest, Body, r, Theme.P.Text, Theme.Wrap);
                ly += LineHeight(bold + rest, w);
            }
            y = Math.Max(y + Px(22), ly);
        }
    }
}

// P2: a row a phone. Columns dot, Phone, Role, Via, Mode, State, as
// 16px, then 1.1, .9, .6, 1.3 and 1.3 parts of the rest.
sealed class PhoneTable : Painted
{
    static readonly string[] heads = ["Phone", "Role", "Via", "Mode", "State"];
    static readonly float[] parts = [1.1f, .9f, .6f, 1.3f, 1.3f];
    List<(Color Dot, string[] Cells)> rows = [];

    public void SetRows(List<(Color Dot, string[] Cells)> r)
    {
        rows = r;
        Invalidate();
    }

    Font Head => Theme.Font(12, DeviceDpi);
    Font Body => Theme.Font(13, DeviceDpi);
    Font Strong => Theme.Font(13, DeviceDpi, true);

    // Each text column's left edge and width.
    (int X, int W)[] Columns(int width)
    {
        int gap = Px(10), x = Px(14) + Px(16) + gap;
        float fr = (width - Px(28) - Px(16) - gap * 5) / parts.Sum();
        var cols = new (int, int)[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int w = (int)(parts[i] * fr);
            cols[i] = (x, w);
            x += w + gap;
        }
        return cols;
    }

    static string Cell(string s) => string.IsNullOrEmpty(s) ? "—" : s;

    int HeaderHeight => Px(16) + TextRenderer.MeasureText("Phone", Head, Size.Empty, Theme.Line).Height;

    int RowHeight(string[] cells, (int X, int W)[] cols) =>
        Px(22) + cells.Select((c, i) => Theme.Height(Cell(c), i == 0 ? Strong : Body, cols[i].W)).Max();

    public override int Measure(int width)
    {
        var cols = Columns(width);
        return HeaderHeight + 1 + rows.Sum(r => RowHeight(r.Cells, cols) + 1);
    }

    protected override void Draw(Graphics g)
    {
        var p = Theme.P;
        var cols = Columns(Width);
        Theme.Card(g, ClientRectangle, DeviceDpi, p.Card);
        // The header's band, inside the card's rounded edge.
        using (var clip = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Px(6)))
        {
            g.SetClip(clip);
            using var b = new SolidBrush(p.Header);
            g.FillRectangle(b, 1, 1, Width - 2, HeaderHeight);
            g.ResetClip();
        }
        using var line = new Pen(p.Border);
        for (int i = 0; i < heads.Length; i++)
            TextRenderer.DrawText(g, heads[i], Head, new Point(cols[i].X, Px(8)), p.Secondary, Theme.Line);
        int y = HeaderHeight;
        g.DrawLine(line, 1, y, Width - 2, y);
        y += 1;
        int lineH = TextRenderer.MeasureText("Ag", Body, Size.Empty, Theme.Line).Height;
        foreach (var (dot, cells) in rows)
        {
            int h = RowHeight(cells, cols);
            Theme.Dot(g, dot, Px(14), y + Px(11) + (lineH - Px(8)) / 2, DeviceDpi);
            for (int i = 0; i < cells.Length; i++)
            {
                var color = i == 3 || string.IsNullOrEmpty(cells[i]) ? p.Secondary : p.Text;
                TextRenderer.DrawText(g, Cell(cells[i]), i == 0 ? Strong : Body,
                                      new Rectangle(cols[i].X, y + Px(11), cols[i].W, h), color, Theme.Wrap);
            }
            y += h;
            if (y < Height - 2)
                g.DrawLine(line, 1, y, Width - 2, y);
            y += 1;
        }
    }
}

// P3: a line a problem, each an amber dot and amber words.
sealed class ProblemLines : Painted
{
    List<string> lines = [];

    public bool SetLines(List<string> l)
    {
        if (l.SequenceEqual(lines))
            return false;
        lines = l;
        Invalidate();
        return true;
    }

    Font Body => Theme.Font(13, DeviceDpi);
    int TextWidth(int width) => width - Px(16);

    public override int Measure(int width) =>
        lines.Sum(l => Theme.Height(l, Body, TextWidth(width))) + Px(6) * Math.Max(0, lines.Count - 1);

    protected override void Draw(Graphics g)
    {
        int y = 0, lineH = TextRenderer.MeasureText("Ag", Body, Size.Empty, Theme.Line).Height;
        foreach (var l in lines)
        {
            Theme.Dot(g, Theme.Waiting, 0, y + (lineH - Px(8)) / 2, DeviceDpi);
            int h = Theme.Height(l, Body, TextWidth(Width));
            TextRenderer.DrawText(g, l, Body, new Rectangle(Px(16), y, TextWidth(Width), h), Theme.P.Amber, Theme.Wrap);
            y += h + Px(6);
        }
    }
}

// The settings card: a rounded card the checkboxes and link sit on.
sealed class CardPanel : Panel
{
    public CardPanel() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Theme.Card(e.Graphics, ClientRectangle, DeviceDpi, Theme.P.Card);
    }
}

// A Windows 11 checkbox: 20px, radius 4, the accent when checked. Busy
// fades the row to 55% and shows a spinner and "Switching…"; it takes no
// input then.
sealed class CheckRow : Painted
{
    bool on, busy;
    float angle;
    readonly System.Windows.Forms.Timer spin = new() { Interval = 40 };

    public event EventHandler CheckedChanged;

    public CheckRow(string text, bool on)
    {
        Text = text;
        this.on = on;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        spin.Tick += (_, _) =>
        {
            angle = (angle + 30) % 360;
            Invalidate();
        };
    }

    public bool Checked => on;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => busy;
        set
        {
            busy = value;
            spin.Enabled = value;
            Invalidate();
        }
    }

    // The 20px box and 3px above and below it for the focus ring.
    public override int Measure(int width) => Px(26);

    // For screen readers: a checkbox, named by its text, its state read here.
    protected override AccessibleObject CreateAccessibilityInstance() => new Access(this);

    sealed class Access(CheckRow row) : ControlAccessibleObject(row)
    {
        public override AccessibleRole Role => AccessibleRole.CheckButton;
        public override string Name => row.Text;
        public override AccessibleStates State =>
            base.State | (row.on ? AccessibleStates.Checked : 0) | (row.busy ? AccessibleStates.Unavailable : 0);
        public override string DefaultAction => row.on ? "Uncheck" : "Check";
        public override void DoDefaultAction() => row.Toggle();
    }

    void Toggle()
    {
        if (busy)
            return;
        on = !on;
        AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        Invalidate();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Toggle();
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
            Toggle();
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            spin.Dispose();
        base.Dispose(disposing);
    }

    protected override void Draw(Graphics g)
    {
        var p = Theme.P;
        float a = busy ? .55f : 1;
        Color C(Color c) => Theme.Fade(c, BackColor, a);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int s = Px(20), top = (Height - s) / 2;
        var box = new RectangleF(0.5f, top + 0.5f, s - 1, s - 1);
        using (var path = Theme.Round(box, Px(4)))
        {
            if (on)
            {
                using var b = new SolidBrush(C(p.Accent));
                g.FillPath(b, path);
                using var tick = new Pen(C(p.OnAccent), Math.Max(1.5f, Px(1.6f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(tick, [new PointF(s * .27f, top + s * .52f), new PointF(s * .43f, top + s * .68f), new PointF(s * .74f, top + s * .34f)]);
            }
            else
            {
                using var pen = new Pen(C(p.Secondary));
                g.DrawPath(pen, path);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(p.Text, Math.Max(1, Px(1)));
            using var path = Theme.Round(new RectangleF(-0.5f + 1, top - Px(3) + 1, s + Px(6) - 2, s + Px(6) - 2), Px(6));
            g.DrawPath(ring, path);
        }
        var font = Theme.Font(14, DeviceDpi);
        int x = s + Px(12);
        var size = TextRenderer.MeasureText(g, Text, font, Size.Empty, Theme.Line);
        TextRenderer.DrawText(g, Text, font, new Point(x, (Height - size.Height) / 2), C(p.Text), Theme.Line);
        if (!busy)
            return;
        // The spinner and its word, at full strength: they are what is happening.
        x += size.Width + Px(12);
        int d = Px(12), w = Math.Max(2, Px(2));
        using (var pen = new Pen(p.Accent, w))
            g.DrawArc(pen, x + w / 2f, (Height - d) / 2f + w / 2f, d - w, d - w, angle, 270);
        var small = Theme.Font(12, DeviceDpi);
        var ws = TextRenderer.MeasureText(g, "Switching…", small, Size.Empty, Theme.Line);
        TextRenderer.DrawText(g, "Switching…", small, new Point(x + d + Px(6), (Height - ws.Height) / 2), p.Secondary, Theme.Line);
    }
}

// "Windows display settings ↗": the accent colour, underlined on hover.
sealed class Hyperlink : Painted
{
    bool hover;

    public event EventHandler Clicked;

    public Hyperlink(string text)
    {
        Text = text;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Link;
        AccessibleName = text;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public override int Measure(int width) => TextRenderer.MeasureText(Text, Theme.Font(14, DeviceDpi), Size.Empty, Theme.Line).Height;

    public int TextWidth => TextRenderer.MeasureText(Text, Theme.Font(14, DeviceDpi), Size.Empty, Theme.Line).Width + Px(4);

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnClick(EventArgs e)
    {
        Clicked?.Invoke(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
            Clicked?.Invoke(this, EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void Draw(Graphics g)
    {
        var f = Theme.Font(14, DeviceDpi);
        using var u = hover ? new Font(f, FontStyle.Underline) : null;
        TextRenderer.DrawText(g, Text, u ?? f, Point.Empty, Theme.P.Accent, Theme.Line);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, ClientRectangle, Theme.P.Text, BackColor);
    }
}
