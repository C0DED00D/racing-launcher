/*
 * version.dll stand-in (x64) for Supermodel's Linked seats: DirectInput "foreground only" becomes "background".
 *
 * Supermodel's DirectInput input system sets DISCL_FOREGROUND on its devices, so of two copies side by side only the
 * window in front reads its joystick (Virtual-On OT: whichever seat's window was placed last could press Start, the
 * other couldn't). Supermodel gets DirectInput through COM (CoCreateInstance, from System32), so a dinput8.dll beside
 * it is never loaded; it does import VERSION.dll, which isn't a KnownDLL. This DLL is that VERSION.dll: every
 * export forwards to version_orig.dll (a copy of System32\version.dll, see the pragmas below), and on load it patches
 * the exe's import of CoCreateInstance. For CLSID_DirectInput8 it hooks IDirectInput8::CreateDevice and then
 * IDirectInputDevice8::SetCooperativeLevel, asking for DISCL_NONEXCLUSIVE | DISCL_BACKGROUND (shared, read
 * while another window is in front). Logs to dinput_bg.log beside the exe. Build: build.cmd.
 */
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

/* Every version.dll export forwards to version_orig.dll (a copy of System32\version.dll). */
#pragma comment(linker, "/export:GetFileVersionInfoA=version_orig.GetFileVersionInfoA")
#pragma comment(linker, "/export:GetFileVersionInfoByHandle=version_orig.GetFileVersionInfoByHandle")
#pragma comment(linker, "/export:GetFileVersionInfoExA=version_orig.GetFileVersionInfoExA")
#pragma comment(linker, "/export:GetFileVersionInfoExW=version_orig.GetFileVersionInfoExW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeA=version_orig.GetFileVersionInfoSizeA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExA=version_orig.GetFileVersionInfoSizeExA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExW=version_orig.GetFileVersionInfoSizeExW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeW=version_orig.GetFileVersionInfoSizeW")
#pragma comment(linker, "/export:GetFileVersionInfoW=version_orig.GetFileVersionInfoW")
#pragma comment(linker, "/export:VerFindFileA=version_orig.VerFindFileA")
#pragma comment(linker, "/export:VerFindFileW=version_orig.VerFindFileW")
#pragma comment(linker, "/export:VerInstallFileA=version_orig.VerInstallFileA")
#pragma comment(linker, "/export:VerInstallFileW=version_orig.VerInstallFileW")
#pragma comment(linker, "/export:VerLanguageNameA=version_orig.VerLanguageNameA")
#pragma comment(linker, "/export:VerLanguageNameW=version_orig.VerLanguageNameW")
#pragma comment(linker, "/export:VerQueryValueA=version_orig.VerQueryValueA")
#pragma comment(linker, "/export:VerQueryValueW=version_orig.VerQueryValueW")

#define DISCL_EXCLUSIVE 0x1
#define DISCL_NONEXCLUSIVE 0x2
#define DISCL_FOREGROUND 0x4
#define DISCL_BACKGROUND 0x8

static const GUID CLSID_DirectInput8_ = {0x25E609E4, 0xB259, 0x11CF, {0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00}};

typedef HRESULT(WINAPI *CoCreateFn)(REFCLSID, LPUNKNOWN, DWORD, REFIID, LPVOID *);
typedef HRESULT(STDMETHODCALLTYPE *CreateDeviceFn)(void *, REFGUID, void **, LPUNKNOWN);
typedef HRESULT(STDMETHODCALLTYPE *SetCoopFn)(void *, HWND, DWORD);

static CoCreateFn g_coCreate;
static CreateDeviceFn g_createDevice;
static SetCoopFn g_setCoop;
static FILE *g_log;
static int g_logs;

static void Log(const char *fmt, ...)
{
    va_list a;
    if (g_logs++ > 60) return;
    if (!g_log)
    {
        char path[MAX_PATH];
        DWORD n = GetModuleFileNameA(NULL, path, MAX_PATH);
        while (n > 0 && path[n - 1] != '\\') n--;
        lstrcpyA(path + n, "dinput_bg.log");
        g_log = fopen(path, "w");
        if (!g_log) return;
    }
    va_start(a, fmt);
    vfprintf(g_log, fmt, a);
    va_end(a);
    fputc('\n', g_log);
    fflush(g_log);
}

static void Patch(void **slot, void *hook, void **orig)
{
    DWORD old;
    if (*slot == hook) return;
    VirtualProtect(slot, sizeof(void *), PAGE_EXECUTE_READWRITE, &old);
    if (orig) *orig = *slot;
    *slot = hook;
    VirtualProtect(slot, sizeof(void *), old, &old);
}

typedef HRESULT(STDMETHODCALLTYPE *GetPropFn)(void *, REFGUID, void *);

/* DIPROP_GUIDANDPATH = MAKEDIPROP(12); DIPROPGUIDANDPATH = DIPROPHEADER + class GUID + device path. */
typedef struct
{
    DWORD dwSize, dwHeaderSize, dwObj, dwHow;
    GUID guidClass;
    WCHAR wszPath[MAX_PATH];
} GuidAndPath;

