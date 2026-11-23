/*
 * seat_names: lets a second TeknoParrot game run beside the first on one PC.
 *
 * TeknoParrot's UI and its game-side core talk through objects with fixed names (the pipe "TeknoParrotPipe", the
 * input block "TeknoParrot_JvsState", ...), so two copies would share one wheel and fight over the pipe. Seat 2's
 * TeknoParrot UI is patched to use "...Pip2" / "...Stat2" (scripts/tp-seat-names.ps1). The core is packed and
 * can't be patched on disk, so this DLL renames the objects inside the game process instead: it hooks the ntdll
 * calls that open or create named objects (NtCreateFile, NtOpenFile, NtCreateSection, NtOpenSection) and replaces
 * the last character of the TeknoParrot names with the seat digit.
 *
 * It is loaded as the game's xinput1_3.dll (placed in the game folder; SEGA Rally 3); every XInput export is
 * forwarded to the original, renamed xinput1_3_orig.dll (the #pragma exports below). Built with PROXY_VERSION it
 * is a version.dll instead (OutRun 2's BudgieLoader), forwarding to a copy of the system one, version_orig.dll. The seat digit comes from seat_names.txt beside the
 * DLL (one character, e.g. "2"; seat 1 keeps the stock names). A seat_tpeth0.txt beside the DLL (keep it read-only: the program reopens files by handle, so a write could reach it) replaces the one
 * TeknoParrot writes for the game (see Rename). Each rename is logged to seat_names.log beside the DLL.
 *
 * 32-bit only (the games are 32-bit). Build: build.cmd.
 */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <winternl.h>
#include <stdio.h>
#include <wchar.h>

#ifdef PROXY_VERSION
/* version.dll exports (BudgieLoader, OutRun 2), forwarded to a copy of the system DLL named version_orig.dll. */
#pragma comment(linker, "/export:GetFileVersionInfoA=version_orig.GetFileVersionInfoA,@1")
#pragma comment(linker, "/export:GetFileVersionInfoByHandle=version_orig.GetFileVersionInfoByHandle,@2")
#pragma comment(linker, "/export:GetFileVersionInfoExA=version_orig.GetFileVersionInfoExA,@3")
#pragma comment(linker, "/export:GetFileVersionInfoExW=version_orig.GetFileVersionInfoExW,@4")
#pragma comment(linker, "/export:GetFileVersionInfoSizeA=version_orig.GetFileVersionInfoSizeA,@5")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExA=version_orig.GetFileVersionInfoSizeExA,@6")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExW=version_orig.GetFileVersionInfoSizeExW,@7")
#pragma comment(linker, "/export:GetFileVersionInfoSizeW=version_orig.GetFileVersionInfoSizeW,@8")
#pragma comment(linker, "/export:GetFileVersionInfoW=version_orig.GetFileVersionInfoW,@9")
#pragma comment(linker, "/export:VerFindFileA=version_orig.VerFindFileA,@10")
#pragma comment(linker, "/export:VerFindFileW=version_orig.VerFindFileW,@11")
#pragma comment(linker, "/export:VerInstallFileA=version_orig.VerInstallFileA,@12")
#pragma comment(linker, "/export:VerInstallFileW=version_orig.VerInstallFileW,@13")
#pragma comment(linker, "/export:VerLanguageNameA=version_orig.VerLanguageNameA,@14")
#pragma comment(linker, "/export:VerLanguageNameW=version_orig.VerLanguageNameW,@15")
#pragma comment(linker, "/export:VerQueryValueA=version_orig.VerQueryValueA,@16")
#pragma comment(linker, "/export:VerQueryValueW=version_orig.VerQueryValueW,@17")
#else
/* XInput exports, forwarded to the original DLL. */
#pragma comment(linker, "/export:XInputGetState=xinput1_3_orig.XInputGetState,@2")
#pragma comment(linker, "/export:XInputSetState=xinput1_3_orig.XInputSetState,@3")
#pragma comment(linker, "/export:XInputGetCapabilities=xinput1_3_orig.XInputGetCapabilities,@4")
#pragma comment(linker, "/export:XInputEnable=xinput1_3_orig.XInputEnable,@5")
#pragma comment(linker, "/export:XInputGetDSoundAudioDeviceGuids=xinput1_3_orig.XInputGetDSoundAudioDeviceGuids,@6")
#pragma comment(linker, "/export:XInputGetBatteryInformation=xinput1_3_orig.XInputGetBatteryInformation,@7")
#pragma comment(linker, "/export:XInputGetKeystroke=xinput1_3_orig.XInputGetKeystroke,@8")
#pragma comment(linker, "/export:XInputGetStateEx=xinput1_3_orig.#100,@100,NONAME")
#endif

