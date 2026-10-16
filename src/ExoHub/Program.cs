// ExoHub.
//
// Plugs ExoDisplay's monitor while the display phone is connected
// (VirtualDisplay), takes each new image of it from the driver
// (DriverFrameSource), converts it to NV12 on the GPU while it is held, and
// offers it to the hardware encoder once it is released. Any loss drops the
// texture and reopens it, forever. The pointer comes from the driver on its
// own thread (DriverCursor). Quit in the tray, or Ctrl+C where there is a
// console, stops it and prints totals.
//
// Every device that reaches adb gets the reverse to a hub port of its own; the
// role is picked in the app on the phone, which then connects.
//
// One hub per Windows session: a second start shows the first one's window
// and exits. Its lines go to stdout when whoever started it reads that, and
// otherwise to a log file (HubLog).
//
// Usage: ExoHub [--debug] [--seconds N] [--dump F [--dump-after S]] [--out F]
//               [--no-reverse] [--tray] [--bitrate M | --quality Q]
// --debug     D3D11 debug layer on, messages printed as they arrive. Slow.
// --seconds N stop after N seconds, as if Ctrl+C was pressed.
// --dump F    write the first converted frame to F as raw NV12. The one
//             debug readback of the display path; nothing reads back
//             without it.
// --dump-after S  dump the first frame converted or resent after S
//             seconds instead, since the first after a plug is black.
// --out F     write the encoded stream to F as Annex-B H.264.
// --no-reverse  track devices but set up no reverse, so no app connects.
//             For the tools' collection runs after a measured one.
// --bitrate M  CBR at M Mbps at 30fps, in place of 40. For the Wi-Fi rate
//             control measurement; the phone's transport does not change it,
//             and over Wi-Fi it stays fixed.
// --quality Q  the encoder's quality mode at Q, 1 to 100, in place of CBR.
//             Also a measurement flag.
// --tray      start in the tray with no window: how Start with Windows
//             starts it. With no stdout, the window opens otherwise.
// Whatever the flags, the hub registers the virtual camera for as long as
// it runs and removes it on exit.

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ExoHub;
using ExoHub.Adb;
using ExoHub.Camera;
using ExoHub.Display;
using ExoHub.Link;
using ExoHub.Touch;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

// The longest the capture loop waits for an image, which can wait on
// nothing else. A new connection is noticed within this.
const double WakeMs = 100;

// Retry waits double from the first to the cap and stay there. Keyed to
// consecutive failed Reopen attempts, not to losses: one user action gives
// a burst of losses that each recover in milliseconds. The
// cap is the most a user waits after unlocking, so it stays short.
const int FirstRetryMs = 100;
const int MaxRetryMs = 1000;

// With nothing offered to the encoder for this long, the kept image is
// offered again: a still desktop presents nothing, so a phone connecting
// to it would otherwise show nothing.
const double HeartbeatMs = 500;

// The encoder's bitrate follows the display phone's rate and transport
// (DisplaySender.BitrateFor). --bitrate replaces it, --quality replaces CBR.
uint bitrateAt30 = 0, quality = 0;

// One hub per Windows session. A second asks the first to show its window
// and exits. Held until the hub exits.
using var single = new Mutex(true, @"Local\ExoHub", out bool firstHub);
if (!firstHub)
{
    Console.WriteLine("another hub runs in this session; asked it to show its window");
    if (EventWaitHandle.TryOpenExisting(@"Local\ExoHub.Show", out EventWaitHandle other))
        other.Set();
    return 0;
}
using var showAsked = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ExoHub.Show");

// Before any other line, so a hub with no stdout logs every one. After the
// check above, which would otherwise empty the running hub's file.
HubLog.Start();
// The encoder and pool failures below throw on purpose. With no console the
// hub would vanish, so the exception goes to the log, and is shown.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.WriteLine($"FATAL {e.ExceptionObject}");
    if (HubLog.FilePath != null)
        System.Windows.Forms.MessageBox.Show($"Exo stopped: {(e.ExceptionObject as Exception)?.Message}\n\nLog: {HubLog.FilePath}",
                                             "Exo", System.Windows.Forms.MessageBoxButtons.OK,
                                             System.Windows.Forms.MessageBoxIcon.Error);
};

