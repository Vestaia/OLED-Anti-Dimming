// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#include <MinHook.h>
#include <array>
#include <atomic>
#include <fstream>
#include <iostream>
#include <thread>
#include <stdexcept>
#include <string>
#include <sstream>
extern "C" bool ClobberVolatiles(void *);
extern "C" bool OledOverlaysEnabledThunk(void *);
extern "C" bool OledOverlaysEnabledImpl(void *self)
{
    return ClobberVolatiles(self);
}
extern "C" void ProbeOverlayRegisters(void *, void *, void *);
using Snapshot = std::array<unsigned long long, 19>;
Snapshot capture(void *fn, void *self)
{
    Snapshot s{};
    ProbeOverlayRegisters(fn, self, s.data());
    s[0] &= ~255ull;
    return s;
}
bool faults(void *fn, void *self)
{
    __try
    {
        ((bool (*)(void *))fn)(self);
        return false;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return true;
    }
}
int main(int argc, char **argv)
{
    try
    {
        DWORD addresses[11]{};
        std::ifstream f(argc > 1 ? argv[1] : "build/overlay-symbols/hook-addresses.bin",
                        std::ios::binary);
        f.read((char *)addresses, sizeof(addresses));
        if (!f)
            throw std::runtime_error("Missing version-bound symbol addresses");
        wchar_t systemPath[MAX_PATH];
        GetSystemDirectoryW(systemPath, MAX_PATH);
        auto imagePath = std::wstring(systemPath) + L"/dwmcore.dll";
        auto image = LoadLibraryExW(imagePath.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
        if (!image)
            throw std::runtime_error("Cannot map DWM image");
        auto pe = (IMAGE_NT_HEADERS *)((char *)image + ((IMAGE_DOS_HEADER *)image)->e_lfanew);
        if (pe->FileHeader.TimeDateStamp != addresses[1] ||
            pe->OptionalHeader.SizeOfImage != addresses[2])
            throw std::runtime_error("Mismatched DWM image");
        auto target = (void *)((char *)image + addresses[5]);
        std::array<unsigned char, 128> object{};
        *(DWORD *)(object.data() + 0x28) = 2;
        std::ifstream names(argc > 2 ? argv[2] : "build/dwm-symbols.txt");
        std::string line;
        unsigned callerRva = 0;
        while (std::getline(names, line))
            if (line.find("COverlayContext::OverlayPlaneInfo::IsDFlipOnMPO(") != std::string::npos)
            {
                std::istringstream row(line);
                row >> std::hex >> callerRva;
            }
        if (!callerRva || callerRva >= addresses[2])
            throw std::runtime_error("Missing exact DWM caller symbol");
        auto caller = (void *)((char *)image + callerRva);
        std::array<unsigned char, 512> plane{};
        *(void **)plane.data() = object.data();
        if (faults(caller, plane.data()))
            throw std::runtime_error("Original DWM caller unexpectedly faulted");
        auto baseline = capture(target, object.data());
        if (MH_Initialize() != MH_OK)
            throw std::runtime_error("MinHook init");
        void *original = nullptr;
        if (MH_CreateHook(target, (void *)OledOverlaysEnabledImpl, &original) != MH_OK ||
            MH_EnableHook(target) != MH_OK)
            throw std::runtime_error("Old hook install");
        auto broken = capture(target, object.data());
        if (broken == baseline)
            throw std::runtime_error("Regression did not reproduce register corruption");
        if (!faults(caller, plane.data()))
            throw std::runtime_error("Original DWM caller did not reproduce access violation");
        std::cout << "REPRODUCED: actual DWM IsDFlipOnMPO caller accesses corrupted live pointer "
                     "with public-ABI hook\n";
        std::cout << "REPRODUCED: public-ABI replacement corrupts live registers (RDX=" << std::hex
                  << broken[2] << ", R9=" << broken[4] << ")\n";
        MH_DisableHook(target);
        MH_RemoveHook(target);
        if (MH_CreateHook(target, (void *)OledOverlaysEnabledThunk, &original) != MH_OK ||
            MH_EnableHook(target) != MH_OK)
            throw std::runtime_error("Preserving hook install");
        if (capture(target, object.data()) != baseline)
            throw std::runtime_error("Wrapper corrupted registers");
        if (faults(caller, plane.data()))
            throw std::runtime_error("Preserving wrapper still faults in DWM caller");
        if (((bool (*)(void *))target)(object.data()))
            throw std::runtime_error("Wrapper did not return replacement AL");
        std::atomic<bool> stop = false;
        std::atomic<unsigned> errors = 0, calls = 0;
        std::thread worker(
            [&]
            {
                while (!stop)
                {
                    if (capture(target, object.data()) != baseline)
                        errors++;
                    calls++;
                }
            });
        for (int i = 0; i < 100; i++)
        {
            if (MH_DisableHook(target) != MH_OK || MH_EnableHook(target) != MH_OK)
            {
                errors++;
                break;
            }
        }
        stop = true;
        worker.join();
        MH_DisableHook(target);
        MH_Uninitialize();
        if (errors)
            throw std::runtime_error("Concurrent toggle corrupted registers");
        std::cout << std::dec
                  << "PASS: RAX upper bits, RCX/RDX/R8-R11, XMM0-XMM5 preserved; replacement AL "
                     "forwarded\nPASS: 100 concurrent toggle cycles, "
                  << calls << " register probes, zero errors\n";
        return 0;
    }
    catch (const std::exception &ex)
    {
        std::cerr << ex.what() << "\n";
        return 1;
    }
}
