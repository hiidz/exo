#include "exo_stream.h"
#include "exo_card.h"
#include "exo_dll.h"
#include "exo_format.h"
#include "exo_log.h"

#include <mfapi.h>
#include <mferror.h>
#include <new>

using Microsoft::WRL::ComPtr;

// Media Foundation time is in 100ns units.
static const LONGLONG kFrameDuration = 10000000LL / EXO_FPS;

// Samples in the pool taken from the Frame Server's allocator. The
// prototype used 10 (ExoCamSource/MediaStream.cpp:150).
static const DWORD kPoolSamples = 10;

ExoStream::ExoStream()
{
    InterlockedIncrement(&g_objects);
}

ExoStream::~ExoStream()
{
    if (m_timer) {
        // Shutdown normally did this already. Cancelling and waiting again
        // is harmless, and covers a stream freed without one.
        SetThreadpoolTimer(m_timer, nullptr, 0, 0);
        WaitForThreadpoolTimerCallbacks(m_timer, TRUE);
        CloseThreadpoolTimer(m_timer);
    }
    InterlockedDecrement(&g_objects);
}

HRESULT ExoStream::Create(IMFMediaSource* source, IMFStreamDescriptor* sd, ExoStream** out)
{
    if (!source || !sd || !out) {
        return E_POINTER;
    }
    *out = nullptr;
    ExoStream* s = new (std::nothrow) ExoStream();
    if (!s) {
        return E_OUTOFMEMORY;
    }
    HRESULT hr = MFCreateEventQueue(&s->m_events);
    if (SUCCEEDED(hr)) {
        hr = ExoCreateCard(&s->m_card);
        s->m_current = s->m_card;
    }
    if (SUCCEEDED(hr)) {
        // The frame clock. Its callback gets this stream as a raw pointer,
        // which is safe because Shutdown and the destructor wait for any
        // callback in progress before the stream can go.
        s->m_timer = CreateThreadpoolTimer(OnTimer, s, nullptr);
        hr = s->m_timer ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }
    if (FAILED(hr)) {
        s->Release();
        return hr;
    }
    s->m_source = source;  // ComPtr assignment AddRefs: a strong reference
    s->m_sd = sd;
    *out = s;
    return S_OK;
}

// ---- called by the source -------------------------------------------

HRESULT ExoStream::Start()
{
    AcquireSRWLockExclusive(&m_lock);
    HRESULT hr = m_shutdown ? MF_E_SHUTDOWN : S_OK;
    if (SUCCEEDED(hr)) hr = CreatePoolLocked();
    if (SUCCEEDED(hr)) {
        m_state = MF_STREAM_STATE_RUNNING;
        // First tick one frame from now, then every frame. A negative due
        // time is relative, in 100ns units, as FILETIME is.
        LARGE_INTEGER due;
        due.QuadPart = -kFrameDuration;
        FILETIME ft = { due.LowPart, (DWORD)due.HighPart };
        SetThreadpoolTimer(m_timer, &ft, (DWORD)(kFrameDuration / 10000), 0);
    }
    ReleaseSRWLockExclusive(&m_lock);
    if (SUCCEEDED(hr)) {
        hr = m_events->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
    }
    // Connect on the first Start, not at creation (TECHNICAL_DESIGN 3.2 (b)).
    // Later Starts, from the source or from SetStreamState, find it started.
    if (SUCCEEDED(hr)) {
        m_link.Start(this);
    }
    return hr;
}

HRESULT ExoStream::Stop()
{
    AcquireSRWLockExclusive(&m_lock);
    HRESULT hr = m_shutdown ? MF_E_SHUTDOWN : S_OK;
    if (SUCCEEDED(hr)) {
        StopLocked();
    }
    ReleaseSRWLockExclusive(&m_lock);
    if (SUCCEEDED(hr)) {
        hr = m_events->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }
    return hr;
}

// Caller holds the lock. Disarms the timer without waiting for a tick in
// progress: the tick checks the state under the same lock and finds the
// stream stopped. Requests still waiting are dropped, as a stopped stream's
// are.
void ExoStream::StopLocked()
{
    m_state = MF_STREAM_STATE_STOPPED;
    SetThreadpoolTimer(m_timer, nullptr, 0, 0);
    for (int i = 0; i < m_pendingCount; i++) {
        m_pending[i].Reset();
    }
    m_pendingCount = 0;
    ReleasePoolLocked();
}

