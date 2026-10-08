#include <windows.h>
#include <iostream>
#include <filesystem>
int main(int argc,char** argv){if(argc!=2)return 2;
 wchar_t system[MAX_PATH];GetSystemDirectoryW(system,MAX_PATH);auto core=std::filesystem::path(system)/L"dwmcore.dll";
 if(!LoadLibraryExW(core.c_str(),nullptr,DONT_RESOLVE_DLL_REFERENCES)){std::cerr<<"Map dwmcore error "<<GetLastError()<<"\n";return 1;}
 auto hook=LoadLibraryW(std::filesystem::absolute(argv[1]).c_str());if(!hook){std::cerr<<"Hook load failed: Windows error "<<GetLastError()<<"\n";return 1;}
 std::cout<<"PASS: hook DLL loaded and initialized in isolated test process\n";FreeLibrary(hook);return 0;
}
