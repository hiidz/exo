// ExoHub. Build order item 3, step 4: hardware H.264 encode.
//
// Duplicates the virtual display and logs what every AcquireNextFrame
// returns. Any failure drops the duplication and rebuilds it, forever, per
// DECISIONS 5.5. Every new image is converted to NV12 by the video
// processor while the frame is held, then offered to the hardware encoder
// once it is released. No network. Ctrl+C stops it and prints totals.
//
// Usage: ExoHub [monitorId] [--debug] [--seconds N] [--dump F] [--fps N] [--out F] [--display SERIAL]
//               [--camera SERIAL | --camera-file F] [--camera-geometry W H S]
// monitorId is the EDID code Windows shows for the monitor, e.g. ACR0721.
// Without one, the virtual display is used.
// --debug     D3D11 debug layer on, messages printed as they arrive. Slow.
// --seconds N stop after N seconds, as if Ctrl+C was pressed.
// --dump F    write the first converted frame to F as raw NV12. The one
//             debug readback DECISIONS 4.1 allows; nothing reads back
//             without it.
// --fps N     the encoded frame rate, 30 by default (DECISIONS 11.2).
// --out F     write the encoded stream to F as Annex-B H.264.
// --display S the adb serial of the device that holds the display role
//             (DECISIONS 5.8). Without it every device is tracked and none
//             is set up. The hub owns adb and issues the reverse itself.
// --camera S  the adb serial of the device that holds the camera role. Its
//             stream is decoded into L7 (DECISIONS 10 item 4, increment D).
// --camera-file F  decode the recorded H.264 stream F into L7, looping it,
//             in place of the test pattern (DECISIONS 10 item 4, increment C).
// --camera-geometry W H S  the test pattern's width, height and stride,
//             1280 720 1344 by default. Anything but 1280x720 is for
//             watching ExoCamera.dll refuse it (TECHNICAL_DESIGN 3.3).
// Whatever the flags, the hub registers the virtual camera for as long as
// it runs and removes it on exit.

using System.Diagnostics;
using System.Net.Sockets;
using ExoHub;
using ExoHub.Adb;
using ExoHub.Camera;
using ExoHub.Display;
using ExoHub.Link;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

// EDID manufacturer and product code of the Virtual Display Driver monitor.
// Measured 2026-10-09: Get-PnpDevice shows DISPLAY\MTT1337\..., "VDD by MTT".
const string VddMonitorId = "MTT1337";

// The longest the capture loop waits in AcquireNextFrame, which can wait on
// nothing else. A new connection is noticed within this (DECISIONS 5.4).
const double WakeMs = 100;

// Retry waits double from the first to the cap and stay there. Keyed to
// consecutive failed Reopen attempts, not to losses: one user action gives
// a burst of losses that each recover in milliseconds (spike 2 step 4). The
// cap is the most a user waits after unlocking, so it stays short.
const int FirstRetryMs = 100;
const int MaxRetryMs = 1000;

// DECISIONS 5.4: with nothing offered to the encoder for this long, the
// kept image is offered again.
const double HeartbeatMs = 500;

// NFR-3: 30 to 50 Mbps, do not tune down.
const uint Bitrate = 40_000_000;

// 2 is PROCESS_PER_MONITOR_DPI_AWARE. Anything else means the manifest did
// not apply, and every rectangle printed below is suspect.
int awareness = Win32.GetAwarenessFromDpiAwarenessContext(Win32.GetThreadDpiAwarenessContext());
Console.WriteLine($"dpi awareness {awareness} {(awareness == 2 ? "per-monitor" : "NOT per-monitor")}");

