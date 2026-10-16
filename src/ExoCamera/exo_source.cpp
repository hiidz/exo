#include "exo_source.h"
#include "exo_dll.h"
#include "exo_format.h"
#include "exo_guid.h"
#include "exo_log.h"

#include <mfapi.h>
#include <mferror.h>
#include <ksmedia.h>
#include <new>

using Microsoft::WRL::ComPtr;

ExoSource::ExoSource() : ExoNoKsControl("ExoSource")
{
    InterlockedIncrement(&g_objects);
}

ExoSource::~ExoSource()
{
    InterlockedDecrement(&g_objects);
}

HRESULT ExoSource::Create(ExoSource** out)
{
    if (!out) {
        return E_POINTER;
    }
    *out = nullptr;
    ExoSource* s = new (std::nothrow) ExoSource();
    if (!s) {
        return E_OUTOFMEMORY;
    }
    HRESULT hr = s->Init();
    ExoLog("ExoSource::Create hr=0x%08lX", (unsigned long)hr);
    if (FAILED(hr)) {
        s->Release();
        return hr;
    }
    *out = s;
    return S_OK;
}

HRESULT ExoSource::Init()
{
    // Reference counted by Media Foundation. Never paired with MFShutdown:
    // this DLL lives in processes it does not own, and dropping a global
    // count during their teardown is the same mistake as WSACleanup would
    // be there.
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;
    hr = MFCreateEventQueue(&m_events);
    if (FAILED(hr)) return hr;
    // Asked once: the Frame Server reads the descriptors only at
    // registration, so a new format comes with a new source.
    m_format = ExoLink::AskFormat();
    hr = ExoCreateDescriptors(m_format, &m_pd, &m_sd);
    if (FAILED(hr)) return hr;

    // A device source must describe itself with these, or registration
    // fails with MF_E_ATTRIBUTENOTFOUND, naming nothing.
    // MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES is left out: no
    // test has shown it matters.
    hr = MFCreateAttributes(&m_sourceAttrs, 2);
    if (FAILED(hr)) return hr;
    hr = m_sourceAttrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (FAILED(hr)) return hr;
    hr = m_sourceAttrs->SetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, L"" EXO_FRIENDLY_NAME);
    if (FAILED(hr)) return hr;

    hr = MFCreateAttributes(&m_streamAttrs, 4);
    if (FAILED(hr)) return hr;
    hr = m_streamAttrs->SetUINT32(MF_DEVICESTREAM_STREAM_ID, EXO_STREAM_ID);
    if (FAILED(hr)) return hr;
    hr = m_streamAttrs->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE);
    if (FAILED(hr)) return hr;
    // Necessary for a second app at once, not sufficient: Multi-App Camera
    // must also be on, per camera in Windows' camera settings. Do not
    // remove it.
    hr = m_streamAttrs->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    if (FAILED(hr)) return hr;
    hr = m_streamAttrs->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);
    if (FAILED(hr)) return hr;

    // Last, because the stream takes a reference to this source: if
    // anything after it could fail, Create's Release would leave the cycle
    // holding the source alive.
    return ExoStream::Create(this, m_sd.Get(), m_format, &m_stream);
}

HRESULT ExoSource::CheckShutdown()
{
    AcquireSRWLockShared(&m_lock);
    HRESULT hr = m_shutdown ? MF_E_SHUTDOWN : S_OK;
    ReleaseSRWLockShared(&m_lock);
    return hr;
}

// ---- IUnknown -------------------------------------------------------

STDMETHODIMP ExoSource::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) {
        return E_POINTER;
    }
    // Each cast yields the address of that interface's part of the object.
    if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaSource) {
        *ppv = static_cast<IMFMediaSource*>(this);
    } else if (riid == IID_IMFMediaSourceEx) {
        *ppv = static_cast<IMFMediaSourceEx*>(this);
    } else if (riid == IID_IMFGetService) {
        *ppv = static_cast<IMFGetService*>(this);
    } else if (riid == IID_IMFSampleAllocatorControl) {
        *ppv = static_cast<IMFSampleAllocatorControl*>(this);
    } else if (riid == EXO_IID_IKsControl) {
        *ppv = static_cast<IKsControl*>(this);
    } else {
        // The Frame Server probes for interfaces nobody documents. A miss is
        // normal; the log shows which ones it asked for.
        *ppv = nullptr;
        ExoLogQiMiss("ExoSource", riid);
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) ExoSource::AddRef()
{
    return (ULONG)InterlockedIncrement(&m_ref);
}

STDMETHODIMP_(ULONG) ExoSource::Release()
{
    LONG r = InterlockedDecrement(&m_ref);
    if (r == 0) {
        delete this;
    }
    return (ULONG)r;
}

// ---- IMFMediaEventGenerator -----------------------------------------
// After Shutdown the queue itself refuses with MF_E_SHUTDOWN (fmttest).

STDMETHODIMP ExoSource::BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state)
{ return m_events->BeginGetEvent(cb, state); }
STDMETHODIMP ExoSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev)
{ return m_events->EndGetEvent(result, ev); }
STDMETHODIMP ExoSource::GetEvent(DWORD flags, IMFMediaEvent** ev)
{ return m_events->GetEvent(flags, ev); }
STDMETHODIMP ExoSource::QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value)
{ return m_events->QueueEventParamVar(type, ext, status, value); }

// ---- IMFMediaSource -------------------------------------------------

