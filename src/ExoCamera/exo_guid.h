// The CLSID Windows knows this media source by. Generated once on
// 2026-10-11 and fixed: changing it orphans the registration. Not the
// spike's {817E768C-...}, which is still registered and loads the spike
// DLL (TECHNICAL_DESIGN 9).
//
// One CLSID, one camera. The CLSID pool for several cameras is open
// (DECISIONS 6.4).
#pragma once

#include <guiddef.h>

// Declares the GUID everywhere, and defines its bytes only in the one file
// that includes <initguid.h> first (exo_dll.cpp).
// {5BB30600-762E-49D3-BB33-F419E5D54EC6}
DEFINE_GUID(CLSID_ExoCamera,
    0x5bb30600, 0x762e, 0x49d3, 0xbb, 0x33, 0xf4, 0x19, 0xe5, 0xd5, 0x4e, 0xc6);

#define EXO_CLSID_STRING  "{5BB30600-762E-49D3-BB33-F419E5D54EC6}"
#define EXO_FRIENDLY_NAME "Exo Camera"