using (IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
    foreach (OutputInfo o in Outputs.List(factory))
        Console.WriteLine($"  {o}");

string wanted = VddMonitorId;
bool debugLayer = false;
double seconds = 0;
string dumpPath = null;
// The encoded rate. DECISIONS 11.2 is open; 30 is PROJECT_SPEC's proposal
// and the only rate encoded so far. It sets the per-frame bit budget and
// the H.264 level, so it is a flag, not a constant.
uint fps = 30;
string outPath = null;
// The user's choice of display device, until a roles UI exists (FR-4.4).
string displaySerial = null;
// L7's test pattern (DECISIONS 10 item 4, increment B). The stride is wider
// than the width on purpose: a reader that ignores it shows a shear.
int camWidth = 1280, camHeight = 720, camStride = 1344;
string cameraFile = null;
// The user's choice of camera device, as displaySerial is for the display.
string cameraSerial = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--debug": debugLayer = true; break;
        case "--fps": fps = uint.Parse(args[++i]); break;
        case "--seconds": seconds = double.Parse(args[++i]); break;
        case "--dump": dumpPath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--display": displaySerial = args[++i]; break;
        case "--camera-geometry":
            camWidth = int.Parse(args[++i]);
            camHeight = int.Parse(args[++i]);
            camStride = int.Parse(args[++i]);
            break;
        case "--camera-file": cameraFile = args[++i]; break;
        case "--camera": cameraSerial = args[++i]; break;
        default: wanted = args[i]; break;
    }
}

var clock = Stopwatch.StartNew();

// The adb owner comes first, so a refusal exits before the display opens.
// The job first of all: every process started after it is born inside.
Job.JoinNew();
// Once per process, before any other Media Foundation call. This early so
// the camera decoder exists before the device listener can accept a camera.
MediaFactory.MFStartup(false).CheckError();
// L7 next, independent of the display path and of --display. Its port is
// fixed and held exclusively, so an orphaned hub fails this one here.
CameraLink camera;
try
{
    camera = new CameraLink(clock);
}
catch (SocketException ex)
{
    Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s camera REFUSED: port {Wire.CameraPort}: {ex.SocketErrorCode}. " +
                      "Another hub, or an orphan of one, holds it.");
    return 2;
}
// The virtual camera, held for the hub's lifetime (DECISIONS 10 item 4).
// After L7, so an orphaned hub fails on the port before registering a
// second camera. Null if registration failed; the hub runs on without it.
using VirtualCamera vcam = VirtualCamera.Register(clock, camera);
// The one producer for L7: the decoder, fed by the camera connection or a
// file, or else the test pattern.
CameraDecoder decoder = null;
TestPattern pattern = null;
if (cameraSerial != null || cameraFile != null)
{
    decoder = new CameraDecoder(camera, clock);
    if (cameraFile != null)
        decoder.PlayFile(cameraFile);
}
else
{
    pattern = new TestPattern(camera, clock, camWidth, camHeight, camStride);
}
var adb = new AdbServer(clock);
string refused = adb.CheckForeign() ?? adb.Start();
if (refused != null)
{
    Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s adb REFUSED: {refused}");
    return 2;
}
// Port 0: Windows picks it, and this instance holds it exclusively. With
// the session ID in WELCOME it is the per-session identifier (5.7, 5.8).
using var display = new DisplaySender(clock);
using var devices = new DeviceListener(0, clock, display, cameraSerial != null ? decoder : null);
Console.WriteLine($"listening on 127.0.0.1:{devices.Port}, session {devices.SessionId:x16}");
var displayRole = displaySerial != null ? new RoleSetup(displaySerial, false, devices.Port, clock) : null;
var cameraRole = cameraSerial != null ? new RoleSetup(cameraSerial, true, devices.Port, clock) : null;
Console.WriteLine(displayRole != null ? $"display role: {displaySerial}"
                                      : "display role: none chosen; pass --display <serial>");
Console.WriteLine(cameraRole != null ? $"camera role: {cameraSerial}"
                                     : "camera role: none chosen; pass --camera <serial>");
var tracker = new DeviceTracker(adb, clock, (serial, state) =>
{
    displayRole?.OnState(serial, state);
    cameraRole?.OnState(serial, state);
});
int exitCode = 0;