static WCHAR g_dir[MAX_PATH];
static WCHAR g_seat = 0;

/* Network to host byte order for a port, without linking ws2_32. */
static unsigned ntohs_(unsigned short v) { return (unsigned)(((v & 0xFF) << 8) | (v >> 8)); }

static const WCHAR* const kNames[] = {
    L"TeknoParrotPipe",
    L"TeknoParrot_JVS", /* ElfLdr2's I/O board pipe */
    L"TeknoParrot_JvsExtendedState",
    L"TeknoParrot_JvsState",
};

static void Log(const WCHAR* fmt, ...) {
  WCHAR path[MAX_PATH], line[1024];
  va_list ap;
  va_start(ap, fmt);
  _vsnwprintf_s(line, 1024, _TRUNCATE, fmt, ap);
  va_end(ap);
  swprintf_s(path, MAX_PATH, L"%sseat_names.log", g_dir);
  HANDLE f = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, 0, NULL);
  if (f == INVALID_HANDLE_VALUE) return;
  char utf8[2048];
  int n = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8) - 2, NULL, NULL);
  if (n > 0) {
    utf8[n - 1] = '\r'; utf8[n] = '\n';
    DWORD w;
    WriteFile(f, utf8, n + 1, &w, NULL);
  }
  CloseHandle(f);
}

static BOOL EndsWithI(const WCHAR* s, size_t len, const WCHAR* tail) {
  size_t t = wcslen(tail);
  return len >= t && _wcsicmp(s + len - t, tail) == 0;
}

/*
 * If the object name contains one of the TeknoParrot names (seat 2 and up), or is a tpeth0.txt that has a per-seat
 * replacement beside this DLL, fills *out (backed by buf) with the new name.
 * tpeth0.txt: TeknoParrot writes the network adapter's first address there for ElfLdr2 games (OutRun 2), the
 * same for both seats when they share one adapter with two addresses; each seat's own copy names its address.
 */
static BOOL Rename(const OBJECT_ATTRIBUTES* oa, UNICODE_STRING* out, WCHAR* buf, size_t cap, BOOL writing) {
  if (!g_seat || !oa || !oa->ObjectName || !oa->ObjectName->Buffer) return FALSE;
  const UNICODE_STRING* s = oa->ObjectName;
  size_t len = s->Length / sizeof(WCHAR);
  if (len + 1 > cap) return FALSE;
  memcpy(buf, s->Buffer, len * sizeof(WCHAR));
  buf[len] = 0;

  /* Only reads: TeknoParrot's core writes the file itself, and that write must not land on our copy. */
  if (!writing && EndsWithI(buf, len, L"\\tpeth0.txt")) {
    WCHAR mine[MAX_PATH];
    swprintf_s(mine, MAX_PATH, L"%sseat_tpeth0.txt", g_dir);
    if (GetFileAttributesW(mine) == INVALID_FILE_ATTRIBUTES) return FALSE;
    swprintf_s(buf, cap, L"\\??\\%s", mine);
    out->Buffer = buf;
    out->Length = (USHORT)(wcslen(buf) * sizeof(WCHAR));
    out->MaximumLength = out->Length + sizeof(WCHAR);
    return TRUE;
  }

  if (g_seat == L'1') return FALSE; /* seat 1 keeps TeknoParrot's own names */
  /* Object names ignore case (the game may open "teknoparrot_jvs"); a match must not run on into more letters,
     so TeknoParrot_JVS doesn't hit TeknoParrot_JvsState. */
  for (int i = 0; i < (int)(sizeof(kNames) / sizeof(kNames[0])); i++) {
    size_t n = wcslen(kNames[i]);
    WCHAR* hit = NULL;
    for (WCHAR* p = buf; p + n <= buf + len; p++) {
      if (_wcsnicmp(p, kNames[i], n) == 0 && !iswalpha(p[n])) { hit = p; break; }
    }
    if (!hit) continue;
    hit[n - 1] = g_seat;
    out->Buffer = buf;
    out->Length = (USHORT)(len * sizeof(WCHAR));
    out->MaximumLength = (USHORT)((len + 1) * sizeof(WCHAR));
    return TRUE;
  }
  return FALSE;
}

