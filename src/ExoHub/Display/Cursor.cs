// The mouse pointer on the EXO monitor. Windows never draws it into the
// image (a hardware cursor); ExoDisplay forwards the shape and the position
// separately (DriverCursor), and the phone draws it over the video, so it
// moves at the phone's rate rather than the stream's. The shape arrives in
// DXGI's Desktop Duplication structs, which DriverCursor fills from IddCx's.

using Vortice.DXGI;

namespace ExoHub.Display;

// One pointer shape as the phone draws it: straight-alpha BGRA, rows tight
// (pitch = Width * 4). Immutable once built, so it can cross threads.
sealed record CursorImage(int Width, int Height, int HotX, int HotY, byte[] Bgra,
                          string Type, int InvertPixels, int StraightAlphaEvidence)
{
    public bool SamePixels(CursorImage o) =>
        o != null && Width == o.Width && Height == o.Height && HotX == o.HotX && HotY == o.HotY &&
        Bgra.AsSpan().SequenceEqual(o.Bgra);

    public override string ToString() =>
        $"{Type} {Width}x{Height} hot ({HotX},{HotY}) invert={InvertPixels} straight-alpha evidence={StraightAlphaEvidence}";

    // DXGI_OUTDUPL_POINTER_SHAPE_TYPE. Not monochrome (1): IddCx has no
    // such shape.
    const int Color = 2, MaskedColor = 4;

    // From DXGI's raw shape. Geometry and pitch come from the shape info,
    // never assumed. Pixels that invert the screen cannot be drawn by the
    // phone: its overlay is a separate layer from the video and never sees
    // the video's pixels. They become black with a white outline instead,
    // so they read on light and dark backgrounds.
    public static CursorImage Convert(OutduplPointerShapeInfo info, ReadOnlySpan<byte> raw)
    {
        int type = (int)info.Type;
        int w = (int)info.Width, h = (int)info.Height, pitch = (int)info.Pitch;
        var px = new byte[w * h * 4];
        var invert = new bool[w * h];
        int inverts = 0, evidence = 0;
        string name;

        switch (type)
        {
            case Color:
                // Straight alpha [inferred from Microsoft's Desktop
                // Duplication sample, which blends it with SrcAlpha]. A
                // channel above alpha cannot occur in premultiplied data, so
                // a count above zero proves it.
                name = "color";
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int s = y * pitch + x * 4, d = (y * w + x) * 4;
                        byte b = raw[s], g = raw[s + 1], r = raw[s + 2], a = raw[s + 3];
                        if (Math.Max(b, Math.Max(g, r)) > a)
                            evidence++;
                        Set(px, d, b, g, r, a);
                    }
                break;

            case MaskedColor:
                // The alpha byte is a mask: 0 means the colour replaces the
                // screen, 0xFF means it is XORed with the screen.
                name = "masked-color";
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int s = y * pitch + x * 4, d = (y * w + x) * 4;
                        byte b = raw[s], g = raw[s + 1], r = raw[s + 2], mask = raw[s + 3];
                        if (mask == 0)
                            Set(px, d, b, g, r, 255);
                        else if ((b | g | r) != 0)
                            invert[y * w + x] = true;
                        // XOR with zero leaves the screen alone: transparent.
                    }
                break;

            default:
                name = $"unknown type {type}";
                break;
        }

        for (int i = 0; i < invert.Length; i++)
        {
            if (!invert[i])
                continue;
            inverts++;
            Set(px, i * 4, 0, 0, 0, 255);
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int n = ny * w + nx;
                    if (!invert[n] && px[n * 4 + 3] == 0)
                        Set(px, n * 4, 255, 255, 255, 255);
                }
        }

        return new CursorImage(w, h, info.HotSpot.X, info.HotSpot.Y, px, name, inverts, evidence);
    }

    static void Set(byte[] px, int d, byte b, byte g, byte r, byte a)
    {
        px[d] = b; px[d + 1] = g; px[d + 2] = r; px[d + 3] = a;
    }
}

// What the phone should show: the current shape, and where the hotspot is
// or that the pointer is not on this monitor. Fed on each of the driver's
// pointer events; says when either changed, so repeats of one hidden state
// come to one change.
sealed class CursorTracker
{
    public CursorImage Shape { get; private set; }
    public bool Visible { get; private set; }
    // The hotspot, in picture pixels: DXGI's Position is the shape's
    // top-left, so the hotspot is that plus the shape's HotSpot. Signed: the
    // top-left goes negative at the output's left and top edges.
    public int X { get; private set; }
    public int Y { get; private set; }
    public bool Known { get; private set; }  // false until the first mouse update

    public int ShapeChanges, PositionChanges, PositionsWithoutShape;

    // The shape first: an event can carry both, and the new hotspot applies
    // to the position that comes with it.
    public bool TakeShape(OutduplPointerShapeInfo info, ReadOnlySpan<byte> raw)
    {
        CursorImage next = CursorImage.Convert(info, raw);
        if (next.SamePixels(Shape))
            return false;
        Shape = next;
        ShapeChanges++;
        return true;
    }

    public bool TakePosition(OutduplPointerPosition p)
    {
        bool visible = p.Visible;
        int x = 0, y = 0;
        if (visible)
        {
            if (Shape == null)
                PositionsWithoutShape++;
            x = p.Position.X + (Shape?.HotX ?? 0);
            y = p.Position.Y + (Shape?.HotY ?? 0);
        }
        // Every hidden state is the same state, wherever DXGI says it is.
        if (Known && visible == Visible && (!visible || (x == X && y == Y)))
            return false;
        Known = true;
        Visible = visible;
        X = x;
        Y = y;
        PositionChanges++;
        return true;
    }
}
