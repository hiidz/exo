// ExoHub. Build order item 3, step 2: capture with ACCESS_LOST recovery.
//
// Duplicates the virtual display and logs what every AcquireNextFrame
// returns. Any failure drops the duplication and rebuilds it, forever, per
// DECISIONS 5.5. No encode, no network. Ctrl+C stops it and prints totals.
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

string wanted = args.Length > 0 ? args[0] : VddMonitorId;
using var source = new DesktopDuplicationSource(wanted);
Console.WriteLine($"target monitor {wanted}, timeout {TimeoutMs}ms, Ctrl+C to stop");
Console.WriteLine();

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

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
        continue;
    }

    Result r = source.Next(TimeoutMs, out FrameKind kind, out OutduplFrameInfo info);

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
Console.WriteLine($"totals {clock.Elapsed.TotalSeconds:F1}s  losses={losses} timeouts={timeouts} newImages={newImages} " +
                  $"cursorOnly={cursorOnly} disagreements={disagreements}");
return 0;
