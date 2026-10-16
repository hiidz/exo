// Opens Exo Camera the way the Windows Camera app does, through WinRT
// MediaCapture and a MediaFrameReader, and prints what each step returns,
// including the error the Windows Camera app does not show.
// Three readers in turn: CPU memory, Auto memory (GPU allowed), and Auto
// converted to BGRA. Close the Camera app first: it holds the camera.
// Usage: CamProbe [seconds per reader, default 5]
//        CamProbe --snap F [seconds, default 3]: reads BGRA frames that
//        long and saves the last as the PNG F, to see what apps get.

using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

if (args.Length > 1 && args[0] == "--snap")
    return await Snap(args[1], args.Length > 2 ? int.Parse(args[2]) : 3);

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

static async Task<int> Snap(string path, int seconds)
{
    var exo = (await MediaFrameSourceGroup.FindAllAsync()).FirstOrDefault(g => g.DisplayName.Contains("Exo"));
    if (exo == null)
    {
        Console.WriteLine("no Exo Camera group; is the hub running?");
        return 1;
    }
    using var capture = new MediaCapture();
    await capture.InitializeAsync(new MediaCaptureInitializationSettings
    {
        SourceGroup = exo,
        SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        StreamingCaptureMode = StreamingCaptureMode.Video,
        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
    });
    var source = capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
    using var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
    SoftwareBitmap last = null;
    int frames = 0;
    reader.FrameArrived += (r, _) =>
    {
        using var f = r.TryAcquireLatestFrame();
        var b = f?.VideoMediaFrame?.SoftwareBitmap;
        if (b == null)
            return;
        // A copy: the frame's own bitmap goes back to the reader with it.
        Interlocked.Exchange(ref last, SoftwareBitmap.Copy(b))?.Dispose();
        Interlocked.Increment(ref frames);
    };
    await reader.StartAsync();
    await Task.Delay(seconds * 1000);
    await reader.StopAsync();
    if (last == null)
    {
        Console.WriteLine($"no frame in {seconds}s");
        return 1;
    }
    using var png = new InMemoryRandomAccessStream();
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
    encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(last, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore));
    await encoder.FlushAsync();
    var bytes = new byte[png.Size];
    using (var r = new DataReader(png.GetInputStreamAt(0)))
    {
        await r.LoadAsync((uint)png.Size);
        r.ReadBytes(bytes);
    }
    File.WriteAllBytes(path, bytes);
    Console.WriteLine($"{frames} frames in {seconds}s; saved {last.PixelWidth}x{last.PixelHeight} to {path}");
    return 0;
}

static string Format(MediaFrameFormat f) =>
    f == null ? "none" : $"{f.Subtype} {f.VideoFormat.Width}x{f.VideoFormat.Height} {f.FrameRate.Numerator}/{f.FrameRate.Denominator}";