using var source = new DesktopDuplicationSource(wanted, debugLayer);
Console.WriteLine($"target monitor {wanted}, heartbeat {HeartbeatMs}ms, wake {WakeMs}ms, debug layer {(debugLayer ? "ON" : "off")}, " +
                  $"{(seconds > 0 ? $"stopping after {seconds}s" : "Ctrl+C to stop")}");
Console.WriteLine();

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (seconds > 0)
    stop.CancelAfter(TimeSpan.FromSeconds(seconds));


// 1ms timers for the encoder's rate gate. How late the timer actually fires
// is measured and printed, rather than assumed from this call.
Win32.timeBeginPeriod(1);

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

// Which texture objects duplication hands out. If it is one object
// reused, a per-frame input view could become a cached one; until then the
// view is made per frame.
var textures = new HashSet<IntPtr>();

// Per-frame Blt times, for the totals. The first NFR-1 data point.
var submitMs = new List<double>();

// Runs inside Next, between AcquireNextFrame and ReleaseFrame. Records
// what to print and returns, so printing never lengthens the frame hold.
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
int timeouts = 0, newImages = 0, cursorOnly = 0, disagreements = 0;
long prevPresent = 0, prevMouse = 0;
long lastOfferQpc = 0;  // 0: never, so the first resend is due at once
int resends = 0;