// 2 is PROCESS_PER_MONITOR_DPI_AWARE. Anything else means the manifest did
// not apply, and every rectangle printed below is suspect.
int awareness = Win32.GetAwarenessFromDpiAwarenessContext(Win32.GetThreadDpiAwarenessContext());
Console.WriteLine($"dpi awareness {awareness} {(awareness == 2 ? "per-monitor" : "NOT per-monitor")}");

using (IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
    foreach (OutputInfo o in Outputs.List(factory))
        Console.WriteLine($"  {o}");

bool debugLayer = false;
double seconds = 0;
string dumpPath = null;
double dumpAfter = 0;
string outPath = null;
bool reverse = true;
bool trayOnly = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--debug": debugLayer = true; break;
        case "--seconds": seconds = double.Parse(args[++i]); break;
        case "--dump": dumpPath = args[++i]; break;
        case "--dump-after": dumpAfter = double.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--no-reverse": reverse = false; break;
        case "--bitrate": bitrateAt30 = uint.Parse(args[++i]) * 1_000_000; break;
        case "--quality": quality = uint.Parse(args[++i]); break;
        case ExoHub.Ui.Settings.TrayFlag: trayOnly = true; break;
        default:
            Console.WriteLine($"unknown argument {args[i]}");
            return 2;
    }
}

var clock = Stopwatch.StartNew();

// The job first of all: every process started after it is born inside.
Job.JoinNew();
// Once per process, before any other Media Foundation call. This early so
// the camera decoder exists before the device listener can accept a camera.
MediaFactory.MFStartup(false).CheckError();
// L7 next, independent of the display path. Its port is
// fixed and held exclusively, so an orphaned hub fails this one here.
CameraLink camera;
try
{
    camera = new CameraLink(clock);
}
catch (SocketException ex)
{
    return Refuse($"{clock.Elapsed.TotalSeconds,8:F3}s camera REFUSED: port {Wire.CameraPort}: {ex.SocketErrorCode}. " +
                  "Another hub, or an orphan of one, holds it.");
}
// The virtual camera, held for the hub's lifetime.
// After L7, so an orphaned hub fails on the port before registering a
// second camera. Null if registration failed; the hub runs on without it.
using VirtualCamera vcam = VirtualCamera.Register(clock, camera);
// The one producer for L7, fed by the camera connection.
var decoder = new CameraDecoder(camera, clock);
// Null to refuse the camera role: no virtual camera for it to reach.
CameraDecoder cameraRole = vcam != null ? decoder : null;
// The listener binds each USB device a port, 0 so Windows picks it, held
// exclusively; with the session ID in WELCOME it names this hub.
using var display = new DisplaySender(clock) { BitrateOverride = bitrateAt30 };
var touch = new TouchInjector(clock);
// Before the listener, which tells it when the display connection comes
// and goes.
using var monitor = new VirtualDisplay(clock);
using var devices = new DeviceListener(clock, display, touch, cameraRole, vcam, monitor);
Console.WriteLine($"session {devices.SessionId:x16}, " +
                  $"camera role {(cameraRole != null ? "open" : "REFUSED")}, " +
                  $"reverse {(reverse ? "for every device, a loopback port each" : "for none")}");
// Wi-Fi: the phone asks on the LAN and dials the port in the answer.
using var discovery = Discovery.Start(clock, devices.WifiPort, devices.SessionId, out string wifiProblem);
Console.WriteLine($"Wi-Fi on 0.0.0.0:{devices.WifiPort}" + (discovery == null ? $", not found: {wifiProblem}" : ""));
var reverses = new ReverseSetup(devices.Listen, devices.Unlisten, clock);
// Off from the window, or refused while a foreign adb holds 5037; the hub
// runs on without it either way (AdbControl).
var adb = new AdbControl(clock, (serial, state) =>
{
    if (reverse)
        reverses.OnState(serial, state);
});
if (ExoHub.Ui.Settings.AdbEnabled)
    adb.Start();
else
    Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s adb off in settings");

