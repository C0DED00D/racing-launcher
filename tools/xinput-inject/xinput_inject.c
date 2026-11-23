/*
 * Stand-in xinput1_4.dll for remote testing (racing launcher).
 *
 * Placed next to a program's exe, Windows loads it instead of the system
 * xinput1_4.dll (XInput DLLs are not KnownDLLs). Every call goes to the real
 * system DLL; on top of that, buttons, triggers and sticks written by another
 * process into the shared memory "Local\RacingXInputInject" are merged into
 * XInputGetState for that controller slot. While a slot is injected it also
 * reports as connected, so a sleeping wheel does not hide the presses.
 *
 * scripts/xinput-press.ps1 writes the shared memory. Nothing is injected
 * unless that script (or another writer) sets a slot active.
 *
 * Seat filter: with the environment variable RACING_XINPUT_ONLY=<0-3>, every
 * other slot reports "not connected", so a program (one linked seat's copy of
 * an emulator) sees only that seat's controller, whatever number SDL or the
 * emulator gives it. Button changes are not logged in that mode (normal play).
 *
 * Twin stick: with RACING_TWINSTICK=<device path part>;<rU>,<rR>,<rD>,<rL>,<lTrig>,<lTurbo>,<rTrig>,<rTurbo>,<start>
 * (DirectInput button numbers, 1-based, as seats.json twinStickLayout), the DirectInput joystick whose
 * device path contains <device path part> (e.g. "pid_187a&mi_00", a Saturn twin stick port) becomes
 * XInput controller 0 and every other slot reports "not connected": left lever (the stick's hat) =
 * left thumbstick, right lever buttons = right thumbstick, triggers = LT/RT, turbos = LB/RB, Start =
 * START. Read through DirectInput without a cooperative level (non-exclusive, background), so an
 * unfocused window still gets it. The shared-memory injection above still merges on top (testing).
 *
 * Build (VS Developer prompt, x86 or x64):
 *   cl /O2 /LD /W4 xinput_inject.c /link /DEF:xinput_inject.def /OUT:xinput1_4.dll user32.lib dinput8.lib dxguid.lib
 */
#define WIN32_LEAN_AND_MEAN
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <xinput.h>
#include <dinput.h>

#define INJECT_MAGIC 0x52584931u /* 'RXI1' */

/* Shared memory layout; keep in sync with scripts/xinput-press.ps1. */
typedef struct {
  DWORD magic;
  DWORD active[4];     /* nonzero: merge this slot's values */
  WORD buttons[4];     /* XINPUT_GAMEPAD_* bits, ORed into the real state */
  BYTE left_trigger[4];
  BYTE right_trigger[4];
  SHORT thumb_lx[4];   /* used when nonzero */
  DWORD sequence;      /* bumped by the writer on every change */
} InjectBlock;

typedef DWORD(WINAPI* GetStateFn)(DWORD, XINPUT_STATE*);
typedef DWORD(WINAPI* SetStateFn)(DWORD, XINPUT_VIBRATION*);
typedef DWORD(WINAPI* GetCapabilitiesFn)(DWORD, DWORD, XINPUT_CAPABILITIES*);
typedef void(WINAPI* EnableFn)(BOOL);
typedef DWORD(WINAPI* GetBatteryFn)(DWORD, BYTE, XINPUT_BATTERY_INFORMATION*);
typedef DWORD(WINAPI* GetKeystrokeFn)(DWORD, DWORD, PXINPUT_KEYSTROKE);
typedef DWORD(WINAPI* GetAudioIdsFn)(DWORD, LPWSTR, UINT*, LPWSTR, UINT*);
typedef DWORD(WINAPI* GetStateExFn)(DWORD, XINPUT_STATE*);

static HMODULE real;
static GetStateFn real_get_state;
static SetStateFn real_set_state;
static GetCapabilitiesFn real_get_capabilities;
static EnableFn real_enable;
static GetBatteryFn real_get_battery;
static GetKeystrokeFn real_get_keystroke;
static GetAudioIdsFn real_get_audio_ids;
static GetStateExFn real_get_state_ex;

static HANDLE mapping;
static volatile InjectBlock* block;

/* RACING_XINPUT_ONLY: the one slot this program may see, or -1 for all. */
static int only_slot = -1;

static BOOL Hidden(DWORD slot) {
  return only_slot >= 0 && slot != (DWORD)only_slot;
}