while (!stop.IsCancellationRequested)
{
    string t = $"{clock.Elapsed.TotalSeconds,8:F3}s";

    if (tracker.Fatal != null)
    {
        Console.WriteLine($"{t} adb REFUSED: {tracker.Fatal}");
        exitCode = 2;
        break;
    }

    if (!source.IsOpen)
    {
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

        // isCurrent is printed again here, after recovery, because spike 2
        // could not tell whether it fires per event or latches false. With a
        // fresh factory it reads true here, and the next loss line tells.
        OutputInfo o = source.Output;
        Console.WriteLine($"{t} {(losses == 0 ? "opened" : "recovered")} after {failedAttempts} failed attempts " +
                          $"in {sinceLoss.ElapsedMilliseconds}ms: {source.Width}x{source.Height} {source.Format} " +
                          $"at ({o.Left},{o.Top}) {o.GdiName} isCurrent={source.FactoryIsCurrent} factories={source.FactoriesCreated}");
        failedAttempts = 0;
        prevPresent = prevMouse = 0;  // gaps are measured within one duplication only
        // DECISIONS 5.4: the heartbeat is due at once after a recovery, and
        // carries the forced IDR below if no new image comes first.
        lastOfferQpc = 0;
        converter ??= new Nv12Converter(source.Device, source.Context);

        // DECISIONS 5.3 and 5.5 step 5. If the size changed, the encoder is
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
    Result r;
    FrameKind kind;
    OutduplFrameInfo info;
    // Until the heartbeat is due, capped so a new connection is noticed
    // soon. At least 1ms: a 0 timeout returns at once and would spin. When
    // no resend can happen, the cap alone, or an overdue heartbeat would
    // spin the loop at 1ms.
    bool canResend = encoder != null && display.HasDevice && !encoder.HasPending;
    double untilDue = HeartbeatMs - Stopwatch.GetElapsedTime(lastOfferQpc).TotalMilliseconds;
    uint acquireMs = (uint)(canResend ? Math.Clamp(untilDue, 1, WakeMs) : WakeMs);
    try
    {
        r = source.Next(acquireMs, OnNewImage, out kind, out info);
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
        // The duplication texture's own description, next to the mode the
        // duplication reported. Spike 5's dupflags read these from the
        // primary monitor; this is the VDD.
        var d = rebuiltFor;
        Console.WriteLine($"{t} converter build #{converter.Rebuilds} for {d.Width}x{d.Height} {d.Format} " +
                          $"(mode {source.Width}x{source.Height} @{source.RefreshRate.Numerator}/{source.RefreshRate.Denominator}), " +
                          $"built in {converter.LastRebuildMs:F1}ms, input view accepted");
        Console.WriteLine($"           texture usage={d.Usage} bind={d.BindFlags} misc={d.MiscFlags} cpu={d.CPUAccessFlags}");
        foreach (string line in converter.ColourReport().Split('\n'))
            Console.WriteLine($"           {line}");
    }

    // The converter has no size until it has converted a frame, and the
    // first acquire after a Reopen is often a timeout or cursor-only.
    // Building then gave the encoder a 0x0 type, which it refused.
    if (converter.Width != 0 &&
        (encoder == null || encoder.Width != converter.Width || encoder.Height != converter.Height))
    {
        if (encoder != null)
            Retire(encoder);
        long e0 = Stopwatch.GetTimestamp();
        encoder = new H264Encoder(source.Device, converter.Width, converter.Height, fps, Bitrate, sink, display);
        encodersBuilt++;
        Console.WriteLine($"{t} encoder #{encodersBuilt} {encoder.Name} built for {encoder.Width}x{encoder.Height}@{fps} " +
                          $"in {Stopwatch.GetElapsedTime(e0).TotalMilliseconds:F1}ms");
        foreach (string line in encoder.Report.Split('\n'))
            Console.WriteLine($"           {line}");
    }

    // After ReleaseFrame, so handing over never lengthens the frame hold.
    // Even when ReleaseFrame reported a loss: the Blt ran while the frame
    // was held, so the image is good.
    if (converted != null)
    {
        if (dumpPath != null)
        {
            DumpAndDecode(converter, dumpPath, t);
            dumpPath = null;
        }
        encoder.Offer(converted, info.LastPresentTime);
        lastOfferQpc = Stopwatch.GetTimestamp();
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

    if (r.Failure)
    {
        losses++;
        string what = r == Vortice.DXGI.ResultCode.AccessLost ? "ACCESS_LOST" : "acquire failed";
        Console.WriteLine($"{t} LOST #{losses} {what} 0x{r.Code:X8} isCurrent={source.FactoryIsCurrent} " +
                          $"removed=0x{source.DeviceRemovedReason.Code:X8}");
        source.Drop();
        sinceLoss.Restart();
        continue;
    }

    // DECISIONS 5.4. Only with a device to send to, and never while a real
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
        }
    }

    // Counted, not printed: up to ten a second now.
    if (kind == FrameKind.Timeout)
    {
        timeouts++;
        continue;
    }

    string blt = "";
    if (kind == FrameKind.NewImage)
    {
        newImages++;
        if (poolEmpty)
        {
            blt = " DROPPED, every pool frame in use";
        }
        else
        {
            submitMs.Add(converter.LastSubmitMs);
            blt = $" blt={converter.LastSubmitMs:F2}ms";
        }
    }
    else
    {
        cursorOnly++;
    }

    // Spike 2 never saw these two indicators disagree, on one machine. Keep
    // counting rather than assume it holds everywhere.
    bool disagree = (info.LastPresentTime > 0) != (info.AccumulatedFrames > 0);
    if (disagree) disagreements++;

    // Timestamps are 100ns units (spike 2), so /10000 is milliseconds. Print
    // the gap since the previous nonzero value; 0 means "did not change".
    string present = info.LastPresentTime > 0 && prevPresent > 0
        ? $"+{(info.LastPresentTime - prevPresent) / 10000.0:F1}ms" : (info.LastPresentTime > 0 ? "first" : "-");
    string mouse = info.LastMouseUpdateTime > 0 && prevMouse > 0
        ? $"+{(info.LastMouseUpdateTime - prevMouse) / 10000.0:F1}ms" : (info.LastMouseUpdateTime > 0 ? "first" : "-");
    if (info.LastPresentTime > 0) prevPresent = info.LastPresentTime;
    if (info.LastMouseUpdateTime > 0) prevMouse = info.LastMouseUpdateTime;

    // PointerPosition is only filled in when the mouse updated in this
    // frame (LastMouseUpdateTime nonzero). Otherwise it reads as hidden at
    // (0,0), which would be a lie.
    var p = info.PointerPosition;
    string pointer = info.LastMouseUpdateTime == 0 ? "-"
        : p.Visible ? $"visible({p.Position.X},{p.Position.Y})" : "hidden";

    Console.WriteLine($"{t} {(kind == FrameKind.NewImage ? "NEW   " : "CURSOR")} " +
                      $"acc={info.AccumulatedFrames} present={present} mouse={mouse} " +
                      $"ptr={pointer} shape={info.PointerShapeBufferSize} meta={info.TotalMetadataBufferSize}" +
                      blt + (disagree ? "  INDICATORS DISAGREE" : ""));
}

