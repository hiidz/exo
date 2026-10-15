// Where the hub's lines go when nothing reads its stdout: started from the
// shortcut or at sign-in, a WinExe has none. The tools redirect stdout and
// keep reading it there. The file sits with the DLL's and the driver's in
// the folder setup made writable, and is cut at a size, keeping one older
// file, since the display role writes a line a frame.

using System.Text;

namespace ExoHub;

static class HubLog
{
    public const string Folder = @"C:\Windows\Temp\Exo\logs";
    const long MaxBytes = 20 * 1024 * 1024;

    // The file, or null when stdout is read by whoever started the hub.
    public static string FilePath { get; private set; }

    public static void Start()
    {
        if (Win32.GetStdHandle(Win32.STD_OUTPUT_HANDLE) is not 0 and not -1)
            return;
        string folder = Directory.Exists(Folder) ? Folder : Path.GetTempPath();
        FilePath = Path.Combine(folder, "exo-hub.log");
        // The last run's file is kept, so a crash is still there after the
        // user, or sign-in, starts the hub again.
        if (File.Exists(FilePath))
            File.Move(FilePath, Older(FilePath), overwrite: true);
        var writer = TextWriter.Synchronized(new CuttingWriter(FilePath));
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    static string Older(string path) => Path.ChangeExtension(path, ".1.log");

    sealed class CuttingWriter(string path) : TextWriter
    {
        StreamWriter file = Open(path);

        public override Encoding Encoding => Encoding.UTF8;

        static StreamWriter Open(string path) =>
            new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { AutoFlush = true };

        public override void Write(char value) => file.Write(value);
        public override void Write(string value) => file.Write(value);

        public override void WriteLine(string value)
        {
            file.WriteLine(value);
            if (file.BaseStream.Length < MaxBytes)
                return;
            file.Dispose();
            File.Move(path, Older(path), overwrite: true);
            file = Open(path);
        }
    }
}
