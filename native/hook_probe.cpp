// SPDX-License-Identifier: GPL-3.0-only
// Resolve exact named functions outside DWM's loader lock. Bind addresses to this PE version.
#include <windows.h>
#include <dbghelp.h>
#include <fstream>
#include <iostream>
#include <filesystem>
#include <vector>
#include <string>
#include <stdexcept>
struct Record
{
    DWORD magic = 0x324b4841, timestamp = 0, imageSize = 0;
    DWORD present = 0, direct = 0, overlays = 0, backBuffer = 0, resource = 0, protectedCheck = 0,
          sourceId = 0, adapterLuid = 0;
};
std::vector<std::pair<std::string, DWORD64>> symbols;
BOOL CALLBACK collect(PSYMBOL_INFO symbol, ULONG, PVOID)
{
    symbols.emplace_back(symbol->Name, symbol->Address);
    return TRUE;
}
int main(int argc, char **argv)
{
    try
    {
        if (argc < 2)
            throw std::runtime_error("hook-probe output-folder");
        std::filesystem::path folder = std::filesystem::absolute(argv[1]);
        std::filesystem::create_directories(folder / "symbols");
        auto process = GetCurrentProcess();
        auto cache = std::string("srv*") + (folder / "symbols").string() +
                     "*https://msdl.microsoft.com/download/symbols";
        bool inspect = argc > 2 && std::string(argv[2]) == "--inspect";
        SymSetOptions((inspect ? 0 : SYMOPT_UNDNAME) | SYMOPT_FAIL_CRITICAL_ERRORS |
                      SYMOPT_EXACT_SYMBOLS);
        if (!SymInitialize(process, cache.c_str(), FALSE))
            throw std::runtime_error("Symbol initialization failed");
        wchar_t windows[MAX_PATH];
        GetSystemDirectoryW(windows, MAX_PATH);
        auto path = std::filesystem::path(windows) / "dwmcore.dll";
        auto image = LoadLibraryExW(path.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
        if (!image)
            throw std::runtime_error("Cannot map dwmcore");
        auto dos = (IMAGE_DOS_HEADER *)image;
        auto pe = (IMAGE_NT_HEADERS *)((char *)image + dos->e_lfanew);
        Record record;
        record.timestamp = pe->FileHeader.TimeDateStamp;
        record.imageSize = pe->OptionalHeader.SizeOfImage;
        auto base = SymLoadModuleExW(process, nullptr, path.c_str(), L"dwmcore", 0x10000000,
                                     record.imageSize, nullptr, 0);
        if (!base)
            throw std::runtime_error("Cannot load matching dwmcore symbols");
        if (!SymEnumSymbols(process, base, "*", collect, nullptr))
            throw std::runtime_error(
                "Microsoft symbols unavailable; no hook addresses were written");
        if (inspect)
        {
            for (auto &s : symbols)
                if (s.first.find("CDDisplaySwapChain") != std::string::npos ||
                    s.first.find("GetDXGISwapChain") != std::string::npos ||
                    s.first.find("COverlayContext") != std::string::npos)
                {
                    char name[4096];
                    UnDecorateSymbolName(s.first.c_str(), name, sizeof(name), UNDNAME_COMPLETE);
                    std::cout << std::hex << (s.second - base) << " " << name << "\n";
                }
            return 0;
        }
        if (symbols.empty())
        {
            IMAGEHLP_MODULE64 info{};
            info.SizeOfStruct = sizeof(info);
            SymGetModuleInfo64(process, base, &info);
            std::cerr << "Symbol type " << info.SymType << "; PDB " << info.LoadedPdbName
                      << "; error " << GetLastError() << "\n";
        }
        auto find = [&](const std::string &suffix)
        {
            DWORD rva = 0;
            int count = 0;
            for (auto &s : symbols)
                if (s.first == "COverlayContext::" + suffix ||
                    s.first.find("COverlayContext::" + suffix + "(") != std::string::npos)
                {
                    rva = DWORD(s.second - base);
                    count++;
                }
            if (count != 1)
            {
                for (auto &s : symbols)
                    if (s.first.find(suffix) != std::string::npos)
                        std::cerr << s.first << "\n";
                throw std::runtime_error("Expected one named COverlayContext::" + suffix +
                                         ", found " + std::to_string(count) + " (" +
                                         std::to_string(symbols.size()) + " available symbols)");
            }
            return rva;
        };
        record.present = find("Present");
        record.direct = find("IsCandidateDirectFlipCompatible");
        record.overlays = find("OverlaysEnabled");
        auto named = [&](const std::string &name)
        {
            DWORD rva = 0;
            int count = 0;
            for (auto &s : symbols)
                if (s.first == name || s.first.find(name + "(") != std::string::npos)
                {
                    rva = DWORD(s.second - base);
                    count++;
                }
            if (count != 1)
                throw std::runtime_error("Missing or ambiguous " + name);
            return rva;
        };
        record.backBuffer = named("CDDisplaySwapChain::GetPhysicalBackBuffer");
        record.resource = named("CDDisplaySwapChainBuffer::GetD3D11Resource");
        record.protectedCheck = named("COverlaySwapChain::IsHardwareProtected");
        record.sourceId = named("COverlaySwapChain::GetVidPnSourceId");
        record.adapterLuid = named("COverlaySwapChain::GetDisplayAdapterLuid");
        std::ofstream f(folder / "hook-addresses.bin", std::ios::binary);
        f.write((char *)&record, sizeof(record));
        std::cout << "Resolved exact dwmcore functions: " << std::hex << record.present << ", "
                  << record.direct << ", " << record.overlays << "; PE timestamp "
                  << record.timestamp << "\n";
        SymCleanup(process);
        FreeLibrary(image);
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << "\n";
        return 1;
    }
}
