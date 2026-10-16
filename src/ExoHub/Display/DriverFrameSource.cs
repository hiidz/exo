using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static ExoHub.Display.ExoDisplayDriver;

namespace ExoHub.Display;

enum FrameKind
{
    Timeout,   // no new image within the wait
    NewImage,  // a new desktop image, handed to onNewImage
    Lost,      // the texture was replaced or is gone; Drop, then Reopen
}

// The display source: ExoDisplay's own copy of each desktop image, through
// the texture the driver shares (exo_display.h, ExoFrameInfo). Windows hands
// every image of the monitor to the driver, the secure desktop's included,
// so the UAC prompt and lock screen lose nothing: a mode change or a new render adapter
// brings a new texture, and an unplug takes it away.
//
// Owns the D3D11 device, made on the render adapter the driver names at the
// first Reopen, and kept: the converter and encoder are built on it.
sealed class DriverFrameSource : IDisposable
{
    const uint WaitTimeout = 0x102;
    // DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE.
    const uint SharedReadWrite = 0x80000001;

    readonly VirtualDisplay monitor;
    readonly bool debugLayer;
    readonly byte[] frameBuffer = new byte[FrameInfoSize];
    SafeFileHandle driver;      // this source's own handle, reopened after a driver restart
    ID3D11Device device;        // null until the first successful Reopen
    Luid deviceAdapter;
    ID3D11Texture2D texture;    // null while lost
    IDXGIKeyedMutex mutex;
    uint generation;

    public DriverFrameSource(VirtualDisplay monitor, bool debugLayer)
    {
        this.monitor = monitor;
        this.debugLayer = debugLayer;
    }

    public bool IsOpen => texture != null;
    public ID3D11Device Device => device;
    public ID3D11DeviceContext Context { get; private set; }

    // The texture's geometry, from the texture itself, and where the monitor
    // is on the desktop. A mode change brings a new texture, so these are
    // refreshed on every Reopen; a move only changes Output.
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public Format Format { get; private set; }
    public Rational RefreshRate { get; private set; }
    public OutputInfo Output { get; private set; }
    public string Name { get; private set; }

    // The image last handed to onNewImage, as the driver described it.
    public FrameInfo Last { get; private set; }

    // Opens the driver's current texture. Throws InvalidOperationException
    // or SharpGenException on any failure; the caller retries.
    public void Reopen()
    {
        Drop();

        if (driver == null)
        {
            driver = ExoDisplayDriver.Open(out string why, out _);
            if (driver == null)
                throw new InvalidOperationException($"ExoDisplay {why}");
        }
        FrameInfo f = GetFrame();
        if (f.Generation == 0)
            throw new InvalidOperationException("ExoDisplay has no image yet");

        if (device == null)
        {
            device = CreateDevice(f.RenderAdapter);
            deviceAdapter = f.RenderAdapter;
        }
        else if (!f.RenderAdapter.Equals(deviceAdapter))
        {
            // The converter and encoder live on the device, so a new render
            // adapter needs them all rebuilt. Not built: render adapters do
            // not change on this machine.
            throw new InvalidOperationException(
                $"render adapter is now {Hex(f.RenderAdapter)}, device is on {Hex(deviceAdapter)}");
        }

        IntPtr p;
        using (var device1 = device.QueryInterface<ID3D11Device1>())
            new Result(ComCalls.OpenSharedResourceByName(device1, f.Name, SharedReadWrite,
                                                         typeof(ID3D11Texture2D).GUID, out p)).CheckError();
        var opened = new ID3D11Texture2D(p);
        Name = f.Name;
        if (!TryPlacement(out OutputInfo o, out Rational hz))
        {
            opened.Dispose();
            throw new InvalidOperationException($"{VirtualDisplay.MonitorId} is not on the desktop");
        }

        texture = opened;
        mutex = texture.QueryInterface<IDXGIKeyedMutex>();
        generation = f.Generation;
        var d = texture.Description;
        Width = d.Width;
        Height = d.Height;
        Format = d.Format;
        Output = o;
        RefreshRate = hz;
    }

