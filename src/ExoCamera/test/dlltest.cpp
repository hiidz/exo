// Loads exocam.dll the way COM would after finding its registration, without
// registering anything: LoadLibrary, then the exported entry points by name.
// Checks the factory contract and the DLL refcount, then unloads. The DLL's
// own log, exo-dlltest-<pid>.log, must agree.
#include <initguid.h>
#include "../exo_guid.h"

#include <windows.h>
#include <mfidl.h>
#include <mfapi.h>
#include <mferror.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <stdio.h>
#include <wchar.h>

typedef HRESULT (STDAPICALLTYPE* GetClassObjectFn)(REFCLSID, REFIID, void**);
typedef HRESULT (STDAPICALLTYPE* CanUnloadFn)(void);

static int g_failed = 0;

static void Expect(const char* what, HRESULT got, HRESULT want)
{
    bool ok = got == want;
    g_failed += ok ? 0 : 1;
    printf("%-4s %-44s 0x%08lX (want 0x%08lX)\n", ok ? "ok" : "FAIL", what,
           (unsigned long)got, (unsigned long)want);
}

static const GUID IID_IKsControl_ =
    { 0x28F54685, 0x06FD, 0x11D2, { 0xB2, 0x7A, 0x00, 0xA0, 0xC9, 0x22, 0x31, 0x96 } };

// The next event from a generator, which must be of type want. Returns it
// with one reference, or NULL.
static IMFMediaEvent* NextEvent(const char* what, IMFMediaEventGenerator* gen, MediaEventType want)
{
    IMFMediaEvent* ev = NULL;
    HRESULT hr = gen->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev);
    MediaEventType type = MEUnknown;
    if (ev) {
        ev->GetType(&type);
    }
    Expect(what, (SUCCEEDED(hr) && type == want) ? S_OK : E_FAIL, S_OK);
    if (type != want && ev) {
        ev->Release();
        ev = NULL;
    }
    return ev;
}

// The payload of an event, as interface iid.
static void* Payload(IMFMediaEvent* ev, REFIID iid)
{
    void* out = NULL;
    PROPVARIANT v;
    PropVariantInit(&v);
    if (ev && SUCCEEDED(ev->GetValue(&v)) && v.vt == VT_UNKNOWN && v.punkVal) {
        v.punkVal->QueryInterface(iid, &out);
    }
    PropVariantClear(&v);
    return out;
}

// The next event, waiting up to ms for it. NULL if none came.
static IMFMediaEvent* WaitEvent(IMFMediaEventGenerator* gen, DWORD ms)
{
    ULONGLONG end = GetTickCount64() + ms;
    IMFMediaEvent* ev = NULL;
    while (FAILED(gen->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev)) && GetTickCount64() < end) {
        Sleep(1);
    }
    return ev;
}

// One request, and the sample the frame clock answered it with, waiting up
// to a second. NULL if none.
static IMFSample* Request(IMFMediaStream* stream, IUnknown* token)
{
    if (FAILED(stream->RequestSample(token))) {
        return NULL;
    }
    IMFMediaEvent* ev = WaitEvent(stream, 1000);
    IMFSample* sample = (IMFSample*)Payload(ev, IID_IMFSample);
    if (ev) ev->Release();
    return sample;
}