void ExoStream::Shutdown()
{
    // First, and outside the lock: the reader takes it to set a picture.
    m_link.Stop();
    ComPtr<IMFMediaSource> source;
    AcquireSRWLockExclusive(&m_lock);
    m_shutdown = true;
    StopLocked();
    m_allocator.Reset();
    source.Swap(m_source);  // breaks the cycle; released outside the lock
    ReleaseSRWLockExclusive(&m_lock);
    // Outside the lock, because a tick in progress may be waiting for it.
    // After this no tick is running or will run, so the stream may be freed.
    WaitForThreadpoolTimerCallbacks(m_timer, TRUE);
    m_events->Shutdown();
}

void ExoStream::SetPicture(IMFMediaBuffer* picture, LONGLONG stampUs)
{
    // Swapped in under the lock, released outside it. A tick copying the old
    // picture took its own reference, so it finishes on the old one.
    ComPtr<IMFMediaBuffer> old;
    AcquireSRWLockExclusive(&m_lock);
    old.Swap(m_current);
    m_current = picture ? picture : m_card.Get();
    m_currentStampUs = picture ? stampUs : 0;
    ReleaseSRWLockExclusive(&m_lock);
}

// ---- IUnknown -------------------------------------------------------

STDMETHODIMP ExoStream::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) {
        return E_POINTER;
    }
    // IMFMediaStream2 inherits IMFMediaStream, so one pointer serves both.
    if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaStream ||
        riid == IID_IMFMediaStream2) {
        *ppv = static_cast<IMFMediaStream2*>(this);
    } else if (riid == IID_IMFGetService) {
        *ppv = static_cast<IMFGetService*>(this);
    } else if (riid == EXO_IID_IKsControl) {
        *ppv = static_cast<IKsControl*>(this);
    } else {
        *ppv = nullptr;
        ExoLogQiMiss("ExoStream", riid);
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) ExoStream::AddRef()
{
    return (ULONG)InterlockedIncrement(&m_ref);
}

STDMETHODIMP_(ULONG) ExoStream::Release()
{
    LONG r = InterlockedDecrement(&m_ref);
    if (r == 0) {
        delete this;
    }
    return (ULONG)r;
}

// ---- IMFMediaEventGenerator -----------------------------------------

STDMETHODIMP ExoStream::BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state)
{ return m_events->BeginGetEvent(cb, state); }
STDMETHODIMP ExoStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev)
{ return m_events->EndGetEvent(result, ev); }
STDMETHODIMP ExoStream::GetEvent(DWORD flags, IMFMediaEvent** ev)
{ return m_events->GetEvent(flags, ev); }
STDMETHODIMP ExoStream::QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value)
{ return m_events->QueueEventParamVar(type, ext, status, value); }

// ---- IMFMediaStream -------------------------------------------------

STDMETHODIMP ExoStream::GetMediaSource(IMFMediaSource** source)
{
    if (!source) {
        return E_POINTER;
    }
    *source = nullptr;
    AcquireSRWLockShared(&m_lock);
    HRESULT hr = m_shutdown ? MF_E_SHUTDOWN : m_source.CopyTo(source);
    ReleaseSRWLockShared(&m_lock);
    return hr;
}

STDMETHODIMP ExoStream::GetStreamDescriptor(IMFStreamDescriptor** sd)
{
    if (!sd) {
        return E_POINTER;
    }
    *sd = nullptr;
    AcquireSRWLockShared(&m_lock);
    bool shutdown = m_shutdown;
    ReleaseSRWLockShared(&m_lock);
    return shutdown ? MF_E_SHUTDOWN : m_sd.CopyTo(sd);
}

// Records the request and returns: the frame clock answers it. Answering at
// once let the Frame Server pull 18,000 times a second, and its own sample
// pool did not slow it (DECISIONS 5.1). Never blocks.
STDMETHODIMP ExoStream::RequestSample(IUnknown* token)
{
    AcquireSRWLockExclusive(&m_lock);
    HRESULT hr = m_shutdown ? MF_E_SHUTDOWN
               : m_state != MF_STREAM_STATE_RUNNING ? MF_E_INVALIDREQUEST
               : m_pendingCount == kMaxPending ? MF_E_SAMPLEALLOCATOR_EMPTY
               : S_OK;
    if (SUCCEEDED(hr)) {
        m_pending[m_pendingCount++] = token;  // AddRefs; may be null
    }
    ReleaseSRWLockExclusive(&m_lock);
    // Counted, and refusals logged only the first time each kind happens: at
    // the Frame Server's pull rate a line each would roll the log over in
    // seconds and lose the startup lines.
    Report(Count(FAILED(hr) ? Refused : Requested, 0, hr));
    return hr;
}