    // Reads where the monitor is again. False when it is no longer on the
    // desktop; moved says whether its rectangle changed.
    public bool RefreshPlacement(out bool moved)
    {
        moved = false;
        if (!TryPlacement(out OutputInfo o, out Rational hz))
            return false;
        moved = o != Output;
        Output = o;
        RefreshRate = hz;
        return true;
    }

    bool TryPlacement(out OutputInfo o, out Rational hz)
    {
        o = null;
        hz = default;
        if (!monitor.TryGetTarget(out uint low, out int high, out uint target) ||
            !Topology.Placement(low, high, target, out var r, out var rate, out int turns))
            return false;
        o = new OutputInfo(0, 0, "ExoDisplay", Name ?? "-", VirtualDisplay.MonitorId, r.x, r.y, r.w, r.h, turns);
        hz = new Rational(rate.num, rate.den);
        return true;
    }

    // Releases the texture and keeps the device. Safe to call when lost.
    public void Drop()
    {
        mutex?.Dispose();
        mutex = null;
        texture?.Dispose();
        texture = null;
    }

    public Result DeviceRemovedReason => device?.DeviceRemovedReason ?? Result.Ok;

    // Waits up to timeoutMs for a new image and hands it to onNewImage while
    // the texture is held, then lets the driver have it back.
    //
    // The texture is only the hub's inside onNewImage: the driver copies the
    // next image in once it is released. A callback rather than returning
    // the texture means no caller can hold it across the next wait.
    public FrameKind Next(int timeoutMs, Action<ID3D11Texture2D> onNewImage, out long presentQpc, out string lost)
    {
        presentQpc = 0;
        lost = null;
        var r = new Result(ComCalls.AcquireSync(mutex, 1, (uint)timeoutMs));
        if ((uint)r.Code == WaitTimeout)
        {
            // Nothing new. A new or vanished texture never signals this one,
            // so it is checked for here.
            FrameInfo now = GetFrame();
            if (now.Generation == generation)
                return FrameKind.Timeout;
            lost = now.Generation == 0 ? "the texture is gone" : $"the texture was replaced by {now.Name}";
            return FrameKind.Lost;
        }
        if (r.Code != 0)
        {
            // WAIT_ABANDONED: the driver's host died holding it.
            lost = $"AcquireSync returned 0x{r.Code:X8}";
            return FrameKind.Lost;
        }

        try
        {
            // While held, the driver cannot copy a newer image in, so this
            // describes the image in the texture.
            Last = GetFrame();
            presentQpc = Last.PresentQpc;
            onNewImage(texture);
        }
        finally
        {
            mutex.ReleaseSync(0);
        }
        return FrameKind.NewImage;
    }

    // The driver's ExoFrameInfo. A failed call means the driver went away:
    // the handle is closed for the next Reopen, and Generation reads 0.
    FrameInfo GetFrame()
    {
        if (driver == null || Ioctl(driver, IoctlGetFrame, null, frameBuffer, out _) != 0)
        {
            driver?.Dispose();
            driver = null;
            return default;
        }
        return FrameInfo.Read(frameBuffer);
    }

    // DriverType must be Unknown when an explicit adapter is passed.
    // VideoSupport is what the BGRA to NV12 stage needs (spike 5).
    ID3D11Device CreateDevice(Luid luid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(luid);
        var flags = DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport;
        if (debugLayer)
            flags |= DeviceCreationFlags.Debug;

        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels,
                                out ID3D11Device created, out FeatureLevel _,
                                out ID3D11DeviceContext context).CheckError();
        Context = context;

        // The encoder MFT calls into this device from its own threads.
        // Without this the symptom is intermittent corruption rather than an
        // error (spike 5).
        using (var mt = created.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        return created;
    }

    static string Hex(Luid l) => $"{l.HighPart:X8}:{l.LowPart:X8}";

    public void Dispose()
    {
        Drop();
        Context?.Dispose();
        device?.Dispose();
        driver?.Dispose();
    }
}
