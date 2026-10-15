using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExoHub.Display;

// BGRA to NV12 on the GPU, through ID3D11VideoProcessor (DECISIONS 4.1).
//
// The video processor is D3D11's colour conversion and scaling unit. Three
// kinds of object drive it: the enumerator says what the hardware can do
// for one input and output size, the processor does the work, and views
// bind a texture to it as input or output, the way a render target view
// binds a texture to the 3D pipeline.
//
// The enumerator and processor are sized, so they are built for the size
// of the texture handed in and rebuilt when a texture arrives at another
// size. A resolution change through recovery is picked up on the first
// frame at the new size, and Reopen, which recovery waits on, does no
// converter work at all.
sealed class Nv12Converter : IDisposable
{
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11VideoDevice videoDevice;
    readonly ID3D11VideoContext videoContext;

    // GPU timing of the Blt without the CPU ever waiting for it. A timestamp
    // query records the GPU clock when the GPU reaches it in the command
    // stream. A disjoint query around a pair gives that clock's frequency
    // and says whether it stayed valid (a power state change can break it).
    // Each Blt takes the next slot of a ring; a slot is read just before it
    // is reused, frames later, with DoNotFlush, and counted as unread rather
    // than waited for if the GPU has not got there.
    const int Ring = 8;
    readonly ID3D11Query[] disjoint = new ID3D11Query[Ring];
    readonly ID3D11Query[] start = new ID3D11Query[Ring];
    readonly ID3D11Query[] end = new ID3D11Query[Ring];
    readonly bool[] issued = new bool[Ring];
    int nextSlot;

    public List<double> GpuMs { get; } = new();
    public int GpuUnread { get; private set; }
    public int GpuDisjoint { get; private set; }

    ID3D11VideoProcessorEnumerator vpEnum;  // null until the first frame
    ID3D11VideoProcessor processor;

    // The NV12 targets, sized with the processor. The encoder holds its
    // inputs, so every frame goes to a texture of its own (Nv12Pool).
    Nv12Pool pool;

    // The frame the last Convert wrote, for DumpLast.
    Nv12Frame last;

    // A copy of the last new desktop image, for resends (DECISIONS 5.4).
    // BGRA rather than NV12 so a pointer can later be drawn on a copy of a
    // pointer-free image before the Blt (11.1). Only ever a copy source:
    // a resend writes a fresh pool frame. Sized with the processor.
    ID3D11Texture2D kept;
    bool keptFilled;

    // The input size the processor was built for.
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public int Rebuilds { get; private set; }
    public double LastRebuildMs { get; private set; }
    public Nv12Pool Pool => pool;

    // CPU time to create the input view, submit the Blt and flush it.
    public double LastSubmitMs { get; private set; }
    public double LastResendMs { get; private set; }

    public Nv12Converter(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device;
        this.context = context;
        // Both QIs fail unless the device was created with VideoSupport.
        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        videoContext = context.QueryInterface<ID3D11VideoContext>();
        for (int i = 0; i < Ring; i++)
        {
            disjoint[i] = device.CreateQuery(QueryType.TimestampDisjoint, QueryFlags.None);
            start[i] = device.CreateQuery(QueryType.Timestamp, QueryFlags.None);
            end[i] = device.CreateQuery(QueryType.Timestamp, QueryFlags.None);
        }
    }

