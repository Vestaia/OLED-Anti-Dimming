// SPDX-License-Identifier: GPL-3.0-only
// Resident entry/return boundary, without graphics resources or filtering.
#include <windows.h>
static SRWLOCK gate = SRWLOCK_INIT;
static volatile LONG active = 0;
static void* callbacks[3]{};
static void* originals[3]{};
struct Call {
    void* target;
    bool tracked;
    explicit Call(int slot) {
        AcquireSRWLockShared(&gate);
        tracked = callbacks[slot] != nullptr;
        target = tracked ? callbacks[slot] : originals[slot];
        if (tracked) InterlockedIncrement(&active);
        ReleaseSRWLockShared(&gate);
    }
    ~Call() { if (tracked) InterlockedDecrement(&active); }
};
extern "C" __declspec(dllexport) void OledBridgeConfigure(void** targets, void** entries) {
    AcquireSRWLockExclusive(&gate);
    for (int i=0;i<3;i++) { callbacks[i]=targets[i]; originals[i]=entries[i]; }
    ReleaseSRWLockExclusive(&gate);
}
extern "C" __declspec(dllexport) BOOL OledBridgeClose(BOOL (*disable)()) {
    AcquireSRWLockExclusive(&gate);
    BOOL ok = disable();
    if (ok) for (auto& callback : callbacks) callback=nullptr;
    ReleaseSRWLockExclusive(&gate);
    if (!ok) return FALSE;
    ULONGLONG end = GetTickCount64()+5000;
    while (InterlockedCompareExchange(&active,0,0)) {
        if (GetTickCount64() >= end) return FALSE;
        Sleep(1);
    }
    return TRUE;
}
extern "C" __declspec(dllexport) long OledBridgePresent(void* a,void* b,unsigned c,void* d,int e,void* f,bool g) {
    Call call(0);
    return reinterpret_cast<long(*)(void*,void*,unsigned,void*,int,void*,bool)>(call.target)(a,b,c,d,e,f,g);
}
extern "C" __declspec(dllexport) bool OledBridgeDirectFlip(void* a,void* b,void* c,void* d,unsigned e,bool f) {
    Call call(1);
    return reinterpret_cast<bool(*)(void*,void*,void*,void*,unsigned,bool)>(call.target)(a,b,c,d,e,f);
}
extern "C" bool OledOverlaysEnabledImpl(void* self) {
    Call call(2);
    return reinterpret_cast<bool(*)(void*)>(call.target)(self);
}
BOOL WINAPI DllMain(HMODULE module,DWORD reason,void*) {
    if (reason==DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        HMODULE pinned=nullptr;
        return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&OledBridgePresent),&pinned);
    }
    return TRUE;
}
