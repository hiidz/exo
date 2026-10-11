// Synthetic camera frames for L7 (DECISIONS 10 item 4, increment B), the
// producer when no recorded stream is given. Built to make a geometry bug
// visible rather than only logged: rows are wider than the picture, with
// the padding bright, so a reader that ignores the stride shows a diagonal
// shear; and a red marker sweeps across, whose colour comes out right only
// if the UV plane is found at stride * height. dlltest --link checks for
// exactly this picture.

using System.Diagnostics;
using ExoHub.Link;

namespace ExoHub.Camera;

sealed class TestPattern : IDisposable
{
    const int Fps = 30;
    // Studio-range NV12. The background is not the test card's, so which
    // one an app shows says at a glance whether the link is up.
    const byte BackY = 64, PadY = 235, Neutral = 128;
    const byte MarkY = 81, MarkU = 90, MarkV = 240;  // red, as the card's red bar
    const int MarkSize = 96;
    const double SweepSeconds = 2;

    readonly CameraLink link;
    readonly Stopwatch clock;
    readonly int width, height, stride;
    readonly Thread thread;
    volatile bool stop;

    public TestPattern(CameraLink link, Stopwatch clock, int width, int height, int stride)
    {
        this.link = link;
        this.clock = clock;
        this.width = width;
        this.height = height;
        this.stride = stride;
        Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s camera test pattern {width}x{height} stride {stride} at {Fps}fps");
        thread = new Thread(Run) { IsBackground = true, Name = "camera test pattern" };
        thread.Start();
    }

    // One frame per 1/30s, against a deadline that advances by one interval
    // per frame, so a late wake does not push every later frame back.
    void Run()
    {
        byte[] y = new byte[stride * height];
        byte[] uv = new byte[stride * height / 2];
        long interval = Stopwatch.Frequency / Fps;
        long due = Stopwatch.GetTimestamp();
        while (!stop)
        {
            long wait = (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (wait > 0)
                Thread.Sleep((int)wait);
            due += interval;
            Draw(y, uv);
            link.Publish(Stopwatch.GetTimestamp(), width, height, stride, y, uv);
        }
    }

    // The whole picture, every frame: 1.4MB of fills is far cheaper than
    // tracking what moved.
    void Draw(Span<byte> y, Span<byte> uv)
    {
        for (int row = 0; row < height; row++)
        {
            y.Slice(row * stride, width).Fill(BackY);
            y.Slice(row * stride + width, stride - width).Fill(PadY);
        }
        for (int row = 0; row < height / 2; row++)
        {
            uv.Slice(row * stride, width).Fill(Neutral);
            uv.Slice(row * stride + width, stride - width).Fill(PadY);
        }
        // Even coordinates, so the marker covers whole U,V pairs.
        double phase = clock.Elapsed.TotalSeconds % SweepSeconds / SweepSeconds;
        int x0 = (int)(phase * (width - MarkSize)) & ~1;
        int y0 = (height - MarkSize) / 2 & ~1;
        for (int row = y0; row < y0 + MarkSize; row++)
            y.Slice(row * stride + x0, MarkSize).Fill(MarkY);
        for (int row = y0 / 2; row < (y0 + MarkSize) / 2; row++)
            for (int col = x0; col < x0 + MarkSize; col += 2)
            {
                uv[row * stride + col] = MarkU;
                uv[row * stride + col + 1] = MarkV;
            }
    }

    public void Dispose()
    {
        stop = true;
        thread.Join();
    }
}