/* ---- hooks: each copies the OBJECT_ATTRIBUTES with the new name and calls the original syscall stub ---- */

typedef NTSTATUS(NTAPI* NtCreateFile_t)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, PLARGE_INTEGER,
                                        ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
typedef NTSTATUS(NTAPI* NtOpenFile_t)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, ULONG, ULONG);
typedef NTSTATUS(NTAPI* NtCreateSection_t)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PLARGE_INTEGER, ULONG, ULONG,
                                           HANDLE);
typedef NTSTATUS(NTAPI* NtOpenSection_t)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES);

static NtCreateFile_t real_NtCreateFile;
static NtOpenFile_t real_NtOpenFile;
static NtCreateSection_t real_NtCreateSection;
static NtOpenSection_t real_NtOpenSection;

#define RENAMED(oa, writing, call_with)                                     \
  WCHAR buf[512];                                                           \
  UNICODE_STRING name;                                                      \
  OBJECT_ATTRIBUTES copy;                                                   \
  if (Rename(oa, &name, buf, 512, writing)) {                               \
    copy = *(oa);                                                           \
    copy.ObjectName = &name;                                                \
    Log(L"%.*s -> %s", (int)((oa)->ObjectName->Length / 2), (oa)->ObjectName->Buffer, buf); \
    oa = &copy;                                                             \
  }                                                                         \
  return call_with;

static NTSTATUS NTAPI hook_NtCreateFile(PHANDLE h, ACCESS_MASK a, POBJECT_ATTRIBUTES oa, PIO_STATUS_BLOCK io,
                                        PLARGE_INTEGER sz, ULONG attr, ULONG share, ULONG disp, ULONG opt, PVOID ea,
                                        ULONG eal) {
  RENAMED(oa, (a & (FILE_WRITE_DATA | FILE_APPEND_DATA | GENERIC_WRITE)) || disp != FILE_OPEN, real_NtCreateFile(h, a, oa, io, sz, attr, share, disp, opt, ea, eal))
}
static NTSTATUS NTAPI hook_NtOpenFile(PHANDLE h, ACCESS_MASK a, POBJECT_ATTRIBUTES oa, PIO_STATUS_BLOCK io,
                                      ULONG share, ULONG opt) {
  RENAMED(oa, (a & (FILE_WRITE_DATA | FILE_APPEND_DATA | GENERIC_WRITE)) != 0, real_NtOpenFile(h, a, oa, io, share, opt))
}
static NTSTATUS NTAPI hook_NtCreateSection(PHANDLE h, ACCESS_MASK a, POBJECT_ATTRIBUTES oa, PLARGE_INTEGER sz,
                                           ULONG prot, ULONG attr, HANDLE file) {
  RENAMED(oa, FALSE, real_NtCreateSection(h, a, oa, sz, prot, attr, file))
}
static NTSTATUS NTAPI hook_NtOpenSection(PHANDLE h, ACCESS_MASK a, POBJECT_ATTRIBUTES oa) {
  RENAMED(oa, FALSE, real_NtOpenSection(h, a, oa))
}

/*
 * Hooks a 32-bit ntdll syscall stub. Under WOW64 (Windows 10/11) each stub is
 *   B8 imm32 (mov eax, service) / BA imm32 (mov edx, transition) / FF D2 (call edx) / C2 imm16 (ret n)
 * = 15 bytes with only absolute operands, so a copy of it works anywhere as the "original" function.
 */