// Start, samples, stop, restart, shutdown: the Frame Server's sequence.
static void Stream(IMFMediaSourceEx* source)
{
    IMFMediaStream* stream = NULL;
    IMFMediaEvent* ev = NULL;
    PROPVARIANT pos;
    PropVariantInit(&pos);
    // The caller's own descriptor, with the stream selected, as the Frame
    // Server passes it.
    IMFPresentationDescriptor* pd = NULL;
    source->CreatePresentationDescriptor(&pd);
    if (!pd) {
        Expect("CreatePresentationDescriptor", E_FAIL, S_OK);
        return;
    }
    // The Frame Server hands the source an allocator before Start; this test
    // makes a system-memory one, as the Frame Server would without a GPU.
    IMFVideoSampleAllocatorEx* allocator = NULL;
    MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&allocator));
    IMFSampleAllocatorControl* control = NULL;
    Expect("QI IMFSampleAllocatorControl", source->QueryInterface(IID_PPV_ARGS(&control)), S_OK);
    MFSampleAllocatorUsage usage = MFSampleAllocatorUsage_DoesNotAllocate;
    DWORD inputId = 0;
    if (control) {
        control->GetAllocatorUsage(1, &inputId, &usage);
        Expect("usage: uses the provided allocator", usage == MFSampleAllocatorUsage_UsesProvidedAllocator ? S_OK : E_FAIL, S_OK);
        Expect("SetDefaultAllocator", control->SetDefaultAllocator(1, allocator), S_OK);
        control->Release();
    }
    if (allocator) allocator->Release();  // the stream holds its own reference

    Expect("Start without a descriptor", source->Start(NULL, NULL, &pos), E_POINTER);
    Expect("Start", source->Start(pd, NULL, &pos), S_OK);
    ev = NextEvent("source event 1 is MENewStream", source, MENewStream);
    stream = (IMFMediaStream*)Payload(ev, IID_IMFMediaStream);
    if (ev) ev->Release();
    ev = NextEvent("source event 2 is MESourceStarted", source, MESourceStarted);
    if (ev) ev->Release();
    if (!stream) {
        Expect("MENewStream carried the stream", E_FAIL, S_OK);
        return;
    }
    ev = NextEvent("stream event is MEStreamStarted", stream, MEStreamStarted);
    if (ev) ev->Release();

    IMFMediaSource* back = NULL;
    stream->GetMediaSource(&back);
    IUnknown *a = NULL, *b = NULL;
    if (back) back->QueryInterface(IID_IUnknown, (void**)&a);
    source->QueryInterface(IID_IUnknown, (void**)&b);
    Expect("GetMediaSource is the source", (a && a == b) ? S_OK : E_FAIL, S_OK);
    if (a) a->Release();
    if (b) b->Release();
    if (back) back->Release();

    // One sample, checked through.
    IMFAttributes* token = NULL;
    MFCreateAttributes(&token, 1);
    MFTIME before = MFGetSystemTime();
    IMFSample* s1 = Request(stream, token);
    MFTIME after = MFGetSystemTime();
    Expect("RequestSample produced MEMediaSample", s1 ? S_OK : E_FAIL, S_OK);
    if (!s1) {
        return;
    }
    LONGLONG t1 = 0;
    s1->GetSampleTime(&t1);
    Expect("sample stamped now, from MFGetSystemTime", (t1 >= before && t1 <= after) ? S_OK : E_FAIL, S_OK);
    IUnknown* echoed = NULL;
    s1->GetUnknown(MFSampleExtension_Token, IID_IUnknown, (void**)&echoed);
    IUnknown* tokenUnk = NULL;
    token->QueryInterface(IID_IUnknown, (void**)&tokenUnk);
    Expect("token carried back", (echoed && echoed == tokenUnk) ? S_OK : E_FAIL, S_OK);
    if (echoed) echoed->Release();
    tokenUnk->Release();

    IMFMediaBuffer* buf1 = NULL;
    s1->GetBufferByIndex(0, &buf1);
    BYTE* data = NULL;
    DWORD length = 0;
    bool bars = false;
    if (buf1 && SUCCEEDED(buf1->Lock(&data, NULL, &length))) {
        // Row 0 luma at each bar's centre, and the first U,V pair.
        static const BYTE want[8] = { 235, 210, 170, 145, 106, 81, 41, 16 };
        bars = length == 1280 * 720 * 3 / 2;
        for (int i = 0; i < 8; i++) {
            bars = bars && data[80 + 160 * i] == want[i];
        }
        bars = bars && data[1280 * 720] == 128 && data[1280 * 720 + 1] == 128;
        buf1->Unlock();
    }
    Expect("test card: 1382400 bytes, bars in row 0", bars ? S_OK : E_FAIL, S_OK);

    // A second request: another sample from the pool, and a later time.
    Sleep(2);
    IMFSample* s2 = Request(stream, NULL);
    IMFMediaBuffer* buf2 = NULL;
    LONGLONG t2 = 0;
    if (s2) {
        s2->GetBufferByIndex(0, &buf2);
        s2->GetSampleTime(&t2);
    }
    Expect("second sample: its own buffer, later time",
           (s2 && s2 != s1 && buf2 != buf1 && t2 > t1) ? S_OK : E_FAIL, S_OK);

    // The pool holds 10. With s1 and s2 out, 8 more are served, and the next
    // request waits, unrefused, until a consumer hands a sample back.
    IMFSample* held[8] = {};
    int got = 0;
    for (int i = 0; i < 8; i++) {
        held[i] = Request(stream, NULL);
        got += held[i] ? 1 : 0;
    }
    Expect("pool: 8 more samples", got == 8 ? S_OK : E_FAIL, S_OK);
    Expect("pool empty: request accepted", stream->RequestSample(NULL), S_OK);
    ev = WaitEvent(stream, 300);
    Expect("  and not answered while the pool is empty", ev ? E_FAIL : S_OK, S_OK);
    if (ev) ev->Release();
    if (held[0]) held[0]->Release();
    ev = WaitEvent(stream, 300);
    held[0] = (IMFSample*)Payload(ev, IID_IMFSample);
    if (ev) ev->Release();
    Expect("  answered once a sample is handed back", held[0] ? S_OK : E_FAIL, S_OK);
    for (int i = 0; i < 8; i++) {
        if (held[i]) held[i]->Release();
    }

    if (buf2) buf2->Release();
    if (s2) s2->Release();
    if (buf1) buf1->Release();
    s1->Release();
    token->Release();

    // The Frame Server's direct state calls: stop, then run again, with the
    // events the source's own Stop and Start would send.
    IMFMediaStream2* stream2 = NULL;
    Expect("QI IMFMediaStream2", stream->QueryInterface(IID_IMFMediaStream2, (void**)&stream2), S_OK);
    if (stream2) {
        MF_STREAM_STATE state = MF_STREAM_STATE_STOPPED;
        stream2->GetStreamState(&state);
        Expect("state is running", state == MF_STREAM_STATE_RUNNING ? S_OK : E_FAIL, S_OK);
        Expect("SetStreamState(stopped)", stream2->SetStreamState(MF_STREAM_STATE_STOPPED), S_OK);
        ev = NextEvent("  queues MEStreamStopped", stream, MEStreamStopped);
        if (ev) ev->Release();
        Expect("  and refuses samples", stream->RequestSample(NULL), MF_E_INVALIDREQUEST);
        Expect("SetStreamState(running)", stream2->SetStreamState(MF_STREAM_STATE_RUNNING), S_OK);
        ev = NextEvent("  queues MEStreamStarted", stream, MEStreamStarted);
        if (ev) ev->Release();
        IMFSample* s3 = Request(stream, NULL);
        Expect("  and serves again", s3 ? S_OK : E_FAIL, S_OK);
        if (s3) s3->Release();
        Expect("SetStreamState(paused) from running", stream2->SetStreamState(MF_STREAM_STATE_PAUSED), S_OK);
        Expect("SetStreamState(running) again", stream2->SetStreamState(MF_STREAM_STATE_RUNNING), S_OK);
        ev = NextEvent("  queues MEStreamStarted", stream, MEStreamStarted);
        if (ev) ev->Release();
        stream2->Release();
    }

    // Pull flat out for 1.2s, so the stream logs its per-second line. The
    // frame clock allows 36; the timer's resolution may shift a few.
    LARGE_INTEGER f, start, now;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&start);
    int pulled = 0;
    do {
        IMFSample* s = Request(stream, NULL);
        if (s) {
            s->Release();
            pulled++;
        }
        QueryPerformanceCounter(&now);
    } while (now.QuadPart - start.QuadPart < f.QuadPart * 12 / 10);
    printf("     pulled %d samples in 1.2s\n", pulled);
    Expect("paced near 30fps: 30 to 42 in 1.2s", (pulled >= 30 && pulled <= 42) ? S_OK : E_FAIL, S_OK);

    Expect("Stop", source->Stop(), S_OK);
    ev = NextEvent("stream event is MEStreamStopped", stream, MEStreamStopped);
    if (ev) ev->Release();
    ev = NextEvent("source event is MESourceStopped", source, MESourceStopped);
    if (ev) ev->Release();
    Expect("RequestSample while stopped", stream->RequestSample(NULL), MF_E_INVALIDREQUEST);
    // A flood of refusals must cost one log line, not one each.
    for (int i = 0; i < 100000; i++) {
        stream->RequestSample(NULL);
    }

    // A stopped stream is new to the caller again; an active one is updated.
    Expect("Start again, stream stopped", source->Start(pd, NULL, &pos), S_OK);
    ev = NextEvent("  announces MENewStream", source, MENewStream);
    if (ev) ev->Release();
    ev = NextEvent("  then MESourceStarted", source, MESourceStarted);
    if (ev) ev->Release();
    Expect("Start again, stream running", source->Start(pd, NULL, &pos), S_OK);
    ev = NextEvent("  announces MEUpdatedStream", source, MEUpdatedStream);
    if (ev) ev->Release();
    ev = NextEvent("  then MESourceStarted", source, MESourceStarted);
    if (ev) ev->Release();
    // Deselected: the stream stops.
    pd->DeselectStream(0);
    Expect("Start with the stream deselected", source->Start(pd, NULL, &pos), S_OK);
    Expect("  stops the stream", stream->RequestSample(NULL), MF_E_INVALIDREQUEST);
    pd->Release();

    Expect("Shutdown", source->Shutdown(), S_OK);
    Expect("after Shutdown: RequestSample", stream->RequestSample(NULL), MF_E_SHUTDOWN);
    Expect("after Shutdown: GetMediaSource", stream->GetMediaSource(&back), MF_E_SHUTDOWN);
    stream->Release();
}

