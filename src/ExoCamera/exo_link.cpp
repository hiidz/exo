// winsock2.h before windows.h: windows.h otherwise pulls in the old
// winsock.h, and the two conflict.
#include <winsock2.h>
#include <ws2tcpip.h>

#include "exo_link.h"
#include "exo_format.h"
#include "exo_log.h"
#include "exo_stream.h"

#include <mfapi.h>
#include <new>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

// 5.7's limits and timeouts, which 5.9 applies to this link.
static const DWORD kMaxBody = 4u << 20;
static const DWORD kWelcomeTimeoutMs = 2000;
// No message for this long is a dead hub, or an orphan that accepts and
// never sends (CLAUDE.md, Process handling): back to the card, and
// reconnect. A live hub with no frame to send sends IDLE every second.
static const DWORD kFrameTimeoutMs = 3000;
// Message types after the handshake.
static const BYTE kTypeFrame = 1, kTypeIdle = 2;
static const DWORD kRetryMs = 1000;
static const DWORD kStopWaitMs = 1000;
// seq u32, generated-at i64, width, height, stride u32 each.
static const DWORD kBodyPrefix = 4 + 8 + 3 * 4;

LONGLONG ExoQpcMicros()
{
    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    // Split, so the multiply cannot overflow after days of uptime.
    return now.QuadPart / freq.QuadPart * 1000000 + now.QuadPart % freq.QuadPart * 1000000 / freq.QuadPart;
}

static double MsSince(LONGLONG startUs)
{
    return (ExoQpcMicros() - startUs) / 1000.0;
}

static void PutU16(BYTE* p, USHORT v) { p[0] = (BYTE)(v >> 8); p[1] = (BYTE)v; }
static void PutU32(BYTE* p, DWORD v) { PutU16(p, (USHORT)(v >> 16)); PutU16(p + 2, (USHORT)v); }
static USHORT GetU16(const BYTE* p) { return (USHORT)(p[0] << 8 | p[1]); }
static DWORD GetU32(const BYTE* p) { return (DWORD)GetU16(p) << 16 | GetU16(p + 2); }
static LONGLONG GetI64(const BYTE* p) { return (LONGLONG)((ULONGLONG)GetU32(p) << 32 | GetU32(p + 4)); }

// Once per process. Never paired with WSACleanup: this DLL must not be the
// one that drops Winsock's count while a host process tears down (CLAUDE.md).
static INIT_ONCE g_wsaOnce = INIT_ONCE_STATIC_INIT;
static int g_wsaResult = -1;
static BOOL CALLBACK StartWinsock(PINIT_ONCE, PVOID, PVOID*)
{
    WSADATA wsa;
    g_wsaResult = WSAStartup(MAKEWORD(2, 2), &wsa);
    ExoLog("link: WSAStartup %d, once per process, never cleaned up", g_wsaResult);
    return TRUE;
}

// The account this process runs as. In the Frame Server it should read
// NT AUTHORITY\LOCAL SERVICE, which is the point of R6.
static void LogAccount()
{
    HANDLE token = nullptr;
    char name[128] = "?", domain[128] = "?";
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        BYTE buffer[256];
        DWORD size = 0;
        if (GetTokenInformation(token, TokenUser, buffer, sizeof(buffer), &size)) {
            DWORD n = sizeof(name), d = sizeof(domain);
            SID_NAME_USE use;
            LookupAccountSidA(nullptr, ((TOKEN_USER*)buffer)->User.Sid, name, &n, domain, &d, &use);
        }
        CloseHandle(token);
    }
    ExoLog("link: reader started, account %s\\%s", domain, name);
}

ExoLink::~ExoLink()
{
    // The reader is gone by now: it held the stream, and this link lives in
    // the stream. It may even be the reader's own last Release running this.
    if (m_thread) CloseHandle(m_thread);
    if (m_stopEvent) CloseHandle(m_stopEvent);
    delete[] m_scratch;
}

void ExoLink::Start(ExoStream* stream)
{
    AcquireSRWLockExclusive(&m_lock);
    bool already = m_thread != nullptr;
    bool stopping = m_stopping;
    bool started = false;
    if (!already && !stopping) {
        // A thread running this DLL's code must keep the DLL loaded: svchost
        // unloaded it 30s after Shutdown in spike 1. So the reader takes its
        // own reference on the module, and gives it back as it exits, in
        // FreeLibraryAndExitThread. It also holds the stream, which keeps
        // DllCanUnloadNow refusing while it runs.
        m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (m_stopEvent &&
            // Any address inside this DLL names it; a static of ours will do.
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)&g_wsaOnce, &m_module)) {
            stream->AddRef();
            m_stream = stream;
            m_thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
            if (m_thread) {
                started = true;
            } else {
                m_stream = nullptr;
                stream->Release();
                FreeLibrary(m_module);
                m_module = nullptr;
            }
        }
    }
    ReleaseSRWLockExclusive(&m_lock);
    if (already) {
        ExoLog("link: Start, reader already started");
    } else if (stopping) {
        ExoLog("link: Start after Stop, no reader");
    } else if (!started) {
        ExoLog("link: reader thread NOT started, error %lu; serving the test card", GetLastError());
    }
}