VOID CALLBACK ExoStream::OnTimer(PTP_CALLBACK_INSTANCE, PVOID context, PTP_TIMER)
{
    static_cast<ExoStream*>(context)->Tick();
}

// One frame interval has passed: answer the oldest waiting request with the
// current picture. One per tick, so at most EXO_FPS samples a second.
void ExoStream::Tick()
{
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);

    // Only pointers and the token are taken under the lock. If Stop releases
    // the pool in between, the allocation just fails; a sample already
    // allocated is ours to fill either way.
    ComPtr<IUnknown> token;
    ComPtr<IMFVideoSampleAllocatorEx> allocator;
    ComPtr<IMFMediaBuffer> picture;
    LONGLONG stampUs = 0;
    //
    // One tick at a time. When consumers hold every sample in the pool,
    // AllocateSample waits up to a second for one to come back (dlltest);
    // the ticks meanwhile skip, and the waiting request is answered as soon
    // as a consumer lets go. The pool's free count is no guide: it read 0
    // with one sample of ten out (dlltest).
    AcquireSRWLockExclusive(&m_lock);
    bool serve = !m_ticking && m_state == MF_STREAM_STATE_RUNNING && m_pendingCount > 0 && m_poolReady;
    if (serve) {
        m_ticking = true;
        token.Swap(m_pending[0]);
        for (int i = 1; i < m_pendingCount; i++) {
            m_pending[i - 1].Swap(m_pending[i]);
        }
        m_pendingCount--;
        allocator = m_allocator;
        picture = m_current;
        stampUs = m_currentStampUs;
    }
    ReleaseSRWLockExclusive(&m_lock);
    if (!serve) {
        return;
    }

    ComPtr<IMFSample> sample;
    HRESULT hr = allocator->AllocateSample(&sample);
    if (hr == MF_E_SAMPLEALLOCATOR_EMPTY) {
        // Still empty after its wait: the request goes back to the front,
        // unless the stream stopped meanwhile and dropped its requests.
        AcquireSRWLockExclusive(&m_lock);
        if (m_state == MF_STREAM_STATE_RUNNING && m_pendingCount < kMaxPending) {
            for (int i = m_pendingCount; i > 0; i--) {
                m_pending[i].Swap(m_pending[i - 1]);
            }
            m_pending[0].Swap(token);
            m_pendingCount++;
        }
        bool first = !m_loggedNoSample;
        m_loggedNoSample = true;
        m_ticking = false;
        ReleaseSRWLockExclusive(&m_lock);
        if (first) {
            ExoLog("tick: every sample held by consumers for a second; logged once");
        }
        return;
    }
    if (SUCCEEDED(hr)) hr = CopyPicture(picture.Get(), sample.Get());
    // Stamped now, from the clock the pipeline compares against. Never from
    // zero, which gets every frame discarded (spike 1's root cause), and not
    // from a fixed step either, which drifted 6.5 minutes ahead.
    if (SUCCEEDED(hr)) hr = sample->SetSampleTime(MFGetSystemTime());
    if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(kFrameDuration);
    // The pipeline matches a delivery to its request by this token.
    if (SUCCEEDED(hr) && token) hr = sample->SetUnknown(MFSampleExtension_Token, token.Get());
    // Delivered as an event, not returned.
    if (SUCCEEDED(hr)) hr = m_events->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());

    AcquireSRWLockExclusive(&m_lock);
    m_ticking = false;
    ReleaseSRWLockExclusive(&m_lock);
    LARGE_INTEGER t1;
    QueryPerformanceCounter(&t1);
    // How old the hub's frame is when an app gets it. Frames arrive out of
    // phase with this clock, so this is the cost of holding requests to it.
    LONGLONG ageUs = stampUs ? ExoQpcMicros() - stampUs : -1;
    Report(Count(Served, t1.QuadPart - t0.QuadPart, hr, ageUs));
}