using var source = new DriverFrameSource(monitor, debugLayer);
// The pointer the phone draws, from the driver.
using var pointer = new DriverCursor(clock, display, () => source.Output);
Console.WriteLine($"target monitor {VirtualDisplay.MonitorId}, heartbeat {HeartbeatMs}ms, wake {WakeMs}ms, " +
                  $"debug layer {(debugLayer ? "ON" : "off")}, " +
                  $"{(seconds > 0 ? $"stopping after {seconds}s" : "Quit in the tray to stop")}");
Console.WriteLine();

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (seconds > 0)
    stop.CancelAfter(TimeSpan.FromSeconds(seconds));

// Launched as an app, not by a tool reading stdout: Start with Windows is
// on from the first run, and the window opens unless started at sign-in.
if (HubLog.FilePath != null)
    ExoHub.Ui.Settings.FirstRun();
var tray = new ExoHub.Ui.Tray(
    () => new ExoHub.Ui.HubState(devices.Snapshot(), adb.Devices, adb.Problem, monitor.Unavailable,
                                 vcam == null ? "the camera could not be registered" : null, wifiProblem,
                                 devices.PairingProblem),
    () => stop.Cancel(),
    on =>
    {
        if (!on)
        {
            adb.Stop();
            return null;
        }
        return adb.Start();
    },
    show: HubLog.FilePath != null && !trayOnly);
devices.Ask = tray.Ask;
ThreadPool.RegisterWaitForSingleObject(showAsked, (_, _) => tray.Show(), null, -1, executeOnlyOnce: false);


// 1ms timers for the encoder's rate gate. How late the timer actually fires
// is measured and printed, rather than assumed from this call.
Win32.timeBeginPeriod(1);
// Windows 11 ignores timeBeginPeriod in a process with no visible window,
// which the hub is in the tray. Without this, while a browser plays video,
// the AMD encoder takes 31ms a frame instead of 9.5 and the display falls
// to 30fps (SuperDisplay falls the same way).
var throttling = new Win32.PowerThrottling
{
    Version = Win32.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
    ControlMask = Win32.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
    StateMask = 0,  // the control bit clear here: always honour the resolution
};
if (!Win32.SetProcessInformation(Process.GetCurrentProcess().Handle, Win32.ProcessPowerThrottling,
                                 ref throttling, Marshal.SizeOf<Win32.PowerThrottling>()))
    Console.WriteLine($"timer resolution may be ignored while hidden: error {Marshal.GetLastWin32Error()}");

// Created once the first Reopen has made the device, and kept with it.
Nv12Converter converter = null;

// Built for the converter's size, outside the frame hold, and rebuilt when
// that size changes. Every encoder writes to the same file, so a size
// change shows in it as a new SPS mid-stream.
H264Encoder encoder = null;
using FileStream sink = outPath != null ? File.Create(outPath) : null;

// Totals of encoders already replaced, so the end of run covers them all.
int encodersBuilt = 0, encSubmitted = 0, encReplaced = 0, encOutputs = 0, encKeyframes = 0;
int encGateWaits = 0, encUnmatched = 0, encClamped = 0;
long encBytes = 0;
var presentToBlt = new List<double>();
var bltToSubmit = new List<double>();
var submitToOut = new List<double>();
var bltToOut = new List<double>();
var timerLate = new List<double>();
void Retire(H264Encoder e)
{
    // Dispose first: until then events keep arriving and the counts move.
    e.Dispose();
    encSubmitted += e.Submitted; encReplaced += e.Replaced; encOutputs += e.Outputs;
    encKeyframes += e.Keyframes; encBytes += e.OutputBytes;
    encGateWaits += e.GateWaits; encUnmatched += e.UnmatchedOutputs; encClamped += e.Clamped;
    presentToBlt.AddRange(e.PresentToBltMs);
    bltToSubmit.AddRange(e.BltToSubmitMs);
    submitToOut.AddRange(e.SubmitToOutMs);
    bltToOut.AddRange(e.BltToOutMs);
    timerLate.AddRange(e.TimerLateMs);
}
int debugMessages = 0;

// Which texture objects the source hands out: one per driver texture. A
// per-frame input view could become a cached one; until then the view is
// made per frame.
var textures = new HashSet<IntPtr>();

// Per-frame Blt times, for the totals.
var submitMs = new List<double>();

