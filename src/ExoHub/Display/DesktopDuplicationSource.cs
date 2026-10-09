using SharpGen.Runtime;
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
// Owns the D3D11 device and the duplication object. The device survived all
// twelve ACCESS_LOST events in spike 2; only the duplication had to be
// rebuilt. That recovery is the next step, so for now a loss is reported and
// the caller exits.
sealed class DesktopDuplicationSource : IDisposable
{
    readonly ID3D11Device device;
    readonly IDXGIOutputDuplication dup;

    public uint Width { get; }
    public uint Height { get; }
    public Format Format { get; }

    DesktopDuplicationSource(ID3D11Device device, IDXGIOutputDuplication dup)
    {
        this.device = device;
        this.dup = dup;
        var mode = dup.Description.ModeDescription;
        Width = mode.Width;
        Height = mode.Height;
        Format = mode.Format;
    }

    // Opens the output the enumeration found, on the adapter that owns it.
    // DuplicateOutput needs a device created on that adapter, so the device
    // is created here rather than shared in from outside.
    //
    // Throws SharpGenException if DuplicateOutput fails. Vortice throws on
    // that call and returns a Result from AcquireNextFrame, which matches
    // how rare each failure is.
    public static DesktopDuplicationSource Open(IDXGIFactory1 factory, OutputInfo target)
    {
        factory.EnumAdapters1(target.AdapterIndex, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs(target.OutputIndex, out IDXGIOutput output).CheckError();
            using (output)
            {
                // The indexes came from an enumeration moments ago. If the
                // topology changed in between, refuse rather than duplicate
                // the wrong display.
                if (output.Description.DeviceName != target.GdiName)
                    throw new InvalidOperationException(
                        $"output {target.AdapterIndex}.{target.OutputIndex} is now " +
                        $"{output.Description.DeviceName}, expected {target.GdiName}");

                // DriverType must be Unknown when an explicit adapter is passed.
                // VideoSupport is what the BGRA to NV12 stage will need (spike 5);
                // BgraSupport is what duplication needs.
                FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                                        DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                                        levels, out ID3D11Device device, out FeatureLevel _,
                                        out ID3D11DeviceContext context).CheckError();
                context.Dispose();

                // DuplicateOutput lives on IDXGIOutput1. QueryInterface is
                // COM's cast: the same object, asked for another interface.
                try
                {
                    using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    return new DesktopDuplicationSource(device, output1.DuplicateOutput(device));
                }
                catch
                {
                    device.Dispose();
                    throw;
                }
            }
        }
    }

    // Waits up to timeoutMs for the next frame, classifies it, and releases
    // it. Nothing consumes the texture yet, so it is not held.
    //
    // Returns the AcquireNextFrame result. WAIT_TIMEOUT comes back as
    // success with FrameKind.Timeout. Any other failure, ACCESS_LOST
    // included, is returned for the caller to report.
    public Result Next(uint timeoutMs, out FrameKind kind, out OutduplFrameInfo info)
    {
        Result r = dup.AcquireNextFrame(timeoutMs, out info, out IDXGIResource resource);

        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            // No frame was acquired, so there is nothing to release.
            kind = FrameKind.Timeout;
            return Result.Ok;
        }
        if (r.Failure)
        {
            kind = FrameKind.Timeout;
            return r;
        }

        // Release every acquired frame before the next acquire, including the
        // cursor-only ones we do not want. Skipping it produces failures that
        // look unrelated to their cause.
        resource.Dispose();
        dup.ReleaseFrame();

        kind = info.LastPresentTime > 0 ? FrameKind.NewImage : FrameKind.CursorOnly;
        return Result.Ok;
    }

    public void Dispose()
    {
        dup.Dispose();
        device.Dispose();
    }
}
