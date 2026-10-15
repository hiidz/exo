// The media source: what ExoActivate::ActivateObject returns and the Frame
// Server drives. One per camera registration, not one per consumer
// (DECISIONS 7.5), so one source serves every app.
#pragma once

#include "exo_ks.h"
#include "exo_stream.h"

#include <windows.h>
#include <mfidl.h>
#include <wrl/client.h>

class ExoSource : public IMFMediaSourceEx, public IMFGetService, public IMFSampleAllocatorControl,
                  public ExoNoKsControl
{
public:
    // Returns the source with one reference, owned by the caller.
    static HRESULT Create(ExoSource** out);

    // IUnknown, one refcount for all of the interfaces above
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator, forwarded to m_events
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override;
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** pd) override;
    STDMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** attrs) override;
    STDMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attrs) override;
    STDMETHODIMP SetD3DManager(IUnknown* manager) override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* ppv) override;

    // IMFSampleAllocatorControl: the Frame Server hands the stream its
    // allocator, whose pool paces the requests (DECISIONS 5.1). The
    // prototype did the same (ExoCamSource/MediaSource.cpp:320-347).
    STDMETHODIMP SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) override;
    STDMETHODIMP GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

private:
    ExoSource();
    ~ExoSource();
    HRESULT Init();
    // S_OK, or MF_E_SHUTDOWN once Shutdown has run.
    HRESULT CheckShutdown();

    LONG m_ref = 1;

    // Set in Init, before the object is shared, and never replaced, so read
    // without the lock.
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_events;
    Microsoft::WRL::ComPtr<IMFPresentationDescriptor> m_pd;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> m_sd;
    ExoFormat m_format = EXO_DEFAULT_FORMAT;  // the hub's when created, else the default
    Microsoft::WRL::ComPtr<IMFAttributes> m_sourceAttrs;
    Microsoft::WRL::ComPtr<IMFAttributes> m_streamAttrs;

    // Change after creation, so guarded.
    SRWLOCK m_lock = SRWLOCK_INIT;
    bool m_shutdown = false;
    // Created last in Init. It holds this source strongly in return, a
    // cycle that Shutdown breaks (exo_stream.h).
    Microsoft::WRL::ComPtr<ExoStream> m_stream;
};