// Runs inside Next, while the texture is held. Records what to print and
// returns, so printing never lengthens the hold.
bool rebuilt = false;
Texture2DDescription rebuiltFor = default;
Nv12Frame converted = null;  // handed to the encoder after the frame is released
bool poolEmpty = false;
void OnNewImage(ID3D11Texture2D texture)
{
    textures.Add(texture.NativePointer);
    converted = converter.Convert(texture, source.RefreshRate, out rebuilt);
    poolEmpty = converted == null;
    if (rebuilt)
        rebuiltFor = texture.Description;
}

var sinceLoss = Stopwatch.StartNew();  // startup counts as the first loss
int losses = 0, failedAttempts = 0;
int timeouts = 0, newImages = 0, frameGaps = 0;
long prevPresent = 0;
uint prevFrame = 0;
long lastOfferQpc = 0;  // 0: never, so the first resend is due at once
long lastPlacementQpc = 0;
int resends = 0, moves = 0;

while (!stop.IsCancellationRequested)
{
    string t = $"{clock.Elapsed.TotalSeconds,8:F3}s";

    if (!source.IsOpen)
    {
        // The monitor exists only while a display phone is connected, and
        // has images once Windows has it on the desktop.
        if (!monitor.Ready)
        {
            stop.Token.WaitHandle.WaitOne((int)WakeMs);
            sinceLoss.Restart();
            continue;
        }
        try
        {
            source.Reopen();
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            failedAttempts++;
            int waitMs = Math.Min(FirstRetryMs << Math.Min(failedAttempts - 1, 10), MaxRetryMs);
            // A SharpGenException message is several lines; the HRESULT is the content.
            string why = ex is SharpGenException sg ? $"0x{sg.ResultCode.Code:X8}" : ex.Message;
            Console.WriteLine($"{t} attempt {failedAttempts} failed {why} " +
                              $"removed=0x{source.DeviceRemovedReason.Code:X8}, retry in {waitMs}ms");
            stop.Token.WaitHandle.WaitOne(waitMs);
            continue;
        }

        OutputInfo o = source.Output;
        Console.WriteLine($"{t} {(losses == 0 ? "opened" : "recovered")} after {failedAttempts} failed attempts " +
                          $"in {sinceLoss.ElapsedMilliseconds}ms: {source.Width}x{source.Height} {source.Format} " +
                          $"@{source.RefreshRate.Numerator}/{source.RefreshRate.Denominator} at ({o.Left},{o.Top}) {source.Name}");
        failedAttempts = 0;
        prevPresent = 0;  // gaps are measured within one texture only
        touch.Target = CheckTouchTarget(o, t);
        lastPlacementQpc = Stopwatch.GetTimestamp();
        // The heartbeat is due at once after a recovery, and
        // carries the forced IDR below if no new image comes first.
        lastOfferQpc = 0;
        converter ??= new Nv12Converter(source.Device, source.Context);

        // After a recovery the next frame is an IDR, as on a new connection,
        // so the phone never waits up to a minute for one. If the
        // size changed, the encoder is
        // rebuilt on the first frame and starts with an IDR anyway, so this
        // only matters when it is kept.
        if (losses > 0 && encoder != null)
        {
            encoder.ForceKeyframe();
            Console.WriteLine($"{t} force IDR requested for the next frame");
        }
        continue;
    }

    rebuilt = false;
    // Until the heartbeat is due, capped so a new connection is noticed
    // soon. At least 1ms: a 0 timeout returns at once and would spin. When
    // no resend can happen, the cap alone, or an overdue heartbeat would
    // spin the loop at 1ms.
    bool canResend = encoder != null && display.HasDevice && !encoder.HasPending;
    double untilDue = HeartbeatMs - Stopwatch.GetElapsedTime(lastOfferQpc).TotalMilliseconds;
    int waitFor = (int)(canResend ? Math.Clamp(untilDue, 1, WakeMs) : WakeMs);
    FrameKind kind;
    long presentQpc;
    string lostWhy;
    try
    {
        kind = source.Next(waitFor, OnNewImage, out presentQpc, out lostWhy);
    }
    catch when (debugLayer)
    {
        // A converter failure is not a display event, so it is not retried.
        // Print what the debug layer knows before it propagates.
        D3DDebug.Drain(source.Device, $"{t} failed");
        throw;
    }
    if (debugLayer)
        debugMessages += D3DDebug.Drain(source.Device, $"{t} frame");

    if (rebuilt)
    {
        // The shared texture's own description, next to the mode Windows
        // runs the monitor at.
        var d = rebuiltFor;
        Console.WriteLine($"{t} converter build #{converter.Rebuilds} for {d.Width}x{d.Height} {d.Format} " +
                          $"(mode {source.Width}x{source.Height} @{source.RefreshRate.Numerator}/{source.RefreshRate.Denominator}), " +
                          $"built in {converter.LastRebuildMs:F1}ms, input view accepted");
        Console.WriteLine($"           texture usage={d.Usage} bind={d.BindFlags} misc={d.MiscFlags} cpu={d.CPUAccessFlags}");
        foreach (string line in converter.ColourReport().Split('\n'))
            Console.WriteLine($"           {line}");
    }

    // The converter has no size until it has converted a frame, and the
    // first wait after a Reopen often times out. Building then gave the
    // encoder a 0x0 type, which it refused. The rate is the display
    // phone's; until one has connected, 30.
    uint fps = display.Fps != 0 ? display.Fps : 30;
    uint bitrate = display.Fps != 0 ? display.Bitrate : display.BitrateFor(30, false);
    if (converter.Width != 0 &&
        (encoder == null || encoder.Width != converter.Width || encoder.Height != converter.Height ||
         encoder.Fps != fps || encoder.Bitrate != bitrate))
    {
        if (encoder != null)
            Retire(encoder);
        long e0 = Stopwatch.GetTimestamp();
        // The rate gate only for a stream slower than the monitor (H264Encoder).
        var hz = source.RefreshRate;
        bool gated = hz.Numerator == 0 || (ulong)fps * hz.Denominator < hz.Numerator;
        encoder = new H264Encoder(source.Device, converter.Width, converter.Height, fps, bitrate, sink,
                                  display, quality, gated);
        encodersBuilt++;
        Console.WriteLine($"{t} encoder #{encodersBuilt} {encoder.Name} built for {encoder.Width}x{encoder.Height}@{fps}, gate {(gated ? "on" : "off")} " +
                          $"in {Stopwatch.GetElapsedTime(e0).TotalMilliseconds:F1}ms");
        foreach (string line in encoder.Report.Split('\n'))
            Console.WriteLine($"           {line}");
    }

    // After the texture is released, so handing over never lengthens the
    // hold. The Blt ran while it was held, so the image is good.
    if (converted != null)
    {
        if (dumpPath != null && clock.Elapsed.TotalSeconds >= dumpAfter)
        {
            DumpAndDecode(converter, dumpPath, t);
            dumpPath = null;
        }
        // With no phone to send to, or its display paused, nothing is
        // encoded; a phone that attaches or resumes gets a resend at once.
        if (display.HasDevice)
        {
            encoder.Offer(converted, presentQpc);
            lastOfferQpc = Stopwatch.GetTimestamp();
        }
        else
        {
            converted.Release();
        }
        converted = null;
    }
    // Printed, not acted on: nothing yet says what recovery from a hung
    // encoder should be, and none has been seen.
    foreach (string line in encoder?.TakeForcedResults() ?? [])
        Console.WriteLine($"{t} {line}");
    double stalled = encoder?.StalledMs ?? 0;
    if (stalled > 2000)
        Console.WriteLine($"{t} ENCODER STALLED: input held {stalled:F0}ms with no output");
    if (encoder?.Error != null)
        throw new InvalidOperationException($"encoder failed on its event thread: {encoder.Error}");
    if (converter.Pool?.Error != null)
        throw new InvalidOperationException($"pool return callback failed: {converter.Pool.Error}");

    // A move in Settings brings no new texture, only a new place for touch
    // to land, so the place is read again every heartbeat.
    if (kind != FrameKind.Lost && Stopwatch.GetElapsedTime(lastPlacementQpc).TotalMilliseconds >= HeartbeatMs)
    {
        lastPlacementQpc = Stopwatch.GetTimestamp();
        if (!source.RefreshPlacement(out bool moved))
        {
            kind = FrameKind.Lost;
            lostWhy = $"{VirtualDisplay.MonitorId} is no longer on the desktop";
        }
        else if (moved)
        {
            moves++;
            OutputInfo o = source.Output;
            Console.WriteLine($"{t} MOVED to ({o.Left},{o.Top}) {o.Width}x{o.Height}");
            touch.Target = CheckTouchTarget(o, t);
        }
    }

    if (kind == FrameKind.Lost)
    {
        losses++;
        Console.WriteLine($"{t} LOST #{losses} {lostWhy} removed=0x{source.DeviceRemovedReason.Code:X8}");
        // A mode change arrives as a loss, so no touch lands on a stale
        // rectangle; Reopen publishes the new one.
        touch.Target = null;
        source.Drop();
        sinceLoss.Restart();
        continue;
    }

    // The heartbeat resend. Only with a device to send to, and never while a real
    // frame is pending, so a resend never displaces one. A connection
    // waiting for its keyframe gets one now rather than at the heartbeat.
    // Stamped now, not with the kept image's present time, so the encoder's
    // sample time and the phone's pts keep increasing.
    bool connect = display.KeyframeWanted;
    if (kind != FrameKind.NewImage && encoder != null && display.HasDevice && !encoder.HasPending &&
        (connect || Stopwatch.GetElapsedTime(lastOfferQpc).TotalMilliseconds >= HeartbeatMs))
    {
        long stamp = Stopwatch.GetTimestamp();
        Nv12Frame again = converter.Resend();
        // Restarted even when there was nothing to send (no image yet at
        // this size, or the pool empty), so the next try is a heartbeat away.
        lastOfferQpc = stamp;
        if (again != null)
        {
            encoder.Offer(again, stamp);
            resends++;
            Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s RESEND #{resends} {(connect ? "for connection" : "heartbeat")} " +
                              $"present={DisplaySender.Micros(stamp)}us blt={converter.LastResendMs:F2}ms");
            if (dumpPath != null && clock.Elapsed.TotalSeconds >= dumpAfter)
            {
                DumpAndDecode(converter, dumpPath, t);
                dumpPath = null;
            }
        }
    }

    // Counted, not printed: up to ten a second.
    if (kind == FrameKind.Timeout)
    {
        timeouts++;
        continue;
    }

    newImages++;
    string blt = poolEmpty ? " DROPPED, every pool frame in use" : $" blt={converter.LastSubmitMs:F2}ms";
    if (!poolEmpty)
        submitMs.Add(converter.LastSubmitMs);

    // Images the driver replaced before the hub took them show as skipped
    // frame numbers. The present time is when Windows means the image
    // shown, so it runs a few ms ahead of now.
    var f = source.Last;
    int skipped = prevFrame != 0 && f.FrameNumber > prevFrame ? (int)(f.FrameNumber - prevFrame - 1) : 0;
    frameGaps += skipped;
    string present = presentQpc > 0 && prevPresent > 0
        ? $"+{(presentQpc - prevPresent) * 1000.0 / Stopwatch.Frequency:F1}ms" : "first";
    prevPresent = presentQpc;
    prevFrame = f.FrameNumber;
    Console.WriteLine($"{t} NEW    frame={f.FrameNumber} present={present}" +
                      (skipped > 0 ? $" skipped={skipped}" : "") +
                      $" copied={f.Copied} waited={f.Waited}" + blt);
}

