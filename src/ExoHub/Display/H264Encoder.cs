using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ExoHub.Display;

// The hardware H.264 encoder MFT, fed NV12 D3D11 textures with no CPU copy
// (DECISIONS 4.1, spike 5).
//
// One instance per geometry. An encoder's media types are fixed for its
// stream, so a new size means disposing this one and building another,
// whose first output is a fresh SPS, PPS and IDR.
//
// The MFT is asynchronous: it asks for input with METransformNeedInput and
// announces output with METransformHaveOutput. Those events are collected
// with BeginGetEvent, so Media Foundation calls EventPump.Invoke on its own
// thread when one arrives and no thread of ours ever waits on the MFT. A
// stalled encoder shows as silence, never as a hang.
//
// Every MFT call and the state below is under one lock, because events
// arrive on Media Foundation threads while frames arrive from the capture
// loop. Whether this MFT tolerates ProcessInput and ProcessOutput at once
// is unknown, so it is never asked to.
sealed class H264Encoder : IDisposable
{
    // eAVEncH264VProfile_High from codecapi.h. MF_MT_MPEG2_PROFILE is the key
    // the encoder honours for H.264 too (spike 5).
    const uint ProfileHigh = 100;

    // MFVideoTransferMatrix_BT709 from mfobjects.h. Vortice has no enum for it.
    const uint MatrixBt709 = 1;

    readonly IMFActivate activate;
    readonly IMFTransform mft;
    readonly IMFDXGIDeviceManager manager;
    readonly CodecApi codec;
    readonly IMFMediaEventGenerator events;
    readonly EventPump pump;  // held so the GC never collects a callback MF can still call
    readonly Stream sink;
    readonly ExoHub.Link.DisplaySender sender;  // null when nothing is sent
    readonly long duration;  // 100ns units, one frame at the declared rate

    readonly object gate = new();
    bool disposed;
    int credits;          // NeedInput events not yet answered with a frame
    Nv12Frame pending;    // the newest converted frame, waiting for a credit
    long pendingQpc;
    long firstQpc = -1;   // sample times count from this encoder's first frame
    long lastSubmittedQpc;
    byte[] copy = new byte[1 << 20];

    // The rate gate: at most one submit per interval of wall time (QPC
    // ticks). The declared frame rate sets the encoder's per-frame bit
    // budget, so feeding faster than it overshoots the bitrate (measured
    // 2026-10-09: about 50 frames a second into a 30fps stream gave 68 Mbps
    // from a 40 Mbps setting). A frame that arrives early waits in pending
    // and the timer submits it when the gate opens, unless a newer one
    // replaces it first.
    //
    // The gate runs on a fixed schedule: each due time is one interval after
    // the previous due time, not after the actual submit. The timer fires
    // about 0.8ms late, and counting from the actual submit added that to
    // every interval (measured 2026-10-11: 28.6fps instead of 30).
    readonly long interval;
    readonly Timer gateTimer;
    long nextDueQpc;      // 0 until the first submit; then when the next is allowed
    long timerDueQpc;     // 0 when the timer is not armed
    long lastOutputQpc;
    bool forceRequested;  // the next submitted frame is to be an IDR
    long forcedTime = -1; // sample time of the frame it was applied to

    // Per submitted frame, by sample time: when it was presented, when its
    // Blt was submitted, when it went into the encoder. Looked up when the
    // output sample with the same time comes out.
    readonly Dictionary<long, (long present, long blt, long submit)> inFlight = new();

    public uint Width { get; }
    public uint Height { get; }
    public uint Fps { get; }
    public uint Bitrate { get; }
    public string Name { get; }

    // What the encoder reported once configured. Printed by the caller.
    public string Report { get; }

    // Counters, read by the caller for its totals.
    public int NeedInputs { get; private set; }
    public int Submitted { get; private set; }
    public int Replaced { get; private set; }   // pending frames overwritten by a newer one
    public int Outputs { get; private set; }
    public int Keyframes { get; private set; }
    public long OutputBytes { get; private set; }
    public Dictionary<MediaEventTypes, int> OtherEvents { get; } = new();
    public int GateWaits { get; private set; }     // times the timer was armed
    public int Clamped { get; private set; }       // present times moved forward to stay increasing

    // A frame waits for a credit, the gate or the sender. A resend is not
    // offered then, so it never displaces a real frame (DECISIONS 5.4).
    public bool HasPending { get { lock (gate) return pending != null; } }