void ExoLink::Stop()
{
    AcquireSRWLockExclusive(&m_lock);
    m_stopping = true;
    bool hadSocket = m_socket != INVALID_SOCKET;
    if (hadSocket) {
        // Under the lock, so the handle is still ours (see m_socket). This
        // wakes a blocked recv; the reader closes the socket itself.
        shutdown((SOCKET)m_socket, SD_BOTH);
    }
    HANDLE thread = m_thread;
    ReleaseSRWLockExclusive(&m_lock);
    if (!thread) {
        return;
    }
    SetEvent(m_stopEvent);
    LONGLONG t0 = ExoQpcMicros();
    DWORD wait = WaitForSingleObject(thread, kStopWaitMs);
    if (wait == WAIT_OBJECT_0) {
        ExoLog("link: Stop, %s, reader exited after %.1fms", hadSocket ? "shutdown() on the socket" : "no socket",
               MsSince(t0));
    } else {
        ExoLog("link: Stop, %s, reader STILL RUNNING after %lums; it holds the stream and the DLL, and exits on its own",
               hadSocket ? "shutdown() on the socket" : "no socket", kStopWaitMs);
    }
}

DWORD WINAPI ExoLink::ThreadProc(LPVOID context)
{
    ExoLink* link = static_cast<ExoLink*>(context);
    HMODULE module = link->m_module;
    ExoStream* stream = link->m_stream;
    link->Run();
    // The last touch of the link: it lives inside the stream, which this
    // Release may free. Then the DLL reference goes and the thread ends in
    // one call, so no instruction of this DLL runs after the DLL may unload.
    stream->Release();
    FreeLibraryAndExitThread(module, 0);
}

void ExoLink::Run()
{
    LogAccount();
    InitOnceExecuteOnce(&g_wsaOnce, StartWinsock, nullptr, nullptr);
    m_scratch = new (std::nothrow) BYTE[kMaxBody];
    if (g_wsaResult != 0 || !m_scratch) {
        ExoLog("link: cannot run (WSAStartup %d, buffer %s); serving the test card", g_wsaResult,
               m_scratch ? "ok" : "not allocated");
        return;
    }
    while (Session() == Retry) {
        if (WaitForSingleObject(m_stopEvent, kRetryMs) == WAIT_OBJECT_0) {
            break;
        }
    }
    ExoLog("link: reader exiting");
}

// One connection, from connect to close. Retry, or GiveUp after a version
// refusal.
ExoLink::Outcome ExoLink::Session()
{
    if (!Connect()) {
        return Retry;
    }
    Outcome outcome = Handshake();
    if (outcome == Ready) {
        ReadFrames();
        outcome = Retry;
    }
    CloseSocket();
    // Whatever the hub sent last is stale now: the card says "not connected".
    m_stream->SetPicture(nullptr, 0);
    return outcome;
}

bool ExoLink::Connect()
{
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) {
        ExoLog("link: socket() failed, WSA %d", WSAGetLastError());
        return false;
    }
    AcquireSRWLockExclusive(&m_lock);
    bool stopping = m_stopping;
    if (!stopping) {
        m_socket = s;  // published before connect, so Stop can reach it
    }
    ReleaseSRWLockExclusive(&m_lock);
    if (stopping) {
        closesocket(s);  // never published, so no one else knows it
        return false;
    }

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(EXO_LINK_PORT);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    // Blocking. How long a refused loopback connect takes on Windows is a
    // number this log records rather than assumes.
    LONGLONG t0 = ExoQpcMicros();
    int rc = connect(s, (sockaddr*)&addr, sizeof(addr));
    double ms = MsSince(t0);
    if (rc == SOCKET_ERROR) {
        // 10061 refused (no hub), 10060 timed out, 10013 access denied
        // (a firewall or service hardening rule, TECHNICAL_DESIGN 3.3).
        int err = WSAGetLastError();
        m_failures++;
        if (err != m_lastConnectError) {
            ExoLog("link: connect 127.0.0.1:%u failed, WSA %d, in %.1fms (first of this kind; retrying every %lums)",
                   EXO_LINK_PORT, err, ms, kRetryMs);
            m_lastConnectError = err;
        }
        CloseSocket();
        return false;
    }
    ExoLog("link: connect 127.0.0.1:%u ok in %.1fms, after %d failed attempts", EXO_LINK_PORT, ms, m_failures);
    m_failures = 0;
    m_lastConnectError = -1;
    BOOL on = TRUE;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char*)&on, sizeof(on));
    return true;
}

