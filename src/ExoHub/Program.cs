// ExoHub. Build order item 3, step 3: BGRA to NV12 on the GPU.
//
// Duplicates the virtual display and logs what every AcquireNextFrame
// returns. Any failure drops the duplication and rebuilds it, forever, per
// DECISIONS 5.5. Every new image is converted to NV12 by the video
// processor while the frame is held. No encode, no network. Ctrl+C stops
// it and prints totals.
//
// Usage: ExoHub [monitorId] [--debug] [--seconds N] [--dump F]
// monitorId is the EDID code Windows shows for the monitor, e.g. ACR0721.
// Without one, the virtual display is used.
// --debug     D3D11 debug layer on, messages printed as they arrive. Slow.
// --seconds N stop after N seconds, as if Ctrl+C was pressed.
// --dump F    write the first converted frame to F as raw NV12. The one
//             debug readback DECISIONS 4.1 allows; nothing reads back
//             without it.

using System.Diagnostics;
using ExoHub;
using ExoHub.Display;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

// EDID manufacturer and product code of the Virtual Display Driver monitor.
// Measured 2026-10-09: Get-PnpDevice shows DISPLAY\MTT1337\..., "VDD by MTT".
const string VddMonitorId = "MTT1337";

// Matches the 500ms heartbeat interval proposed in DECISIONS 5.4, so the log
// shows exactly the gaps the heartbeat will have to fill.
const uint TimeoutMs = 500;

// Retry waits double from the first to the cap and stay there. Keyed to
// consecutive failed Reopen attempts, not to losses: one user action gives
// a burst of losses that each recover in milliseconds (spike 2 step 4). The
// cap is the most a user waits after unlocking, so it stays short.
const int FirstRetryMs = 100;
const int MaxRetryMs = 1000;

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
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--debug": debugLayer = true; break;
        case "--seconds": seconds = double.Parse(args[++i]); break;
        case "--dump": dumpPath = args[++i]; break;
        default: wanted = args[i]; break;
    }
}

using var source = new DesktopDuplicationSource(wanted, debugLayer);
Console.WriteLine($"target monitor {wanted}, timeout {TimeoutMs}ms, debug layer {(debugLayer ? "ON" : "off")}, " +
                  $"{(seconds > 0 ? $"stopping after {seconds}s" : "Ctrl+C to stop")}");
Console.WriteLine();

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (seconds > 0)
    stop.CancelAfter(TimeSpan.FromSeconds(seconds));

// Created once the first Reopen has made the device, and kept with it.
Nv12Converter converter = null;
int debugMessages = 0;

// Which texture objects duplication hands out. If it is one object
// reused, a per-frame input view could become a cached one; until then the
// view is made per frame.
var textures = new HashSet<IntPtr>();

// Per-frame Blt times, for the totals. The first NFR-1 data point.
var submitMs = new List<double>();
var doneMs = new List<double>();

// Runs inside Next, between AcquireNextFrame and ReleaseFrame. Records
// what to print and returns, so printing never lengthens the frame hold.
bool rebuilt = false;
Texture2DDescription rebuiltFor = default;
void OnNewImage(ID3D11Texture2D texture)
{
    textures.Add(texture.NativePointer);
    rebuilt = converter.Convert(texture, source.RefreshRate);
    if (rebuilt)
        rebuiltFor = texture.Description;
}

var clock = Stopwatch.StartNew();
var sinceLoss = Stopwatch.StartNew();  // startup counts as the first loss
int losses = 0, failedAttempts = 0;
int timeouts = 0, newImages = 0, cursorOnly = 0, disagreements = 0;
long prevPresent = 0, prevMouse = 0;

while (!stop.IsCancellationRequested)
{
    string t = $"{clock.Elapsed.TotalSeconds,8:F3}s";

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
        converter ??= new Nv12Converter(source.Device, source.Context);
        continue;
    }

    rebuilt = false;
    Result r;
    FrameKind kind;
    OutduplFrameInfo info;
    try
    {
        r = source.Next(TimeoutMs, OnNewImage, out kind, out info);
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

    if (kind == FrameKind.Timeout)
    {
        timeouts++;
        Console.WriteLine($"{t} timeout");
        continue;
    }

    string blt = "";
    if (kind == FrameKind.NewImage)
    {
        newImages++;
        submitMs.Add(converter.LastSubmitMs);
        doneMs.Add(converter.LastDoneMs);
        blt = $" blt={converter.LastSubmitMs:F2}/{converter.LastDoneMs:F2}ms";

        // After Next returned, so the frame is already released.
        if (dumpPath != null)
        {
            Console.WriteLine($"{t} dump {converter.DumpLast(dumpPath)}");

            // Throwaway inspection, so ffmpeg is fine here. BT.709 full
            // range is what the processor was measured to write on
            // 2026-10-09, not a decision on DECISIONS 11.7. Without these
            // flags ffmpeg assumes BT.601 limited and the PNG looks wrong
            // when the conversion is right.
            string ffArgs = $"-v error -y -f rawvideo -pix_fmt nv12 -s {converter.Width}x{converter.Height} " +
                            $"-i \"{dumpPath}\" -vf scale=in_range=full:in_color_matrix=bt709:out_range=full,format=rgb24 " +
                            $"\"{dumpPath}.png\"";
            try
            {
                using var ff = Process.Start("ffmpeg", ffArgs);
                ff.WaitForExit();
                Console.WriteLine($"           ffmpeg exit {ff.ExitCode}: {dumpPath}.png");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"           ffmpeg not found, run: ffmpeg {ffArgs}");
            }
            dumpPath = null;
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
Console.WriteLine($"blt done   ms {Spread(doneMs)}");
converter?.Dispose();
return 0;

static string Spread(List<double> ms)
{
    if (ms.Count == 0)
        return "no frames";
    double[] s = ms.Order().ToArray();
    double At(double q) => s[(int)Math.Min(s.Length - 1, Math.Floor(q * s.Length))];
    return $"n={s.Length} min={s[0]:F2} median={At(0.5):F2} p95={At(0.95):F2} p99={At(0.99):F2} max={s[^1]:F2}";
}
