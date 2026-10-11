// Checks the advertised format by reading it back, the descriptor tree the
// source will hand out, and the contract of Media Foundation's own event
// queue, which the source and stream use with no wrapper: after Shutdown
// every call must refuse with MF_E_SHUTDOWN rather than block or crash.
#include "../exo_format.h"

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <wrl/client.h>
#include <stdio.h>

using Microsoft::WRL::ComPtr;

static int g_failed = 0;

static void Check(const char* what, bool ok, const char* detail = "")
{
    g_failed += ok ? 0 : 1;
    printf("%-4s %-40s %s\n", ok ? "ok" : "FAIL", what, detail);
}

static void Expect(const char* what, HRESULT got, HRESULT want)
{
    char detail[64];
    sprintf_s(detail, "0x%08lX (want 0x%08lX)", (unsigned long)got, (unsigned long)want);
    Check(what, got == want, detail);
}

static void Format()
{
    ComPtr<IMFMediaType> type;
    Expect("ExoCreateMediaType", ExoCreateMediaType(&type), S_OK);
    if (!type) {
        return;
    }
    GUID subtype = GUID_NULL;
    type->GetGUID(MF_MT_SUBTYPE, &subtype);
    Check("subtype NV12", subtype == MFVideoFormat_NV12);

    UINT32 w = 0, h = 0, num = 0, den = 0, size = 0, stride = 0;
    MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
    MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
    type->GetUINT32(MF_MT_SAMPLE_SIZE, &size);
    type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride);
    char detail[64];
    sprintf_s(detail, "%ux%u at %u/%u, stride %u", w, h, num, den, stride);
    Check("size, rate and stride read back", w == 1280 && h == 720 && num == 30 && den == 1 && stride == 1280, detail);

    // Media Foundation's own arithmetic must agree with ours.
    UINT32 mfSize = 0;
    MFCalculateImageSize(MFVideoFormat_NV12, w, h, &mfSize);
    sprintf_s(detail, "ours %u, MF %u", size, mfSize);
    Check("sample size agrees with MF", size == mfSize && size == EXO_FRAME_BYTES, detail);
}

static void Descriptors()
{
    ComPtr<IMFPresentationDescriptor> pd;
    ComPtr<IMFStreamDescriptor> sd;
    Expect("ExoCreateDescriptors", ExoCreateDescriptors(&pd, &sd), S_OK);
    if (!pd) {
        return;
    }
    DWORD count = 0;
    pd->GetStreamDescriptorCount(&count);
    Check("one stream", count == 1);

    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> got;
    pd->GetStreamDescriptorByIndex(0, &selected, &got);
    Check("stream 0 selected", selected == TRUE);
    DWORD id = 0;
    got->GetStreamIdentifier(&id);
    Check("stream id", id == EXO_STREAM_ID);

    ComPtr<IMFMediaTypeHandler> handler;
    got->GetMediaTypeHandler(&handler);
    DWORD types = 0;
    handler->GetMediaTypeCount(&types);
    Check("one media type offered", types == 1);

    ComPtr<IMFMediaType> current, ours;
    Expect("current media type is set", handler->GetCurrentMediaType(&current), S_OK);
    ExoCreateMediaType(&ours);
    DWORD flags = 0;
    Expect("current type equals ours", current ? current->IsEqual(ours.Get(), &flags) : E_FAIL, S_OK);
}

// A callback that is never invoked, only passed: BeginGetEvent checks its
// arguments before it checks for shutdown, so a null one says nothing
// about the shutdown contract. Static, so its refcount does not count.
class NullCallback : public IMFAsyncCallback
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IMFAsyncCallback) {
            *ppv = static_cast<IMFAsyncCallback*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 1; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP GetParameters(DWORD*, DWORD*) override { return E_NOTIMPL; }
    STDMETHODIMP Invoke(IMFAsyncResult*) override { return S_OK; }
};

static NullCallback g_callback;

static void EventQueue()
{
    ComPtr<IMFMediaEventQueue> q;
    Expect("MFCreateEventQueue", MFCreateEventQueue(&q), S_OK);
    if (!q) {
        return;
    }
    ComPtr<IMFMediaEvent> ev;
    // Empty, and NO_WAIT: must return at once, not block.
    Expect("GetEvent empty, NO_WAIT", q->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev), MF_E_NO_EVENTS_AVAILABLE);

    Expect("QueueEvent MESourceStarted", q->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, nullptr), S_OK);
    Expect("GetEvent", q->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev), S_OK);
    MediaEventType type = MEUnknown;
    if (ev) {
        ev->GetType(&type);
    }
    Check("event type MESourceStarted", type == MESourceStarted);
    ev.Reset();

    // A payload object, the way MENewStream hands the stream over.
    ComPtr<IMFAttributes> payload;
    MFCreateAttributes(&payload, 1);
    Expect("QueueEventParamUnk", q->QueueEventParamUnk(MENewStream, GUID_NULL, S_OK, payload.Get()), S_OK);
    q->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev);
    PROPVARIANT v;
    PropVariantInit(&v);
    if (ev) {
        ev->GetValue(&v);
    }
    Check("payload is the same object", v.vt == VT_UNKNOWN && v.punkVal == payload.Get());
    PropVariantClear(&v);
    ev.Reset();

    Expect("Shutdown", q->Shutdown(), S_OK);
    Expect("after Shutdown: GetEvent", q->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev), MF_E_SHUTDOWN);
    Expect("after Shutdown: QueueEvent", q->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, nullptr), MF_E_SHUTDOWN);
    Expect("after Shutdown: BeginGetEvent", q->BeginGetEvent(&g_callback, nullptr), MF_E_SHUTDOWN);
    // The pipeline may shut a source down more than once.
    HRESULT again = q->Shutdown();
    char detail[64];
    sprintf_s(detail, "0x%08lX", (unsigned long)again);
    Check("second Shutdown returns", true, detail);
}

int main()
{
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) {
        printf("MFStartup failed 0x%08lX\n", (unsigned long)hr);
        return 1;
    }
    Format();
    Descriptors();
    EventQueue();
    printf("%s\n", g_failed ? "FAILED" : "all ok");
    return g_failed ? 1 : 0;
}
