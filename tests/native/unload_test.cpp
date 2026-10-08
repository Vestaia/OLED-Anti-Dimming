// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#ifdef UNLOAD_PAYLOAD
extern "C" __declspec(dllexport) long Callback(void*,void*,unsigned,void*,int,void*,bool) {
    Sleep(2);
    return 99;
}
#else
#include <MinHook.h>
#include <atomic>
#include <thread>
#include <vector>
#include <iostream>
using Present = long(*)(void*,void*,unsigned,void*,int,void*,bool);
__declspec(noinline) long Original(void*,void*,unsigned,void*,int,void*,bool) { return 7; }
BOOL Disable() { auto status=MH_DisableHook(MH_ALL_HOOKS); return status==MH_OK||status==MH_ERROR_DISABLED; }
BOOL RejectDisable() { return FALSE; }
int main() {
    auto bridge=LoadLibraryW(L"oled-hook-bridge.dll");
    if(!bridge) return 1;
    auto configure=(void(*)(void**,void**))GetProcAddress(bridge,"OledBridgeConfigure");
    auto close=(BOOL(*)(BOOL(*)()))GetProcAddress(bridge,"OledBridgeClose");
    auto entry=GetProcAddress(bridge,"OledBridgePresent");
    if(!configure||!close||!entry) return 2;
    std::atomic<bool> running=true;
    std::atomic<unsigned> failures=0,calls=0;
    std::vector<std::thread> workers;
    for(int i=0;i<8;i++)workers.emplace_back([&] {
        Present volatile original=&Original;
        while(running) {
            long value=original(nullptr,nullptr,0,nullptr,0,nullptr,false);
            if(value!=7&&value!=99)failures++;
            calls++;
        }
    });
    for(int cycle=0;cycle<50;cycle++) {
        auto payload=LoadLibraryW(L"unload-payload.dll");
        if(!payload||MH_Initialize()!=MH_OK) return 3;
        void* trampoline=nullptr;
        void* targets[]{(void*)GetProcAddress(payload,"Callback"),nullptr,nullptr};
        void* originals[]{(void*)&Original,nullptr,nullptr};
        if(MH_CreateHook((void*)&Original,(void*)entry,&trampoline)!=MH_OK)return 4;
        configure(targets,originals);
        if(MH_EnableHook(MH_ALL_HOOKS)!=MH_OK)return 5;
        if(cycle==0) {
            if(close(RejectDisable)||!GetModuleHandleW(L"unload-payload.dll"))return 10;
            if(((Present)entry)(nullptr,nullptr,0,nullptr,0,nullptr,false)!=99)return 11;
        }
        Sleep(5);
        if(!close(Disable)||MH_Uninitialize()!=MH_OK)return 6;
        if(!FreeLibrary(payload)||GetModuleHandleW(L"unload-payload.dll"))return 7;
        // Late bridge arrivals must use the restored executable entry, not a
        // freed trampoline or a callback in the unloaded payload.
        if(((Present)entry)(nullptr,nullptr,0,nullptr,0,nullptr,false)!=7)return 8;
    }
    running=false;
    for(auto& thread:workers)thread.join();
    if(failures)return 9;
    std::cout<<"PASS: 50 concurrent hook/drain/unload/reload cycles, "<<calls<<" calls; payload fully unmapped\n";
}
#endif
