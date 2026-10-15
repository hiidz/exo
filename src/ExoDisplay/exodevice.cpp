// exodevice: the Exo display adapter's root device, for the installer.
// pnputil cannot create a root device and the WDK's devcon may not be
// shipped (PLAN.md, Release), so this does the two calls devcon would.
// Elevated.
//
//   exodevice install <ExoDisplay.inf>   create Root\ExoDisplay, or update its driver
//   exodevice remove                     remove it and every package of its driver
//
// Exit 0 on success; anything else, with a line saying why.
#include <windows.h>
#include <setupapi.h>
#include <newdev.h>
#include <stdio.h>
#include <wchar.h>

static const wchar_t kHwid[] = L"Root\\ExoDisplay";

static int Fail(const char *what)
{
    printf("FAILED: %s, error %lu\n", what, GetLastError());
    return 1;
}

static bool IsExo(HDEVINFO set, SP_DEVINFO_DATA *dev)
{
    wchar_t ids[512] = {};
    if (!SetupDiGetDeviceRegistryPropertyW(set, dev, SPDRP_HARDWAREID, nullptr, (BYTE *)ids, sizeof ids - 2 * sizeof(wchar_t), nullptr))
        return false;
    for (const wchar_t *id = ids; *id; id += wcslen(id) + 1)
        if (_wcsicmp(id, kHwid) == 0) return true;
    return false;
}

// Calls fn on every device with Exo's hardware ID, present or not; returns
// how many there were.
template <class Fn> static int EachExo(Fn fn)
{
    HDEVINFO set = SetupDiGetClassDevsW(nullptr, nullptr, nullptr, DIGCF_ALLCLASSES);
    if (set == INVALID_HANDLE_VALUE) return -1;
    int n = 0;
    SP_DEVINFO_DATA dev = { sizeof dev };
    for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &dev); i++)
        if (IsExo(set, &dev)) { n++; fn(set, &dev); }
    SetupDiDestroyDeviceInfoList(set);
    return n;
}

static int Install(const wchar_t *infArg)
{
    wchar_t inf[MAX_PATH];
    if (!GetFullPathNameW(infArg, MAX_PATH, inf, nullptr)) return Fail("GetFullPathName");

    bool created = false;
    if (EachExo([](HDEVINFO, SP_DEVINFO_DATA *) {}) == 0) {
        GUID cls;
        wchar_t clsName[64];
        if (!SetupDiGetINFClassW(inf, &cls, clsName, 64, nullptr)) return Fail("SetupDiGetINFClass");
        HDEVINFO set = SetupDiCreateDeviceInfoList(&cls, nullptr);
        if (set == INVALID_HANDLE_VALUE) return Fail("SetupDiCreateDeviceInfoList");
        SP_DEVINFO_DATA dev = { sizeof dev };
        wchar_t ids[] = L"Root\\ExoDisplay\0";
        bool ok = SetupDiCreateDeviceInfoW(set, clsName, &cls, nullptr, nullptr, DICD_GENERATE_ID, &dev)
            && SetupDiSetDeviceRegistryPropertyW(set, &dev, SPDRP_HARDWAREID, (BYTE *)ids, sizeof ids)
            && SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &dev);
        DWORD err = GetLastError();
        SetupDiDestroyDeviceInfoList(set);
        SetLastError(err);
        if (!ok) return Fail("creating the root device");
        created = true;
        printf("created %ls\n", kHwid);
    }

    BOOL reboot = FALSE;
    if (!UpdateDriverForPlugAndPlayDevicesW(nullptr, kHwid, inf, INSTALLFLAG_FORCE, &reboot)) {
        DWORD err = GetLastError();
        // A device this run made goes again; one that was there stays.
        if (created) EachExo([](HDEVINFO set, SP_DEVINFO_DATA *dev) { DiUninstallDevice(nullptr, set, dev, 0, nullptr); });
        SetLastError(err);
        return Fail("installing the driver");
    }
    printf("driver installed%s\n", reboot ? ", restart needed" : "");
    return 0;
}

// Every package in the driver store that was once ExoDisplay.inf: an
// update adds a new oemNN.inf and leaves the old one.
static int RemovePackages()
{
    int failed = 0;
    wchar_t dir[MAX_PATH], pattern[MAX_PATH];
    GetWindowsDirectoryW(dir, MAX_PATH);
    swprintf(pattern, MAX_PATH, L"%ls\\INF\\oem*.inf", dir);
    WIN32_FIND_DATAW f;
    HANDLE find = FindFirstFileW(pattern, &f);
    if (find == INVALID_HANDLE_VALUE) return 0;
    do {
        wchar_t path[MAX_PATH];
        swprintf(path, MAX_PATH, L"%ls\\INF\\%ls", dir, f.cFileName);
        BYTE buf[4096];
        auto info = (SP_INF_INFORMATION *)buf;
        SP_ORIGINAL_FILE_INFO_W orig = { sizeof orig };
        if (!SetupGetInfInformationW(path, INFINFO_INF_NAME_IS_ABSOLUTE, info, sizeof buf, nullptr)
            || !SetupQueryInfOriginalFileInformationW(info, 0, nullptr, &orig)
            || _wcsicmp(orig.OriginalInfName, L"ExoDisplay.inf") != 0)
            continue;
        if (SetupUninstallOEMInfW(f.cFileName, SUOI_FORCEDELETE, nullptr)) printf("removed %ls\n", f.cFileName);
        else { failed++; Fail("removing a driver package"); }
    } while (FindNextFileW(find, &f));
    FindClose(find);
    return failed;
}

static int Remove()
{
    int failed = 0;
    int n = EachExo([&](HDEVINFO set, SP_DEVINFO_DATA *dev) {
        if (DiUninstallDevice(nullptr, set, dev, 0, nullptr)) printf("removed the device\n");
        else { failed++; Fail("removing the device"); }
    });
    if (n < 0) return Fail("SetupDiGetClassDevs");
    if (n == 0) printf("no device\n");
    failed += RemovePackages();
    return failed ? 1 : 0;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc == 3 && _wcsicmp(argv[1], L"install") == 0) return Install(argv[2]);
    if (argc == 2 && _wcsicmp(argv[1], L"remove") == 0) return Remove();
    printf("usage: exodevice install <ExoDisplay.inf> | exodevice remove\n");
    return 2;
}