    // Converts one frame into a frame from the pool. Must be called while
    // the texture is held, which is what DriverFrameSource.Next's callback
    // guarantees. rebuilt is true when this frame caused a rebuild.
    // Returns null when every pool frame is in use, and the image is dropped.
    public Nv12Frame Convert(ID3D11Texture2D bgra, Rational frameRate, out bool rebuilt)
    {
        // Geometry from the texture itself, read every frame. Reading a
        // description is a field copy, not a GPU round trip.
        Texture2DDescription d = bgra.Description;
        rebuilt = processor == null || d.Width != Width || d.Height != Height;
        if (rebuilt)
        {
            long r0 = Stopwatch.GetTimestamp();
            Rebuild(d, frameRate);
            LastRebuildMs = Stopwatch.GetElapsedTime(r0).TotalMilliseconds;
        }

        long t0 = Stopwatch.GetTimestamp();
        Nv12Frame frame = pool.Take();
        if (frame != null)
            Blt(bgra, frame, t0);

        // After the Blt, so it queues behind it on the GPU rather than
        // delaying it. Kept even when the pool was empty: the image is new.
        context.CopyResource(kept, bgra);
        keptFilled = true;

        // Flush sends the queued commands to the GPU now rather than when
        // the driver chooses, and does not wait for them. Nothing here waits
        // for the GPU: the source's ReleaseSync follows at once, and the
        // shared texture's keyed mutex is what keeps the driver's next copy
        // behind this read. The encoder
        // reads the NV12 texture on the same device, which orders it after
        // the Blt.
        context.Flush();
        LastSubmitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return frame;
    }

    // The kept image converted into a fresh pool frame (DECISIONS 5.4).
    // Null before any new image at this size, or when every pool frame is
    // in use. Called on the capture thread, like Convert, but outside any
    // frame hold: the kept texture is the hub's own.
    public Nv12Frame Resend()
    {
        if (!keptFilled)
            return null;
        Nv12Frame frame = pool.Take();
        if (frame == null)
            return null;
        long t0 = Stopwatch.GetTimestamp();
        Blt(kept, frame, t0);
        context.Flush();
        LastResendMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return frame;
    }

    // Converts input into frame, timed by the query ring.
    void Blt(ID3D11Texture2D bgra, Nv12Frame frame, long t0)
    {
        frame.BltQpc = t0;

        // One view per frame, released before the frame is. A cached view
        // would keep a reference to a texture DWM has taken back.
        using var input = videoDevice.CreateVideoProcessorInputView(bgra, vpEnum,
            new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });

        int slot = nextSlot;
        nextSlot = (nextSlot + 1) % Ring;
        if (issued[slot])
            Harvest(slot);
        context.Begin(disjoint[slot]);
        context.End(start[slot]);

        // The whole input to the whole output, by the rectangles Rebuild set.
        var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
        videoContext.VideoProcessorBlt(processor, frame.View, 0, new[] { stream }).CheckError();

