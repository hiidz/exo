// Registers ExoCamera.dll's media source as a virtual camera and holds it
// for the hub's lifetime. The DLL must already be registered in HKLM.
//
// Session lifetime and current-user access: the camera exists only while
// the hub holds it, and no administrator is needed. With session lifetime
// Multi-App Camera resets to Off on every hub start.
//
// Windows reports whether anyone is streaming through a callback passed to
// IMFVirtualCamera.Start: SOURCE_START when the first consumer starts,
// SOURCE_STOP when the last one stops. They switch L7's frames on and off;
// while off, the DLL gets IDLE and shows its test card.
//
// The callback is a COM object implemented in C#. SharpGen's CallbackBase
// supplies QueryInterface, AddRef and Release and builds the native vtable
// whose slots call Invoke and GetParameters, so Windows sees an ordinary
// COM pointer. The garbage collector cannot see Windows' copy of that
// pointer, so this class keeps the callback in a field for the camera's
// whole life.

using System.Diagnostics;
using ExoHub.Link;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ExoHub.Camera;

sealed class VirtualCamera : IDisposable
{
    // exo_guid.h. The CLSID is what Windows loads; the two must match.
    const string ClsidString = "{5BB30600-762E-49D3-BB33-F419E5D54EC6}";
    // Windows appends "Windows Virtual Camera" itself, in some lists.
    const string FriendlyName = "Exo Camera";

    // MF_FRAMESERVER_VCAMEVENT_EXTENDED_*, from mfvirtualcamera.h (SDK
    // 10.0.26100). Vortice does not carry them.
    static readonly Dictionary<Guid, string> EventNames = new()
    {
        [new("e52c4dff-e46d-4d0b-bc75-ddd4c8723f96")] = "SOURCE_INITIALIZE",
        [new("b1eeb989-b456-4f4a-ae40-079c28e24af8")] = "SOURCE_START",
        [new("b7fe7a61-fe91-415e-8608-d37dedb1a58b")] = "SOURCE_STOP",
        [new("a0ebaba7-a422-4e33-8401-b37d2800aa67")] = "SOURCE_UNINITIALIZE",
        [new("45a81b31-43f8-4e5d-8ce2-22dce026996d")] = "PIPELINE_SHUTDOWN",
        [new("6e59489c-47d3-4467-83ef-12d34e871665")] = "CUSTOM_EVENT",
    };

    readonly Stopwatch clock;
    readonly CameraLink link;
    IMFVirtualCamera cam;
    readonly Callback callback;

    // Windows calls these on a Media Foundation work queue thread.
    sealed class Callback(VirtualCamera owner) : CallbackBase, IMFAsyncCallback
    {
        // Not implemented means "use the default work queue". C# requires
        // out parameters to be assigned even so.
        public Result GetParameters(out AsyncCallbackFlags flags, out int queue)
        {
            flags = AsyncCallbackFlags.None;
            queue = 0;
            return Result.NotImplemented;
        }

        public Result Invoke(IMFAsyncResult result)
        {
            owner.OnEvent(result);
            return Result.Ok;
        }
    }

    readonly object gate = new();  // one registration change at a time
    bool disposed;                 // under gate

    VirtualCamera(Stopwatch clock, CameraLink link)
    {
        this.clock = clock;
        this.link = link;
        callback = new Callback(this);
    }

    // After MFStartup, at link's format. Returns null after logging why, so
    // the hub runs on without a camera: nothing else depends on it, and link
    // keeps sending frames to any client.
    public static VirtualCamera Register(Stopwatch clock, CameraLink link)
    {
        var v = new VirtualCamera(clock, link);
        if (v.Add())
            return v;
        v.callback.Dispose();
        return null;
    }

    // The camera phone's format. Windows reads a camera's formats only when
    // it is registered, so a new one is registered again: an app streaming
    // from the camera loses it, and the next to open it gets the new one,
    // which the DLL's new source asks link for.
    public void Use(CameraFormat format)
    {
        lock (gate)
        {
            if (disposed || cam == null || format == link.Format)
                return;
            Log($"format {link.Format} to {format}: registering again");
            RemoveLocked();
            link.Format = format;
            Add();
        }
    }

    // Registers and starts the camera. False after logging why.
    bool Add()
    {
        Result r = MediaFactory.MFCreateVirtualCamera(
            VirtualCameraType.SoftwareCameraSource, VirtualCameraLifetime.Session, VirtualCameraAccess.CurrentUser,
            FriendlyName, ClsidString, null, 0, out IMFVirtualCamera created);
        Log($"MFCreateVirtualCamera 0x{r.Code:X8}, {ClsidString}, session lifetime, current user, {link.Format}");
        if (r.Failure)
        {
            if (r == Result.AccessDenied)
                Log("REFUSED: access denied. Camera privacy may deny desktop apps (Settings > Privacy & security > Camera)");
            return false;
        }

        // Before Start, so a SOURCE_START cannot come first: a camera that
        // does not exist yet has nobody watching it.
        link.SetWatching(false, "camera registering, nobody streaming yet");
        cam = created;
        r = cam.Start(callback);
        Log($"Start 0x{r.Code:X8}");
        if (r.Failure)
        {
            RemoveLocked();
            link.SetWatching(true, "no camera registered");
            return false;
        }
        Log($"symbolic link {cam.GetString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink)}");
        return true;
    }

    void Log(string line) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s vcam {line}");

    // Nothing may escape into Windows, so everything is caught here. The
    // event is on GetObject; the result itself is not one, and its
    // state is empty. The result is Windows' and is not released here; the
    // object and the event are ours.
    void OnEvent(IMFAsyncResult result)
    {
        try
        {
            using var obj = result.GetObject() as ComObject;
            using IMFMediaEvent e = obj?.QueryInterfaceOrNull<IMFMediaEvent>();
            if (e == null)
            {
                Log($"callback with no event, status 0x{result.Status.Code:X8}, object {(obj == null ? "none" : "not an event")}");
                return;
            }
            string what = e.EventType == MediaEventTypes.ExtendedType
                ? EventNames.GetValueOrDefault(e.ExtendedType, $"extended {e.ExtendedType}")
                : e.EventType.ToString();
            Log($"event {what}, status 0x{e.Status.Code:X8}");
            if (what == "SOURCE_START")
                link.SetWatching(true, "SOURCE_START");
            else if (what == "SOURCE_STOP")
                link.SetWatching(false, "SOURCE_STOP");
        }
        catch (Exception ex)
        {
            Log($"callback FAILED: {ex.GetType().Name}: {ex.Message.Trim()}");
        }
    }

    // Removes the camera, which shuts down the source in the Frame Server.
    // Safe to call twice: Program.cs calls it before L7 closes, and the
    // using declaration calls it again on the way out.
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            RemoveLocked();
            callback.Dispose();
        }
    }

    // Under gate, or before the camera is shared.
    void RemoveLocked()
    {
        if (cam == null)
            return;
        Result r = cam.Remove();
        Log($"Remove 0x{r.Code:X8}");
        cam.Dispose();
        cam = null;
    }
}
