// Decodes a recorded camera stream into L7 (DECISIONS 10 item 4, increment
// C), once for every client (FR-1.3). Stands in for the camera connection
// until ExoPhone's camera role exists (increment D).
//
// Microsoft's software H.264 decoder, a synchronous MFT: one thread hands it
// a compressed frame with ProcessInput, then asks for decoded ones with
// ProcessOutput until it answers NEED_MORE_INPUT. No events and no D3D
// device, unlike the encoder in H264Encoder.cs. Software because L7 is a
// TCP socket, so a hardware decoder's GPU output would be read back anyway
// (TECHNICAL_DESIGN 2.5), and 720p at 30fps is light work for a CPU.
//
// Geometry comes from the decoder, never a constant (CLAUDE.md). Its first
// ProcessOutput answers STREAM_CHANGE once it has parsed the SPS; the
// output type is then set again and its frame size and stride read back,
// and every frame goes onto L7 at exactly that size and stride.

using System.Diagnostics;
using ExoHub.Display;
using ExoHub.Link;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ExoHub.Camera;

sealed unsafe class CameraDecoder : IDisposable
{
    // The file has no timestamps, so it is fed at the advertised rate.
    const int Fps = 30;
    const long Duration = 10_000_000 / Fps;  // 100ns units
    // MF_E_TRANSFORM_STREAM_CHANGE, which Vortice's ResultCode lacks.
    const int StreamChange = unchecked((int)0xC00D6D61);

    readonly CameraLink link;
    readonly Stopwatch clock;
    readonly byte[] file;
    readonly List<AccessUnit> units;
    readonly IMFTransform mft;
    readonly Thread thread;
    volatile bool stop;

    // Set from the decoder's output type by SelectOutput.
    int width, height, stride;
    IMFSample output;  // ours, when the decoder does not provide samples
    int outputSize;
    bool providesSamples;

    // Input time of each frame inside the decoder, by sample time.
    readonly Dictionary<long, long> inFlight = new();
    int windowIn, windowOut, windowUnmatched;
    double windowSumMs, windowMaxMs;

    public string Error { get; private set; }

