// Opens Exo Camera the way the Windows Camera app does, through WinRT
// MediaCapture and a MediaFrameReader, and prints what each step returns,
// including the error the app does not show (DECISIONS 10 item 6, SC-2).
// Three readers in turn: CPU memory, Auto memory (GPU allowed), and Auto
// converted to BGRA. Close the Camera app first: it holds the camera.
// Usage: CamProbe [seconds per reader, default 5]

using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

int seconds = args.Length > 0 ? int.Parse(args[0]) : 5;

var groups = await MediaFrameSourceGroup.FindAllAsync();
foreach (var g in groups)
{
    Console.WriteLine($"group \"{g.DisplayName}\" {g.Id}");
    foreach (var s in g.SourceInfos)
        Console.WriteLine($"  source {s.Id}: {s.MediaStreamType} {s.SourceKind}");
}
var exo = groups.FirstOrDefault(g => g.DisplayName.Contains("Exo"));
if (exo == null)
{
    Console.WriteLine("no Exo Camera group; is the hub running?");
    return 1;
}

var runs = new (MediaCaptureMemoryPreference memory, string subtype)[]
{
    (MediaCaptureMemoryPreference.Cpu, null),
    (MediaCaptureMemoryPreference.Auto, null),
    (MediaCaptureMemoryPreference.Auto, MediaEncodingSubtypes.Bgra8),
};
foreach (var (memory, subtype) in runs)
{
    Console.WriteLine($"--- memory {memory}, reader subtype {subtype ?? "as the source gives"}");
    using var capture = new MediaCapture();
    capture.Failed += (_, e) => Console.WriteLine($"  Failed event: 0x{e.Code:X8} {e.Message}");
    try
    {
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = exo,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = memory,
        });
        Console.WriteLine($"  initialized, device characteristic {capture.MediaCaptureSettings.VideoDeviceCharacteristic}");
        foreach (var type in new[] { MediaStreamType.VideoPreview, MediaStreamType.VideoRecord, MediaStreamType.Photo })
        {
            var props = capture.VideoDeviceController.GetAvailableMediaStreamProperties(type);
            Console.WriteLine($"  {type}: {props.Count} [{string.Join(", ", props.Select(Describe))}]");
        }

        Console.WriteLine($"  frame sources: {capture.FrameSources.Count}");
        foreach (var s in capture.FrameSources.Values)
            Console.WriteLine($"    {s.Info.Id}: {s.Info.MediaStreamType} {s.Info.SourceKind}, current {Format(s.CurrentFormat)}, " +
                              $"{s.SupportedFormats.Count} formats");
        var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        if (source == null)
        {
            Console.WriteLine("  no colour frame source, so no reader");
            continue;
        }
        using var reader = subtype == null
            ? await capture.CreateFrameReaderAsync(source)
            : await capture.CreateFrameReaderAsync(source, subtype);
        int frames = 0, nulls = 0;
        string first = null;
        reader.FrameArrived += (r, _) =>
        {
            using var f = r.TryAcquireLatestFrame();
            if (f == null)
            {
                Interlocked.Increment(ref nulls);
                return;
            }
            if (Interlocked.Increment(ref frames) == 1)
            {
                var v = f.VideoMediaFrame;
                first = v?.SoftwareBitmap != null ? $"SoftwareBitmap {v.SoftwareBitmap.BitmapPixelFormat}"
                      : v?.Direct3DSurface != null ? $"Direct3DSurface {v.Direct3DSurface.Description.Format}"
                      : "neither a bitmap nor a surface";
            }
        };
        var status = await reader.StartAsync();
        Console.WriteLine($"  reader StartAsync: {status}");
        await Task.Delay(seconds * 1000);
        await reader.StopAsync();
        Console.WriteLine($"  frames in {seconds}s: {frames}, empty acquires {nulls}, first frame {first ?? "none"}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  threw 0x{ex.HResult:X8}: {ex.Message.Trim()}");
    }
}
return 0;

static string Describe(IMediaEncodingProperties p) =>
    p is VideoEncodingProperties v ? $"{v.Subtype} {v.Width}x{v.Height} {v.FrameRate.Numerator}/{v.FrameRate.Denominator}"
    : p is ImageEncodingProperties i ? $"{i.Subtype} {i.Width}x{i.Height}"
    : p.Subtype;

static string Format(MediaFrameFormat f) =>
    f == null ? "none" : $"{f.Subtype} {f.VideoFormat.Width}x{f.VideoFormat.Height} {f.FrameRate.Numerator}/{f.FrameRate.Denominator}";