/* xinput_inject.log next to the DLL: which program loaded it and the first
   calls per function and slot, to see that a program really uses it. */
static WCHAR log_path[MAX_PATH];
static volatile LONG logged[8][4];

static void Log(const char* text) {
  if (!log_path[0]) {
    return;
  }
  HANDLE f = CreateFileW(log_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                         NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
  if (f == INVALID_HANDLE_VALUE) {
    return;
  }
  DWORD written;
  WriteFile(f, text, lstrlenA(text), &written, NULL);
  WriteFile(f, "\r\n", 2, &written, NULL);
  CloseHandle(f);
}

static void LogFirstCalls(int function, DWORD slot, DWORD result) {
  if (slot >= 4 || InterlockedIncrement(&logged[function][slot]) > 3) {
    return;
  }
  static const char* const names[] = {"GetState", "GetStateEx", "GetCapabilities",
                                      "SetState", "Enable", "Battery", "Keystroke",
                                      "AudioIds"};
  char line[128];
  wsprintfA(line, "%s slot %lu -> %lu", names[function], slot, result);
  Log(line);
}

static void LoadReal(void) {
  if (real) {
    return;
  }
  WCHAR path[MAX_PATH];
  UINT n = GetSystemDirectoryW(path, MAX_PATH);
  if (n == 0 || n > MAX_PATH - 20) {
    return;
  }
  lstrcatW(path, L"\\xinput1_4.dll");
  real = LoadLibraryW(path);
  if (!real) {
    return;
  }
  real_get_state = (GetStateFn)GetProcAddress(real, "XInputGetState");
  real_set_state = (SetStateFn)GetProcAddress(real, "XInputSetState");
  real_get_capabilities =
      (GetCapabilitiesFn)GetProcAddress(real, "XInputGetCapabilities");
  real_enable = (EnableFn)GetProcAddress(real, "XInputEnable");
  real_get_battery =
      (GetBatteryFn)GetProcAddress(real, "XInputGetBatteryInformation");
  real_get_keystroke =
      (GetKeystrokeFn)GetProcAddress(real, "XInputGetKeystroke");
  real_get_audio_ids =
      (GetAudioIdsFn)GetProcAddress(real, "XInputGetAudioDeviceIds");
  real_get_state_ex = (GetStateExFn)GetProcAddress(real, (LPCSTR)100);
}

/* The block, or NULL while no writer has created it. Opened lazily, so the
   writer may start before or after the program. */
static volatile InjectBlock* Block(void) {
  if (block) {
    return block->magic == INJECT_MAGIC ? block : NULL;
  }
  mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\RacingXInputInject");
  if (!mapping) {
    return NULL;
  }
  block = (volatile InjectBlock*)MapViewOfFile(mapping, FILE_MAP_READ, 0, 0,
                                               sizeof(InjectBlock));
  if (!block) {
    CloseHandle(mapping);
    mapping = NULL;
    return NULL;
  }
  return block->magic == INJECT_MAGIC ? block : NULL;
}

/* Logs each change of a slot's result or buttons as the program sees them. */
static DWORD last_result[4] = {0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF};
static WORD last_buttons[4];

static void LogChange(DWORD slot, DWORD result, const XINPUT_STATE* state) {
  WORD buttons = (result == ERROR_SUCCESS && state) ? state->Gamepad.wButtons : 0;
  if (only_slot >= 0 || slot >= 4 ||
      (result == last_result[slot] && buttons == last_buttons[slot])) {
    return;
  }
  last_result[slot] = result;
  last_buttons[slot] = buttons;
  char line[96];
  wsprintfA(line, "slot %lu: result %lu buttons %04X", slot, result, buttons);
  Log(line);
}

/* ---- twin stick (RACING_TWINSTICK) ---- */
static int twin;                 /* the mode is on */
static char twin_path[128];      /* lower-case device path part */
static int twin_btn[9];          /* rU rR rD rL lTrig lTurbo rTrig rTurbo start, 1-based */
static LPDIRECTINPUT8A twin_di;
static LPDIRECTINPUTDEVICE8A twin_dev;
static DWORD twin_next_scan;     /* GetTickCount of the next search while not found */
static DWORD twin_packet;

static BOOL CALLBACK TwinEnum(LPCDIDEVICEINSTANCEA inst, LPVOID ctx) {
  (void)ctx;
  LPDIRECTINPUTDEVICE8A dev = NULL;
  if (FAILED(IDirectInput8_CreateDevice(twin_di, &inst->guidInstance, &dev, NULL))) {
    return DIENUM_CONTINUE;
  }
  DIPROPGUIDANDPATH gp;
  ZeroMemory(&gp, sizeof(gp));
  gp.diph.dwSize = sizeof(gp);
  gp.diph.dwHeaderSize = sizeof(DIPROPHEADER);
  gp.diph.dwHow = DIPH_DEVICE;
  char path[MAX_PATH] = {0};
  if (SUCCEEDED(IDirectInputDevice8_GetProperty(dev, DIPROP_GUIDANDPATH, &gp.diph))) {
    WideCharToMultiByte(CP_ACP, 0, gp.wszPath, -1, path, sizeof(path), NULL, NULL);
    CharLowerA(path);
  }
  if (path[0] && strstr(path, twin_path) &&
      SUCCEEDED(IDirectInputDevice8_SetDataFormat(dev, &c_dfDIJoystick2))) {
    twin_dev = dev;
    char line[MAX_PATH + 32];
    wsprintfA(line, "twin stick: %s", path);
    Log(line);
    return DIENUM_STOP;
  }
  IDirectInputDevice8_Release(dev);
  return DIENUM_CONTINUE;
}

static void TwinParse(const char* value) {
  const char* semi = strchr(value, ';');
  if (!semi || semi - value >= (int)sizeof(twin_path)) {
    return;
  }
  lstrcpynA(twin_path, value, (int)(semi - value) + 1);
  CharLowerA(twin_path);
  const char* p = semi + 1;
  for (int i = 0; i < 9; i++) {
    twin_btn[i] = atoi(p);
    if (i == 8) {
      break;
    }
    p = strchr(p, ',');
    if (!p) {
      return; /* fewer than nine numbers */
    }
    p++;
  }
  twin = 1;
}

static int TwinDown(const DIJOYSTATE2* js, int n) {
  return n >= 1 && n <= 128 && (js->rgbButtons[n - 1] & 0x80);
}

/* Fills state from the twin stick; ERROR_DEVICE_NOT_CONNECTED while it is missing. */
static DWORD TwinState(XINPUT_STATE* state) {
  if (!twin_dev) {
    DWORD now = GetTickCount();
    if ((LONG)(now - twin_next_scan) < 0) {
      return ERROR_DEVICE_NOT_CONNECTED;
    }
    twin_next_scan = now + 2000;
    if (!twin_di && FAILED(DirectInput8Create(GetModuleHandleA(NULL), DIRECTINPUT_VERSION,
                                              &IID_IDirectInput8A, (void**)&twin_di, NULL))) {
      return ERROR_DEVICE_NOT_CONNECTED;
    }
    IDirectInput8_EnumDevices(twin_di, DI8DEVCLASS_GAMECTRL, TwinEnum, NULL, DIEDFL_ATTACHEDONLY);
    if (!twin_dev) {
      return ERROR_DEVICE_NOT_CONNECTED;
    }
  }
  DIJOYSTATE2 js;
  HRESULT hr = IDirectInputDevice8_Poll(twin_dev);
  if (FAILED(hr)) {
    IDirectInputDevice8_Acquire(twin_dev);
    IDirectInputDevice8_Poll(twin_dev);
  }
  hr = IDirectInputDevice8_GetDeviceState(twin_dev, sizeof(js), &js);
  if (hr == DIERR_INPUTLOST || hr == DIERR_NOTACQUIRED) {
    IDirectInputDevice8_Acquire(twin_dev);
    hr = IDirectInputDevice8_GetDeviceState(twin_dev, sizeof(js), &js);
  }
  if (FAILED(hr)) {
    if (hr != DIERR_INPUTLOST && hr != DIERR_NOTACQUIRED) {
      /* Unplugged: search again later. */
      IDirectInputDevice8_Release(twin_dev);
      twin_dev = NULL;
    }
    return ERROR_DEVICE_NOT_CONNECTED;
  }
  ZeroMemory(state, sizeof(*state));
  XINPUT_GAMEPAD* g = &state->Gamepad;
  /* Left lever = the hat (hundredths of a degree, 0 = up, clockwise). */
  DWORD pov = js.rgdwPOV[0];
  if (LOWORD(pov) != 0xFFFF) {
    int up = pov >= 31500 || pov <= 4500, right = pov >= 4500 && pov <= 13500;
    int down = pov >= 13500 && pov <= 22500, left = pov >= 22500 && pov <= 31500;
    g->sThumbLY = up ? 32767 : down ? -32768 : 0;
    g->sThumbLX = right ? 32767 : left ? -32768 : 0;
  }
  g->sThumbRY = TwinDown(&js, twin_btn[0]) ? 32767 : TwinDown(&js, twin_btn[2]) ? -32768 : 0;
  g->sThumbRX = TwinDown(&js, twin_btn[1]) ? 32767 : TwinDown(&js, twin_btn[3]) ? -32768 : 0;
  g->bLeftTrigger = TwinDown(&js, twin_btn[4]) ? 255 : 0;
  g->bRightTrigger = TwinDown(&js, twin_btn[6]) ? 255 : 0;
  if (TwinDown(&js, twin_btn[5])) g->wButtons |= XINPUT_GAMEPAD_LEFT_SHOULDER;
  if (TwinDown(&js, twin_btn[7])) g->wButtons |= XINPUT_GAMEPAD_RIGHT_SHOULDER;
  if (TwinDown(&js, twin_btn[8])) g->wButtons |= XINPUT_GAMEPAD_START;
  state->dwPacketNumber = ++twin_packet;
  return ERROR_SUCCESS;
}

/* RACING_INJECT_SLOT=<0-3>: which injected slot to merge into this program's controllers (testing two
   seats that both see their own stick as controller 0). Unset: the same slot. */
static int inject_slot = -1;

static DWORD Merge(DWORD slot, DWORD result, XINPUT_STATE* state) {
  volatile InjectBlock* b = slot < 4 ? Block() : NULL;
  if (b && inject_slot >= 0) {
    slot = (DWORD)inject_slot;
  }
  if (!b || !b->active[slot] || !state) {
    LogChange(slot, result, state);
    return result;
  }
  if (result != ERROR_SUCCESS) {
    ZeroMemory(state, sizeof(*state));
  }
  state->Gamepad.wButtons |= b->buttons[slot];
  if (b->left_trigger[slot]) {
    state->Gamepad.bLeftTrigger = b->left_trigger[slot];
  }
  if (b->right_trigger[slot]) {
    state->Gamepad.bRightTrigger = b->right_trigger[slot];
  }
  if (b->thumb_lx[slot]) {
    state->Gamepad.sThumbLX = b->thumb_lx[slot];
  }
  /* Programs only look at a state whose packet number changed. */
  state->dwPacketNumber += b->sequence;
  LogChange(slot, ERROR_SUCCESS, state);
  return ERROR_SUCCESS;
}

DWORD WINAPI XInputGetState(DWORD slot, XINPUT_STATE* state) {
  if (Hidden(slot)) {
    return ERROR_DEVICE_NOT_CONNECTED;
  }
  LoadReal();
  DWORD result = twin ? TwinState(state)
                 : real_get_state ? real_get_state(slot, state) : ERROR_DEVICE_NOT_CONNECTED;
  result = Merge(slot, result, state);
  LogFirstCalls(0, slot, result);
  return result;
}

DWORD WINAPI XInputGetStateEx(DWORD slot, XINPUT_STATE* state) {
  if (Hidden(slot)) {
    return ERROR_DEVICE_NOT_CONNECTED;
  }
  LoadReal();
  DWORD result = twin ? TwinState(state)
                 : real_get_state_ex ? real_get_state_ex(slot, state)
                 : real_get_state  ? real_get_state(slot, state)
                                   : ERROR_DEVICE_NOT_CONNECTED;
  result = Merge(slot, result, state);
  LogFirstCalls(1, slot, result);
  return result;
}

DWORD WINAPI XInputSetState(DWORD slot, XINPUT_VIBRATION* vibration) {
  if (Hidden(slot)) {
    return ERROR_DEVICE_NOT_CONNECTED;
  }
  LoadReal();
  return real_set_state ? real_set_state(slot, vibration)
                        : ERROR_DEVICE_NOT_CONNECTED;
}

DWORD WINAPI XInputGetCapabilities(DWORD slot, DWORD flags,
                                   XINPUT_CAPABILITIES* caps) {
  if (Hidden(slot)) {
    return ERROR_DEVICE_NOT_CONNECTED;
  }
  LoadReal();
  DWORD result = twin ? ERROR_DEVICE_NOT_CONNECTED
                 : real_get_capabilities ? real_get_capabilities(slot, flags, caps)
                                         : ERROR_DEVICE_NOT_CONNECTED;
  volatile InjectBlock* b = slot < 4 ? Block() : NULL;
  XINPUT_STATE probe;
  if (result != ERROR_SUCCESS && caps &&
      ((b && b->active[slot]) || (twin && TwinState(&probe) == ERROR_SUCCESS))) {
    /* An injected slot with nothing plugged in: report a plain gamepad. */
    ZeroMemory(caps, sizeof(*caps));
    caps->Type = XINPUT_DEVTYPE_GAMEPAD;
    caps->SubType = XINPUT_DEVSUBTYPE_GAMEPAD;
    caps->Gamepad.wButtons = 0xF3FF;
    caps->Gamepad.bLeftTrigger = 0xFF;
    caps->Gamepad.bRightTrigger = 0xFF;
    caps->Gamepad.sThumbLX = -64;
    result = ERROR_SUCCESS;
  }
  LogFirstCalls(2, slot, result);
  return result;
}

void WINAPI XInputEnable(BOOL enable) {
  LoadReal();
  if (real_enable) {
    real_enable(enable);
  }
}

DWORD WINAPI XInputGetBatteryInformation(DWORD slot, BYTE type,
                                         XINPUT_BATTERY_INFORMATION* info) {
  if (Hidden(slot)) {
    return ERROR_DEVICE_NOT_CONNECTED;
  }
  LoadReal();
  return real_get_battery ? real_get_battery(slot, type, info)
                          : ERROR_DEVICE_NOT_CONNECTED;
}

DWORD WINAPI XInputGetKeystroke(DWORD slot, DWORD reserved,
                                PXINPUT_KEYSTROKE keystroke) {
  if (Hidden(slot) && slot != XUSER_INDEX_ANY) {
    return ERROR_DEVICE_NOT_CONNECTED;
  }
  LoadReal();
  return real_get_keystroke ? real_get_keystroke(slot, reserved, keystroke)
                            : ERROR_DEVICE_NOT_CONNECTED;
}

DWORD WINAPI XInputGetAudioDeviceIds(DWORD slot, LPWSTR render, UINT* render_count,
                                     LPWSTR capture, UINT* capture_count) {
  LoadReal();
  return real_get_audio_ids
             ? real_get_audio_ids(slot, render, render_count, capture,
                                  capture_count)
             : ERROR_DEVICE_NOT_CONNECTED;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
  (void)reserved;
  if (reason == DLL_PROCESS_ATTACH) {
    char only[8];
    DWORD len = GetEnvironmentVariableA("RACING_XINPUT_ONLY", only, sizeof(only));
    if (len == 1 && only[0] >= '0' && only[0] <= '3') {
      only_slot = only[0] - '0';
    }
    len = GetEnvironmentVariableA("RACING_INJECT_SLOT", only, sizeof(only));
    if (len == 1 && only[0] >= '0' && only[0] <= '3') {
      inject_slot = only[0] - '0';
    }
    char tw[256];
    len = GetEnvironmentVariableA("RACING_TWINSTICK", tw, sizeof(tw));
    if (len > 0 && len < sizeof(tw)) {
      TwinParse(tw);
      if (twin) {
        only_slot = 0; /* the stick is controller 0; nothing else is visible */
      }
    }
    DWORD n = GetModuleFileNameW(instance, log_path, MAX_PATH);
    WCHAR* slash = n ? wcsrchr(log_path, L'\\') : NULL;
    if (slash && (slash - log_path) < MAX_PATH - 20) {
      lstrcpyW(slash + 1, L"xinput_inject.log");
      char line[MAX_PATH + 32];
      char exe[MAX_PATH];
      GetModuleFileNameA(NULL, exe, MAX_PATH);
      wsprintfA(line, "loaded by %s (pid %lu), only slot %d", exe, GetCurrentProcessId(),
                only_slot);
      Log(line);
    } else {
      log_path[0] = 0;
    }
  } else if (reason == DLL_PROCESS_DETACH) {
    if (block) {
      UnmapViewOfFile((LPCVOID)block);
    }
    if (mapping) {
      CloseHandle(mapping);
    }
  }
  return TRUE;
}
