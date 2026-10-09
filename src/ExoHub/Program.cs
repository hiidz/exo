// ExoHub. Build order item 3, step 1: capture only.
//
// Duplicates the virtual display and logs what every AcquireNextFrame
// returns. No encode, no network, no recovery: a failure is reported and
// the process exits. Ctrl+C stops it and prints totals.
//
// Usage: ExoHub [monitorId]
// monitorId is the EDID code Windows shows for the monitor, e.g. ACR0721.
// Without one, the virtual display is used.

using System.Diagnostics;
using ExoHub;
using ExoHub.Display;
using SharpGen.Runtime;
using Vortice.DXGI;

// EDID manufacturer and product code of the Virtual Display Driver monitor.
// Measured 2026-10-09: Get-PnpDevice shows DISPLAY\MTT1337\..., "VDD by MTT".
const string VddMonitorId = "MTT1337";

// Matches the 500ms heartbeat interval proposed in DECISIONS 5.4, so the log
// shows exactly the gaps the heartbeat will have to fill.
const uint TimeoutMs = 500;

// 2 is PROCESS_PER_MONITOR_DPI_AWARE. Anything else means the manifest did
// not apply, and every rectangle printed below is suspect.
int awareness = Win32.GetAwarenessFromDpiAwarenessContext(Win32.GetThreadDpiAwarenessContext());
Console.WriteLine($"dpi awareness {awareness} {(awareness == 2 ? "per-monitor" : "NOT per-monitor")}");

using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
List<OutputInfo> outputs = Outputs.List(factory);
foreach (OutputInfo o in outputs)
    Console.WriteLine($"  {o}");

string wanted = args.Length > 0 ? args[0] : VddMonitorId;
List<OutputInfo> matches = outputs.Where(o => o.MonitorId == wanted).ToList();
if (matches.Count != 1)
{
    Console.WriteLine($"need exactly one output with monitor {wanted ?? "(none given)"}, found {matches.Count}");
    return 1;
}
OutputInfo target = matches[0];
Console.WriteLine($"target {target}");

DesktopDuplicationSource source;
try
{
    source = DesktopDuplicationSource.Open(factory, target);
}
catch (SharpGenException ex)
{
    Console.WriteLine($"open failed 0x{ex.ResultCode.Code:X8} {ex.Message}");
    return 2;
}
using var _ = source;
Console.WriteLine($"duplicating {source.Width}x{source.Height} {source.Format}, timeout {TimeoutMs}ms, Ctrl+C to stop");
Console.WriteLine();

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

var clock = Stopwatch.StartNew();
int timeouts = 0, newImages = 0, cursorOnly = 0, disagreements = 0;
long prevPresent = 0, prevMouse = 0;
int exitCode = 0;

while (!stop.IsCancellationRequested)
{
    Result r = source.Next(TimeoutMs, out FrameKind kind, out OutduplFrameInfo info);
    string t = $"{clock.Elapsed.TotalSeconds,8:F3}s";

    if (r.Failure)
    {
        string what = r == Vortice.DXGI.ResultCode.AccessLost ? "ACCESS_LOST" : "acquire failed";
        Console.WriteLine($"{t} {what} 0x{r.Code:X8}, exiting (recovery is the next step)");
        exitCode = 3;
        break;
    }

    if (kind == FrameKind.Timeout)
    {
        timeouts++;
        Console.WriteLine($"{t} timeout");
        continue;
    }

    if (kind == FrameKind.NewImage) newImages++; else cursorOnly++;

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
                      (disagree ? "  INDICATORS DISAGREE" : ""));
}

Console.WriteLine();
Console.WriteLine($"totals {clock.Elapsed.TotalSeconds:F1}s  timeouts={timeouts} newImages={newImages} " +
                  $"cursorOnly={cursorOnly} disagreements={disagreements}");
return exitCode;
