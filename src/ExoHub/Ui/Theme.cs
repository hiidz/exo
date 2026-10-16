// The window's colours and fonts, light or dark as Windows' app theme
// (AppsUseLightTheme), and the drawing helpers the painted controls share.
// Sizes are the design's pixels at 100% scaling; Px turns them into the
// control's own DPI.

using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExoHub.Ui;

sealed record Palette(Color Window, Color Card, Color Border, Color Header, Color Text, Color Secondary,
                      Color Accent, Color OnAccent, Color Amber, Color Code, Color Footer, Color ButtonBorder, Color Body);

static class Theme
{
    public static readonly Color Working = Color.FromArgb(0x3D, 0xB0, 0x60);
    public static readonly Color Waiting = Color.FromArgb(0xE0, 0xA0, 0x30);
    public static readonly Color Off = Color.FromArgb(0x9A, 0x9A, 0xA2);

    static readonly Palette Light = new(
        Window: Hex(0xF3F3F3), Card: Hex(0xFFFFFF), Border: Hex(0xE5E5E5), Header: Hex(0xFAFAFA),
        Text: Hex(0x1B1B1F), Secondary: Hex(0x5F5F66), Accent: Hex(0x3A3F8F), OnAccent: Hex(0xFFFFFF),
        Amber: Hex(0x8A5A00), Code: Hex(0x232659), Footer: Hex(0xF3F3F3), ButtonBorder: Hex(0xD6D6D6), Body: Hex(0xF9F9F9));

    static readonly Palette Dark = new(
        Window: Hex(0x202020), Card: Hex(0x2B2B2B), Border: Hex(0x3A3A3A), Header: Hex(0x323232),
        Text: Hex(0xFFFFFF), Secondary: Hex(0xC5C5C5), Accent: Hex(0xA9AEF0), OnAccent: Hex(0x14163A),
        Amber: Hex(0xF0B850), Code: Hex(0xA9AEF0), Footer: Hex(0x202020), ButtonBorder: Hex(0x4A4A4A), Body: Hex(0x2B2B2B));

    public static bool IsDark { get; private set; } = ReadDark();
    public static Palette P => IsDark ? Dark : Light;

    /// Reads the app theme again; true when it changed.
    public static bool Update()
    {
        bool dark = ReadDark();
        if (dark == IsDark)
            return false;
        IsDark = dark;
        return true;
    }

    static bool ReadDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    static Color Hex(int rgb) => Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);

    /// A colour at this opacity over a background, for a row drawn faded.
    public static Color Fade(Color c, Color over, float alpha) => Color.FromArgb(
        (int)(c.R * alpha + over.R * (1 - alpha)), (int)(c.G * alpha + over.G * (1 - alpha)), (int)(c.B * alpha + over.B * (1 - alpha)));

    public static int Px(float px, int dpi) => (int)Math.Round(px * dpi / 96f);

    // Segoe UI Variable Text, or Segoe UI where it is missing; GDI+ quietly
    // substitutes a family it lacks, so the name is checked.
    static readonly Dictionary<(string, float, int), Font> fonts = new();

    public static Font Font(float px, int dpi, bool semibold = false) =>
        Get(semibold ? ["Segoe UI Variable Text Semibold", "Segoe UI Semibold"] : ["Segoe UI Variable Text", "Segoe UI"], px, dpi);

    public static Font Mono(float px, int dpi) => Get(["Cascadia Mono SemiBold", "Cascadia Mono", "Consolas"], px, dpi);

    static Font Get(string[] families, float px, int dpi)
    {
        var key = (families[0], px, dpi);
        if (fonts.TryGetValue(key, out var f))
            return f;
        foreach (var name in families)
        {
            f = new Font(name, px * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
            if (f.Name == name)
                break;
            f.Dispose();
            f = null;
        }
        f ??= new Font(FontFamily.GenericSansSerif, px * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        return fonts[key] = f;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// A card: filled, with a one-pixel border, its corners rounded.
    public static void Card(Graphics g, Rectangle bounds, int dpi, Color fill)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1);
        using var path = Round(r, Px(6, dpi));
        using var b = new SolidBrush(fill);
        using var pen = new Pen(P.Border);
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }

    public static void Dot(Graphics g, Color c, int x, int y, int dpi)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        g.FillEllipse(b, x, y, Px(8, dpi), Px(8, dpi));
    }

    public const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
    public const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    public static int Height(string text, Font f, int width) =>
        TextRenderer.MeasureText(text, f, new Size(width, int.MaxValue), Wrap).Height;

    // The title bar follows the theme; Windows draws it.
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void TitleBar(IntPtr hwnd)
    {
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);  // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    /// Rounded corners and a border colour for a window with no frame.
    public static void Popup(IntPtr hwnd)
    {
        int round = 2;  // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE
        int border = ColorTranslator.ToWin32(P.Border);
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);  // DWMWA_BORDER_COLOR
    }
}
