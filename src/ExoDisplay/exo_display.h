// The hub to ExoDisplay.dll link: IOCTLs on the adapter's device interface.
// src/ExoHub/Display/ExoDisplayDriver.cs mirrors every struct here. Change
// both and bump ExoDisplayVersion.
//
// Any IOCTL resets the watchdog. Once a monitor is plugged, the driver
// unplugs every monitor if no IOCTL arrives for WatchdogSeconds, so a hub
// that dies takes its display with it.

#pragma once

#include <windows.h>
#include <stdint.h>

#define IOCTL_EXO_ADD_MONITOR    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_EXO_REMOVE_MONITOR CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_EXO_SET_RENDER_ADAPTER CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_EXO_PING           CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_EXO_GET_VERSION    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_EXO_GET_FRAME      CTL_CODE(FILE_DEVICE_UNKNOWN, 0x805, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_EXO_GET_CURSOR     CTL_CODE(FILE_DEVICE_UNKNOWN, 0x806, METHOD_BUFFERED, FILE_ANY_ACCESS)

// 2 added IOCTL_EXO_GET_FRAME and the shared texture. 3 added
// IOCTL_EXO_GET_CURSOR and its event.
static const uint32_t ExoDisplayVersion = 3;
static const uint32_t ExoWatchdogSeconds = 3;

// {55599bd1-bd1f-4aec-b857-87bcd97f2f8f}
static const GUID ExoDisplayInterface = { 0x55599bd1, 0xbd1f, 0x4aec, { 0xb8, 0x57, 0x87, 0xbc, 0xd9, 0x7f, 0x2f, 0x8f } };

#define EXO_MAX_MODES 8
#define EXO_EDID_STRING 13

struct ExoMode {
    uint32_t Width;
    uint32_t Height;
    uint32_t Hz;
};

// In: the monitor to plug. Id is its identity: the same Id gives the same
// EDID serial, so Windows keeps its position and scale. The monitor offers
// exactly Modes, the first preferred. Name and Serial are the EDID's
// strings, ASCII, at most 13 characters, zero-padded. Sizes in mm, 0 when
// unknown. Plugging an Id already plugged returns its target again.
struct ExoAddMonitor {
    GUID Id;
    uint32_t WidthMm;
    uint32_t HeightMm;
    uint32_t ModeCount;
    ExoMode Modes[EXO_MAX_MODES];
    char Name[16];
    char Serial[16];
};

// Out: where Windows put it, for QueryDisplayConfig.
struct ExoAddMonitorOut {
    LUID AdapterLuid;
    uint32_t TargetId;
};

// In: the monitor to unplug.
struct ExoRemoveMonitor {
    GUID Id;
};

// In: the GPU that renders the virtual display's desktop.
struct ExoSetRenderAdapter {
    LUID AdapterLuid;
};

// Out: ExoDisplayVersion.
struct ExoVersionOut {
    uint32_t Version;
};

// The desktop image, shared. The driver copies each new image of the
// monitor into one texture, named Name, which the hub opens with
// OpenSharedResourceByName on a device on RenderAdapter. A new swap-chain,
// on a mode change or a new render adapter, brings a new texture and a new
// Generation; 0 means there is none.
//
// The texture's keyed mutex: key 1 means a new image is in it, for the
// hub; key 0 that the hub has taken it. The hub acquires 1, reads, and
// releases 0. The driver never waits on the hub: it acquires 0, or 1 when
// the hub has not taken the last image yet, copies, and releases 1. While
// the hub holds the texture, the driver keeps the newest image and copies
// it as soon as the hub lets go.
//
// Out: the texture and the image now in it. Read while holding the keyed
// mutex, FrameNumber and PresentQpc describe the image the hub holds.
struct ExoFrameInfo {
    uint32_t Generation;
    LUID RenderAdapter;
    uint32_t Width;
    uint32_t Height;
    uint32_t Format;       // DXGI_FORMAT
    uint32_t FrameNumber;  // Windows' presentation frame number
    uint64_t PresentQpc;   // when Windows meant it shown, system QPC
    uint32_t Copied;       // images copied into this texture
    uint32_t Waited;       // images that found the hub holding it
    wchar_t Name[64];
};

static_assert(sizeof(ExoFrameInfo) == 176, "ExoFrameInfo layout");

// The pointer. Windows leaves it out of the images (a hardware cursor), and
// the driver forwards it for the phone to draw. The driver signals the
// event named ExoCursorEvent on every change, and the hub then asks.
//
// In: ExoCursorQuery, the shape the hub already has, or 0xFFFFFFFF for
// none. Out: ExoCursorInfo, followed by the shape's pixels when ShapeId
// differs from HaveShapeId and the buffer has room for them: Height rows
// of Pitch bytes, 32 bits a pixel. Type 2 (alpha) is straight-alpha BGRA.
// Type 1 (masked colour) is BGR with the top byte a mask: 0 replaces the
// screen, 0xFF is XORed with it.
#define EXO_CURSOR_MAX 256
#define EXO_CURSOR_EVENT L"Global\\ExoDisplayCursor"

struct ExoCursorQuery {
    uint32_t HaveShapeId;
};

struct ExoCursorInfo {
    uint32_t Seq;         // counts updates
    int32_t Visible;      // on this monitor
    int32_t X;            // the shape's top-left in the monitor's pixels,
    int32_t Y;            // negative at its left and top edges
    uint64_t Qpc;         // when the driver read it, system QPC
    uint32_t ShapeId;     // Windows' id for the current shape
    uint32_t Type;        // 1 masked colour, 2 alpha
    uint32_t Width;
    uint32_t Height;
    uint32_t Pitch;
    uint32_t XHot;
    uint32_t YHot;
    uint32_t ShapeBytes;  // pixel bytes that follow, 0 for none
};

static_assert(sizeof(ExoCursorInfo) == 56, "ExoCursorInfo layout");

static_assert(sizeof(ExoAddMonitor) == 16 + 12 + 12 * EXO_MAX_MODES + 32, "ExoAddMonitor layout");
static_assert(sizeof(ExoAddMonitorOut) == 12, "ExoAddMonitorOut layout");