// First, so the icon leaves the tray while the rest shuts down.
tray.Close();
Console.WriteLine();
Console.WriteLine($"totals {clock.Elapsed.TotalSeconds:F1}s  losses={losses} timeouts={timeouts} newImages={newImages} " +
                  $"frames skipped={frameGaps} moves={moves}");
Console.WriteLine($"converter builds={converter?.Rebuilds ?? 0} distinct textures={textures.Count} " +
                  $"debug messages={(debugLayer ? debugMessages.ToString() : "n/a")}");
if (debugLayer && source.Device != null)
{
    // Positive control. The live object report always produces messages,
    // so a nonzero count here proves the queue was real and every zero
    // above meant the layer had nothing to say.
    using (var debug = source.Device.QueryInterface<Vortice.Direct3D11.Debug.ID3D11Debug>())
        debug.ReportLiveDeviceObjects(Vortice.Direct3D11.Debug.ReportLiveDeviceObjectFlags.Summary);
    Console.WriteLine($"debug layer control: live object report gave {D3DDebug.Drain(source.Device, "control")} messages");
}
Console.WriteLine($"blt submit ms {Spread(submitMs)}");
converter?.HarvestAll();
Console.WriteLine($"blt gpu    ms {Spread(converter?.GpuMs ?? new())} unread={converter?.GpuUnread} disjoint={converter?.GpuDisjoint}");

