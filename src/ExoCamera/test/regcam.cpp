// Registers Exo's media source as a virtual camera and holds it for N
// seconds, then removes it. A test tool standing in for the hub, so the DLL
// can be tried inside the Frame Server before MFCreateVirtualCamera is ever
// called from C# (DECISIONS 10, item 4, A4). Lifted from spike 1's vcam.cpp.
//
//   regcam [seconds]     default 300
//
// Session lifetime and current-user access, as spike 1: the camera exists
// only while this process holds it, and no administrator is needed. Both are
// carried forward, not decided (DECISIONS 5.2, 6.2). The DLL itself must
// already be registered in HKLM, by an elevated regsvr32.
#include "../exo_guid.h"

#include <windows.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>
#include <wrl/client.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

using Microsoft::WRL::ComPtr;

static void Say(const char* fmt, ...)
{
    SYSTEMTIME t;
    GetLocalTime(&t);
    printf("%02u:%02u:%02u.%03u ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list args;
    va_start(args, fmt);
    vprintf(fmt, args);
    va_end(args);
    printf("\n");
    fflush(stdout);
}

int main(int argc, char** argv)
{
    int seconds = argc > 1 ? atoi(argv[1]) : 300;
    Say("regcam pid=%lu, clsid %s", GetCurrentProcessId(), EXO_CLSID_STRING);

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) {
        Say("MFStartup 0x%08lX", (unsigned long)hr);
        return 1;
    }

    // Windows appends "Windows Virtual Camera" to the name in some lists.
    ComPtr<IMFVirtualCamera> cam;
    hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
                               MFVirtualCameraLifetime_Session,
                               MFVirtualCameraAccess_CurrentUser,
                               L"" EXO_FRIENDLY_NAME, L"" EXO_CLSID_STRING,
                               nullptr, 0, &cam);
    Say("MFCreateVirtualCamera 0x%08lX", (unsigned long)hr);
    if (FAILED(hr)) {
        return 1;
    }

    hr = cam->Start(nullptr);
    Say("IMFVirtualCamera::Start 0x%08lX", (unsigned long)hr);
    if (FAILED(hr)) {
        cam->Remove();
        return 1;
    }

    WCHAR* link = nullptr;
    UINT32 len = 0;
    cam->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, &link, &len);
    Say("symbolic link %ls", link ? link : L"?");
    CoTaskMemFree(link);

    Say("camera live for %d seconds", seconds);
    Sleep(seconds * 1000);

    hr = cam->Remove();
    Say("Remove 0x%08lX", (unsigned long)hr);
    cam.Reset();
    MFShutdown();
    return 0;
}
