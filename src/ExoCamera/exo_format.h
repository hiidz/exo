// The one video format this camera advertises, and the descriptors that
// carry it to the pipeline.
//
// Set when the source is created: the Frame Server reads the formats once,
// at registration, and never again. So the
// source asks the hub, which answers with the size and rate set on the
// camera phone, and the hub registers the camera again when that changes.
// With no hub to ask, the default. Frames arriving from the hub carry
// their own geometry and are checked against it, never assumed.
#pragma once

#include <windows.h>
#include <mfidl.h>

struct ExoFormat
{
    UINT32 width;
    UINT32 height;
    UINT32 fps;

    // NV12: a full-size plane of 8-bit luma, then a half-height plane of
    // interleaved U,V pairs, so 12 bits per pixel. Rows are width bytes in
    // the samples this DLL builds, because this DLL builds them.
    UINT32 FrameBytes() const { return width * height * 3 / 2; }
    // Media Foundation time is in 100ns units.
    LONGLONG FrameDuration() const { return 10000000LL / fps; }
};

// The format before any camera phone has said otherwise.
const ExoFormat EXO_DEFAULT_FORMAT = { 1280, 720, 30 };

// The id the pipeline uses for our one stream. Must be 0, its index. With 1,
// WinRT MediaCapture named the source Source#0 and built no frame source
// from it, so no frame reader: the Windows Camera app and the Settings
// preview stayed grey while browsers worked (src/tools/CamProbe shows it).
const DWORD EXO_STREAM_ID = 0;

// Each returns a new object with one reference, owned by the caller.
HRESULT ExoCreateMediaType(const ExoFormat& format, IMFMediaType** out);

// The presentation descriptor holding our one stream descriptor, which
// holds the media type, already selected and set as current.
HRESULT ExoCreateDescriptors(const ExoFormat& format, IMFPresentationDescriptor** outPd, IMFStreamDescriptor** outSd);