    // One line per forced IDR once its output has come out: whether the
    // encoder honoured it. Filled on the event thread, so read through
    // TakeForcedResults.
    readonly List<string> forcedResults = new();

    public string[] TakeForcedResults()
    {
        lock (gate)
        {
            string[] r = forcedResults.ToArray();
            forcedResults.Clear();
            return r;
        }
    }
    public int UnmatchedOutputs { get; private set; }

    // Milliseconds per frame, for the caller's spreads. NFR-1's display rows.
    public List<double> PresentToBltMs { get; } = new();  // DWM present to our Blt submit
    public List<double> BltToSubmitMs { get; } = new();   // waiting for the gate or a credit
    public List<double> SubmitToOutMs { get; } = new();   // inside the encoder
    public List<double> BltToOutMs { get; } = new();      // Blt submit to encoded sample out
    public List<double> TimerLateMs { get; } = new();     // gate timer firing after its due time

    // The first failure on an event thread. The caller checks it every
    // iteration, since an exception cannot propagate from there.
    public string Error { get; private set; }

    public H264Encoder(ID3D11Device device, uint width, uint height, uint fps, uint bitrate, Stream sink,
                       ExoHub.Link.DisplaySender sender = null, uint quality = 0)
    {
        Width = width;
        Height = height;
        Fps = fps;
        Bitrate = bitrate;
        this.sink = sink;
        this.sender = sender;
        sender?.SetKick(Kick);
        duration = 10_000_000 / fps;
        interval = Stopwatch.Frequency / fps;
        gateTimer = new Timer(_ => OnGateTimer());
        var report = new List<string>();

        // Hardware encoders that take NV12 and produce H.264. The hardware
        // flag keeps Microsoft's software encoder out of the list.
        using (IMFActivateCollection found = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 }))
        {
            List<IMFActivate> all = found.ToList();
            if (all.Count == 0)
                throw new InvalidOperationException("no hardware H.264 encoder enumerated");
            report.Add($"hardware H.264 encoders: {string.Join(", ", all.Select(a => a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)))}");

            // Spike 5 saw two identical AMDh264Encoder entries and used the
            // first. Disposing the collection releases every entry, so the
            // chosen one gets its own reference: QueryInterface returns the
            // same object with a reference this class owns. Kept because
            // ShutdownObject on it is how the MFT is torn down.
            activate = all[0].QueryInterface<IMFActivate>();
        }
        Name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        mft = activate.ActivateObject<IMFTransform>();

        // Hardware MFTs are async and ship locked. Any other call before the
        // unlock fails.
        using (IMFAttributes attrs = mft.Attributes)
        {
            if (attrs.GetUInt32(TransformAttributeKeys.TransformAsync) != 1)
                throw new InvalidOperationException($"{Name} is not an async MFT; this class only drives async ones");
            attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
        }

        // Our device, through a DXGI device manager. This is what lets the
        // encoder take D3D11 textures directly.
        manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)manager.NativePointer);

        // Low latency and CBR before the media types, as in spike 5, and read
        // back after them: S_OK from SetValue does not prove a value stuck.
        codec = new CodecApi(mft.NativePointer);
        codec.Set(CodecApi.LowLatencyMode, 1, CodecApi.VT_BOOL);
        // CBR, or with quality (1 to 100) the encoder's quality mode, for
        // the Wi-Fi rate control measurement (PLAN.md, Transport and Wi-Fi).
        if (quality == 0)
            codec.Set(CodecApi.RateControlMode, 0, CodecApi.VT_UI4);  // eAVEncCommonRateControlMode_CBR
        else
        {
            codec.Set(CodecApi.RateControlMode, 3, CodecApi.VT_UI4);  // eAVEncCommonRateControlMode_Quality
            codec.Set(CodecApi.Quality, quality, CodecApi.VT_UI4);
        }
        // DECISIONS 11.6: a keyframe a minute at the encoded rate. Every
        // keyframe 5.3 needs is forced, and a forced one restarts the count.
        codec.Set(CodecApi.GopSize, fps * 60, CodecApi.VT_UI4);

        // Output first: the encoder only knows what input it accepts once it
        // knows what it produces.
        using (IMFMediaType output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            output.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileHigh).CheckError();
            SetVideoCommon(output, width, height, fps);
            mft.SetOutputType(0, output, 0);
        }
        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            SetVideoCommon(input, width, height, fps);
            mft.SetInputType(0, input, 0);
        }

        // Readbacks: what the encoder actually holds, not what was asked.
        using (IMFMediaType current = mft.GetOutputCurrentType(0))
        {
            report.Add($"output type: {Unpack(Read64(current, MediaTypeAttributeKeys.FrameSize))} " +
                       $"@{Unpack(Read64(current, MediaTypeAttributeKeys.FrameRate), '/')} " +
                       $"bitrate={Read32(current, MediaTypeAttributeKeys.AvgBitrate)} " +
                       $"profile={Read32(current, MediaTypeAttributeKeys.Mpeg2Profile)} " +
                       $"level={Read32(current, MediaTypeAttributeKeys.Mpeg2Level)}");
            report.Add($"output colour: primaries={Read32(current, MediaTypeAttributeKeys.VideoPrimaries)} " +
                       $"transfer={Read32(current, MediaTypeAttributeKeys.TransferFunction)} " +
                       $"matrix={Read32(current, MediaTypeAttributeKeys.YuvMatrix)} " +
                       $"range={Read32(current, MediaTypeAttributeKeys.VideoNominalRange)}");
        }
        InputStreamInfo si = mft.GetInputStreamInfo(0);
        OutputStreamInfo so = mft.GetOutputStreamInfo(0);
        report.Add($"input stream flags=0x{si.Flags:X} (HOLDS_BUFFERS={(si.Flags & 0x8) != 0}) size={si.Size}, " +
                   $"output stream flags=0x{so.Flags:X} (PROVIDES_SAMPLES={(so.Flags & 0x100) != 0})");
        report.Add(codec.Describe("AVLowLatencyMode", CodecApi.LowLatencyMode));
        report.Add(codec.Describe("AVEncCommonRateControlMode", CodecApi.RateControlMode));
        report.Add(codec.Describe("AVEncCommonQuality", CodecApi.Quality));
        report.Add(codec.Describe($"AVEncMPVGOPSize (set {fps * 60})", CodecApi.GopSize));
        report.Add(codec.Describe("AVEncVideoForceKeyFrame", CodecApi.ForceKeyFrame));
        Report = string.Join("\n", report);

        // Start streaming, then ask for the first event. From here events
        // arrive on Media Foundation threads.
        events = mft.QueryInterface<IMFMediaEventGenerator>();
        pump = new EventPump(this);
        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        events.BeginGetEvent(pump, null);
    }

    // Takes a converted frame. Latest wins: a frame still waiting for the
    // encoder is replaced by a newer one and goes back to its pool, so the
    // encoder always gets the newest image and the last image before the
    // screen goes still is never the one dropped.
    //
    // presentQpc is the frame's LastPresentTime, a QueryPerformanceCounter
    // value.
    public void Offer(Nv12Frame frame, long presentQpc)
    {
        lock (gate)
        {
            if (disposed)
            {
                frame.Release();
                return;
            }
            if (pending != null)
            {
                pending.Release();
                Replaced++;
            }
            pending = frame;
            pendingQpc = presentQpc;
            TrySubmit();
        }
    }

    // Under the lock. Sends the pending frame if the encoder has asked for
    // one and the rate gate is open; arms the timer if only the gate is
    // shut.
    void TrySubmit()
    {
        // DECISIONS 5.7: no new input while an encoded frame still waits
        // for the sender. pending keeps being replaced meanwhile.
        if (credits == 0 || pending == null || (sender?.Busy ?? false))
            return;

        long now = Stopwatch.GetTimestamp();
        if (nextDueQpc != 0 && now < nextDueQpc)
        {
            if (timerDueQpc == 0)
            {
                timerDueQpc = nextDueQpc;
                GateWaits++;
                // Rounded up, so the timer never fires before the gate opens.
                long ms = ((nextDueQpc - now) * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency;
                gateTimer.Change(ms, Timeout.Infinite);
            }
            return;
        }

        if (firstQpc < 0)
            firstQpc = pendingQpc;

        // A resend is stamped when offered (DECISIONS 5.4), so an image
        // presented just before that stamp but acquired after it would go
        // backwards. The time keys inFlight here and the phone's pts there,
        // so it must only increase.
        if (pendingQpc <= lastSubmittedQpc)
        {
            pendingQpc = lastSubmittedQpc + 1;
            Clamped++;
        }
        lastSubmittedQpc = pendingQpc;

        // Media Foundation time is 100ns units. QPC ticks are converted
        // through the counter's frequency, which is 10MHz on this machine
        // but is not guaranteed to be. Present times only increase, so every
        // sample time is distinct and can key inFlight.
        long time = (pendingQpc - firstQpc) * 10_000_000 / Stopwatch.Frequency;

        Nv12Frame f = pending;
        pending = null;
        credits--;
        inFlight[time] = (pendingQpc, f.BltQpc, now);
        // Asked for here, at submit, so the request is only taken when a
        // frame is actually going in to carry it.
        bool force = forceRequested | (sender?.TakeKeyframeRequest(Fps, Bitrate) ?? false);
        forceRequested = false;
        f.Owner.Submit(f, sample =>
        {
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            // Set immediately before the input it applies to, so there is
            // no doubt which frame the encoder makes the IDR.
            if (force)
            {
                codec.Set(CodecApi.ForceKeyFrame, 1, CodecApi.VT_UI4);
                forcedTime = time;
            }
            mft.ProcessInput(0, sample, 0);
        });
        // Keep the schedule. If this submit came more than an interval after
        // it was due (an idle screen, or no credit for a while), the old
        // schedule is in the past and would let a burst through to catch up,
        // so start a new one from now.
        nextDueQpc = nextDueQpc == 0 ? now + interval : nextDueQpc + interval;
        if (nextDueQpc <= now)
            nextDueQpc = now + interval;
        Submitted++;
    }

    // DECISIONS 5.3: the next frame submitted is encoded as an IDR. Called
    // after an ACCESS_LOST recovery, and later on phone reconnect.
    public void ForceKeyframe()
    {
        lock (gate)
            forceRequested = true;
    }

    // The sender calls this when it takes a frame to write, which may be
    // what TrySubmit was waiting for.
    public void Kick()
    {
        lock (gate)
            if (!disposed)
                TrySubmit();
    }

    // On a thread pool thread, once the gate has opened.
    void OnGateTimer()
    {
        try
        {
            lock (gate)
            {
                if (disposed || timerDueQpc == 0)
                    return;
                TimerLateMs.Add(Ms(Stopwatch.GetTimestamp() - timerDueQpc));
                timerDueQpc = 0;
                TrySubmit();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    static double Ms(long qpcTicks) => qpcTicks * 1000.0 / Stopwatch.Frequency;

    // Called by the pump on a Media Foundation thread.
    void OnEvent(IMFMediaEvent ev)
    {
        lock (gate)
        {
            if (disposed)
                return;
            ev.Status.CheckError();

            switch (ev.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    NeedInputs++;
                    credits++;
                    TrySubmit();
                    break;
                case MediaEventTypes.TransformHaveOutput:
                    Collect();
                    break;
                default:
                    OtherEvents[ev.EventType] = OtherEvents.GetValueOrDefault(ev.EventType) + 1;
                    break;
            }
        }
    }

    // Under the lock. The encoder provides its own output samples
    // (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES, spike 5), so the buffer goes in
    // with no sample and comes back with one this code owns.
    void Collect()
    {
        var db = new OutputDataBuffer { StreamID = 0 };
        mft.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _).CheckError();
        db.Events?.Dispose();
        using IMFSample sample = db.Sample;
        long outQpc = Stopwatch.GetTimestamp();
        lastOutputQpc = outQpc;

        long presentQpc = 0;
        if (inFlight.Remove(sample.SampleTime, out var times))
        {
            presentQpc = times.present;
            PresentToBltMs.Add(Ms(times.blt - times.present));
            BltToSubmitMs.Add(Ms(times.submit - times.blt));
            SubmitToOutMs.Add(Ms(outQpc - times.submit));
            BltToOutMs.Add(Ms(outQpc - times.blt));
        }
        else
        {
            UnmatchedOutputs++;
        }

        bool keyframe = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint clean).Success && clean != 0;
        if (keyframe)
            Keyframes++;
        if (sample.SampleTime == forcedTime)
        {
            forcedResults.Add($"forced IDR on output #{Outputs + 1} (sample time {forcedTime / 10_000.0:F1}ms): " +
                              $"{(keyframe ? "keyframe" : "NOT a keyframe")}");
            forcedTime = -1;
        }

        // The only CPU copy on this path, and it is the compressed output:
        // about 170KB a frame, against 8MB of NV12.
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr data, out _, out int length);
        try
        {
            if (copy.Length < length)
                copy = new byte[length * 2];
            Marshal.Copy(data, copy, 0, length);
        }
        finally
        {
            buffer.Unlock();
        }
        sink?.Write(copy, 0, length);
        sender?.Put(copy, length, keyframe, presentQpc, outQpc, Fps, Bitrate);
        Outputs++;
        OutputBytes += length;
    }

    void Fail(Exception ex)
    {
        lock (gate)
            Error ??= ex.ToString();
    }

    // Shared by both media types. Colour per DECISIONS 11.7: BT.709 limited
    // range, which is what the video processor writes. Tagging the types
    // tells the encoder what to write into the stream's VUI.
    static void SetVideoCommon(IMFMediaType t, uint width, uint height, uint fps)
    {
        t.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | height).CheckError();
        t.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fps << 32) | 1).CheckError();
        t.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709).CheckError();
        t.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709).CheckError();
        t.Set(MediaTypeAttributeKeys.YuvMatrix, MatrixBt709).CheckError();
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)NominalRange.Range16_235).CheckError();
    }

    // Attribute reads for the report. Absent prints as "unset", which is
    // itself a finding.
    static string Read32(IMFAttributes a, Guid key) =>
        a.GetUInt32(key, out uint v).Success ? v.ToString() : "unset";

    static ulong? Read64(IMFAttributes a, Guid key) =>
        a.GetUInt64(key, out ulong v).Success ? v : null;

    static string Unpack(ulong? v, char sep = 'x') =>
        v is ulong x ? $"{x >> 32}{sep}{x & 0xFFFFFFFF}" : "unset";

    // Milliseconds the encoder has held input without producing output, or 0
    // when nothing is inside it. A hung MFT shows here, since no thread of
    // ours is blocked on it to show it any other way.
    public double StalledMs
    {
        get
        {
            lock (gate)
            {
                if (inFlight.Count == 0)
                    return 0;
                long since = Math.Max(lastOutputQpc, inFlight.Values.Min(v => v.submit));
                return Ms(Stopwatch.GetTimestamp() - since);
            }
        }
    }

    // No drain: frames still inside are dropped. Shutdown is the async MFT's
    // own teardown. It does not complete the pending BeginGetEvent: on
    // AMDh264Encoder no Invoke arrived within 6s of Shutdown (measured
    // 2026-10-09), so nothing waits for the pump to stop. An Invoke that
    // does arrive late finds disposed set under the lock and touches
    // nothing. The pump itself is not disposed: Media Foundation may still
    // hold a reference to it, and drops it when it lets go.
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending?.Release();
            pending = null;
        }
        gateTimer.Dispose();
        using (IMFShutdown shutdown = mft.QueryInterface<IMFShutdown>())
            shutdown.Shutdown();

        // Under the lock, so no Invoke is between its disposed check and
        // its use of events.
        lock (gate)
            events.Dispose();
        codec.Dispose();
        mft.Dispose();
        activate.ShutdownObject();
        activate.Dispose();
        manager.Dispose();
    }

    // The BeginGetEvent callback. Each Invoke collects one event, handles it
    // and asks for the next, so exactly one request is outstanding at a time.
    // An exception must not escape onto a Media Foundation thread, so all are
    // caught and handed to the encoder as Error, which also ends the pump.
    sealed class EventPump : CallbackBase, IMFAsyncCallback
    {
        readonly H264Encoder owner;

        public EventPump(H264Encoder owner) => this.owner = owner;

        public Result GetParameters(out AsyncCallbackFlags flags, out int queue)
        {
            flags = 0;
            queue = 0;
            return Result.NotImplemented;  // documented as "use the defaults"
        }

        // result is not disposed: the wrapper Vortice passes in owns no
        // reference (measured 2026-10-09). EndGetEvent's return does.
        //
        // The whole body is under the encoder's lock, so Dispose cannot
        // release events between the disposed check and the calls on it.
        // OnEvent takes the same lock again; a C# lock is re-entrant on the
        // thread that holds it.
        public Result Invoke(IMFAsyncResult result)
        {
            try
            {
                lock (owner.gate)
                {
                    if (owner.disposed)
                        return Result.Ok;
                    IMFMediaEvent ev;
                    try
                    {
                        ev = owner.events.EndGetEvent(result);
                    }
                    catch (SharpGenException ex) when (ex.ResultCode == Vortice.MediaFoundation.ResultCode.Shutdown)
                    {
                        return Result.Ok;  // the MFT was shut down; never seen on AMD
                    }
                    using (ev)
                        owner.OnEvent(ev);
                    if (!owner.disposed)
                        owner.events.BeginGetEvent(this, null);
                }
            }
            catch (Exception ex)
            {
                owner.Fail(ex);
            }
            return Result.Ok;
        }
    }
}