if (encoder != null)
{
    Retire(encoder);
    string other = string.Join(" ", encoder.OtherEvents.Select(kv => $"{kv.Key}={kv.Value}"));
    Console.WriteLine($"encoder (current) needInput={encoder.NeedInputs} other events: {(other == "" ? "none" : other)}");
}
Console.WriteLine($"encoders built={encodersBuilt} submitted={encSubmitted} replaced={encReplaced} outputs={encOutputs} " +
                  $"keyframes={encKeyframes} bytes={encBytes} " +
                  $"({encBytes * 8 / Math.Max(clock.Elapsed.TotalSeconds, 0.001) / 1e6:F1} Mbps over the whole run, " +
                  $"{(encOutputs > 0 ? encBytes / encOutputs / 1024 : 0)}KB a frame)");
Console.WriteLine($"resends={resends} present times clamped={encClamped}");
pointer.Dispose();
CursorTracker cursor = pointer.Tracker;
Console.WriteLine($"cursor shape changes={cursor.ShapeChanges} position changes={cursor.PositionChanges} " +
                  $"visible without a shape={cursor.PositionsWithoutShape} GetCursorPos disagreements={pointer.Disagreements} " +
                  $"driver read failures={pointer.ReadFailures}");
Console.WriteLine($"gate waits={encGateWaits} unmatched outputs={encUnmatched} " +
                  $"submit rate={encSubmitted / Math.Max(clock.Elapsed.TotalSeconds, 0.001):F1}/s");
