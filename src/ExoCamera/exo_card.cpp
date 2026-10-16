#include "exo_card.h"
#include "exo_format.h"

#include <mfapi.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

// Eight bars in studio range, Y 16 to 235, values verified by
// eye and by reading frames back. Colour accuracy is not the point of a
// test card.
struct Bar { BYTE y, u, v; };

static const Bar kBars[8] = {
    { 235, 128, 128 },  // white
    { 210,  16, 146 },  // yellow
    { 170, 166,  16 },  // cyan
    { 145,  54,  34 },  // green
    { 106, 202, 222 },  // magenta
    {  81,  90, 240 },  // red
    {  41, 240, 110 },  // blue
    {  16, 128, 128 },  // black
};

HRESULT ExoCreateCard(const ExoFormat& format, IMFMediaBuffer** out)
{
    if (!out) {
        return E_POINTER;
    }
    *out = nullptr;
    const UINT32 width = format.width, height = format.height;
    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = MFCreateMemoryBuffer(format.FrameBytes(), &buffer);
    if (FAILED(hr)) return hr;

    BYTE* data = nullptr;
    hr = buffer->Lock(&data, nullptr, nullptr);
    if (FAILED(hr)) return hr;

    // NV12 at our own stride, width, because this DLL defines this
    // buffer. Y plane: one byte per pixel.
    BYTE* y = data;
    for (UINT32 row = 0; row < height; row++) {
        for (UINT32 col = 0; col < width; col++) {
            y[row * width + col] = kBars[col * 8 / width].y;
        }
    }
    // UV plane: half height, and each U,V pair covers two pixels across, so
    // a row is still width bytes. U first; swapping them gives
    // plausible but wrong colours.
    BYTE* uv = data + width * height;
    for (UINT32 row = 0; row < height / 2; row++) {
        for (UINT32 pair = 0; pair < width / 2; pair++) {
            const Bar& bar = kBars[pair * 2 * 8 / width];
            uv[row * width + pair * 2 + 0] = bar.u;
            uv[row * width + pair * 2 + 1] = bar.v;
        }
    }
    buffer->Unlock();

    // Without this the buffer reports length 0 and the frame is empty
    // rather than an error.
    hr = buffer->SetCurrentLength(format.FrameBytes());
    if (FAILED(hr)) return hr;
    *out = buffer.Detach();
    return S_OK;
}