static void* HookStub(const char* name, void* hook) {
  BYTE* fn = (BYTE*)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), name);
  if (!fn || fn[0] != 0xB8 || fn[5] != 0xBA || fn[10] != 0xFF || fn[11] != 0xD2 || fn[12] != 0xC2) {
    Log(L"%S: unexpected stub, not hooked", name);
    return NULL;
  }
  BYTE* tramp = (BYTE*)VirtualAlloc(NULL, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
  if (!tramp) return NULL;
  memcpy(tramp, fn, 15);
  DWORD old;
  if (!VirtualProtect(fn, 5, PAGE_EXECUTE_READWRITE, &old)) return NULL;
  fn[0] = 0xE9; /* jmp rel32 */
  *(INT32*)(fn + 1) = (INT32)((BYTE*)hook - (fn + 5));
  VirtualProtect(fn, 5, old, &old);
  FlushInstructionCache(GetCurrentProcess(), fn, 5);
  return tramp;
}

/*
 * Hooks a function that starts with the usual hot-patch prologue 8B FF 55 8B EC (mov edi,edi / push ebp /
 * mov ebp,esp): the trampoline runs those 5 bytes, then jumps back into the function.
 */
static void* HookPrologue(HMODULE mod, const char* name, void* hook) {
  BYTE* fn = mod ? (BYTE*)GetProcAddress(mod, name) : NULL;
  static const BYTE kPrologue[5] = {0x8B, 0xFF, 0x55, 0x8B, 0xEC};
  if (!fn || memcmp(fn, kPrologue, 5) != 0) {
    Log(L"%S: unexpected prologue, not hooked", name);
    return NULL;
  }
  BYTE* tramp = (BYTE*)VirtualAlloc(NULL, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
  if (!tramp) return NULL;
  memcpy(tramp, fn, 5);
  tramp[5] = 0xE9;
  *(INT32*)(tramp + 6) = (INT32)((fn + 5) - (tramp + 10));
  DWORD old;
  if (!VirtualProtect(fn, 5, PAGE_EXECUTE_READWRITE, &old)) return NULL;
  fn[0] = 0xE9;
  *(INT32*)(fn + 1) = (INT32)((BYTE*)hook - (fn + 5));
  VirtualProtect(fn, 5, old, &old);
  FlushInstructionCache(GetCurrentProcess(), fn, 5);
  return tramp;
}

/*
 * bind: TeknoParrot's ElfLdr2 binds the game's sockets to the network adapter's first address, the same for
 * both seats when they share one adapter with two addresses. With a seat_tpeth0.txt beside this DLL, any bind to an
 * address in that file's /24 is moved to the file's address (this seat's own).
 */
typedef int(WSAAPI* bind_t)(SOCKET, const struct sockaddr*, int);
static bind_t real_bind;
static ULONG g_ip; /* network byte order, 0 = none */

static int WSAAPI hook_bind(SOCKET s, const struct sockaddr* name, int len) {
  if (g_ip && name && len >= (int)sizeof(struct sockaddr_in) && name->sa_family == AF_INET) {
    const struct sockaddr_in* in = (const struct sockaddr_in*)name;
    ULONG want = g_ip, have = in->sin_addr.s_addr;
    /* Also INADDR_ANY: Windows would then pick the source address per packet, and a packet to the other seat's
       (local) address goes out with that same address as its source, so the receiver takes it for its own. */
    if (have != want && (have == 0 || (have & 0x00FFFFFF) == (want & 0x00FFFFFF))) {
      struct sockaddr_in copy = *in;
      copy.sin_addr.s_addr = want;
      const BYTE* a = (const BYTE*)&have;
      const BYTE* b = (const BYTE*)&want;
      Log(L"bind %u.%u.%u.%u:%u -> %u.%u.%u.%u", a[0], a[1], a[2], a[3], ntohs_(in->sin_port), b[0], b[1], b[2], b[3]);
      return real_bind(s, (const struct sockaddr*)&copy, sizeof(copy));
    }
    const BYTE* a = (const BYTE*)&have;
    Log(L"bind %u.%u.%u.%u:%u (unchanged)", a[0], a[1], a[2], a[3], ntohs_(in->sin_port));
  }
  return real_bind(s, name, len);
}

/* Logs where the game sends its first packets (to see how the copies look for each other). */
typedef int(WSAAPI* sendto_t)(SOCKET, const char*, int, int, const struct sockaddr*, int);
typedef int(WSAAPI* WSASendTo_t)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const struct sockaddr*, int,
                                 LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static sendto_t real_sendto;
static WSASendTo_t real_WSASendTo;
static volatile LONG g_sends;

static void LogSend(const char* fn, const struct sockaddr* to, int bytes) {
  if (InterlockedIncrement(&g_sends) > 40 || !to || to->sa_family != AF_INET) return;
  const struct sockaddr_in* in = (const struct sockaddr_in*)to;
  const BYTE* a = (const BYTE*)&in->sin_addr.s_addr;
  Log(L"%S %d bytes -> %u.%u.%u.%u:%u", fn, bytes, a[0], a[1], a[2], a[3], ntohs_(in->sin_port));
}
/*
 * Broadcast copies: the linked copies find each other by subnet broadcast (192.168.0.255), which doesn't reach a
 * socket bound to the other address on the same PC. seat_peers.txt beside this DLL lists the other seats'
 * addresses (one per line); each broadcast is also sent straight to each of them.
 */
static ULONG g_peers[8];
static int g_npeers;

static BOOL IsBroadcast(const struct sockaddr* to) {
  if (!to || to->sa_family != AF_INET) return FALSE;
  ULONG a = ((const struct sockaddr_in*)to)->sin_addr.s_addr;
  return (a >> 24) == 255; /* x.x.x.255 or 255.255.255.255 (network byte order: last octet in the high byte) */
}

static volatile LONG g_copies, g_copy_errors;

/*
 * The game sends from sockets it never binds; Windows then binds them at the first send, choosing the source
 * address for that destination, which for the other seat's (local) address is that address itself. The other
 * game then sees packets "from itself" and drops them. So bind an unbound socket to this seat's address first.
 */
typedef int(WSAAPI* getsockname_t)(SOCKET, struct sockaddr*, int*);
static getsockname_t real_getsockname;

static void EnsureBound(SOCKET s) {
  if (!g_ip || !real_getsockname || !real_bind) return;
  struct sockaddr_in me;
  int len = sizeof(me);
  if (real_getsockname(s, (struct sockaddr*)&me, &len) == 0) return; /* already bound */
  memset(&me, 0, sizeof(me));
  me.sin_family = AF_INET;
  me.sin_addr.s_addr = g_ip;
  if (real_bind(s, (const struct sockaddr*)&me, sizeof(me)) == 0) {
    struct sockaddr_in got;
    len = sizeof(got);
    if (real_getsockname(s, (struct sockaddr*)&got, &len) == 0) {
      const BYTE* a = (const BYTE*)&got.sin_addr.s_addr;
      Log(L"bound sending socket to %u.%u.%u.%u:%u", a[0], a[1], a[2], a[3], ntohs_(got.sin_port));
    }
  }
}

static void CopyToPeers(SOCKET s, const char* data, int len, const struct sockaddr* to) {
  for (int i = 0; i < g_npeers; i++) {
    struct sockaddr_in p = *(const struct sockaddr_in*)to;
    p.sin_addr.s_addr = g_peers[i];
    int r = real_sendto(s, data, len, 0, (const struct sockaddr*)&p, sizeof(p));
    LONG n = r == len ? InterlockedIncrement(&g_copies) : InterlockedIncrement(&g_copy_errors);
    if (n == 1 || n == 100) {
      const BYTE* a = (const BYTE*)&p.sin_addr.s_addr;
      typedef int(WSAAPI * lasterr_t)(void);
      lasterr_t le = (lasterr_t)GetProcAddress(GetModuleHandleW(L"ws2_32.dll"), "WSAGetLastError");
      Log(L"broadcast copy #%ld to %u.%u.%u.%u:%u: %s (error %d)", n, a[0], a[1], a[2], a[3], ntohs_(p.sin_port),
          r == len ? L"sent" : L"FAILED", r == len ? 0 : (le ? le() : -1));
    }
  }
}

static int WSAAPI hook_sendto(SOCKET s, const char* buf, int len, int flags, const struct sockaddr* to, int tolen) {
  LogSend("sendto", to, len);
  EnsureBound(s);
  if (g_npeers && IsBroadcast(to)) CopyToPeers(s, buf, len, to);
  return real_sendto(s, buf, len, flags, to, tolen);
}
static int WSAAPI hook_WSASendTo(SOCKET s, LPWSABUF bufs, DWORD n, LPDWORD sent, DWORD flags, const struct sockaddr* to,
                                 int tolen, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cr) {
  LogSend("WSASendTo", to, n ? (int)bufs[0].len : 0);
  EnsureBound(s);
  if (g_npeers && real_sendto && IsBroadcast(to)) {
    /* The original call may be overlapped; the copies go out as plain datagrams from one joined buffer. */
    DWORD total = 0;
    for (DWORD i = 0; i < n; i++) total += bufs[i].len;
    char* data = total ? (char*)HeapAlloc(GetProcessHeap(), 0, total) : NULL;
    if (data) {
      DWORD at = 0;
      for (DWORD i = 0; i < n; i++) { memcpy(data + at, bufs[i].buf, bufs[i].len); at += bufs[i].len; }
      CopyToPeers(s, data, (int)total, to);
      HeapFree(GetProcessHeap(), 0, data);
    }
  }
  return real_WSASendTo(s, bufs, n, sent, flags, to, tolen, ov, cr);
}

/* Logs who the first received packets came from (completed calls only; overlapped ones finish later). */
typedef int(WSAAPI* recvfrom_t)(SOCKET, char*, int, int, struct sockaddr*, int*);
typedef int(WSAAPI* WSARecvFrom_t)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, struct sockaddr*, LPINT,
                                   LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static recvfrom_t real_recvfrom;
static WSARecvFrom_t real_WSARecvFrom;
static volatile LONG g_recvs, g_recv_calls;

static ULONGLONG g_sources[32];
static volatile LONG g_nsources;

/* Logs the first packet from each sender (address + port), up to 32 senders. */
static void LogRecv(const char* fn, const struct sockaddr* from, int bytes) {
  if (!from || from->sa_family != AF_INET) return;
  const struct sockaddr_in* in = (const struct sockaddr_in*)from;
  ULONGLONG key = ((ULONGLONG)in->sin_addr.s_addr << 16) | in->sin_port;
  for (LONG i = 0; i < g_nsources && i < 32; i++)
    if (g_sources[i] == key) return;
  LONG slot = InterlockedIncrement(&g_nsources) - 1;
  if (slot >= 32) return;
  g_sources[slot] = key;
  InterlockedIncrement(&g_recvs);
  const BYTE* a = (const BYTE*)&in->sin_addr.s_addr;
  Log(L"%S %d bytes <- %u.%u.%u.%u:%u", fn, bytes, a[0], a[1], a[2], a[3], ntohs_(in->sin_port));
}
static int WSAAPI hook_recvfrom(SOCKET s, char* buf, int len, int flags, struct sockaddr* from, int* fromlen) {
  int r = real_recvfrom(s, buf, len, flags, from, fromlen);
  if (InterlockedIncrement(&g_recv_calls) == 1) Log(L"first recvfrom call");
  if (r > 0) LogRecv("recvfrom", from, r);
  return r;
}
static int WSAAPI hook_WSARecvFrom(SOCKET s, LPWSABUF bufs, DWORD n, LPDWORD got, LPDWORD flags, struct sockaddr* from,
                                   LPINT fromlen, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cr) {
  int r = real_WSARecvFrom(s, bufs, n, got, flags, from, fromlen, ov, cr);
  if (InterlockedIncrement(&g_recv_calls) == 1) Log(L"first WSARecvFrom call (overlapped %s)", ov ? L"yes" : L"no");
  if (r == 0 && got) LogRecv("WSARecvFrom", from, (int)*got);
  return r;
}

static void HookNetwork(void) {
  WCHAR path[MAX_PATH];
  swprintf_s(path, MAX_PATH, L"%sseat_tpeth0.txt", g_dir);
  FILE* f = NULL;
  unsigned a, b, c, d;
  if (_wfopen_s(&f, path, L"r") != 0 || !f) return;
  int ok = fscanf_s(f, "%u.%u.%u.%u", &a, &b, &c, &d) == 4 && a < 256 && b < 256 && c < 256 && d < 256;
  fclose(f);
  if (!ok) return;
  g_ip = (ULONG)(a | (b << 8) | (c << 16) | (d << 24));
  /* Loading a system DLL from DllMain is normally avoided; ws2_32's own start-up is light and this is the only
     way to be in place before the game's first socket call. */
  swprintf_s(path, MAX_PATH, L"%sseat_peers.txt", g_dir);
  if (_wfopen_s(&f, path, L"r") == 0 && f) {
    while (g_npeers < 8 && fscanf_s(f, "%u.%u.%u.%u", &a, &b, &c, &d) == 4)
      if (a < 256 && b < 256 && c < 256 && d < 256) g_peers[g_npeers++] = (ULONG)(a | (b << 8) | (c << 16) | (d << 24));
    fclose(f);
  }
  HMODULE ws = LoadLibraryW(L"ws2_32.dll");
  real_bind = (bind_t)HookPrologue(ws, "bind", hook_bind);
  real_getsockname = (getsockname_t)GetProcAddress(ws, "getsockname");
  real_sendto = (sendto_t)HookPrologue(ws, "sendto", hook_sendto);
  real_WSASendTo = (WSASendTo_t)HookPrologue(ws, "WSASendTo", hook_WSASendTo);
  real_recvfrom = (recvfrom_t)HookPrologue(ws, "recvfrom", hook_recvfrom);
  real_WSARecvFrom = (WSARecvFrom_t)HookPrologue(ws, "WSARecvFrom", hook_WSARecvFrom);
  const BYTE* own = (const BYTE*)&g_ip;
  Log(L"network: own address %u.%u.%u.%u, %d peer(s), hooks bind %s sendto %s WSASendTo %s", own[0], own[1], own[2],
      own[3], g_npeers, real_bind ? L"OK" : L"FAILED", real_sendto ? L"OK" : L"FAILED", real_WSASendTo ? L"OK" : L"FAILED");
}

static void Init(HMODULE self) {
  GetModuleFileNameW(self, g_dir, MAX_PATH);
  WCHAR* slash = wcsrchr(g_dir, L'\\');
  if (slash) slash[1] = 0;

  WCHAR cfg[MAX_PATH];
  swprintf_s(cfg, MAX_PATH, L"%sseat_names.txt", g_dir);
  FILE* f = NULL;
  if (_wfopen_s(&f, cfg, L"r") == 0 && f) {
    int c = fgetc(f);
    if (c >= '1' && c <= '9') g_seat = (WCHAR)c;
    fclose(f);
  }
  if (!g_seat) {
    Log(L"no seat digit in seat_names.txt; names left as they are");
    return;
  }
  real_NtCreateFile = (NtCreateFile_t)HookStub("NtCreateFile", hook_NtCreateFile);
  real_NtOpenFile = (NtOpenFile_t)HookStub("NtOpenFile", hook_NtOpenFile);
  real_NtCreateSection = (NtCreateSection_t)HookStub("NtCreateSection", hook_NtCreateSection);
  real_NtOpenSection = (NtOpenSection_t)HookStub("NtOpenSection", hook_NtOpenSection);
  HookNetwork();
  WCHAR exe[MAX_PATH];
  GetModuleFileNameW(NULL, exe, MAX_PATH);
  Log(L"seat %c, pid %lu, %s: hooks %s", g_seat, GetCurrentProcessId(), exe,
      real_NtCreateFile && real_NtOpenFile && real_NtCreateSection && real_NtOpenSection ? L"OK" : L"INCOMPLETE");
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved) {
  (void)reserved;
  if (reason == DLL_PROCESS_ATTACH) {
    DisableThreadLibraryCalls(inst);
    Init(inst);
  }
  return TRUE;
}
