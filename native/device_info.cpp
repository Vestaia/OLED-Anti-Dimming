#include <dshow.h>
#include <dxgi1_6.h>
#include <iostream>
#include <string>
#include <vector>
#include <windows.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;
bool queryHdr(const wchar_t* device,bool& hdr){
  ComPtr<IDXGIFactory1> factory;
  if(FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))return false;
  ComPtr<IDXGIAdapter1> adapter;
  for(UINT a=0;factory->EnumAdapters1(a,&adapter)==S_OK;a++,adapter.Reset()){
    ComPtr<IDXGIOutput> output;
    for(UINT o=0;adapter->EnumOutputs(o,&output)==S_OK;o++,output.Reset()){
      ComPtr<IDXGIOutput6> modern;DXGI_OUTPUT_DESC1 desc{};
      if(SUCCEEDED(output.As(&modern))&&SUCCEEDED(modern->GetDesc1(&desc))&&wcscmp(desc.DeviceName,device)==0){
        hdr=desc.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
        return true;
      }
    }
  }
  return false;
}
std::string json(const std::wstring &value) {
  int n = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), int(value.size()),
                              nullptr, 0, nullptr, nullptr);
  std::string utf(n, 0), out = "\"";
  WideCharToMultiByte(CP_UTF8, 0, value.c_str(), int(value.size()), utf.data(),
                      n, nullptr, nullptr);
  for (char c : utf) {
    if (c == '\\' || c == '"')
      out += '\\';
    if ((unsigned char)c < 32)
      out += ' ';
    else
      out += c;
  }
  return out + '"';
}
int main() {
  CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  std::cout << "{\"cameras\":[";
  bool first = true;
  {
    ComPtr<ICreateDevEnum> dev;
    ComPtr<IEnumMoniker> list;
    if (SUCCEEDED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr,
                                   CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dev))) &&
        dev->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &list, 0) ==
            S_OK) {
      ComPtr<IMoniker> item;
      while (list->Next(1, &item, nullptr) == S_OK) {
        ComPtr<IPropertyBag> bag;
        if (SUCCEEDED(
                item->BindToStorage(nullptr, nullptr, IID_PPV_ARGS(&bag)))) {
          VARIANT v;
          VariantInit(&v);
          if (SUCCEEDED(bag->Read(L"FriendlyName", &v, nullptr)) &&
              v.vt == VT_BSTR) {
            if (!first)
              std::cout << ',';
            first = false;
            std::cout << json(v.bstrVal);
          }
          VariantClear(&v);
        }
        item.Reset();
      }
    }
  }
  std::cout << "],\"displays\":[";
  UINT pn = 0, mn = 0;
  first = true;
  if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pn, &mn) ==
      ERROR_SUCCESS) {
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pn);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(mn);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pn, paths.data(), &mn,
                           modes.data(), nullptr) == ERROR_SUCCESS)
      for (UINT i = 0; i < pn; i++) {
        auto &p = paths[i];
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
        source.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                         sizeof(source), p.sourceInfo.adapterId,
                         p.sourceInfo.id};
        if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS)
          continue;
        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 color{};
        color.header = {DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,
                        sizeof(color), p.targetInfo.adapterId, p.targetInfo.id};
        LONG state = DisplayConfigGetDeviceInfo(&color.header);
        bool hdr =
            state == ERROR_SUCCESS &&
            color.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR;
        bool detected=state==ERROR_SUCCESS;
        bool advanced=color.advancedColorActive;
        if(!detected){
          // Windows 10 and early Windows 11 do not support INFO_2. DXGI
          // reports actual HDR output, avoiding confusing SDR ACM with HDR.
          detected=queryHdr(source.viewGdiDeviceName,hdr);
          DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO legacy{};
          legacy.header={DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,sizeof(legacy),p.targetInfo.adapterId,p.targetInfo.id};
          if(DisplayConfigGetDeviceInfo(&legacy.header)==ERROR_SUCCESS)advanced=legacy.advancedColorEnabled;
        }
        if (!first)
          std::cout << ',';
        first = false;
        std::cout << "{\"device\":" << json(source.viewGdiDeviceName)
                  << ",\"hdr\":" << (hdr ? "true" : "false") << ",\"detected\":"
                  << (detected ? "true" : "false")
                  << ",\"advancedColor\":"
                  << (advanced ? "true" : "false")
                  << '}';
      }
  }
  std::cout << "]}";
  CoUninitialize();
  return 0;
}