// Copies src, NV12 in the advertised geometry with rows EXO_WIDTH bytes
// apart, into dst's buffer. dst's row pitch comes from the buffer itself:
// the Frame Server's samples may be GPU surfaces whose rows are padded, and
// a pitch assumed from a constant would write out of bounds inside svchost
// (CLAUDE.md). The UV plane follows pitch * height bytes after the first
// row, as the prototype placed it (ExoCamSource/MediaStream.cpp:412).
HRESULT ExoStream::CopyPicture(IMFMediaBuffer* src, IMFSample* dst)
{
    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = dst->GetBufferByIndex(0, &buffer);
    if (FAILED(hr)) return hr;
    ComPtr<IMF2DBuffer2> buffer2d;
    hr = buffer.As(&buffer2d);
    if (FAILED(hr)) return hr;

    BYTE* in = nullptr;
    DWORD inLength = 0;
    hr = src->Lock(&in, nullptr, &inLength);
    if (FAILED(hr)) return hr;
    BYTE *row0 = nullptr, *start = nullptr;
    LONG pitch = 0;
    DWORD length = 0;
    hr = buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Write, &row0, &pitch, &start, &length);
    if (SUCCEEDED(hr)) {
        // Room for every row at this pitch, counted from where row 0 starts.
        size_t needed = (size_t)(row0 - start) + (size_t)pitch * (EXO_HEIGHT * 3 / 2);
        if (pitch < (LONG)EXO_WIDTH || needed > length || inLength < EXO_FRAME_BYTES) {
            hr = MF_E_BUFFERTOOSMALL;
        } else {
            for (UINT32 row = 0; row < EXO_HEIGHT * 3 / 2; row++) {
                memcpy(row0 + (size_t)row * pitch, in + (size_t)row * EXO_WIDTH, EXO_WIDTH);
            }
        }
        buffer2d->Unlock2D();
    }
    src->Unlock();
    return hr;
}

HRESULT ExoStream::SetAllocator(IUnknown* allocator)
{
    ComPtr<IMFVideoSampleAllocatorEx> a;
    HRESULT hr = allocator ? allocator->QueryInterface(IID_PPV_ARGS(&a)) : E_POINTER;
    ExoLog("SetAllocator hr=0x%08lX", (unsigned long)hr);
    if (FAILED(hr)) return hr;
    AcquireSRWLockExclusive(&m_lock);
    ReleasePoolLocked();  // a new allocator replaces the old one's pool
    m_allocator = a;
    ReleaseSRWLockExclusive(&m_lock);
    return S_OK;
}

// Caller holds the lock. The pool is created when the stream starts, at
// the current media type, and released when it stops.
HRESULT ExoStream::CreatePoolLocked()
{
    if (m_poolReady || !m_allocator) {
        return S_OK;  // without an allocator, RequestSample says so
    }
    ComPtr<IMFMediaTypeHandler> handler;
    ComPtr<IMFMediaType> type;
    HRESULT hr = m_sd->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->GetCurrentMediaType(&type);
    if (SUCCEEDED(hr)) hr = m_allocator->InitializeSampleAllocator(kPoolSamples, type.Get());
    m_poolReady = SUCCEEDED(hr);
    ExoLog("sample pool of %lu created hr=0x%08lX", kPoolSamples, (unsigned long)hr);
    return hr;
}

void ExoStream::ReleasePoolLocked()
{
    if (m_poolReady) {
        m_allocator->UninitializeSampleAllocator();
        m_poolReady = false;
    }
}

