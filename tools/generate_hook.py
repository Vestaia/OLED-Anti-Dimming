# SPDX-License-Identifier: GPL-3.0-only
"""Build-local adaptation of the pinned upstream hook; upstream source remains intact."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
source=(root/'external/dwm_lut/lutdwm/dllmain.cpp').read_text(encoding='utf-8-sig')
source=source.replace('#include "pch.h"','#include "pch.h"\n#include "adaptive_filter.hpp"\n#include "filter_shader.hpp"\nAdaptiveFilter adaptive;\nbool adaptiveInitialized=false;\n'+r'''
void hookLog(const char* text) {
    wchar_t path[MAX_PATH];ExpandEnvironmentStringsW(L"%SYSTEMROOT%\\Temp\\oled-apl\\hook-diagnostic.log",path,MAX_PATH);
    HANDLE file=CreateFileW(path,FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file!=INVALID_HANDLE_VALUE){DWORD written;WriteFile(file,text,DWORD(strlen(text)),&written,nullptr);WriteFile(file,"\r\n",2,&written,nullptr);CloseHandle(file);}
}
bool hookStatus(MH_STATUS status,const char* operation){char message[256];sprintf_s(message,"%s: %s",operation,MH_StatusToString(status));hookLog(message);return status==MH_OK;}
using PrivateGetter=void*(__fastcall*)(void*);
PrivateGetter getPhysicalBuffer=nullptr,getBufferResource=nullptr;
bool(__fastcall* isProtected)(void*)=nullptr;
UINT(__fastcall* getSourceId)(void*)=nullptr;
LUID*(__fastcall* getAdapterLuid)(void*,LUID*)=nullptr;
''')
source=source.replace('#define LUT_FOLDER "%SYSTEMROOT%\\\\Temp\\\\luts"','#define LUT_FOLDER "%SYSTEMROOT%\\\\Temp\\\\oled-apl\\\\luts"')
# Release upstream macros ignore HRESULTs; fail safely instead.
source=source.replace('#define EXECUTE_WITH_LOG(winapi_func_hr) winapi_func_hr;', '#define EXECUTE_WITH_LOG(winapi_func_hr) AdaptiveFilter::require(winapi_func_hr);')
source=source.replace('#define EXECUTE_D3DCOMPILE_WITH_LOG(winapi_func_hr, error_interface) winapi_func_hr;', '#define EXECUTE_D3DCOMPILE_WITH_LOG(winapi_func_hr, error_interface) AdaptiveFilter::require(winapi_func_hr);')
source=source.replace('const D3D11_VIEWPORT d3d11_viewport(0, 0, backBufferDesc.Width, backBufferDesc.Height, 0.0f, 1.0f);','const D3D11_VIEWPORT d3d11_viewport{0, 0, FLOAT(backBufferDesc.Width), FLOAT(backBufferDesc.Height), 0.0f, 1.0f};')
source=source.replace('width = textureDesc[index].Width;', 'width = backBufferDesc.Width;').replace('height = textureDesc[index].Height;', 'height = backBufferDesc.Height;')
source=source.replace('samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;', 'samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;')
source=source.replace('if (index == -1 || !lut)', 'if (index == -1 || !lut || lut->isHdr != (index == 1))')
# Use documented DXGI output identity instead of a private COverlayContext clip-box offset.
source=source.replace('lut = GetLUTDataFromCOverlayContext(cOverlayContext, index == 1)', 'lut = GetLUTForSwapChain(swapChain, index == 1)')
source=source.replace('bool ApplyLUT(void* cOverlayContext', '''lutData* GetLUTForSwapChain(IDXGISwapChain* swapChain,bool hdr) {
    IDXGIOutput* output=nullptr;HRESULT outputResult=swapChain->GetContainingOutput(&output);if(FAILED(outputResult)){static bool reported=false;if(!reported){char message[128];sprintf_s(message,"GetContainingOutput failed: %08lx",outputResult);hookLog(message);reported=true;}return nullptr;}
    DXGI_OUTPUT_DESC desc{};HRESULT hr=output->GetDesc(&desc);output->Release();if(FAILED(hr))return nullptr;
    for(int i=0;i<numLuts;i++)if(luts[i].left==desc.DesktopCoordinates.left&&luts[i].top==desc.DesktopCoordinates.top&&luts[i].isHdr==hdr)return &luts[i];
    static bool reported=false;if(!reported){char message[128];sprintf_s(message,"No LUT for output at %ld,%ld HDR %d",desc.DesktopCoordinates.left,desc.DesktopCoordinates.top,hdr);hookLog(message);reported=true;}return nullptr;
}
bool ApplyLUT(void* cOverlayContext''')
# Report failed MinHook installation instead of returning a successful LoadLibrary.
source=source.replace('MH_Initialize();','if(!hookStatus(MH_Initialize(),"Initialize"))return FALSE;')
source=source.replace('MH_EnableHook(MH_ALL_HOOKS);','if(!hookStatus(MH_EnableHook(MH_ALL_HOOKS),"Enable")){MH_Uninitialize();return FALSE;}hookLog("Hooks installed");')
import re
source=re.sub(r'MH_CreateHook\((.*?)\);',r'if(!hookStatus(MH_CreateHook(\1),"Create")){MH_Uninitialize();return FALSE;}',source,flags=re.S)
hookStart=source.index('\t\t\t\tif(!hookStatus(MH_Initialize()')
hookEnd=source.index('\n\t\t\t\tLOG_ONLY_ONCE("DWM HOOK DLL INITIALIZATION',hookStart)
source=source[:hookStart]+'''                if(!hookStatus(MH_Initialize(),"Initialize"))return FALSE;
                if(!hookStatus(MH_CreateHook((PVOID)COverlayContext_Present_orig_24h2,(PVOID)COverlayContext_Present_hook_24h2,(PVOID*)&COverlayContext_Present_orig_24h2),"Create Present")
                  ||!hookStatus(MH_CreateHook((PVOID)COverlayContext_IsCandidateDirectFlipCompatbile_orig_24h2,(PVOID)COverlayContext_IsCandidateDirectFlipCompatbile_hook_24h2,(PVOID*)&COverlayContext_IsCandidateDirectFlipCompatbile_orig_24h2),"Create DirectFlip")
                  ||!hookStatus(MH_CreateHook((PVOID)COverlayContext_OverlaysEnabled_orig,(PVOID)COverlayContext_OverlaysEnabled_hook,(PVOID*)&COverlayContext_OverlaysEnabled_orig),"Create Overlays")){
                    MH_Uninitialize();return FALSE;
                }
                if(!hookStatus(MH_EnableHook(MH_ALL_HOOKS),"Enable")){MH_Uninitialize();return FALSE;}
                hookLog("All three hooks installed");
''' +source[hookEnd:]
versionStart=source.index('\t\t\tOSVERSIONINFOEX versionInfo;')
versionEnd=source.index('\n\t\t\t// TODO: Remove this debug instruction',versionStart)
source=source[:versionStart]+'''            hookLog("DLL attach entered");
            typedef LONG(WINAPI* GetVersionFn)(OSVERSIONINFOW*);
            auto getVersion=(GetVersionFn)GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"RtlGetVersion");
            OSVERSIONINFOW actual{};actual.dwOSVersionInfoSize=sizeof(actual);
            if(!getVersion||getVersion(&actual)!=0){hookLog("Cannot read actual Windows build");return FALSE;}
            char versionMessage[80];sprintf_s(versionMessage,"Windows build %lu",actual.dwBuildNumber);hookLog(versionMessage);
            isWindows11_24h2=actual.dwBuildNumber>=26100;
            isWindows11=actual.dwBuildNumber>=22000&&!isWindows11_24h2;
            if(!isWindows11_24h2){hookLog("Unsupported pre-24H2 private ABI");return FALSE;}
''' +source[versionEnd:]
start=source.index('\t\t\t\tfor (size_t i = 0;',source.index('\t\t\tif (isWindows11_24h2)'))
end=source.index('\n\t\t\t}\n\t\t\telse if (isWindows11)',start)
source=source[:start]+'''                struct Addresses {DWORD magic,timestamp,imageSize,present,direct,overlays,backBuffer,resource,protectedCheck,sourceId,adapterLuid;} addresses{};
                wchar_t file[MAX_PATH];ExpandEnvironmentStringsW(L"%SYSTEMROOT%\\\\Temp\\\\oled-apl\\\\hook-addresses.bin",file,MAX_PATH);
                std::ifstream input(file,std::ios::binary);input.read((char*)&addresses,sizeof(addresses));
                auto pe=(IMAGE_NT_HEADERS*)((char*)dwmcore+((IMAGE_DOS_HEADER*)dwmcore)->e_lfanew);
                if(!input||addresses.magic!=0x324b4841||addresses.timestamp!=pe->FileHeader.TimeDateStamp||addresses.imageSize!=moduleInfo.SizeOfImage
                    ||addresses.present>=addresses.imageSize||addresses.direct>=addresses.imageSize||addresses.overlays>=addresses.imageSize){hookLog("Missing or mismatched symbol address file");return FALSE;}
                auto present=(unsigned char*)dwmcore+addresses.present;
                auto direct=(unsigned char*)dwmcore+addresses.direct;
                // Present's prologue differs on 25H2; its exact named PDB address is authoritative.
                if(aob_match_inverse(direct,COverlayContext_IsCandidateDirectFlipCompatbile_bytes_w11_24h2,sizeof(COverlayContext_IsCandidateDirectFlipCompatbile_bytes_w11_24h2))){hookLog("DirectFlip function ABI check failed");return FALSE;}
                COverlayContext_Present_orig_24h2=(COverlayContext_Present_24h2_t*)present;
                COverlayContext_Present_real_orig_24h2=COverlayContext_Present_orig_24h2;
                COverlayContext_IsCandidateDirectFlipCompatbile_orig_24h2=(COverlayContext_IsCandidateDirectFlipCompatbile_24h2_t*)direct;
                COverlayContext_OverlaysEnabled_orig=(COverlayContext_OverlaysEnabled_t*)((char*)dwmcore+addresses.overlays);
                getPhysicalBuffer=(PrivateGetter)((char*)dwmcore+addresses.backBuffer);
                getBufferResource=(PrivateGetter)((char*)dwmcore+addresses.resource);
                isProtected=(bool(__fastcall*)(void*))((char*)dwmcore+addresses.protectedCheck);
                getSourceId=(UINT(__fastcall*)(void*))((char*)dwmcore+addresses.sourceId);
                getAdapterLuid=(LUID*(__fastcall*)(void*,LUID*))((char*)dwmcore+addresses.adapterLuid);
''' +source[end:]
# Unload GPU resources before MinHook tears down. Do not leave COM references in DWM.
source=source.replace('UninitializeStuff();','UninitializeStuff();\n        adaptive=AdaptiveFilter{}; adaptiveInitialized=false;')
marker='\t\tfor (int i = 0; i < numRects; i++)\n\t\t{\n\t\t\tD3D11_BOX sourceRegion;'
start=source.index(marker);end=source.index('\n\t\tbackBuffer->Release();',start)
source=source[:start]+'''\t\tif (!adaptiveInitialized) {
            wchar_t folder[MAX_PATH]; ExpandEnvironmentStringsW(L"%SYSTEMROOT%\\\\Temp\\\\oled-apl",folder,MAX_PATH);
            adaptive.initialize(device,folder,filterShader); adaptiveInitialized=true;
        }
        // Unbind the previous corrected scene before updating its pristine cache.
        ID3D11ShaderResourceView* nil[6]{}; deviceContext->PSSetShaderResources(0,6,nil);
        adaptive.prepare(device,deviceContext,backBuffer,swapChain,rects,numRects,lut->textureView,lut->size,index==1,samplerState);
        RECT full{0,0,LONG(backBufferDesc.Width),LONG(backBufferDesc.Height)};
        DrawRectangle(&full,index);
        static bool reported=false;if(!reported){hookLog("First complete adaptive filter frame submitted");reported=true;}
        deviceContext->PSSetShaderResources(0,6,nil);
''' +source[end:]
source=source.replace('catch (std::exception& ex)\n\t{','catch (std::exception& ex)\n\t{\n        hookLog(ex.what());')
# Gain changes affect unchanged regions: present the whole output after the filter draw.
source=source.replace('return COverlayContext_Present_orig_24h2(self, overlaySwapChain, a3, rectVec, a5, a6, a7);','''RECT full{0,0,LONG(backBufferDesc.Width),LONG(backBufferDesc.Height)};
    ::rectVec all{&full,&full+1,&full+1};
    return COverlayContext_Present_orig_24h2(self, overlaySwapChain, a3, adaptiveInitialized&&IsLUTActive(self)?&all:rectVec, a5, a6, a7);''')
source=source.replace('return COverlayContext_Present_orig(self, overlaySwapChain, a3, rectVec, a5, a6);','''RECT full{0,0,LONG(backBufferDesc.Width),LONG(backBufferDesc.Height)};
    ::rectVec all{&full,&full+1,&full+1};
    return COverlayContext_Present_orig(self, overlaySwapChain, a3, adaptiveInitialized&&IsLUTActive(self)?&all:rectVec, a5, a6);''')
# Keep the 24H2/25H2 wrapper small, guard private pointer access, and use documented
# swap-chain protection flags rather than an unchecked private bool offset.
start=source.index('long long COverlayContext_Present_hook_24h2(')
end=source.index('\n\nlong COverlayContext_Present_hook(',start)
source=source[:start]+'''IDXGISwapChain* ReadPrivateSwapChain(void* overlay) {
    __try {return *(IDXGISwapChain**)((unsigned char*)overlay+IOverlaySwapChain_IDXGISwapChain_offset_w11_24h2);}
    __except(EXCEPTION_EXECUTE_HANDLER){return nullptr;}
}
bool SafeApply(void* self,IDXGISwapChain* swapChain,RECT* rects,int count) {
    __try {DXGI_SWAP_CHAIN_DESC desc{};if(FAILED(swapChain->GetDesc(&desc))||(desc.Flags&0x400)){static bool reported=false;if(!reported){hookLog("Swap-chain description unavailable or protected");reported=true;}return false;}
        return ApplyLUT(self,swapChain,rects,count);}
    __except(EXCEPTION_EXECUTE_HANDLER){static bool reported=false;if(!reported){hookLog("Private swap-chain ABI access fault");reported=true;}return false;}
}
long long COverlayContext_Present_hook_24h2(void* self,void* overlay,unsigned int a3,rectVec* damage,int a5,void* a6,bool a7) {
    static bool reported=false;if(!reported){hookLog("Present callback reached");reported=true;}
    auto swapChain=ReadPrivateSwapChain(overlay);
    bool applied=swapChain&&damage&&SafeApply(self,swapChain,damage->start,int(damage->end-damage->start));
    if(applied)SetLUTActive(self);else UnsetLUTActive(self);
    RECT full{0,0,LONG(backBufferDesc.Width),LONG(backBufferDesc.Height)};
    rectVec all{&full,&full+1,&full+1};
    return COverlayContext_Present_orig_24h2(self,overlay,a3,applied?&all:damage,a5,a6,a7);
}
''' +source[end:]
# DisplayCore exposes a D3D11 resource, not an IDXGISwapChain at a fixed offset.
source=source.replace('void InitializeStuff(IDXGISwapChain* swapChain)','void InitializeStuff(ID3D11Texture2D* sourceBuffer)')
source=source.replace('EXECUTE_WITH_LOG(swapChain->GetDevice(IID_ID3D11Device, (void**)&device))','sourceBuffer->GetDevice(&device);')
source=source.replace('LOG_ADDRESS("Current swapchain address is: ", swapChain)','LOG_ADDRESS("Current buffer address is: ", sourceBuffer)')
source=source.replace('bool ApplyLUT(void* cOverlayContext, IDXGISwapChain* swapChain, struct tagRECT* rects, int numRects)','bool ApplyTexture(void* cOverlayContext, ID3D11Texture2D* sourceBuffer, lutData* lut, struct tagRECT* rects, int numRects)')
source=source.replace('InitializeStuff(swapChain);','InitializeStuff(sourceBuffer);')
source=source.replace('\t\tID3D11Texture2D* backBuffer;','\t\tComPtr<ID3D11Texture2D> owned=sourceBuffer;auto backBuffer=owned.Get();')
source=source.replace('EXECUTE_WITH_LOG(swapChain->GetBuffer(0, IID_ID3D11Texture2D, (void**)&backBuffer))','')
source=source.replace('\t\tlutData* lut;\n\t\tif (index == -1 || !(lut = GetLUTForSwapChain(swapChain, index == 1)))','\t\tif (index == -1 || !lut)')
source=source.replace('backBuffer->Release();','')
source=source.replace('adaptive.prepare(device,deviceContext,backBuffer,swapChain,','adaptive.prepare(device,deviceContext,backBuffer,cOverlayContext,')
source=source.replace('typedef struct rectVec','bool ApplyLUT(void*,IDXGISwapChain*,RECT*,int){return false;}\n\ntypedef struct rectVec')
start=source.index('IDXGISwapChain* ReadPrivateSwapChain(');end=source.index('\n\nlong COverlayContext_Present_hook(',start)
source=source[:start]+'''lutData* LutForDisplayCore(void* overlay) {
    static std::unordered_map<void*,lutData*> cache;auto found=cache.find(overlay);if(found!=cache.end())return found->second;
    LUID luid{};getAdapterLuid(overlay,&luid);auto id=getSourceId(overlay);UINT pathsCount=0,modesCount=0;
    if(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,&pathsCount,&modesCount)!=ERROR_SUCCESS)return nullptr;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathsCount);std::vector<DISPLAYCONFIG_MODE_INFO> modes(modesCount);
    if(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,&pathsCount,paths.data(),&modesCount,modes.data(),nullptr)!=ERROR_SUCCESS)return nullptr;
    for(UINT i=0;i<pathsCount;i++){auto& info=paths[i].sourceInfo;
        if(info.id!=id||info.adapterId.LowPart!=luid.LowPart||info.adapterId.HighPart!=luid.HighPart)continue;
        if(info.modeInfoIdx>=modesCount)continue;auto position=modes[info.modeInfoIdx].sourceMode.position;
        for(int n=0;n<numLuts;n++)if(luts[n].left==position.x&&luts[n].top==position.y){cache[overlay]=&luts[n];return &luts[n];}
    }
    static bool reported=false;if(!reported){hookLog("DisplayCore output not matched to selected display");reported=true;}return nullptr;
}
bool SafeDisplayCoreApply(void* overlay,RECT* rects,int count) {
    ID3D11Texture2D* texture=nullptr;bool applied=false;int stage=0;
    __try {
        static bool described=false;if(!described){char message[128];sprintf_s(message,"Overlay vtable RVA %llx",(ULONGLONG)*(void**)overlay-(ULONGLONG)GetModuleHandleW(L"dwmcore.dll"));hookLog(message);described=true;}
        stage=1;
        // These PDB-resolved accessors use the same interface this-pointer as Present.
        stage=2;
        auto lut=LutForDisplayCore(overlay);if(!lut)return false;
        stage=3;
        auto physical=getPhysicalBuffer(overlay);if(!physical){hookLog("No physical back buffer");return false;}
        stage=4;
        auto resource=(ID3D11Resource*)getBufferResource(physical);if(!resource){hookLog("No D3D11 buffer resource");return false;}
        stage=5;
        if(FAILED(resource->QueryInterface(IID_ID3D11Texture2D,(void**)&texture)))return false;
        D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);
        static bool formatReported=false;if(!formatReported){char m[128];sprintf_s(m,"Selected DWM texture format %u (%ux%u)",unsigned(desc.Format),desc.Width,desc.Height);hookLog(m);formatReported=true;}
        if(desc.MiscFlags&D3D11_RESOURCE_MISC_HW_PROTECTED){hookLog("Hardware-protected texture skipped");texture->Release();return false;}
        stage=6;
        applied=ApplyTexture(overlay,texture,lut,rects,count);
    } __except(EXCEPTION_EXECUTE_HANDLER){static bool reported=false;if(!reported){char message[128];sprintf_s(message,"DisplayCore accessor ABI fault at stage %d",stage);hookLog(message);reported=true;}}
    if(texture)texture->Release();return applied;
}
long long COverlayContext_Present_hook_24h2(void* self,void* overlay,unsigned int a3,rectVec* damage,int a5,void* a6,bool a7) {
    static bool reported=false;if(!reported){hookLog("Present callback reached");reported=true;}
    bool applied=damage&&SafeDisplayCoreApply(overlay,damage->start,int(damage->end-damage->start));
    if(applied)SetLUTActive(self);else UnsetLUTActive(self);
    RECT full{0,0,LONG(backBufferDesc.Width),LONG(backBufferDesc.Height)};rectVec all{&full,&full+1,&full+1};
    return COverlayContext_Present_orig_24h2(self,overlay,a3,applied?&all:damage,a5,a6,a7);
}
''' +source[end:]
# Toggle callbacks without unmapping executing code. All mutable hook state is
# serialized; graphics/model reload happens only on the compositor render thread.
source=source.replace('#include "adaptive_filter.hpp"','#include "adaptive_filter.hpp"\n#include "dwm_context_state.hpp"\n#include <mutex>\nstd::recursive_mutex filterMutex;\nbool filterEnabled=true;\nextern "C" __declspec(dllexport) volatile LONG OledFilterState=1;\nbool filterReload=false;\nDwmContextState dwmState;\nextern "C" __declspec(dllexport) DWORD WINAPI OledFilterControl(void* command) {bool enable=command!=nullptr;{std::lock_guard<std::recursive_mutex> lock(filterMutex);filterEnabled=enable;filterReload=enable;}auto status=enable?MH_EnableHook(MH_ALL_HOOKS):MH_DisableHook(MH_ALL_HOOKS);bool ok=status==MH_OK||status==(enable?MH_ERROR_ENABLED:MH_ERROR_DISABLED);InterlockedExchange(&OledFilterState,ok?(enable?1:0):2);return ok?1:0;}')
source=source.replace('if (IsLUTActive(self))','if (filterEnabled && IsLUTActive(self))')
for signature in ['bool COverlayContext_IsCandidateDirectFlipCompatbile_hook_24h2(', 'bool COverlayContext_OverlaysEnabled_hook(']:
    start=source.index(signature);brace=source.index('{',start);source=source[:brace+1]+'\n    std::lock_guard<std::recursive_mutex> lock(filterMutex);'+source[brace+1:]
source=source.replace('    bool applied=damage&&SafeDisplayCoreApply(overlay,damage->start,int(damage->end-damage->start));','''    bool applied=false;
    {
        std::lock_guard<std::recursive_mutex> lock(filterMutex);
        if(filterEnabled&&damage&&damage->start&&damage->end>=damage->start&&damage->end-damage->start<=16384)
            applied=SafeDisplayCoreApply(overlay,damage->start,int(damage->end-damage->start));
        if(applied)SetLUTActive(self);else UnsetLUTActive(self);
    }''')
source=source.replace('    if(applied)SetLUTActive(self);else UnsetLUTActive(self);\n    RECT full','    RECT full')
source=source.replace('\t\tif (!device)\n\t\t{','''        ComPtr<ID3D11Device> sourceDevice;sourceBuffer->GetDevice(&sourceDevice);
        ComPtr<ID3D11DeviceContext> sourceContext;sourceDevice->GetImmediateContext(&sourceContext);
        dwmState.initialize(sourceDevice.Get(),sourceContext.Get());
        DwmContextState::Scope preserve(dwmState);
        if(device&&device!=sourceDevice.Get()){UninitializeStuff();adaptive=AdaptiveFilter{};adaptiveInitialized=false;}
        if(filterReload){adaptive=AdaptiveFilter{};adaptiveInitialized=false;filterReload=false;}
        if (!device)
        {''')
source=source.replace('            hookLog("All three hooks installed");','''            HMODULE pinned=nullptr;
            if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,(LPCWSTR)&OledFilterControl,&pinned)){hookLog("Cannot pin filter callbacks");return FALSE;}
            hookLog("All three hooks installed; resident toggle API enabled");''')
source=source.replace('                if(!hookStatus(MH_EnableHook(MH_ALL_HOOKS),"Enable")){MH_Uninitialize();return FALSE;}\n','')
source=source.replace('            hookLog("All three hooks installed; resident toggle API enabled");','            if(!hookStatus(MH_EnableHook(MH_ALL_HOOKS),"Enable")){filterEnabled=false;InterlockedExchange(&OledFilterState,2);return FALSE;}\n            hookLog("All three hooks installed; resident toggle API enabled");')
source=source.replace('typedef long long (COverlayContext_Present_24h2_t)', 'typedef long (COverlayContext_Present_24h2_t)')
source=source.replace('long long COverlayContext_Present_hook_24h2(', 'long COverlayContext_Present_hook_24h2(')
# The private OverlaysEnabled leaf has interprocedural register-preservation
# assumptions in DWM callers. A public-ABI C++ detour corrupts their live RDX/R9.
source=source.replace('bool COverlayContext_OverlaysEnabled_hook(void* self)', 'extern "C" bool OledOverlaysEnabledImpl(void* self)')
source=source.replace('BOOL APIENTRY DllMain(', 'extern "C" bool OledOverlaysEnabledThunk(void*);\n\nBOOL APIENTRY DllMain(')
source=source.replace('(PVOID)COverlayContext_OverlaysEnabled_hook,', '(PVOID)OledOverlaysEnabledThunk,')
# Abort rendering on a graphics initialization failure, leaving no repeated
# per-frame exception churn. GUI activation verifies a successful filtered frame.
source=source.replace('        hookLog(ex.what());','        hookLog(ex.what()); filterEnabled=false; InterlockedExchange(&OledFilterState,2);')
# Never perform MinHook/COM teardown under loader lock at process termination.
start=source.index('\tcase DLL_PROCESS_DETACH:');end=source.index('\tdefault:',start)
source=source[:start]+'\tcase DLL_PROCESS_DETACH:\n\t\tbreak;\n'+source[end:]
(root/'build/hook.cpp').write_text(source,encoding='utf-8')
(root/'build/filter_shader.hpp').write_text('static const char* filterShader=R"APL('+ (root/'native/adaptive_filter.hlsl').read_text() +')APL";',encoding='utf-8')