STDMETHODIMP ExoSource::GetCharacteristics(DWORD* characteristics)
{
    if (!characteristics) {
        return E_POINTER;
    }
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) {
        return hr;
    }
    // A camera is live: no seeking, no meaningful pause.
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP ExoSource::CreatePresentationDescriptor(IMFPresentationDescriptor** pd)
{
    if (!pd) {
        return E_POINTER;
    }
    *pd = nullptr;
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) {
        return hr;
    }
    // A clone: callers select streams and set types on what they get, and
    // must not be editing ours.
    return m_pd->Clone(pd);
}

STDMETHODIMP ExoSource::Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPosition)
{
    // Only the default time format, 100ns units, makes sense.
    if (!pd) {
        return E_POINTER;
    }
    if (timeFormat && *timeFormat != GUID_NULL) {
        return MF_E_UNSUPPORTED_TIME_FORMAT;
    }
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) {
        return hr;
    }

    // Whether the caller selected our one stream, and what state the stream
    // is in now. The Frame Server also stops the stream directly, through
    // IMFMediaStream2, so its state, not this source's history, says whether
    // it is still active.
    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> sd;
    hr = pd->GetStreamDescriptorByIndex(0, &selected, &sd);
    if (FAILED(hr)) {
        return hr;
    }
    MF_STREAM_STATE state = MF_STREAM_STATE_STOPPED;
    m_stream->GetStreamState(&state);
    bool active = state != MF_STREAM_STATE_STOPPED;

    if (selected) {
        // The pipeline learns of the stream from this event, which carries
        // it. A stopped stream is new to the caller; an active one is
        // updated. Announcing a stopped stream as updated got it stopped
        // again at once and never pulled.
        hr = m_events->QueueEventParamUnk(active ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
                                          static_cast<IMFMediaStream*>(m_stream.Get()));
        if (SUCCEEDED(hr)) hr = m_stream->Start();
    } else if (active) {
        hr = m_stream->Stop();
    }
    if (SUCCEEDED(hr)) {
        PROPVARIANT start;
        PropVariantInit(&start);
        if (startPosition) {
            PropVariantCopy(&start, startPosition);
        } else {
            start.vt = VT_I8;
            start.hVal.QuadPart = 0;
        }
        hr = m_events->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &start);
        PropVariantClear(&start);
    }
    ExoLog("ExoSource::Start selected=%d stream was %s hr=0x%08lX", selected,
           active ? "active" : "stopped", (unsigned long)hr);
    return hr;
}

STDMETHODIMP ExoSource::Stop()
{
    // The Frame Server has never been seen calling this.
    // Logged so that the first time it does is visible.
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = m_stream->Stop();
    if (SUCCEEDED(hr)) hr = m_events->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    ExoLog("ExoSource::Stop hr=0x%08lX", (unsigned long)hr);
    return hr;
}

STDMETHODIMP ExoSource::Pause()
{
    // A live source does not pause, and saying so beats pretending.
    return MF_E_INVALID_STATE_TRANSITION;
}

STDMETHODIMP ExoSource::Shutdown()
{
    AcquireSRWLockExclusive(&m_lock);
    bool already = m_shutdown;
    m_shutdown = true;
    ReleaseSRWLockExclusive(&m_lock);
    if (already) {
        return S_OK;  // the pipeline may call it more than once
    }
    // The stream drops its reference to this source, breaking the cycle.
    // m_stream itself is kept: it is set once and read without the lock,
    // and the stream is freed with this source.
    m_stream->Shutdown();
    // Every later event call now refuses, and anyone waiting is released.
    m_events->Shutdown();
    ExoLog("ExoSource::Shutdown");
    return S_OK;
}

// ---- IMFMediaSourceEx -----------------------------------------------

STDMETHODIMP ExoSource::GetSourceAttributes(IMFAttributes** attrs)
{
    if (!attrs) {
        return E_POINTER;
    }
    *attrs = nullptr;
    ExoLog("GetSourceAttributes");
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) {
        return hr;
    }
    // The caller gets its own reference to our store.
    return m_sourceAttrs.CopyTo(attrs);
}

STDMETHODIMP ExoSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attrs)
{
    if (!attrs) {
        return E_POINTER;
    }
    *attrs = nullptr;
    // There is one stream. Whether callers pass its identifier or its index
    // is not documented; ignoring the argument has always worked, so this
    // does too, and logs what arrives.
    ExoLog("GetStreamAttributes streamId=%lu", streamId);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) {
        return hr;
    }
    return m_streamAttrs.CopyTo(attrs);
}

STDMETHODIMP ExoSource::SetD3DManager(IUnknown* manager)
{
    // Accepted and not used: frames come from system memory, over a socket,
    // and are copied into whatever samples the allocator provides.
    // Returning E_NOTIMPL here is suspected of keeping the Windows Camera
    // app from starting the stream.
    ExoLog("SetD3DManager %p", (void*)manager);
    return manager ? S_OK : E_POINTER;
}

// ---- IMFSampleAllocatorControl --------------------------------------

STDMETHODIMP ExoSource::SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator)
{
    ExoLog("SetDefaultAllocator stream %lu", outputStreamId);
    HRESULT hr = CheckShutdown();
    return FAILED(hr) ? hr : m_stream->SetAllocator(allocator);
}

STDMETHODIMP ExoSource::GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
{
    if (!inputStreamId || !usage) {
        return E_POINTER;
    }
    *inputStreamId = outputStreamId;
    *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
    return S_OK;
}

// ---- IMFGetService --------------------------------------------------

STDMETHODIMP ExoSource::GetService(REFGUID service, REFIID riid, LPVOID* ppv)
{
    // Serves the interfaces this object has, whatever service is named.
    UNREFERENCED_PARAMETER(service);
    if (!ppv) {
        return E_POINTER;
    }
    return SUCCEEDED(QueryInterface(riid, ppv)) ? S_OK : MF_E_UNSUPPORTED_SERVICE;
}
