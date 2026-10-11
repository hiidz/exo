// The COM in-process server: DllMain, the four exported entry points, and
// the class factory. MFCreateVirtualCamera asks COM for CLSID_ExoCamera,
// COM loads this DLL and calls DllGetClassObject, and the factory it gets
// back creates the object (TECHNICAL_DESIGN 3.1).
#include <initguid.h>  // first, and only here: defines the GUID bytes once
#include "exo_guid.h"
#include "exo_activate.h"
#include "exo_dll.h"
#include "exo_log.h"

#include <windows.h>
#include <olectl.h>
#include <stdio.h>
#include <string.h>

LONG g_objects = 0;
static LONG g_locks = 0;  // IClassFactory::LockServer holds
static HMODULE g_module = NULL;

// Interfaces already logged as missing, so each is logged once. A small
// fixed table: the spike and A4 saw about a dozen distinct IIDs. Past its
// end, misses are counted and not logged.
struct QiMiss
{
    const char* object;
    GUID iid;
};
static SRWLOCK g_missLock = SRWLOCK_INIT;
static QiMiss g_misses[64];
static LONG g_missCount = 0;
static LONG g_missTotal = 0;

void ExoLogQiMiss(const char* object, REFIID riid)
{
    bool seen = false;
    AcquireSRWLockExclusive(&g_missLock);
    LONG total = ++g_missTotal;
    for (LONG i = 0; i < g_missCount && !seen; i++) {
        seen = g_misses[i].object == object && g_misses[i].iid == riid;
    }
    if (!seen && g_missCount < ARRAYSIZE(g_misses)) {
        g_misses[g_missCount++] = { object, riid };
    }
    ReleaseSRWLockExclusive(&g_missLock);
    if (!seen) {
        OLECHAR text[40];
        StringFromGUID2(riid, text, ARRAYSIZE(text));
        ExoLog("%s QI miss %ls (first time; %ld misses in all so far)", object, text, total);
    }
}

// One instance with static storage, never freed, so AddRef and Release do
// not count: there is nothing to free at zero.
class ExoClassFactory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *ppv = NULL;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return 1; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) {
            return E_POINTER;
        }
        *ppv = NULL;
        if (outer) {
            // COM aggregation is not supported, and saying so is required.
            return CLASS_E_NOAGGREGATION;
        }
        ExoActivate* obj = nullptr;
        HRESULT hr = ExoActivate::Create(&obj);
        if (SUCCEEDED(hr)) {
            // The caller's reference comes from the QI. Ours is dropped
            // either way, so if the QI failed the object is destroyed.
            hr = obj->QueryInterface(riid, ppv);
            obj->Release();
        }
        ExoLog("CreateInstance hr=0x%08lX", (unsigned long)hr);
        return hr;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        LONG n = lock ? InterlockedIncrement(&g_locks) : InterlockedDecrement(&g_locks);
        ExoLog("LockServer(%d) locks=%ld", lock ? 1 : 0, n);
        return S_OK;
    }
};

static ExoClassFactory g_factory;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    // The loader lock is held for every call here, so do as little as
    // possible: no file creation, no threads, no other DLLs.
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    } else if (reason == DLL_PROCESS_DETACH && reserved == NULL) {
        // FreeLibrary: an unload, whose timing in svchost is an open
        // question (TECHNICAL_DESIGN 3.5), so it is logged.
        //
        // reserved is non-NULL when the whole process is exiting. Every
        // other thread is already gone by then, possibly one that held the
        // log's lock, so taking it would hang the host's exit. Do nothing
        // and let Windows close the file.
        ExoLog("DllMain PROCESS_DETACH, unload, objects=%ld locks=%ld", g_objects, g_locks);
        ExoLogClose();
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (!ppv) {
        return E_POINTER;
    }
    *ppv = NULL;
    if (rclsid != CLSID_ExoCamera) {
        OLECHAR* text = NULL;
        StringFromCLSID(rclsid, &text);
        ExoLog("DllGetClassObject: not ours, %ls", text ? text : L"?");
        CoTaskMemFree(text);
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    HRESULT hr = g_factory.QueryInterface(riid, ppv);
    ExoLog("DllGetClassObject: ours, hr=0x%08lX", (unsigned long)hr);
    return hr;
}

STDAPI DllCanUnloadNow(void)
{
    LONG objects = InterlockedCompareExchange(&g_objects, 0, 0);
    LONG locks = InterlockedCompareExchange(&g_locks, 0, 0);
    HRESULT hr = (objects == 0 && locks == 0) ? S_OK : S_FALSE;
    ExoLog("DllCanUnloadNow objects=%ld locks=%ld -> %s",
           objects, locks, hr == S_OK ? "S_OK" : "S_FALSE");
    return hr;
}

static LONG SetString(const char* subkey, const char* name, const char* value)
{
    HKEY key = NULL;
    LONG rc = RegCreateKeyExA(HKEY_LOCAL_MACHINE, subkey, 0, NULL, REG_OPTION_NON_VOLATILE,
                              KEY_WRITE, NULL, &key, NULL);
    if (rc != ERROR_SUCCESS) {
        return rc;
    }
    rc = RegSetValueExA(key, name, 0, REG_SZ, (const BYTE*)value, (DWORD)(strlen(value) + 1));
    RegCloseKey(key);
    return rc;
}

// HKLM, never HKCU: the Frame Server runs as LOCAL SERVICE, whose HKCU is
// not the user's (CLAUDE.md). So this needs an elevated regsvr32.
// ThreadingModel Both: COM may call the objects from any thread, and they
// lock for themselves.
STDAPI DllRegisterServer(void)
{
    char dll[MAX_PATH];
    DWORD n = GetModuleFileNameA(g_module, dll, MAX_PATH);
    if (n == 0 || n == MAX_PATH) {
        ExoLog("DllRegisterServer: GetModuleFileName failed %lu", GetLastError());
        return SELFREG_E_CLASS;
    }
    char clsidKey[128], inprocKey[160];
    _snprintf_s(clsidKey, sizeof(clsidKey), _TRUNCATE, "SOFTWARE\\Classes\\CLSID\\%s", EXO_CLSID_STRING);
    _snprintf_s(inprocKey, sizeof(inprocKey), _TRUNCATE, "%s\\InprocServer32", clsidKey);

    LONG a = SetString(clsidKey, NULL, EXO_FRIENDLY_NAME);
    LONG b = SetString(inprocKey, NULL, dll);
    LONG c = SetString(inprocKey, "ThreadingModel", "Both");
    ExoLog("DllRegisterServer %s rc=(%ld,%ld,%ld)", dll, a, b, c);
    return (a == ERROR_SUCCESS && b == ERROR_SUCCESS && c == ERROR_SUCCESS) ? S_OK : SELFREG_E_CLASS;
}

STDAPI DllUnregisterServer(void)
{
    char clsidKey[128], inprocKey[160];
    _snprintf_s(clsidKey, sizeof(clsidKey), _TRUNCATE, "SOFTWARE\\Classes\\CLSID\\%s", EXO_CLSID_STRING);
    _snprintf_s(inprocKey, sizeof(inprocKey), _TRUNCATE, "%s\\InprocServer32", clsidKey);

    // Deepest first: RegDeleteKey refuses a key that still has subkeys.
    LONG a = RegDeleteKeyA(HKEY_LOCAL_MACHINE, inprocKey);
    LONG b = RegDeleteKeyA(HKEY_LOCAL_MACHINE, clsidKey);
    ExoLog("DllUnregisterServer rc=(%ld,%ld)", a, b);
    return S_OK;
}
