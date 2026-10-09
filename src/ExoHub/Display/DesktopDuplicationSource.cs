using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExoHub.Display;

enum FrameKind
{
    Timeout,     // nothing happened on the display; no frame object at all
    NewImage,    // the desktop image changed (LastPresentTime > 0)
    CursorOnly,  // only the pointer changed; the texture is blank, never encode it
}

// The display source behind DECISIONS 4.2: a frame, its geometry, and
// whether it is new. Desktop Duplication is the only implementation.
//
// Owns the DXGI factory, the D3D11 device and the duplication object, each
// with its own lifetime (DECISIONS 5.5). The device is created on the first
// Reopen and kept: it survived all twelve ACCESS_LOST events in spike 2.
// The duplication is dropped on every loss and rebuilt by Reopen. The
// factory is replaced whenever it reports itself stale.
sealed class DesktopDuplicationSource : IDisposable
{
    readonly string monitorId;
    readonly bool debugLayer;
    IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
    ID3D11Device device;           // null until the first successful Reopen
    Luid deviceAdapter;            // the adapter the device was created on
    IDXGIOutputDuplication dup;    // null while lost

    public bool IsOpen => dup != null;

    // The device and its immediate context, for whatever consumes frames.
    // Both are created on the first Reopen and live as long as the source.
    public ID3D11Device Device => device;
    public ID3D11DeviceContext Context { get; private set; }
    public Rational RefreshRate { get; private set; }

    // Geometry of the current duplication. A resolution change alters all
    // of these, so they are refreshed on every Reopen, never cached.
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public Format Format { get; private set; }
    public OutputInfo Output { get; private set; }

    // False once the factory's view of adapters and outputs is out of date.
    public bool FactoryIsCurrent => factory.IsCurrent;
    public int FactoriesCreated { get; private set; } = 1;

    public DesktopDuplicationSource(string monitorId, bool debugLayer)
    {
        this.monitorId = monitorId;
        this.debugLayer = debugLayer;
    }

    // Finds the target afresh and duplicates it. Called at startup and after
    // every loss.
    //
    // Enumerates every time: after a mode change the old IDXGIOutput is stale
    // (spike 2 step 4), and the GDI name is not stable across a driver
    // restart, so the target is matched by monitor ID only.
    //
    // A stale factory is replaced first. Re-enumerating a stale factory was
    // enough for the mode changes spike 2 saw, but after a VDD disable and
    // enable (measured 2026-10-09) the old factory kept reporting the display
    // missing while a fresh one found it at once.
    //
    // Throws on any failure: SharpGenException when a DXGI or D3D11 call
    // fails, InvalidOperationException when the target is missing or moved.
    // The caller treats every one of them as "retry".
    public void Reopen()
    {
        Drop();

        if (!factory.IsCurrent)
        {
            factory.Dispose();
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            FactoriesCreated++;
        }

        List<OutputInfo> matches = Outputs.List(factory).Where(o => o.MonitorId == monitorId).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"need exactly one output with monitor {monitorId}, found {matches.Count}");
        OutputInfo target = matches[0];

        factory.EnumAdapters1(target.AdapterIndex, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs(target.OutputIndex, out IDXGIOutput output).CheckError();
            using (output)
            {
                // The indexes came from an enumeration moments ago. One user
                // action produces a burst of topology changes, so this can
                // move in between. Refuse rather than duplicate the wrong one.
                if (output.Description.DeviceName != target.GdiName)
                    throw new InvalidOperationException(
                        $"output {target.AdapterIndex}.{target.OutputIndex} is now " +
                        $"{output.Description.DeviceName}, expected {target.GdiName}");

                Luid luid = adapter.Description1.Luid;
                if (device == null)
                {
                    device = CreateDevice(adapter);
                    deviceAdapter = luid;
                }
                else if (!luid.Equals(deviceAdapter))
                {
                    // DuplicateOutput needs a device on the adapter that owns
                    // the output. Spike 2 never saw the target move adapter.
                    // If this fires, the hub needs a device-rebuild path.
                    throw new InvalidOperationException(
                        $"target is now on adapter {Hex(luid)}, device is on {Hex(deviceAdapter)}");
                }

                // DuplicateOutput lives on IDXGIOutput1. QueryInterface is
                // COM's cast: the same object, asked for another interface.
                using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                dup = output1.DuplicateOutput(device);
            }
        }

        var mode = dup.Description.ModeDescription;
        Width = mode.Width;
        Height = mode.Height;
        Format = mode.Format;
        RefreshRate = mode.RefreshRate;
        Output = target;
    }

    // Releases the duplication and keeps the device. Safe to call when
    // already lost.
    public void Drop()
    {
        dup?.Dispose();
        dup = null;
    }

    // Why the device died, or S_OK if it is alive. Logged on every loss to
    // find out whether a VDD restart or removal takes the device with it.
    public Result DeviceRemovedReason => device?.DeviceRemovedReason ?? Result.Ok;

    // DriverType must be Unknown when an explicit adapter is passed.
    // VideoSupport is what the BGRA to NV12 stage needs (spike 5);
    // BgraSupport is what duplication needs. Debug adds the D3D11 debug
    // layer, which costs speed, so it is off for any timed run.
    ID3D11Device CreateDevice(IDXGIAdapter1 adapter)
    {
        var flags = DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport;
        if (debugLayer)
            flags |= DeviceCreationFlags.Debug;

        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels,
                                out ID3D11Device created, out FeatureLevel _,
                                out ID3D11DeviceContext context).CheckError();
        Context = context;

        // One thread today, but the encoder MFT will call into this device
        // from its own threads. Without this the symptom is intermittent
        // corruption rather than an error (spike 5), so it is on from the
        // start rather than added when the corruption shows up.
        using (var mt = created.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        return created;
    }

    static string Hex(Luid l) => $"{l.HighPart:X8}:{l.LowPart:X8}";

    // Waits up to timeoutMs for the next frame, classifies it, hands a
    // NewImage texture to onNewImage, and releases it.
    //
    // The texture is only valid inside onNewImage: ReleaseFrame gives it
    // back to DWM. A callback rather than returning the texture means no
    // caller can hold a frame across the next acquire. Cursor-only frames
    // are never passed on; their texture is blank.
    //
    // Returns the AcquireNextFrame result. WAIT_TIMEOUT comes back as
    // success with FrameKind.Timeout. Any other failure, ACCESS_LOST
    // included, is returned for the caller to report.
    public Result Next(uint timeoutMs, Action<ID3D11Texture2D> onNewImage,
                       out FrameKind kind, out OutduplFrameInfo info)
    {
        Result r = dup.AcquireNextFrame(timeoutMs, out info, out IDXGIResource resource);
        kind = FrameKind.Timeout;

        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
            return Result.Ok;  // no frame was acquired, so nothing to release
        if (r.Failure)
            return r;

        kind = info.LastPresentTime > 0 ? FrameKind.NewImage : FrameKind.CursorOnly;
        try
        {
            if (kind == FrameKind.NewImage)
            {
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                onNewImage(texture);
            }
        }
        finally
        {
            // Release every acquired frame before the next acquire, including
            // the cursor-only ones, and even when onNewImage threw. Skipping
            // it produces failures that look unrelated to their cause.
            // ReleaseFrame can report ACCESS_LOST too, so its result is
            // passed on rather than ignored.
            resource.Dispose();
            r = dup.ReleaseFrame();
        }
        return r;
    }

    public void Dispose()
    {
        Drop();
        Context?.Dispose();
        device?.Dispose();
        factory.Dispose();
    }
}