ExoLink::Outcome ExoLink::Handshake()
{
    SOCKET s = (SOCKET)m_socket;
    DWORD timeout = kWelcomeTimeoutMs;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));

    // HELLO: 5.7's frozen prefix, then this process's ID, so the hub logs
    // which host it is talking to.
    BYTE hello[12] = { 'E', 'X', 'O', 0 };
    PutU16(hello + 4, EXO_LINK_VERSION);
    PutU16(hello + 6, 4);
    PutU32(hello + 8, GetCurrentProcessId());
    if (send(s, (const char*)hello, sizeof(hello), 0) != sizeof(hello)) {
        ExoLog("link: HELLO not sent, WSA %d", WSAGetLastError());
        return Retry;
    }

    // WELCOME: magic, version, status, body length, body.
    BYTE head[9];
    if (!RecvAll(head, sizeof(head))) {
        ExoLog("link: no WELCOME, WSA %d (0 is EOF; 10060 is %lums with nothing)", m_lastError, kWelcomeTimeoutMs);
        return Retry;
    }
    USHORT version = GetU16(head + 4);
    BYTE status = head[6];
    USHORT length = GetU16(head + 7);
    if (memcmp(head, "EXO\0", 4) != 0 || length > 1024 || !RecvAll(m_scratch, length)) {
        ExoLog("link: WELCOME malformed or cut short, body %u bytes", length);
        return Retry;
    }
    if (status != 0) {
        // A refusal's body is always UTF-8 text (5.7). A version mismatch
        // will not fix itself, so stop retrying, as the phone does.
        ExoLog("link: REFUSED by hub (protocol %u), status %u: %.*s%s", version, status, (int)length,
               (const char*)m_scratch,
               status == 1 ? "; not retrying until the camera is re-created" : "");
        return status == 1 ? GiveUp : Retry;
    }
    if (version != EXO_LINK_VERSION) {
        ExoLog("link: REFUSING hub: it accepted at protocol %u, this DLL speaks %u; not retrying", version,
               EXO_LINK_VERSION);
        return GiveUp;
    }
    unsigned long long session = length == 8 ? (ULONGLONG)GetU32(m_scratch) << 32 | GetU32(m_scratch + 4) : 0;
    ExoLog("link: WELCOME, protocol %u, session %016llx", version, session);
    return Ready;
}

// Reads FRAMEs until the connection ends. Each one checked against the
// advertised format becomes the stream's picture.
void ExoLink::ReadFrames()
{
    SOCKET s = (SOCKET)m_socket;
    DWORD timeout = kFrameTimeoutMs;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));

    LONGLONG windowStart = ExoQpcMicros();
    int frames = 0, rejected = 0, skipped = 0, gaps = 0, total = 0;
    LONGLONG latencySum = 0, latencyMax = 0;
    DWORD lastSeq = 0;
    bool haveSeq = false;
    bool idle = false;  // the hub's last word was IDLE; logged on change only
    // The last rejected geometry, so each new kind is logged once.
    DWORD badW = 0, badH = 0, badStride = 0, badLength = 0;

    while (true) {
        BYTE head[8];
        if (!RecvAll(head, sizeof(head))) break;
        BYTE type = head[0];
        DWORD length = GetU32(head + 4);
        if (length > kMaxBody) {
            // Never allocated, never trusted: the stream is out of step.
            ExoLog("link: protocol error, message type %u claims %lu bytes, dropping", type, length);
            break;
        }
        if (!RecvAll(m_scratch, length)) break;
        if (type == kTypeIdle) {
            // The hub is alive with no picture for us: nobody is watching,
            // or no new frame came. Either way the card says so.
            if (!idle) {
                ExoLog("link: IDLE, reason %u (1 nobody watching, 2 no new frame), after %d frames; serving the test card",
                       length >= 1 ? m_scratch[0] : 0, total);
                m_stream->SetPicture(nullptr, 0);
                idle = true;
            }
            continue;
        }
        if (type != kTypeFrame) {
            if (skipped++ == 0) ExoLog("link: skipping message type %u, %lu bytes; logged once", type, length);
            continue;
        }

        LONGLONG nowUs = ExoQpcMicros();
        DWORD seq = 0, w = 0, h = 0, stride = 0;
        LONGLONG stampUs = 0;
        if (length >= kBodyPrefix) {
            seq = GetU32(m_scratch);
            stampUs = GetI64(m_scratch + 4);
            w = GetU32(m_scratch + 12);
            h = GetU32(m_scratch + 16);
            stride = GetU32(m_scratch + 20);
        }
        // Geometry comes with every frame and is checked, never assumed: a
        // wrong stride is an out-of-bounds read inside svchost (CLAUDE.md).
        // Computed in 64 bits, so no stride can wrap the size to a small one.
        ULONGLONG want = kBodyPrefix + (ULONGLONG)stride * h * 3 / 2;
        bool fits = length >= kBodyPrefix && w == EXO_WIDTH && h == EXO_HEIGHT && stride >= w && want == length;
        if (!fits) {
            rejected++;
            if (w != badW || h != badH || stride != badStride || length != badLength) {
                ExoLog("link: frame REJECTED, %lux%lu stride %lu in %lu bytes, advertised %ux%u (first of this kind)",
                       w, h, stride, length, EXO_WIDTH, EXO_HEIGHT);
                badW = w;
                badH = h;
                badStride = stride;
                badLength = length;
            }
        } else {
            if (!Accept(m_scratch)) break;
            if (total++ == 0) {
                ExoLog("link: first frame, seq %lu, %lux%lu stride %lu, now the picture", seq, w, h, stride);
            } else if (idle) {
                ExoLog("link: frames again after IDLE, seq %lu", seq);
            }
            if (idle) {
                // Frames held while idle were never due: not a gap.
                haveSeq = false;
                windowStart = nowUs;
                frames = rejected = skipped = gaps = 0;
                latencySum = latencyMax = 0;
                idle = false;
            }
            frames++;
            gaps += haveSeq && seq != lastSeq + 1 ? 1 : 0;
            lastSeq = seq;
            haveSeq = true;
            LONGLONG latency = nowUs - stampUs;  // hub stamp to here, one QPC
            latencySum += latency;
            latencyMax = latency > latencyMax ? latency : latencyMax;
        }

        if (nowUs - windowStart >= 1000000) {
            ExoLog("link: last second %d frames, %d rejected, %d skipped, %d seq gaps, hub to here %.2fms mean %.2fms max",
                   frames, rejected, skipped, gaps, frames ? latencySum / 1000.0 / frames : 0.0, latencyMax / 1000.0);
            windowStart = nowUs;
            frames = rejected = skipped = gaps = 0;
            latencySum = latencyMax = 0;
        }
    }
    ExoLog("link: closed, WSA %d (0 EOF, 10058 Stop, 10060 no message for %lums, 10053/10054 aborted or reset), after %d frames; "
           "serving the test card", m_lastError, kFrameTimeoutMs, total);
}

