using System.Runtime.InteropServices;

namespace ExoHub;

// The hub's job object, with KILL_ON_JOB_CLOSE, so adb dies with it. The hub
// joins it first thing, and every process it starts afterwards is born
// inside, so nothing it starts can outlive it.
//
// The handle is a raw IntPtr in a static field on purpose. A SafeHandle
// would close it from a finalizer once nothing referenced it, and closing
// the last handle to a KILL_ON_JOB_CLOSE job kills every process in it,
// the hub included. A raw handle is closed only by the process ending,
// which is exactly when the job should fire.
static class Job
{
    static IntPtr handle;

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // JOBOBJECT_BASIC_LIMIT_INFORMATION. IntPtr and UIntPtr stand for the
    // SIZE_T and ULONG_PTR fields, so the padding comes out as in C on x64.
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, with IO_COUNTERS inlined as six
    // ULONGLONGs. Only LimitFlags is set; the rest stays zero.
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    // Creates the job and puts this process in it. Once per process, before
    // any child is started.
    public static void JoinNew()
    {
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
            throw new System.ComponentModel.Win32Exception();
        if (!AssignProcessToJobObject(handle, GetCurrentProcess()))
            throw new System.ComponentModel.Win32Exception();
    }

    // Asked of the OS rather than assumed.
    public static bool Contains(System.Diagnostics.Process p) =>
        IsProcessInJob(p.Handle, handle, out bool inJob) ? inJob : throw new System.ComponentModel.Win32Exception();
}
