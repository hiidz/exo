#include "exo_log.h"

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

// The directory must already exist with a DACL that lets LOCAL SERVICE
// write. Only an elevated process can set that on an existing directory,
// so it is ExoSetup's job, not the DLL's (TECHNICAL_DESIGN 3.4).
static const char* kDir = "C:\\Windows\\Temp\\Exo\\logs";
static const LONGLONG kMaxBytes = 4LL * 1024 * 1024;

// Everything below is guarded by g_lock. SRWLOCK_INIT is a constant, so
// the lock is usable before any code runs and needs no init call.
static SRWLOCK  g_lock = SRWLOCK_INIT;
static bool     g_tried = false;   // the first ExoLog has tried to open
static HANDLE   g_file = INVALID_HANDLE_VALUE;
static char     g_path[MAX_PATH];
static LONGLONG g_written = 0;

// "C:\Windows\System32\svchost.exe" -> "svchost".
static void ExeName(char* out, size_t size)
{
    char full[MAX_PATH];
    DWORD n = GetModuleFileNameA(NULL, full, MAX_PATH);
    if (n == 0 || n == MAX_PATH) {
        strcpy_s(out, size, "unknown");
        return;
    }
    const char* base = strrchr(full, '\\');
    base = base ? base + 1 : full;
    strcpy_s(out, size, base);
    char* dot = strrchr(out, '.');
    if (dot) {
        *dot = 0;
    }
}

// Caller holds the lock.
static void OpenLocked()
{
    g_file = CreateFileA(g_path, FILE_APPEND_DATA,
                         // Never block a tail tool or another process.
                         FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                         NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    g_written = 0;
    LARGE_INTEGER size;
    if (g_file != INVALID_HANDLE_VALUE && GetFileSizeEx(g_file, &size)) {
        g_written = size.QuadPart;
    }
}

// Caller holds the lock. Keeps one previous file, as <name>.1.
static void RollLocked()
{
    CloseHandle(g_file);
    char old[MAX_PATH];
    _snprintf_s(old, sizeof(old), _TRUNCATE, "%s.1", g_path);
    DeleteFileA(old);
    MoveFileA(g_path, old);
    OpenLocked();
}

void ExoLog(const char* fmt, ...)
{
    // Formatted before taking the lock, so threads only queue for the write.
    char line[1024];
    SYSTEMTIME st;
    GetLocalTime(&st);
    int n = _snprintf_s(line, sizeof(line), _TRUNCATE,
                        "%02d:%02d:%02d.%03d [%lu:%lu] ",
                        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                        GetCurrentProcessId(), GetCurrentThreadId());
    if (n < 0) {
        return;
    }
    va_list args;
    va_start(args, fmt);
    int m = _vsnprintf_s(line + n, sizeof(line) - n, _TRUNCATE, fmt, args);
    va_end(args);
    if (m < 0) {
        m = (int)strlen(line + n);  // truncated, still terminated
    }
    int len = n + m;
    if (len > (int)sizeof(line) - 2) {
        len = (int)sizeof(line) - 2;
    }
    line[len++] = '\r';
    line[len++] = '\n';

    AcquireSRWLockExclusive(&g_lock);
    if (!g_tried) {
        // Once per process. A failure here is permanent for the process: a
        // missing directory or a denied DACL does not fix itself.
        g_tried = true;
        char exe[MAX_PATH];
        ExeName(exe, sizeof(exe));
        _snprintf_s(g_path, sizeof(g_path), _TRUNCATE, "%s\\exo-%s-%lu.log",
                    kDir, exe, GetCurrentProcessId());
        OpenLocked();
    }
    if (g_file != INVALID_HANDLE_VALUE) {
        if (g_written + len > kMaxBytes) {
            RollLocked();
        }
        DWORD wrote = 0;
        if (g_file != INVALID_HANDLE_VALUE && WriteFile(g_file, line, (DWORD)len, &wrote, NULL)) {
            g_written += wrote;
        }
    }
    ReleaseSRWLockExclusive(&g_lock);
}

void ExoLogClose()
{
    AcquireSRWLockExclusive(&g_lock);
    if (g_file != INVALID_HANDLE_VALUE) {
        CloseHandle(g_file);
        g_file = INVALID_HANDLE_VALUE;
    }
    ReleaseSRWLockExclusive(&g_lock);
}
