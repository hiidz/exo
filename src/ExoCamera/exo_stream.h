// The media stream: the object the Frame Server pulls samples from, with
// RequestSample. One stream, serving every app.
#pragma once

#include "exo_format.h"
#include "exo_ks.h"
#include "exo_link.h"

#include <windows.h>
#include <mfidl.h>
#include <wrl/client.h>

// IMFMediaStream2 adds the stream state the Frame Server sets directly. It
// asked for it when an app stopped and reopened the camera, and pulled
// nothing more without it.
class ExoStream : public IMFMediaStream2, public IMFGetService, public ExoNoKsControl
{
public:
    // The stream holds its source strongly, and the source holds the stream:
    // a cycle, broken by Shutdown. A missed Shutdown therefore leaks rather
    // than leaving a dangling pointer inside a host process.
    // format is the one the source advertises.
    static HRESULT Create(IMFMediaSource* source, IMFStreamDescriptor* sd, const ExoFormat& format, ExoStream** out);

    // Called by the source, not through COM.
    HRESULT Start();   // queues MEStreamStarted, and starts the link once
    HRESULT Stop();    // queues MEStreamStopped; the link stays up
    void Shutdown();   // stops the link, refuses everything after, drops the source
    // Called by the link's reader: a new picture, NV12 at the format's width, and
    // the hub's QPC microsecond stamp for it. Null puts the test card back.
    void SetPicture(IMFMediaBuffer* picture, LONGLONG stampUs);
    // The Frame Server's sample allocator, passed on by the source's
    // IMFSampleAllocatorControl::SetDefaultAllocator.
    HRESULT SetAllocator(IUnknown* allocator);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator, forwarded to m_events
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override;
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** sd) override;
    STDMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE value) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* value) override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* ppv) override;

private:
    ExoStream();
    ~ExoStream();

    // What one event's bookkeeping found worth logging.
    enum Kind { Requested, Refused, Served };
    struct Counts
    {
        HRESULT hr;
        bool firstServed;   // the first sample ever served
        bool firstFailure;  // a failure of a kind not seen just before
        bool report;        // a second has passed; the totals below are set
        LONG requests, refused, served;
        double meanUs, maxUs;  // time to serve one sample
        LONG fromHub;          // samples served from a hub frame, not the card
        double ageMeanMs, ageMaxMs;  // their age when served: now minus the hub's stamp
    };

    // Counts requests, refusals and samples served, the time to serve, and
    // for a hub frame its age when served (ageUs, -1 for the card).
    // Under the lock; the logging happens in Report, outside it.
    Counts Count(Kind kind, LONGLONG serveTicks, HRESULT hr, LONGLONG ageUs = -1);
    void Report(const Counts& c);

    // The frame clock: a thread-pool timer that fires once a frame while the
    // stream runs, and answers one waiting request each time.
    static VOID CALLBACK OnTimer(PTP_CALLBACK_INSTANCE, PVOID context, PTP_TIMER);
    void Tick();
    void StopLocked();

    static HRESULT CopyPicture(const ExoFormat& f, IMFMediaBuffer* src, IMFSample* dst);
    HRESULT CreatePoolLocked();
    void ReleasePoolLocked();

    LONG m_ref = 1;

    // Set in Create and never replaced, so read without the lock.
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_events;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> m_sd;
    ExoFormat m_format = EXO_DEFAULT_FORMAT;
    PTP_TIMER m_timer = nullptr;
    Microsoft::WRL::ComPtr<IMFMediaBuffer> m_card;  // the test card, "not connected"
    // L7. Has its own lock; called outside this stream's.
    ExoLink m_link;

    SRWLOCK m_lock = SRWLOCK_INIT;  // guards everything below
    bool m_shutdown = false;
    // Samples are served only while RUNNING.
    MF_STREAM_STATE m_state = MF_STREAM_STATE_STOPPED;
    Microsoft::WRL::ComPtr<IMFMediaSource> m_source;  // dropped by Shutdown
    // The picture every request is served, copied into a sample from the
    // Frame Server's pool. Only ever replaced, never written.
    Microsoft::WRL::ComPtr<IMFMediaBuffer> m_current;
    LONGLONG m_currentStampUs = 0;  // the hub's stamp for it; 0 for the card
    // Its pool of samples exists while the stream runs.
    Microsoft::WRL::ComPtr<IMFVideoSampleAllocatorEx> m_allocator;
    bool m_poolReady = false;
    // Requests waiting for the frame clock, oldest first, by token (which
    // may be null). Dropped when the stream stops.
    static const int kMaxPending = 64;
    Microsoft::WRL::ComPtr<IUnknown> m_pending[kMaxPending];
    int m_pendingCount = 0;
    bool m_ticking = false;  // a tick is serving; others skip

    // Statistics, for the once-a-second log line.
    LONGLONG m_windowStart = 0;
    LONG m_requests = 0;
    LONG m_refused = 0;
    LONG m_served = 0;
    LONGLONG m_ticksTotal = 0;
    LONGLONG m_ticksMax = 0;
    LONG m_fromHub = 0;
    LONGLONG m_ageTotal = 0;
    LONGLONG m_ageMax = 0;
    bool m_everServed = false;
    bool m_loggedNoSample = false;
    HRESULT m_lastFailure = S_OK;
};
