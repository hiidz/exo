// The one video format this camera advertises, and the descriptors that
// carry it to the pipeline.
//
// Fixed at build time on purpose: the Frame Server creates the source when
// the camera is registered, before any device has connected, and the DLL
// reads no config (TECHNICAL_DESIGN 3.2 (a)). Which formats to offer is
// open (DECISIONS 11.3). This is the format the camera ADVERTISES; frames
// arriving from the hub carry their own geometry and are checked against
// it (DECISIONS 5.9).
#pragma once

#include <windows.h>
#include <mfidl.h>

const UINT32 EXO_WIDTH = 1280;
const UINT32 EXO_HEIGHT = 720;
const UINT32 EXO_FPS = 30;

// NV12: a full-size plane of 8-bit luma, then a half-height plane of
// interleaved U,V pairs, so 12 bits per pixel. Rows are EXO_WIDTH bytes in
// the samples this DLL builds, because this DLL builds them.
const UINT32 EXO_FRAME_BYTES = EXO_WIDTH * EXO_HEIGHT * 3 / 2;

// The id the pipeline uses for our one stream. Any value, used consistently.
const DWORD EXO_STREAM_ID = 1;

// Each returns a new object with one reference, owned by the caller.
HRESULT ExoCreateMediaType(IMFMediaType** out);

// The presentation descriptor holding our one stream descriptor, which
// holds the media type, already selected and set as current.
HRESULT ExoCreateDescriptors(IMFPresentationDescriptor** outPd, IMFStreamDescriptor** outSd);