Console.WriteLine($"present->blt    ms {Spread(presentToBlt)}");
Console.WriteLine($"blt->submit     ms {Spread(bltToSubmit)}");
Console.WriteLine($"submit->out     ms {Spread(submitToOut)}");
Console.WriteLine($"blt->out        ms {Spread(bltToOut)}");
Console.WriteLine($"gate timer late ms {Spread(timerLate)}");
// The phone connections first: closes the display connection, so the
// sender's counts stop moving, and ends the camera connection on a closed
// socket rather than on a disposed decoder, which it logs as a failure.
devices.Dispose();
// Unplugs the monitor now rather than leaving it to the driver's watchdog.
monitor.Dispose();
decoder.Dispose();
// Before L7 closes, so the DLL in the Frame Server gets Shutdown with its
// link still up, rather than a reset and a reconnect first.
vcam?.Dispose();
camera.Dispose();
display.Dispose();
Console.WriteLine($"sender connections={display.Connections} pauses={display.Pauses} sent={display.Sent} bytes={display.SentBytes} " +
                  $"discarded: no device={display.DiscardedNoDevice} before keyframe={display.DiscardedBeforeKeyframe} other rate={display.DiscardedOtherRate}; " +
                  $"max waiting={display.MaxWaiting}");
touch.Dispose();
Console.WriteLine($"touch received: messages={touch.Messages} gestures={touch.Gestures} dropped={touch.Dropped} " +
                  $"refused fingers={touch.Refused} events for no finger={touch.Unknown}");
Console.WriteLine($"touch injected: frames={touch.Frames} gestures={touch.InjectedGestures} moves coalesced={touch.Coalesced} " +
                  $"retried={touch.Retried} failures={(touch.Failures.Count == 0 ? "none" : string.Join(" ", touch.Failures.Select(kv => $"error {kv.Key}={kv.Value}")))}");
Console.WriteLine($"touch interval  ms {Spread(touch.IntervalMs)}");
Console.WriteLine($"recv->inject    ms {Spread(touch.RecvToInjectMs)}");
Console.WriteLine($"sender cursor: shapes sent={display.ShapesSent} positions given={display.PositionsGiven} " +
                  $"sent={display.PositionsSent} replaced before sending={display.PositionsReplaced}");