    // After MFStartup. Throws if no decoder is found or it refuses NV12.
    public CameraDecoder(CameraLink link, Stopwatch clock, string path)
    {
        this.link = link;
        this.clock = clock;
        file = File.ReadAllBytes(path);
        units = AccessUnits.Split(file);
        Log($"file {path}: {file.Length} bytes, {units.Count} frames, {units.Count(u => u.Keyframe)} keyframes, " +
            $"first frame {(units.Count > 0 && units[0].Config && units[0].Keyframe ? "carries SPS, PPS and an IDR" : "DOES NOT START WITH SPS, PPS AND AN IDR")}");
        if (units.Count == 0)
            throw new InvalidDataException($"{path}: no H.264 frames found");

        // Synchronous decoders only, so hardware ones, which are async and
        // want a D3D device, stay out of the list.
        using (IMFActivateCollection found = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 }))
        {
            List<IMFActivate> all = found.ToList();
            if (all.Count == 0)
                throw new InvalidOperationException("no synchronous H.264 decoder enumerated");
            string names = string.Join(", ", all.Select(a => a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)));
            mft = all[0].ActivateObject<IMFTransform>();
            Log($"decoder {all[0].GetString(TransformAttributeKeys.MftFriendlyNameAttribute)}, of: {names}");
        }

        // Low latency before the types, as on the encoder (DECISIONS 11.5):
        // the decoder then outputs each frame as soon as it is decoded,
        // instead of holding frames for reordering. This decoder refused it
        // as VT_BOOL, the type the encoder takes, with E_INVALIDARG
        // (2026-10-11). The same GUID is also MF_LOW_LATENCY, an attribute
        // on the MFT, so that is the fallback. Each outcome is logged.
        using (var codec = new CodecApi(mft.NativePointer))
        {
            string how;
            try
            {
                codec.Set(CodecApi.LowLatencyMode, 1, CodecApi.VT_UI4);
                how = "set through ICodecAPI as VT_UI4";
            }
            catch (ArgumentException ex)
            {
                using IMFAttributes attrs = mft.Attributes;
                Result r = attrs.Set(CodecApi.LowLatencyMode, 1u);
                how = $"ICodecAPI refused VT_UI4 ({ex.Message.TrimEnd('.')}); MF_LOW_LATENCY attribute 0x{r.Code:X8}, " +
                      $"reads {(attrs.GetUInt32(CodecApi.LowLatencyMode, out uint v).Success ? v.ToString() : "unset")}";
            }
            Log($"low latency: {how}; {codec.Describe("AVLowLatencyMode", CodecApi.LowLatencyMode)}");
        }

        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            mft.SetInputType(0, input, 0);
        }
        SelectOutput("before the first frame");
        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        thread = new Thread(Run) { IsBackground = true, Name = "camera decoder" };
        thread.Start();
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s camera {line}");

    // Picks the decoder's NV12 output type and reads back what it holds.
    // Called at start and on every STREAM_CHANGE.
    void SelectOutput(string when)
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try
            {
                t = mft.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                throw new InvalidOperationException($"decoder offers no NV12 output ({i} types)");
            }
            using (t)
                if (t.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoFormatGuids.NV12)
                {
                    mft.SetOutputType(0, t, 0);
                    break;
                }
        }

        using IMFMediaType current = mft.GetOutputCurrentType(0);
        ulong size = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        width = (int)(size >> 32);
        height = (int)(size & 0xFFFFFFFF);
        // Unset means rows are packed, as Media Foundation defines it for
        // NV12; said so in the log rather than hidden.
        bool hasStride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint s).Success;
        stride = hasStride ? (int)s : width;
        string aperture = "unset";
        if (current.GetBlobSize(MediaTypeAttributeKeys.MinimumDisplayAperture, out uint n).Success && n >= 16)
        {
            // MFVideoArea: two 16.16 fixed offsets, then the size as two ints.
            byte[] a = current.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
            aperture = $"{BitConverter.ToInt32(a, 8)}x{BitConverter.ToInt32(a, 12)} at {BitConverter.ToInt16(a, 2)},{BitConverter.ToInt16(a, 6)}";
        }

        OutputStreamInfo info = mft.GetOutputStreamInfo(0);
        providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        outputSize = info.Size;
        Log($"output {when}: NV12 {width}x{height}, stride {stride}{(hasStride ? "" : " (default stride unset, so the width)")}, " +
            $"aperture {aperture}, sample {info.Size} bytes, {(providesSamples ? "decoder's samples" : "our samples, a new one per frame")}");
    }

    // Feeds one frame per 1/30s against a deadline, looping the file. Each
    // pass restarts at SPS, PPS and an IDR, so the decoder runs straight on.
    void Run()
    {
        try
        {
            long interval = Stopwatch.Frequency / Fps;
            long due = Stopwatch.GetTimestamp();
            long windowStart = due;
            long time = 0;
            int gen2Before = GC.CollectionCount(2);
            for (int i = 0; !stop; i = (i + 1) % units.Count)
            {
                long wait = (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (wait > 0)
                    Thread.Sleep((int)wait);
                due += interval;

                AccessUnit u = units[i];
                using (IMFSample sample = MediaFactory.MFCreateSample())
                using (IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(u.Length))
                {
                    buffer.Lock(out IntPtr p, out _, out _);
                    file.AsSpan(u.Offset, u.Length).CopyTo(new Span<byte>((void*)p, u.Length));
                    buffer.Unlock();
                    buffer.CurrentLength = u.Length;
                    sample.AddBuffer(buffer);
                    sample.SampleTime = time;
                    sample.SampleDuration = Duration;
                    inFlight[time] = Stopwatch.GetTimestamp();
                    mft.ProcessInput(0, sample, 0);
                }
                time += Duration;
                windowIn++;
                Drain();

                long now = Stopwatch.GetTimestamp();
                if (now - windowStart >= Stopwatch.Frequency * 5)
                {
                    int gen2 = GC.CollectionCount(2);
                    Log($"decode last 5s: {windowIn} in, {windowOut} out, " +
                        $"in to out {(windowOut > 0 ? windowSumMs / windowOut : 0):F2}ms mean {windowMaxMs:F2}ms max, " +
                        $"{windowUnmatched} unmatched, {inFlight.Count} held, {gen2 - gen2Before} gen-2 GCs");
                    windowStart = now;
                    gen2Before = gen2;
                    windowIn = windowOut = windowUnmatched = 0;
                    windowSumMs = windowMaxMs = 0;
                }
            }
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            Error = ex.Message;
            Log($"decoder STOPPED: {ex.Message.Trim()} at {ex.StackTrace?.Replace(Environment.NewLine, " | ")}");
        }
    }

    // Takes every frame the decoder has ready.
    void Drain()
    {
        while (true)
        {
            // A new sample for every call. Handed the same sample again, this
            // decoder filled it once and then answered E_FAIL on the next
            // frame, with the sample's reference count unchanged throughout
            // (2026-10-11); why is not established. Its buffer is native
            // memory, so this costs no garbage collection.
            if (!providesSamples)
            {
                output?.Dispose();
                output = MediaFactory.MFCreateSample();
                using IMFMediaBuffer b = MediaFactory.MFCreateMemoryBuffer(outputSize);
                output.AddBuffer(b);
            }
            var db = new OutputDataBuffer { StreamID = 0, Sample = output };
            Result r = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);
            db.Events?.Dispose();
            if (r == ResultCode.TransformNeedMoreInput)
                return;
            if (r.Code == StreamChange)
            {
                SelectOutput("on STREAM_CHANGE");
                continue;
            }
            r.CheckError();
            // Our own sample comes back in db.Sample; only one the decoder
            // made is ours to release.
            IMFSample sample = providesSamples ? db.Sample : output;
            try
            {
                Publish(sample);
            }
            finally
            {
                if (providesSamples)
                    sample.Dispose();
            }
        }
    }

    void Publish(IMFSample sample)
    {
        long outQpc = Stopwatch.GetTimestamp();
        if (inFlight.Remove(sample.SampleTime, out long inQpc))
        {
            double ms = (outQpc - inQpc) * 1000.0 / Stopwatch.Frequency;
            windowSumMs += ms;
            windowMaxMs = Math.Max(windowMaxMs, ms);
        }
        else
        {
            windowUnmatched++;
        }
        windowOut++;

        using IMFMediaBuffer buffer = sample.GetBufferByIndex(0);
        buffer.Lock(out IntPtr p, out _, out int length);
        try
        {
            // NV12 at the decoder's stride: height rows of Y, then the UV rows.
            int ySize = stride * height, uvSize = stride * height / 2;
            if (length < ySize + uvSize)
                throw new InvalidOperationException($"decoded buffer {length} bytes, {width}x{height} stride {stride} needs {ySize + uvSize}");
            link.Publish(outQpc, width, height, stride,
                         new ReadOnlySpan<byte>((void*)p, ySize),
                         new ReadOnlySpan<byte>((byte*)p + ySize, uvSize));
        }
        finally
        {
            buffer.Unlock();
        }
    }

    public void Dispose()
    {
        stop = true;
        thread.Join();
        output?.Dispose();
        mft.Dispose();
    }
}
