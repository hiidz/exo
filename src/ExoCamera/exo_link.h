// The DLL's end of L7, the link from the hub (DECISIONS 5.9). One reader
// thread per stream connects to the hub over loopback and hands each frame
// to the stream as its new picture. With no link, the stream serves the test
// card.
//
// It connects on the stream's first Start, not when the source is created:
// creation happens inside the registering process and in svchost long
// before anyone watches (TECHNICAL_DESIGN 3.2 (b)). It keeps the connection,
// reconnecting as needed, until the stream shuts down.
#pragma once

#include <windows.h>

// The same two numbers as ExoHub's Wire.cs. The port is a constant because
// the DLL reads no config, so it has no way to learn a port Windows chose.
const USHORT EXO_LINK_PORT = 27184;
const USHORT EXO_LINK_VERSION = 1;

class ExoStream;

// QPC in microseconds, the clock the hub stamps frames with. One counter
// for the whole machine, so the hub's stamps compare directly.
LONGLONG ExoQpcMicros();

class ExoLink
{
public:
    ExoLink() = default;
    ~ExoLink();
    ExoLink(const ExoLink&) = delete;
    ExoLink& operator=(const ExoLink&) = delete;

    // Starts the reader thread, once. Later calls only log.
    void Start(ExoStream* stream);
    // Wakes the reader and waits for it, up to a second. Then no new thread
    // starts. A reader that outlives the wait is safe: it holds the stream
    // and the DLL until it exits.
    void Stop();

private:
    // Ready: the handshake was accepted. GiveUp: the hub refused this
    // version, which does not fix itself, so stop retrying.
    enum Outcome { Ready, Retry, GiveUp };

    static DWORD WINAPI ThreadProc(LPVOID context);
    void Run();
    Outcome Session();
    bool Connect();
    Outcome Handshake();
    void ReadFrames();
    bool Accept(const BYTE* body);
    void CloseSocket();
    bool RecvAll(void* out, DWORD length);

    SRWLOCK m_lock = SRWLOCK_INIT;  // guards the two below
    // The socket, typed as Winsock's SOCKET is, so this header need not
    // include winsock2.h. Invariant: a value here is an open socket. Only the
    // reader closes it, after clearing it here under the lock, and Stop calls
    // shutdown() under the same lock. So Stop never reaches a handle number
    // Winsock has already handed to someone else in this process.
    UINT_PTR m_socket = ~(UINT_PTR)0;  // INVALID_SOCKET
    bool m_stopping = false;

    // Set by Start and never replaced after.
    HANDLE m_thread = nullptr;
    HANDLE m_stopEvent = nullptr;
    HMODULE m_module = nullptr;    // the reader's own reference on this DLL
    ExoStream* m_stream = nullptr; // the reader's own reference on the stream

    // The reader's alone.
    BYTE* m_scratch = nullptr;     // one frame body, at most Wire.MaxBody
    int m_lastError = 0;           // the last WSA error, for the close line
    int m_failures = 0;            // connect failures since the last success
    int m_lastConnectError = -1;   // log a failure only when its kind changes
};
