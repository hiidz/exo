#include "exo_format.h"

#include <mfapi.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

HRESULT ExoCreateMediaType(const ExoFormat& format, IMFMediaType** out)
{
    if (!out) {
        return E_POINTER;
    }
    *out = nullptr;

    // Built in a ComPtr, so an early return below releases it.
    ComPtr<IMFMediaType> type;
    HRESULT hr = MFCreateMediaType(&type);
    if (FAILED(hr)) return hr;
    hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return hr;
    hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (FAILED(hr)) return hr;
    // Size and rate are each one 64-bit attribute holding two 32-bit halves.
    hr = MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, format.width, format.height);
    if (FAILED(hr)) return hr;
    hr = MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, format.fps, 1);
    if (FAILED(hr)) return hr;
    hr = MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(hr)) return hr;
    hr = type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return hr;
    hr = type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    if (FAILED(hr)) return hr;
    hr = type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
    if (FAILED(hr)) return hr;
    hr = type->SetUINT32(MF_MT_SAMPLE_SIZE, format.FrameBytes());
    if (FAILED(hr)) return hr;
    hr = type->SetUINT32(MF_MT_DEFAULT_STRIDE, format.width);
    if (FAILED(hr)) return hr;

    *out = type.Detach();  // the caller takes over our reference
    return S_OK;
}

HRESULT ExoCreateDescriptors(const ExoFormat& format, IMFPresentationDescriptor** outPd, IMFStreamDescriptor** outSd)
{
    if (!outPd || !outSd) {
        return E_POINTER;
    }
    *outPd = nullptr;
    *outSd = nullptr;

    ComPtr<IMFMediaType> type;
    HRESULT hr = ExoCreateMediaType(format, &type);
    if (FAILED(hr)) return hr;

    IMFMediaType* types[] = { type.Get() };
    ComPtr<IMFStreamDescriptor> sd;
    hr = MFCreateStreamDescriptor(EXO_STREAM_ID, ARRAYSIZE(types), types, &sd);
    if (FAILED(hr)) return hr;

    // One type on offer, so it is the current one from the start.
    ComPtr<IMFMediaTypeHandler> handler;
    hr = sd->GetMediaTypeHandler(&handler);
    if (FAILED(hr)) return hr;
    hr = handler->SetCurrentMediaType(type.Get());
    if (FAILED(hr)) return hr;

    IMFStreamDescriptor* sds[] = { sd.Get() };
    ComPtr<IMFPresentationDescriptor> pd;
    hr = MFCreatePresentationDescriptor(ARRAYSIZE(sds), sds, &pd);
    if (FAILED(hr)) return hr;
    // An unselected stream produces nothing, which looks like a camera that
    // opens and shows black.
    hr = pd->SelectStream(0);
    if (FAILED(hr)) return hr;

    *outPd = pd.Detach();
    *outSd = sd.Detach();
    return S_OK;
}