// The source as the Frame Server meets it.
static void Source(IMFActivate* activate)
{
    IMFMediaSourceEx* source = NULL;
    Expect("ActivateObject(IMFMediaSourceEx)", activate->ActivateObject(IID_IMFMediaSourceEx, (void**)&source), S_OK);
    if (!source) {
        return;
    }
    // Cached: a second activation is the same object. Identity in COM is
    // the IUnknown pointer.
    IUnknown *a = NULL, *b = NULL, *again = NULL;
    activate->ActivateObject(IID_IUnknown, (void**)&again);
    source->QueryInterface(IID_IUnknown, (void**)&a);
    again->QueryInterface(IID_IUnknown, (void**)&b);
    Expect("second ActivateObject is the same source", a == b ? S_OK : E_FAIL, S_OK);
    a->Release();
    b->Release();
    again->Release();

    // The interfaces the Frame Server needs before it calls Start.
    IUnknown* itf = NULL;
    Expect("QI IMFGetService", source->QueryInterface(IID_IMFGetService, (void**)&itf), S_OK);
    if (itf) itf->Release();
    IKsControl* ks = NULL;
    Expect("QI IKsControl", source->QueryInterface(IID_IKsControl_, (void**)&ks), S_OK);
    if (ks) {
        KSPROPERTY prop = {};
        ULONG returned = 99;
        Expect("KsProperty refused", ks->KsProperty(&prop, sizeof(prop), NULL, 0, &returned),
               HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND));
        Expect("KsProperty returned 0 bytes", returned == 0 ? S_OK : E_FAIL, S_OK);
        ks->Release();
    }
    Expect("QI miss", source->QueryInterface(IID_IClassFactory, (void**)&itf), E_NOINTERFACE);

    DWORD chars = 0;
    source->GetCharacteristics(&chars);
    Expect("characteristics IS_LIVE", chars == MFMEDIASOURCE_IS_LIVE ? S_OK : E_FAIL, S_OK);

    // Each call is a fresh clone with our one selected stream.
    IMFPresentationDescriptor *pd1 = NULL, *pd2 = NULL;
    source->CreatePresentationDescriptor(&pd1);
    source->CreatePresentationDescriptor(&pd2);
    DWORD count = 0;
    BOOL selected = FALSE;
    IMFStreamDescriptor* sd = NULL;
    if (pd1) {
        pd1->GetStreamDescriptorCount(&count);
        pd1->GetStreamDescriptorByIndex(0, &selected, &sd);
    }
    Expect("descriptor: clones, one selected stream",
           (pd1 && pd2 && pd1 != pd2 && count == 1 && selected) ? S_OK : E_FAIL, S_OK);
    if (sd) sd->Release();
    if (pd1) pd1->Release();
    if (pd2) pd2->Release();

    IMFAttributes* attrs = NULL;
    GUID type = GUID_NULL;
    WCHAR* name = NULL;
    UINT32 len = 0;
    source->GetSourceAttributes(&attrs);
    if (attrs) {
        attrs->GetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, &type);
        attrs->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len);
        attrs->Release();
    }
    Expect("source attributes: vidcap, \"Exo Camera\"",
           (type == MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID && name && wcscmp(name, L"Exo Camera") == 0) ? S_OK : E_FAIL, S_OK);
    CoTaskMemFree(name);

    UINT32 shared = 0, sources = 0;
    GUID category = GUID_NULL;
    source->GetStreamAttributes(1, &attrs);
    if (attrs) {
        attrs->GetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, &shared);
        attrs->GetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, &sources);
        attrs->GetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, &category);
        attrs->Release();
    }
    Expect("stream attributes: shared, colour, capture pin",
           (shared == 1 && sources == MFFrameSourceTypes_Color && category == PINNAME_VIDEO_CAPTURE) ? S_OK : E_FAIL, S_OK);

    Expect("Pause refused", source->Pause(), MF_E_INVALID_STATE_TRANSITION);
    Stream(source);

    Expect("after Shutdown: GetCharacteristics", source->GetCharacteristics(&chars), MF_E_SHUTDOWN);
    IMFMediaEvent* ev = NULL;
    Expect("after Shutdown: GetEvent", source->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev), MF_E_SHUTDOWN);
    Expect("second Shutdown", source->Shutdown(), S_OK);
    source->Release();
}