static WCHAR g_exclusive[512]; /* DINPUT_BG_EXCLUSIVE: ';'-separated parts of device paths, upper case */

/* Is this device one this copy may hold exclusively (its own wheel, for force feedback)? */
static int WantsExclusive(void *dev, WCHAR *path, int pathLen)
{
    GuidAndPath p;
    WCHAR *part, *next, list[512];
    path[0] = 0;
    ZeroMemory(&p, sizeof p);
    p.dwSize = sizeof p;
    p.dwHeaderSize = 16;
    if (FAILED(((GetPropFn)(*(void ***)dev)[5])(dev, (REFGUID)(ULONG_PTR)12, &p))) return 0; /* GetProperty */
    lstrcpynW(path, p.wszPath, pathLen);
    CharUpperW(p.wszPath);
    if (!g_exclusive[0]) return 0;
    lstrcpynW(list, g_exclusive, 512);
    for (part = list; part && *part; part = next)
    {
        next = wcschr(part, L';');
        if (next) *next++ = 0;
        if (*part && wcsstr(p.wszPath, part)) return 1;
    }
    return 0;
}

static HRESULT STDMETHODCALLTYPE HookSetCoop(void *dev, HWND wnd, DWORD flags)
{
    DWORD wanted;
    HRESULT hr;
    WCHAR path[MAX_PATH];
    /* Background, so the copy whose window isn't in front still reads its controller. Supermodel asks for
       exclusive joysticks (even with force feedback off), and two copies can't both hold a device exclusively, so
       every device is shared, except the ones named in DINPUT_BG_EXCLUSIVE (this seat's own wheel): those stay
       exclusive, which force feedback needs. DirectInput allows exclusive + background for joysticks. */
    wanted = WantsExclusive(dev, path, MAX_PATH) ? (DISCL_EXCLUSIVE | DISCL_BACKGROUND) : (DISCL_NONEXCLUSIVE | DISCL_BACKGROUND);
    hr = g_setCoop(dev, wnd, wanted);
    Log("SetCooperativeLevel 0x%lx -> 0x%lx: 0x%08lx (%ls)", flags, wanted, hr, path[0] ? path : L"no path");
    return hr;
}

static HRESULT STDMETHODCALLTYPE HookCreateDevice(void *di, REFGUID guid, void **out, LPUNKNOWN outer)
{
    HRESULT hr = g_createDevice(di, guid, out, outer);
    if (SUCCEEDED(hr) && out && *out)
    {
        void **vt = *(void ***)*out;
        if (vt[13] != (void *)HookSetCoop) Patch(&vt[13], (void *)HookSetCoop, (void **)&g_setCoop); /* SetCooperativeLevel */
    }
    return hr;
}

static HRESULT WINAPI HookCoCreate(REFCLSID clsid, LPUNKNOWN outer, DWORD ctx, REFIID riid, LPVOID *out)
{
    HRESULT hr = g_coCreate(clsid, outer, ctx, riid, out);
    if (SUCCEEDED(hr) && out && *out && IsEqualGUID(clsid, &CLSID_DirectInput8_))
    {
        void **vt = *(void ***)*out;
        Log("CoCreateInstance(DirectInput8) hooked");
        if (vt[3] != (void *)HookCreateDevice) Patch(&vt[3], (void *)HookCreateDevice, (void **)&g_createDevice); /* CreateDevice */
    }
    return hr;
}

/* Points the exe's import of ole32!CoCreateInstance at HookCoCreate. */
static void HookImports(void)
{
    BYTE *base = (BYTE *)GetModuleHandleA(NULL);
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    IMAGE_IMPORT_DESCRIPTOR *imp;
    if (!dir.VirtualAddress) return;
    for (imp = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress); imp->Name; imp++)
    {
        IMAGE_THUNK_DATA *names, *funcs;
        if (lstrcmpiA((char *)(base + imp->Name), "ole32.dll") != 0) continue;
        names = (IMAGE_THUNK_DATA *)(base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        funcs = (IMAGE_THUNK_DATA *)(base + imp->FirstThunk);
        for (; names->u1.AddressOfData; names++, funcs++)
        {
            IMAGE_IMPORT_BY_NAME *ibn;
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            ibn = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
            if (lstrcmpA((char *)ibn->Name, "CoCreateInstance") != 0) continue;
            Patch((void **)&funcs->u1.Function, (void *)HookCoCreate, (void **)&g_coCreate);
            Log("dinput_bg: CoCreateInstance import patched");
            return;
        }
    }
    Log("dinput_bg: no CoCreateInstance import found");
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)inst;
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        if (GetEnvironmentVariableW(L"DINPUT_BG_EXCLUSIVE", g_exclusive, 512) >= 512) g_exclusive[0] = 0;
        CharUpperW(g_exclusive);
        HookImports();
    }
    return TRUE;
}