        context.End(end[slot]);
        context.End(disjoint[slot]);
        issued[slot] = true;
        last = frame;
    }

    // Reads one ring slot without waiting. GetData returns false until the
    // GPU has passed the query.
    void Harvest(int slot)
    {
        issued[slot] = false;
        if (!context.GetData(disjoint[slot], AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint dj) ||
            !context.GetData(start[slot], AsyncGetDataFlags.DoNotFlush, out ulong t0) ||
            !context.GetData(end[slot], AsyncGetDataFlags.DoNotFlush, out ulong t1))
            GpuUnread++;
        else if (dj.Disjoint)
            GpuDisjoint++;
        else
            GpuMs.Add((t1 - t0) * 1000.0 / dj.Frequency);
    }

    // The slots still outstanding, for end-of-run totals. Still no waiting:
    // a slot the GPU has not finished counts as unread.
    public void HarvestAll()
    {
        for (int i = 0; i < Ring; i++)
            if (issued[i])
                Harvest(i);
    }

    // A frame rate change alone does not rebuild: the rate is a hint to the
    // driver, not geometry.
    void Rebuild(Texture2DDescription d, Rational frameRate)
    {
        DisposeSized();

        // NV12 stores one chroma sample per 2x2 block, so an odd dimension
        // has no valid NV12 layout.
        if (d.Width % 2 != 0 || d.Height % 2 != 0)
            throw new InvalidOperationException($"NV12 needs even dimensions, input is {d.Width}x{d.Height}");

        // Spike 5 found zero frame rates are not what broke it, but the
        // enumerator has documented error paths for invalid ones.
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = d.Width,
            InputHeight = d.Height,
            InputFrameRate = frameRate,
            OutputWidth = d.Width,
            OutputHeight = d.Height,
            OutputFrameRate = frameRate,
            Usage = VideoUsage.PlaybackNormal,
        };
        vpEnum = videoDevice.CreateVideoProcessorEnumerator(content);

        if ((vpEnum.CheckVideoProcessorFormat(d.Format) & VideoProcessorFormatSupport.Input) == 0)
            throw new InvalidOperationException($"video processor cannot take {d.Format} as input");
        if ((vpEnum.CheckVideoProcessorFormat(Format.NV12) & VideoProcessorFormatSupport.Output) == 0)
            throw new InvalidOperationException("video processor cannot produce NV12");

        processor = videoDevice.CreateVideoProcessor(vpEnum, 0);
        // The stream's destination rectangle defaults to empty, which by the
        // documentation writes nothing. The AMD driver writes the whole frame
        // anyway, but nothing promises that, so all three are set to the
        // whole surface.
        var whole = new Vortice.RawRect(0, 0, (int)d.Width, (int)d.Height);
        videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, whole);
        videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, whole);
        videoContext.VideoProcessorSetOutputTargetRect(processor, true, whole);

        // The shared texture's own description, so size and format
        // match for CopyResource, and its bind flags, which an input view
        // already accepts. Not its sharing flags: this copy is the hub's own.
        kept = device.CreateTexture2D(d with
        {
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        // Colour spaces, DECISIONS 11.7. Both are set rather than left to
        // the driver, whose own getters misreport its defaults. In: the SDR
        // desktop, sRGB full range. Out: BT.709 limited ("studio") range,
        // which is what the stream is tagged and what a decoder assumes
        // when unsure. The ID3D11VideoContext1 setters speak the DXGI names
        // the getters below report in.
        using (var vc1 = videoContext.QueryInterface<ID3D11VideoContext1>())
        {
            vc1.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.RgbFullG22NoneP709);
            vc1.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.YcbcrStudioG22LeftP709);
        }

        // The pool makes its textures on demand, and each one's output view
        // through this processor's enumerator.
        ID3D11VideoProcessorEnumerator e = vpEnum;
        pool = new Nv12Pool(device, d.Width, d.Height, texture =>
            videoDevice.CreateVideoProcessorOutputView(texture, e,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D }));

        Width = d.Width;
        Height = d.Height;
        Rebuilds++;
    }

    // The one CPU readback DECISIONS 4.1 allows: debugging only, off by
    // default, never on the production path. Writes the last converted
    // frame as tightly packed NV12 (the Y rows, then the interleaved UV
    // rows, each Width bytes) and returns what it saw.
    //
    // Call it outside the frame hold: it reads our NV12 texture, not the
    // shared texture, and file I/O must not delay its release. Call it
    // before the frame goes to the encoder, so nothing else touches it.
    public string DumpLast(string path)
    {
        // The CPU can only read a staging texture. Same size and format,
        // no bind flags, CPU read access.
        Texture2DDescription sd = last.Texture.Description;
        sd.Usage = ResourceUsage.Staging;
        sd.BindFlags = BindFlags.None;
        sd.CPUAccessFlags = CpuAccessFlags.Read;
        sd.MiscFlags = ResourceOptionFlags.None;
        using ID3D11Texture2D staging = device.CreateTexture2D(sd);

        // A GPU copy, then Map, which waits for that copy to finish.
        context.CopyResource(staging, last.Texture);
        MappedSubresource m = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int w = (int)Width, h = (int)Height;
            long rowPitch = m.RowPitch;
            string pitches = $"RowPitch={m.RowPitch} DepthPitch={m.DepthPitch} for {w}x{h}";

            // D3D11 gives NV12 one pitch for both planes. The DXGI_FORMAT
            // documentation for NV12 staging resources puts the Y plane in
            // the first RowPitch*H bytes and the UV plane right after, at
            // the same pitch. DepthPitch does not confirm it: measured
            // 2026-10-09 on the RX 6600 it is RowPitch*H, the Y plane only.
            // Any other DepthPitch means a layout this code does not know.
            if (m.DepthPitch != rowPitch * h && m.DepthPitch != rowPitch * h * 3 / 2)
                return $"{pitches}: DepthPitch is neither RowPitch*H nor RowPitch*H*3/2, refusing to guess where UV starts";

            var bytes = new byte[w * h * 3 / 2];
            for (int y = 0; y < h; y++)
                Marshal.Copy(m.DataPointer + (nint)(y * rowPitch), bytes, y * w, w);
            nint uv = m.DataPointer + (nint)(h * rowPitch);
            for (int y = 0; y < h / 2; y++)
                Marshal.Copy(uv + (nint)(y * rowPitch), bytes, w * h + y * w, w);
            File.WriteAllBytes(path, bytes);

            // The range of Y says which range the processor wrote. Limited
            // range, as set, keeps Y within 16..235 and UV within 16..240;
            // a 0 or a 255 means the setting did not take.
            byte yMin = 255, yMax = 0, cMin = 255, cMax = 0;
            for (int i = 0; i < w * h; i++) { yMin = Math.Min(yMin, bytes[i]); yMax = Math.Max(yMax, bytes[i]); }
            for (int i = w * h; i < bytes.Length; i++) { cMin = Math.Min(cMin, bytes[i]); cMax = Math.Max(cMax, bytes[i]); }

            return $"{pitches}: wrote {bytes.Length} bytes, Y {yMin}..{yMax}, UV {cMin}..{cMax}";
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    // What the processor reports it is set to. Rebuild sets both colour
    // spaces; the legacy getters are printed too, since with nothing set
    // they disagreed with what the processor actually wrote.
    public string ColourReport()
    {
        VideoProcessorColorSpace o = videoContext.VideoProcessorGetOutputColorSpace(processor);
        VideoProcessorColorSpace s = videoContext.VideoProcessorGetStreamColorSpace(processor, 0);
        string text =
            $"output colour space: usage={o.Usage} rgbRange={o.RGB_Range} matrix={o.YCbCr_Matrix} " +
            $"xvYCC={o.YCbCr_xvYCC} nominalRange={o.Nominal_Range}\n" +
            $"stream colour space: usage={s.Usage} rgbRange={s.RGB_Range} matrix={s.YCbCr_Matrix} " +
            $"xvYCC={s.YCbCr_xvYCC} nominalRange={s.Nominal_Range}\n" +
            $"stream auto processing: {(bool)videoContext.VideoProcessorGetStreamAutoProcessingMode(processor, 0)}";

        // The newer getters speak DXGI colour space names. Only present when
        // the context implements ID3D11VideoContext1.
        using var vc1 = videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>();
        if (vc1 != null)
            text += $"\noutput colour space1: {vc1.VideoProcessorGetOutputColorSpace1(processor)}" +
                    $"\nstream colour space1: {vc1.VideoProcessorGetStreamColorSpace1(processor, 0)}";
        return text;
    }

    void DisposeSized()
    {
        // Frames the encoder still holds are destroyed when they come back.
        pool?.Dispose();
        pool = null;
        last = null;
        kept?.Dispose();
        kept = null;
        keptFilled = false;
        processor?.Dispose();
        processor = null;
        vpEnum?.Dispose();
        vpEnum = null;
    }

    public void Dispose()
    {
        DisposeSized();
        for (int i = 0; i < Ring; i++)
        {
            disjoint[i].Dispose();
            start[i].Dispose();
            end[i].Dispose();
        }
        videoContext.Dispose();
        videoDevice.Dispose();
    }
}
