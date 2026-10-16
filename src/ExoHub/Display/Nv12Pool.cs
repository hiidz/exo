using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ExoHub.Display;

// One NV12 texture that is both a video processor target and an encoder
// input. Built once and reused for the life of its pool.
sealed class Nv12Frame
{
    public Nv12Pool Owner;
    public ID3D11Texture2D Texture;
    public ID3D11VideoProcessorOutputView View;

    // When the Blt into this frame was submitted, as a QPC value. Set by the
    // converter; the start of the stage "Blt submit to sample out".
    public long BltQpc;

    // Null while the encoder holds the frame. The pool must hold no
    // reference to the sample then, or its count never reaches zero and the
    // return callback never fires.
    public IMFSample Sample;
    public ReturnCallback Return;

    // Back to the pool without going through the encoder: a frame replaced
    // before it was submitted.
    public void Release() => Owner.Put(this, null);
}

// The NV12 textures the converter writes and the encoder reads.
//
// The encoder keeps input samples after ProcessInput returns
// (MFT_INPUT_STREAM_HOLDS_BUFFERS), so one reused texture would be
// overwritten while the encoder may still read it. Every frame is a tracked
// sample: IMFTrackedSample.SetAllocator arms a one-shot callback that fires
// when the last reference to the sample is released, and that callback puts
// the frame back on the free list. That is how the hub learns the encoder is
// done with a texture.
//
// Grows on demand up to Cap, so Allocated ends up as the most frames ever in
// use at once: the pool size, measured rather than guessed.
//
// Per geometry, like the converter that owns it. Callbacks arrive on Media
// Foundation threads, so all state is under one lock.
sealed class Nv12Pool : IDisposable
{
    public const int Cap = 16;

    // Every frame the encoder holds right now, in any pool. SharpGen keeps
    // only a weak reference from a callback's native side to the C# object,
    // so while the encoder holds a frame, something managed must hold its
    // ReturnCallback or the GC frees it and the return call fails with
    // "Shadow ... is dead". Nothing else references a submitted frame: it
    // is off the free list and out of pending. Measured 2026-10-11: a
    // 600s run died at 124.9s, and with a full GC every loop iteration the
    // same build died at 0.46s. Static, because after a resize the old pool
    // is itself unreferenced while the old encoder still holds its frames.
    static readonly HashSet<Nv12Frame> AtEncoder = new();

    readonly ID3D11Device device;
    readonly Func<ID3D11Texture2D, ID3D11VideoProcessorOutputView> makeView;
    readonly uint width, height;
    readonly object gate = new();
    readonly Stack<Nv12Frame> free = new();
    bool disposed;

    public int Allocated { get; private set; }
    public int InUse { get; private set; }
    public int MaxInUse { get; private set; }
    public int Exhausted { get; private set; }     // Take found nothing free at Cap
    public int EncoderReturns { get; private set; } // tracked-sample callbacks fired
    public string Error { get; private set; }       // first failure inside a callback

    public Nv12Pool(ID3D11Device device, uint width, uint height,
                    Func<ID3D11Texture2D, ID3D11VideoProcessorOutputView> makeView)
    {
        this.device = device;
        this.width = width;
        this.height = height;
        this.makeView = makeView;
    }

    // A free frame, a new one if none is free and the pool is under Cap, or
    // null when it is at Cap. Null means every frame is in use: the caller
    // drops the image.
    public Nv12Frame Take()
    {
        lock (gate)
        {
            Nv12Frame f;
            if (free.Count > 0)
                f = free.Pop();
            else if (Allocated < Cap)
                f = Create();
            else
            {
                Exhausted++;
                return null;
            }
            InUse++;
            MaxInUse = Math.Max(MaxInUse, InUse);
            return f;
        }
    }

    // Hands the frame's sample to use, which passes it to the encoder, then
    // drops the pool's reference. From then on only the encoder holds it,
    // and the return callback fires when it lets go.
    public void Submit(Nv12Frame f, Action<IMFSample> use)
    {
        // Re-armed on every use: the callback is one-shot.
        using (IMFTrackedSample tracked = f.Sample.QueryInterface<IMFTrackedSample>())
            tracked.SetAllocator(f.Return, null);

        lock (AtEncoder)
            AtEncoder.Add(f);
        IMFSample s = f.Sample;
        f.Sample = null;
        try
        {
            use(s);
        }
        finally
        {
            // If use threw, nothing else holds the sample, so this Dispose
            // fires the callback at once and the frame still comes back.
            s.Dispose();
        }
    }

    // Puts a frame back. sample is null for a frame that never left the
    // pool, and the revived sample when it comes back from the encoder.
    internal void Put(Nv12Frame f, IMFSample sample)
    {
        lock (AtEncoder)
            AtEncoder.Remove(f);
        lock (gate)
        {
            if (sample != null)
            {
                f.Sample = sample;
                EncoderReturns++;
            }
            InUse--;
            if (disposed)
                Destroy(f);
            else
                free.Push(f);
        }
    }

    internal void Fail(Exception ex)
    {
        lock (gate)
            Error ??= ex.ToString();
    }

    // RenderTarget is what the video processor's output view requires.
    // The sample wraps the texture through a DXGI surface
    // buffer, so the encoder reads the texture itself, never a copy.
    // MFCreateVideoSampleFromSurface makes a sample that implements
    // IMFTrackedSample; MFCreateSample does not.
    Nv12Frame Create()
    {
        var f = new Nv12Frame { Owner = this };
        f.Texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        f.View = makeView(f.Texture);
        MediaFactory.MFCreateVideoSampleFromSurface(null, out f.Sample).CheckError();
        using (IMFMediaBuffer buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, f.Texture, 0, false))
            f.Sample.AddBuffer(buffer);
        f.Return = new ReturnCallback(f);
        Allocated++;
        return f;
    }

    static void Destroy(Nv12Frame f)
    {
        f.Sample?.Dispose();
        f.Return.Dispose();
        f.View.Dispose();
        f.Texture.Dispose();
    }

    // Frames on the free list go now. Frames the encoder still holds go when
    // their callback brings them back, which happens when the encoder that
    // holds them is released.
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            while (free.Count > 0)
                Destroy(free.Pop());
        }
    }
}

// The tracked-sample callback. Media Foundation calls Invoke on one of its
// own threads once the encoder has released the frame's sample.
sealed class ReturnCallback : CallbackBase, IMFAsyncCallback
{
    readonly Nv12Frame frame;

    public ReturnCallback(Nv12Frame frame) => this.frame = frame;

    public Result GetParameters(out AsyncCallbackFlags flags, out int queue)
    {
        flags = 0;
        queue = 0;
        return Result.NotImplemented;  // documented as "use the defaults"
    }

    // result is not disposed: the wrapper Vortice passes in owns no
    // reference (measured 2026-10-09). GetObject's return does own one. An
    // exception must not escape onto a native thread, so all are caught.
    public Result Invoke(IMFAsyncResult result)
    {
        try
        {
            using var obj = (ComObject)result.GetObject();
            frame.Owner.Put(frame, obj.QueryInterface<IMFSample>());
        }
        catch (Exception ex)
        {
            frame.Owner.Fail(ex);
        }
        return Result.Ok;
    }
}