ExoStream::Counts ExoStream::Count(Kind kind, LONGLONG serveTicks, HRESULT hr, LONGLONG ageUs)
{
    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    Counts c = {};
    c.hr = hr;

    AcquireSRWLockExclusive(&m_lock);
    if (m_windowStart == 0) {
        m_windowStart = now.QuadPart;
    }
    if (kind == Served) {
        m_served++;
        m_ticksTotal += serveTicks;
        if (serveTicks > m_ticksMax) {
            m_ticksMax = serveTicks;
        }
        if (ageUs >= 0 && SUCCEEDED(hr)) {
            m_fromHub++;
            m_ageTotal += ageUs;
            m_ageMax = ageUs > m_ageMax ? ageUs : m_ageMax;
        }
    } else {
        m_requests++;
    }
    if (FAILED(hr)) {
        m_refused += kind == Refused ? 1 : 0;
        // A kind not seen before: MF_E_INVALIDREQUEST and MF_E_SHUTDOWN are
        // the expected two, anything else is a failure building the sample.
        c.firstFailure = hr != m_lastFailure;
        m_lastFailure = hr;
    } else if (kind == Served) {
        c.firstServed = !m_everServed;
        m_everServed = true;
    }
    if (now.QuadPart - m_windowStart >= freq.QuadPart) {
        c.report = true;
        c.requests = m_requests;
        c.refused = m_refused;
        c.served = m_served;
        c.meanUs = m_served ? m_ticksTotal * 1e6 / freq.QuadPart / m_served : 0;
        c.maxUs = m_ticksMax * 1e6 / freq.QuadPart;
        c.fromHub = m_fromHub;
        c.ageMeanMs = m_fromHub ? m_ageTotal / 1000.0 / m_fromHub : 0;
        c.ageMaxMs = m_ageMax / 1000.0;
        m_fromHub = 0;
        m_ageTotal = 0;
        m_ageMax = 0;
        m_windowStart = now.QuadPart;
        m_requests = 0;
        m_refused = 0;
        m_served = 0;
        m_ticksTotal = 0;
        m_ticksMax = 0;
    }
    ReleaseSRWLockExclusive(&m_lock);
    return c;
}

// Logs outside the lock. The first success gets its own line: "no
// requests" and "requests served unseen" must not look the same (spike 1).
void ExoStream::Report(const Counts& c)
{
    if (c.firstServed) {
        ExoLog("first sample served");
    }
    if (c.firstFailure) {
        ExoLog("request refused or sample failed, hr=0x%08lX (first of this kind; later ones are counted)",
               (unsigned long)c.hr);
    }
    if (c.report) {
        ExoLog("last second: %ld requests, %ld refused, %ld served, %.1fus mean to serve, %.1fus max; "
               "%ld from the hub, aged %.1fms mean %.1fms max when served",
               c.requests, c.refused, c.served, c.meanUs, c.maxUs, c.fromHub, c.ageMeanMs, c.ageMaxMs);
    }
}

// ---- IMFMediaStream2 ------------------------------------------------
// The Frame Server sets the state directly, without going through the
// source. Running and stopped behave as the source's Start and Stop do for
// this stream, events included; paused only from running, with no event.
// The prototype did the same (ExoCamSource/MediaStream.cpp:574-600).

STDMETHODIMP ExoStream::SetStreamState(MF_STREAM_STATE value)
{
    AcquireSRWLockShared(&m_lock);
    MF_STREAM_STATE current = m_state;
    bool shutdown = m_shutdown;
    ReleaseSRWLockShared(&m_lock);
    ExoLog("SetStreamState %d -> %d", (int)current, (int)value);
    if (shutdown) {
        return MF_E_SHUTDOWN;
    }
    if (value == current) {
        return S_OK;
    }
    switch (value) {
    case MF_STREAM_STATE_RUNNING:
        return Start();
    case MF_STREAM_STATE_STOPPED:
        return Stop();
    case MF_STREAM_STATE_PAUSED:
        // The timer keeps running; ticks serve nothing until running again.
        if (current != MF_STREAM_STATE_RUNNING) {
            return MF_E_INVALID_STATE_TRANSITION;
        }
        AcquireSRWLockExclusive(&m_lock);
        m_state = MF_STREAM_STATE_PAUSED;
        ReleaseSRWLockExclusive(&m_lock);
        return S_OK;
    default:
        return MF_E_INVALID_STATE_TRANSITION;
    }
}

STDMETHODIMP ExoStream::GetStreamState(MF_STREAM_STATE* value)
{
    if (!value) {
        return E_POINTER;
    }
    AcquireSRWLockShared(&m_lock);
    HRESULT hr = m_shutdown ? MF_E_SHUTDOWN : S_OK;
    *value = m_state;
    ReleaseSRWLockShared(&m_lock);
    return hr;
}

// ---- IMFGetService --------------------------------------------------

STDMETHODIMP ExoStream::GetService(REFGUID service, REFIID riid, LPVOID* ppv)
{
    UNREFERENCED_PARAMETER(service);
    if (!ppv) {
        return E_POINTER;
    }
    return SUCCEEDED(QueryInterface(riid, ppv)) ? S_OK : MF_E_UNSUPPORTED_SERVICE;
}