Console.WriteLine($"encoded->sent   ms {Spread(display.WaitMs)}");
Console.WriteLine($"sender acks: messages={display.Acks} frames acked={display.Acked} busy at the cap={display.AtCap} " +
                  $"stall write-offs={display.StallReleases} Wi-Fi target cuts={display.TargetCuts} target rises={display.TargetRises} " +
                  $"too large={display.Oversized}");
Console.WriteLine($"sent->acked     ms {Spread(display.AckMs)}");
Console.WriteLine($"write blocked   ms {Spread(display.WriteMs)}");
if (converter?.Pool is Nv12Pool pool)
    Console.WriteLine($"pool (current) allocated={pool.Allocated} maxInUse={pool.MaxInUse} inUse={pool.InUse} " +
                      $"exhausted={pool.Exhausted} encoderReturns={pool.EncoderReturns}");
converter?.Dispose();
MediaFactory.MFShutdown();
Win32.timeEndPeriod(1);
if (outPath != null)
    Console.WriteLine($"wrote {outPath}");
return 0;

// A refusal before the window exists. With no stdout it is also shown, or
// the hub would vanish without a word.
static int Refuse(string line)
{
    Console.WriteLine(line);
    if (HubLog.FilePath != null)
        System.Windows.Forms.MessageBox.Show(line.Trim(), "Exo", System.Windows.Forms.MessageBoxButtons.OK,
                                             System.Windows.Forms.MessageBoxIcon.Error);
    return 2;
}

// The one debug readback. Writes the frame just converted
// as raw NV12, then has ffmpeg make a PNG of it. Throwaway inspection, so
// ffmpeg is fine here. BT.709 limited range is what the converter is set
// to write; ffmpeg would assume BT.601 without being
// told, and the PNG colours would be off when the conversion is right.
static void DumpAndDecode(Nv12Converter converter, string path, string t)
{
    Console.WriteLine($"{t} dump {converter.DumpLast(path)}");
    string ffArgs = $"-v error -y -f rawvideo -pix_fmt nv12 -s {converter.Width}x{converter.Height} " +
                    $"-i \"{path}\" -vf scale=in_range=limited:in_color_matrix=bt709:out_range=full,format=rgb24 " +
                    $"\"{path}.png\"";
    try
    {
        using var ff = Process.Start("ffmpeg", ffArgs);
        ff.WaitForExit();
        Console.WriteLine($"           ffmpeg exit {ff.ExitCode}: {path}.png");
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.WriteLine($"           ffmpeg not found, run: ffmpeg {ffArgs}");
    }
}

// Where touch lands: DXGI's rectangle for the EXO monitor's output, checked
// against the monitor rectangle Windows gives for its centre. Null, so
// touch is refused, if the two disagree: touch would land on the wrong
// monitor.
static OutputInfo CheckTouchTarget(OutputInfo o, string t)
{
    var centre = new Win32.POINT { X = o.Left + o.Width / 2, Y = o.Top + o.Height / 2 };
    var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
    IntPtr monitor = Win32.MonitorFromPoint(centre, Win32.MONITOR_DEFAULTTONULL);
    bool got = monitor != IntPtr.Zero && Win32.GetMonitorInfoW(monitor, ref mi);
    var m = mi.rcMonitor;
    bool agree = got && m.Left == o.Left && m.Top == o.Top &&
                 m.Right - m.Left == o.Width && m.Bottom - m.Top == o.Height;
    Console.WriteLine($"{t} touch target: DXGI ({o.Left},{o.Top}) {o.Width}x{o.Height}, turned {o.Turns * 90}, " +
                      (got ? $"GetMonitorInfo ({m.Left},{m.Top})-({m.Right},{m.Bottom})" : "no monitor at its centre") +
                      $", {(agree ? "agree" : "DISAGREE, touch refused")}");
    return agree ? o : null;
}

static string Spread(List<double> ms)
{
    if (ms.Count == 0)
        return "no frames";
    double[] s = ms.Order().ToArray();
    double At(double q) => s[(int)Math.Min(s.Length - 1, Math.Floor(q * s.Length))];
    return $"n={s.Length} min={s[0]:F2} median={At(0.5):F2} p95={At(0.95):F2} p99={At(0.99):F2} max={s[^1]:F2}";
}