Console.WriteLine();
Console.WriteLine($"totals {clock.Elapsed.TotalSeconds:F1}s  losses={losses} timeouts={timeouts} newImages={newImages} " +
                  $"cursorOnly={cursorOnly} disagreements={disagreements}");
Console.WriteLine($"converter builds={converter?.Rebuilds ?? 0} distinct duplication textures={textures.Count} " +
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
Console.WriteLine($"gate waits={encGateWaits} unmatched outputs={encUnmatched} " +
                  $"submit rate={encSubmitted / Math.Max(clock.Elapsed.TotalSeconds, 0.001):F1}/s");
Console.WriteLine($"present->blt    ms {Spread(presentToBlt)}");
Console.WriteLine($"blt->submit     ms {Spread(bltToSubmit)}");
Console.WriteLine($"submit->out     ms {Spread(submitToOut)}");
Console.WriteLine($"blt->out        ms {Spread(bltToOut)}");
Console.WriteLine($"gate timer late ms {Spread(timerLate)}");
pattern?.Dispose();
decoder?.Dispose();
// Before L7 closes, so the DLL in the Frame Server gets Shutdown with its
// link still up, rather than a reset and a reconnect first (DECISIONS 5.9).
vcam?.Dispose();
camera.Dispose();
devices.Dispose();  // closes the display connection, so the sender's counts stop moving
display.Dispose();
Console.WriteLine($"sender connections={display.Connections} sent={display.Sent} bytes={display.SentBytes} " +
                  $"discarded: no device={display.DiscardedNoDevice} before keyframe={display.DiscardedBeforeKeyframe}; " +
                  $"max waiting={display.MaxWaiting}");
Console.WriteLine($"encoded->sent   ms {Spread(display.WaitMs)}");
Console.WriteLine($"write blocked   ms {Spread(display.WriteMs)}");
if (converter?.Pool is Nv12Pool pool)
    Console.WriteLine($"pool (current) allocated={pool.Allocated} maxInUse={pool.MaxInUse} inUse={pool.InUse} " +
                      $"exhausted={pool.Exhausted} encoderReturns={pool.EncoderReturns}");
converter?.Dispose();
MediaFactory.MFShutdown();
Win32.timeEndPeriod(1);
if (outPath != null)
    Console.WriteLine($"wrote {outPath}");
return exitCode;

// The one debug readback (DECISIONS 4.1). Writes the frame just converted
// as raw NV12, then has ffmpeg make a PNG of it. Throwaway inspection, so
// ffmpeg is fine here. BT.709 limited range is what the converter is set
// to write (DECISIONS 11.7); ffmpeg would assume BT.601 without being
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

static string Spread(List<double> ms)
{
    if (ms.Count == 0)
        return "no frames";
    double[] s = ms.Order().ToArray();
    double At(double q) => s[(int)Math.Min(s.Length - 1, Math.Floor(q * s.Length))];
    return $"n={s.Length} min={s[0]:F2} median={At(0.5):F2} p95={At(0.95):F2} p99={At(0.99):F2} max={s[^1]:F2}";
}
