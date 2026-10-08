#pragma once
#include <d3d11_1.h>
#include <wrl/client.h>
#include <stdexcept>
#include <cstdio>
// Render-thread-only isolation of all graphics/compute bindings, not a partial
// hand-written list. Never call from DllMain or a remote control thread.
struct DwmContextState {
 Microsoft::WRL::ComPtr<ID3D11Device> owner;
 Microsoft::WRL::ComPtr<ID3D11DeviceContext1> context;
 Microsoft::WRL::ComPtr<ID3DDeviceContextState> scratch;
 void initialize(ID3D11Device* device,ID3D11DeviceContext* immediate) {
  if(owner.Get()==device)return;
  Microsoft::WRL::ComPtr<ID3D11Device1> d;
  if(FAILED(device->QueryInterface(IID_PPV_ARGS(&d)))||FAILED(immediate->QueryInterface(IID_PPV_ARGS(&context))))throw std::runtime_error("DWM context state isolation unavailable");
  auto actual=device->GetFeatureLevel();auto level=actual>D3D_FEATURE_LEVEL_11_1?D3D_FEATURE_LEVEL_11_1:actual;
  UINT flags=(device->GetCreationFlags()&D3D11_CREATE_DEVICE_SINGLETHREADED)?D3D11_1_CREATE_DEVICE_CONTEXT_STATE_SINGLETHREADED:0;
  scratch.Reset();HRESULT hr=d->CreateDeviceContextState(flags,&level,1,D3D11_SDK_VERSION,__uuidof(ID3D11Device),nullptr,&scratch);
  if(FAILED(hr)){char message[192];sprintf_s(message,"Cannot isolate DWM context: HRESULT %08lx, device flags %x, level %x, state flags %x",hr,device->GetCreationFlags(),unsigned(level),flags);throw std::runtime_error(message);}
  owner=device;
 }
 struct Scope {
  DwmContextState& state;Microsoft::WRL::ComPtr<ID3DDeviceContextState> previous;
  explicit Scope(DwmContextState& s):state(s){state.context->SwapDeviceContextState(state.scratch.Get(),&previous);}
  ~Scope(){state.context->SwapDeviceContextState(previous.Get(),nullptr);}
 };
};
