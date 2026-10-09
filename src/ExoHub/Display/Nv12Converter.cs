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

    // An event query is D3D11's fence: End() puts a marker in the command
    // stream and GetData reports whether the GPU has passed it. Used only
    // to time the Blt.
    readonly ID3D11Query done;

    ID3D11VideoProcessorEnumerator vpEnum;  // null until the first frame
    ID3D11VideoProcessor processor;

    // One NV12 target, overwritten every frame. The encoder holds input
    // buffers (spike 5), so step 4 replaces this with a pool.
    ID3D11Texture2D target;
    ID3D11VideoProcessorOutputView targetView;

    // The input size the processor was built for.
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public int Rebuilds { get; private set; }
    public double LastRebuildMs { get; private set; }

    // CPU time to create the input view and submit the Blt, and time from
    // the start of that until the GPU has finished it.
    public double LastSubmitMs { get; private set; }
    public double LastDoneMs { get; private set; }

    public Nv12Converter(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device;
        this.context = context;
        // Both QIs fail unless the device was created with VideoSupport.
        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        videoContext = context.QueryInterface<ID3D11VideoContext>();
        done = device.CreateQuery(QueryType.Event, QueryFlags.None);
    }

    // Converts one frame. Must be called while the frame is acquired, which
    // is what DesktopDuplicationSource.Next's callback guarantees. Returns
    // true when this frame caused a rebuild.
    public bool Convert(ID3D11Texture2D bgra, Rational frameRate)
    {
        // Geometry from the texture itself, read every frame. Reading a
        // description is a field copy, not a GPU round trip.
        Texture2DDescription d = bgra.Description;
        bool rebuilt = processor == null || d.Width != Width || d.Height != Height;
        if (rebuilt)
        {
            long r0 = Stopwatch.GetTimestamp();
            Rebuild(d, frameRate);
            LastRebuildMs = Stopwatch.GetElapsedTime(r0).TotalMilliseconds;
        }

        long t0 = Stopwatch.GetTimestamp();

        // One view per frame, released before the frame is. A cached view
        // would keep a reference to a texture DWM has taken back.
        using var input = videoDevice.CreateVideoProcessorInputView(bgra, vpEnum,
            new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });

        // No source or destination rectangles: the whole input to the whole
        // output, which is the same size.
        var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
        videoContext.VideoProcessorBlt(processor, targetView, 0, new[] { stream }).CheckError();
        LastSubmitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // Measurement only. Production does not wait here: the encoder takes
        // the NV12 texture and the GPU orders the work. Flush sends the
        // queued commands now rather than whenever the driver chooses.
        // GetData with no buffer only reports status: S_FALSE until the GPU
        // passes the marker, then S_OK.
        context.End(done);
        context.Flush();
        Result status;
        while ((status = context.GetData(done, IntPtr.Zero, 0, AsyncGetDataFlags.None)).Code == 1)
            Thread.SpinWait(20);
        status.CheckError();
        LastDoneMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        return rebuilt;
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

        // RenderTarget is what CreateVideoProcessorOutputView requires
        // (spike 5). Default usage: GPU only, no CPU access.
        target = device.CreateTexture2D(new Texture2DDescription
        {
            Width = d.Width,
            Height = d.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        targetView = videoDevice.CreateVideoProcessorOutputView(target, vpEnum,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        Width = d.Width;
        Height = d.Height;
        Rebuilds++;
    }

    // The one CPU readback DECISIONS 4.1 allows: debugging only, off by
    // default, never on the production path. Writes the last converted
    // frame as tightly packed NV12 (the Y rows, then the interleaved UV
    // rows, each Width bytes) and returns what it saw.
    //
    // Call it outside the frame hold: it reads our NV12 target, not the
    // duplication texture, and file I/O must not delay ReleaseFrame.
    public string DumpLast(string path)
    {
        // The CPU can only read a staging texture. Same size and format,
        // no bind flags, CPU read access.
        Texture2DDescription sd = target.Description;
        sd.Usage = ResourceUsage.Staging;
        sd.BindFlags = BindFlags.None;
        sd.CPUAccessFlags = CpuAccessFlags.Read;
        sd.MiscFlags = ResourceOptionFlags.None;
        using ID3D11Texture2D staging = device.CreateTexture2D(sd);

        // A GPU copy, then Map, which waits for that copy to finish.
        context.CopyResource(staging, target);
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
        targetView?.Dispose();
        targetView = null;
        target?.Dispose();
        target = null;
        processor?.Dispose();
        processor = null;
        vpEnum?.Dispose();
        vpEnum = null;
    }

    public void Dispose()
    {
        DisposeSized();
        done.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
    }
}