int main(int argc, char** argv)
{
    // Unbuffered, so a crash cannot swallow the lines before it.
    setvbuf(stdout, NULL, _IONBF, 0);
    const char* path = argc > 1 ? argv[1] : "exocam.dll";
    HMODULE dll = LoadLibraryA(path);
    if (!dll) {
        printf("LoadLibrary %s failed %lu\n", path, GetLastError());
        return 1;
    }
    auto getClassObject = (GetClassObjectFn)GetProcAddress(dll, "DllGetClassObject");
    auto canUnload = (CanUnloadFn)GetProcAddress(dll, "DllCanUnloadNow");
    if (!getClassObject || !canUnload) {
        printf("exports missing\n");
        return 1;
    }

    IClassFactory* factory = NULL;
    Expect("DllGetClassObject(other CLSID)",
           getClassObject(IID_IClassFactory, IID_IClassFactory, (void**)&factory),
           CLASS_E_CLASSNOTAVAILABLE);
    Expect("DllGetClassObject(ours, IClassFactory)",
           getClassObject(CLSID_ExoCamera, IID_IClassFactory, (void**)&factory), S_OK);
    if (!factory) {
        return 1;
    }

    IUnknown* obj = NULL;
    Expect("CreateInstance(aggregated)", factory->CreateInstance(factory, IID_IUnknown, (void**)&obj), CLASS_E_NOAGGREGATION);
    // An interface the object lacks: refused, and nothing left alive.
    Expect("CreateInstance(IClassFactory)", factory->CreateInstance(NULL, IID_IClassFactory, (void**)&obj), E_NOINTERFACE);
    Expect("DllCanUnloadNow, after refusal", canUnload(), S_OK);

    IMFActivate* activate = NULL;
    Expect("CreateInstance(IMFActivate)", factory->CreateInstance(NULL, IID_IMFActivate, (void**)&activate), S_OK);
    if (!activate) {
        return 1;
    }
    Expect("DllCanUnloadNow, object alive", canUnload(), S_FALSE);

    // Refcount: our one reference, plus one, minus one.
    ULONG up = activate->AddRef();
    ULONG down = activate->Release();
    Expect("AddRef then Release returns 2 then 1", (up == 2 && down == 1) ? S_OK : E_FAIL, S_OK);

    // The attribute forwarding, through both interfaces of one object.
    IMFAttributes* attrs = NULL;
    Expect("QI IMFAttributes", activate->QueryInterface(IID_IMFAttributes, (void**)&attrs), S_OK);
    Expect("SetString(friendly name)", activate->SetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, L"Exo Camera"), S_OK);
    WCHAR* name = NULL;
    UINT32 len = 0;
    HRESULT got = attrs->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len);
    Expect("GetAllocatedString via IMFAttributes", (SUCCEEDED(got) && wcscmp(name, L"Exo Camera") == 0) ? S_OK : E_FAIL, S_OK);
    CoTaskMemFree(name);
    UINT32 count = 0;
    attrs->GetCount(&count);
    Expect("GetCount is 1", count == 1 ? S_OK : E_FAIL, S_OK);
    attrs->Release();

    Source(activate);
    Expect("ShutdownObject", activate->ShutdownObject(), S_OK);
    Expect("final Release returns 0", activate->Release() == 0 ? S_OK : E_FAIL, S_OK);

    Expect("DllCanUnloadNow, idle", canUnload(), S_OK);
    factory->LockServer(TRUE);
    Expect("DllCanUnloadNow, locked", canUnload(), S_FALSE);
    factory->LockServer(FALSE);
    Expect("DllCanUnloadNow, unlocked", canUnload(), S_OK);
    factory->Release();

    FreeLibrary(dll);
    printf("%s, pid=%lu\n", g_failed ? "FAILED" : "all ok", GetCurrentProcessId());
    return g_failed ? 1 : 0;
}