// One FRAME body whose geometry has been checked. Copied into a new buffer
// at the advertised stride, EXO_WIDTH, the layout the stream's CopyPicture
// reads, and swapped in as the picture. A tick still copying the old one
// holds its own reference to it. False if no buffer could be made.
bool ExoLink::Accept(const BYTE* body)
{
    DWORD stride = GetU32(body + 20);
    LONGLONG stampUs = GetI64(body + 4);
    ComPtr<IMFMediaBuffer> picture;
    BYTE* out = nullptr;
    HRESULT hr = MFCreateMemoryBuffer(EXO_FRAME_BYTES, &picture);
    if (SUCCEEDED(hr)) hr = picture->Lock(&out, nullptr, nullptr);
    if (FAILED(hr)) {
        ExoLog("link: picture buffer failed, hr=0x%08lX", (unsigned long)hr);
        return false;
    }
    const BYTE* in = body + kBodyPrefix;
    // Y rows, then UV rows, which on the wire start at stride * height.
    for (UINT32 row = 0; row < EXO_HEIGHT * 3 / 2; row++) {
        memcpy(out + (size_t)row * EXO_WIDTH, in + (size_t)row * stride, EXO_WIDTH);
    }
    picture->Unlock();
    picture->SetCurrentLength(EXO_FRAME_BYTES);  // or the buffer reads as empty
    m_stream->SetPicture(picture.Get(), stampUs);
    return true;
}

// Clears the published handle under the lock, then closes it. After the
// clear Stop cannot reach it, so the close cannot race a shutdown().
void ExoLink::CloseSocket()
{
    AcquireSRWLockExclusive(&m_lock);
    SOCKET s = (SOCKET)m_socket;
    m_socket = INVALID_SOCKET;
    ReleaseSRWLockExclusive(&m_lock);
    if (s != INVALID_SOCKET) {
        closesocket(s);
    }
}

// Exactly length bytes, or false with m_lastError set: 0 for EOF, else the
// WSA error, 10060 for a receive timeout.
bool ExoLink::RecvAll(void* out, DWORD length)
{
    SOCKET s = (SOCKET)m_socket;
    char* p = static_cast<char*>(out);
    while (length > 0) {
        int chunk = length > 0x10000000 ? 0x10000000 : (int)length;
        int n = recv(s, p, chunk, 0);
        if (n <= 0) {
            m_lastError = n == 0 ? 0 : WSAGetLastError();
            return false;
        }
        p += n;
        length -= (DWORD)n;
    }
    return true;
}
