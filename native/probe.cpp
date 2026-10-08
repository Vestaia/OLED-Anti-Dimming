#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <dshow.h>
#include <dvdmedia.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <fstream>
#include <iostream>
#include <sstream>
#include <vector>
#include <mutex>
#include <atomic>
#include <regex>
using Microsoft::WRL::ComPtr;
void check(HRESULT hr, const char* what) { if(FAILED(hr)) { std::ostringstream s; s<<what<<" HRESULT 0x"<<std::hex<<hr; throw std::runtime_error(s.str()); } }
// ABI declarations for the legacy SDK Sample Grabber (qedit.h is no longer shipped).
struct __declspec(uuid("6B652FFF-11FE-4FCE-92AD-0266B5D7C78F")) Grabber : IUnknown {
 virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL)=0;
 virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE*)=0;
 virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType(AM_MEDIA_TYPE*)=0;
 virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL)=0;
 virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long*,long*)=0;
 virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample**)=0;
 virtual HRESULT STDMETHODCALLTYPE SetCallback(IUnknown*,long)=0;
};
const GUID GrabberCLSID={0xc1f400a0,0x3f08,0x11d3,{0x9f,0x0b,0x00,0x60,0x08,0x03,0x9e,0x37}};
const GUID NullRendererCLSID={0xc1f400a4,0x3f08,0x11d3,{0x9f,0x0b,0x00,0x60,0x08,0x03,0x9e,0x37}};
struct __declspec(uuid("0579154A-2B53-4994-B0D0-E773148EFF85")) GrabCallback : IUnknown {
 virtual HRESULT STDMETHODCALLTYPE SampleCB(double,IMediaSample*)=0;
 virtual HRESULT STDMETHODCALLTYPE BufferCB(double,BYTE*,long)=0;
};
struct CaptureCallback final : GrabCallback {
 std::atomic<ULONG> refs{1};std::mutex mutex;std::vector<unsigned char> latest;uint64_t sequence=0;double time=0;ULONGLONG arrival=0;
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {if(id==IID_IUnknown||id==__uuidof(GrabCallback)){*out=static_cast<GrabCallback*>(this);AddRef();return S_OK;}*out=nullptr;return E_NOINTERFACE;}
 ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
 ULONG STDMETHODCALLTYPE Release() override{ULONG n=--refs;if(!n)delete this;return n;}
 HRESULT STDMETHODCALLTYPE SampleCB(double t,IMediaSample* sample) override {BYTE* p=nullptr;if(FAILED(sample->GetPointer(&p)))return S_OK;long n=sample->GetActualDataLength();if(n<=0)return S_OK;std::lock_guard<std::mutex> lock(mutex);latest.assign(p,p+n);time=t;arrival=GetTickCount64();++sequence;return S_OK;}
 HRESULT STDMETHODCALLTYPE BufferCB(double,BYTE*,long) override{return E_NOTIMPL;}
};
std::ofstream logFile("session.log"), samples("samples.csv");
void log(const std::string& s) { logFile<<s<<std::endl; std::cout<<s<<std::endl; }
std::string utf8(const std::wstring& s) { int n=WideCharToMultiByte(CP_UTF8,0,s.data(),int(s.size()),nullptr,0,nullptr,nullptr); std::string out(n,'\0');if(n)WideCharToMultiByte(CP_UTF8,0,s.data(),int(s.size()),out.data(),n,nullptr,nullptr);return out; }
struct Camera {
 ComPtr<IGraphBuilder> graph; ComPtr<ICaptureGraphBuilder2> builder;
 ComPtr<IBaseFilter> source, filter, sink; ComPtr<Grabber> grab;
 ComPtr<IMediaControl> control; ComPtr<IAMCameraControl> cc; ComPtr<IAMVideoProcAmp> vp;
 long oldExp=0,oldExpFlags=0,oldWB=0,oldWBFlags=0,oldGain=0,oldGainFlags=0;
 bool saveExp=false,saveWB=false,saveGain=false,manualExp=false,manualWB=false;
 bool externalExposure=false,externalWhiteBalance=false,exposureApi=false;
 bool fixedExposure() const {return manualExp||externalExposure;}
 bool fixedWhiteBalance() const {return manualWB||externalWhiteBalance;}
 // DirectShow exposure is log2(seconds): -5 is 1/32 s, fitting a 30 fps frame.
 long exp=-5,wb=6500,emin=0,emax=0,estep=1; BITMAPINFO bmi{}; std::vector<unsigned char> bytes;
 ComPtr<CaptureCallback> callback;uint64_t sequence=0;double sampleTime=0;ULONGLONG arrival=0;
 void exposure(long value) {
  if(externalExposure||!exposureApi||!cc || !saveExp) return;
  value=std::clamp(value,emin,emax); value=emin+((value-emin)/std::max(1L,estep))*std::max(1L,estep);
  HRESULT hr=cc->Set(CameraControl_Exposure,value,CameraControl_Flags_Manual);
  long flags=0; if(SUCCEEDED(hr)) hr=cc->Get(CameraControl_Exposure,&exp,&flags);
  manualExp=SUCCEEDED(hr)&&(flags&CameraControl_Flags_Manual);
  log("Exposure="+std::to_string(exp)+" fixed="+std::to_string(manualExp));
  if(!manualExp) throw std::runtime_error("Camera could not retain manual exposure; lock exposure externally and restart calibration");
 }
 static void freeMedia(AM_MEDIA_TYPE* mt){if(!mt)return;if(mt->pbFormat)CoTaskMemFree(mt->pbFormat);if(mt->pUnk)mt->pUnk->Release();CoTaskMemFree(mt);}
 void selectCaptureFormat(){
  ComPtr<IAMStreamConfig> config;
  check(builder->FindInterface(&PIN_CATEGORY_CAPTURE,&MEDIATYPE_Video,source.Get(),IID_IAMStreamConfig,reinterpret_cast<void**>(config.GetAddressOf())),"Camera capture format configuration");
  int count=0,size=0;check(config->GetNumberOfCapabilities(&count,&size),"Camera capture capabilities");
  if(size<=0||size>1024*1024)throw std::runtime_error("Invalid camera capture capabilities");
  std::vector<BYTE> caps(size);
  struct Mode {AM_MEDIA_TYPE* media;long pixels;REFERENCE_TIME interval;bool low;};
  std::vector<Mode> modes;
  for(int i=0;i<count;i++){
   AM_MEDIA_TYPE* mt=nullptr;
   if(FAILED(config->GetStreamCaps(i,&mt,caps.data()))){freeMedia(mt);continue;}
   BITMAPINFOHEADER* bitmap=nullptr;REFERENCE_TIME interval=0;
   if(mt&&mt->pbFormat&&mt->formattype==FORMAT_VideoInfo&&mt->cbFormat>=sizeof(VIDEOINFOHEADER)){auto info=reinterpret_cast<VIDEOINFOHEADER*>(mt->pbFormat);bitmap=&info->bmiHeader;interval=info->AvgTimePerFrame;}
   if(mt&&mt->pbFormat&&mt->formattype==FORMAT_VideoInfo2&&mt->cbFormat>=sizeof(VIDEOINFOHEADER2)){auto info=reinterpret_cast<VIDEOINFOHEADER2*>(mt->pbFormat);bitmap=&info->bmiHeader;interval=info->AvgTimePerFrame;}
   if(bitmap&&bitmap->biWidth>0&&bitmap->biWidth<=16384&&std::abs(bitmap->biHeight)>0&&std::abs(bitmap->biHeight)<=16384)
    modes.push_back({mt,bitmap->biWidth*std::abs(bitmap->biHeight),interval>0?interval:MAXLONGLONG,bitmap->biWidth<=854&&std::abs(bitmap->biHeight)<=480});
   else freeMedia(mt);
  }
  // Prefer largest mode within 480p; otherwise the lowest usable resolution.
  std::stable_sort(modes.begin(),modes.end(),[](const Mode& a,const Mode& b){if(a.low!=b.low)return a.low;return a.pixels!=b.pixels?(a.low?a.pixels>b.pixels:a.pixels<b.pixels):a.interval<b.interval;});
  bool selected=false;
  for(auto& mode:modes){if(!selected&&SUCCEEDED(config->SetFormat(mode.media)))selected=true;freeMedia(mode.media);}
  if(!selected)throw std::runtime_error("Camera has no usable advertised capture mode");
 }
 void open() {
  ComPtr<ICreateDevEnum> dev; ComPtr<IEnumMoniker> en;
  check(CoCreateInstance(CLSID_SystemDeviceEnum,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dev)),"Camera enumerator");
  if(dev->CreateClassEnumerator(CLSID_VideoInputDeviceCategory,&en,0)!=S_OK) throw std::runtime_error("No webcam found");
  wchar_t requestedCamera[256]{};GetEnvironmentVariableW(L"OLED_CALIBRATION_CAMERA",requestedCamera,256);
  ComPtr<IMoniker> m,first;
  while(en->Next(1,&m,nullptr)==S_OK) {
   ComPtr<IPropertyBag> bag; VARIANT v; VariantInit(&v);
   check(m->BindToStorage(nullptr,nullptr,IID_PPV_ARGS(&bag)),"Camera name");
   bag->Read(L"FriendlyName",&v,nullptr);
   std::wstring name=v.vt==VT_BSTR?v.bstrVal:L""; VariantClear(&v);
  log("Camera: "+utf8(name));
   if(!first)first=m;
   if(requestedCamera[0]?name.find(requestedCamera)!=std::wstring::npos:(name.find(L"Brio")!=std::wstring::npos||name.find(L"BRIO")!=std::wstring::npos)) {
    check(m->BindToObject(nullptr,nullptr,IID_PPV_ARGS(&source)),"Open Brio"); break;
   } m.Reset();
  }
  if(!source&&first&&!requestedCamera[0])check(first->BindToObject(nullptr,nullptr,IID_PPV_ARGS(&source)),"Open default webcam");
  if(!source) throw std::runtime_error("Requested webcam not found");
  source.As(&cc); source.As(&vp);
  if(cc) {
   saveExp=SUCCEEDED(cc->Get(CameraControl_Exposure,&oldExp,&oldExpFlags));
   long def=0,flags=0;
   if(saveExp&&SUCCEEDED(cc->GetRange(CameraControl_Exposure,&emin,&emax,&estep,&def,&flags))&&(flags&CameraControl_Flags_Manual)&&emin<=emax) {
    exposureApi=true;
    log("Exposure range="+std::to_string(emin)+".."+std::to_string(emax)+" step="+std::to_string(estep));
    // Probe support without aborting: drivers may advertise manual mode but reject it.
    long requested=emin+((std::clamp(-5L,emin,emax)-emin)/std::max(1L,estep))*std::max(1L,estep);
    HRESULT result=cc->Set(CameraControl_Exposure,requested,CameraControl_Flags_Manual);
    if(SUCCEEDED(result))result=cc->Get(CameraControl_Exposure,&exp,&flags);
    manualExp=SUCCEEDED(result)&&(flags&CameraControl_Flags_Manual);
    exposureApi=manualExp;
    log("Initial exposure="+std::to_string(exp)+" fixed="+std::to_string(manualExp)+" HRESULT="+std::to_string(result));
   }
  }
  if(vp) {
   saveWB=SUCCEEDED(vp->Get(VideoProcAmp_WhiteBalance,&oldWB,&oldWBFlags));
   long lo=0,hi=0,step=1,def=0,flags=0;
   if(saveWB&&SUCCEEDED(vp->GetRange(VideoProcAmp_WhiteBalance,&lo,&hi,&step,&def,&flags))) {
    wb=lo+((std::clamp(6500L,lo,hi)-lo)/std::max(1L,step))*std::max(1L,step);
    HRESULT hr=vp->Set(VideoProcAmp_WhiteBalance,wb,VideoProcAmp_Flags_Manual);
    if(SUCCEEDED(hr)) hr=vp->Get(VideoProcAmp_WhiteBalance,&wb,&flags);
    manualWB=SUCCEEDED(hr)&&(flags&VideoProcAmp_Flags_Manual);
    log("White balance="+std::to_string(wb)+" fixed="+std::to_string(manualWB));
   }
   saveGain=SUCCEEDED(vp->Get(VideoProcAmp_Gain,&oldGain,&oldGainFlags));
   if(saveGain&&SUCCEEDED(vp->GetRange(VideoProcAmp_Gain,&lo,&hi,&step,&def,&flags))) {
    log("Fixed gain minimum HRESULT="+std::to_string(vp->Set(VideoProcAmp_Gain,lo,VideoProcAmp_Flags_Manual)));
   }
  }
  if(!manualExp||!manualWB){
   std::wstring message=L"The camera cannot verify all required manual controls through its API.\n\n";
   if(!manualExp)message+=L"Lock the camera exposure using its own software or hardware controls. Calibration will NOT work with auto exposure enabled. The app cannot adjust exposure for this camera.\n\n";
   if(!manualWB)message+=L"Also lock white balance externally; automatic white balance can invalidate measurements.\n\n";
   message+=L"Click OK only after these settings are locked, or Cancel to stop calibration.";
   if(MessageBoxW(nullptr,message.c_str(),L"Confirm fixed camera settings",MB_OKCANCEL|MB_ICONWARNING|MB_SETFOREGROUND|MB_TASKMODAL)!=IDOK)throw std::runtime_error("Camera lock confirmation cancelled; calibration not started");
   externalExposure=!manualExp;externalWhiteBalance=!manualWB;
   log("Camera controls externally locked by user confirmation; unsupported API adjustments disabled");
  }
  check(CoCreateInstance(CLSID_FilterGraph,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&graph)),"Graph");
  check(CoCreateInstance(CLSID_CaptureGraphBuilder2,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&builder)),"Capture builder");
  check(builder->SetFiltergraph(graph.Get()),"Set graph"); check(graph->AddFilter(source.Get(),L"Brio"),"Add camera");
  selectCaptureFormat();
  check(CoCreateInstance(GrabberCLSID,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&filter)),"Sample grabber");
  check(filter.As(&grab),"Grabber interface"); AM_MEDIA_TYPE mt{}; mt.majortype=MEDIATYPE_Video; mt.subtype=MEDIASUBTYPE_RGB24;
  check(grab->SetMediaType(&mt),"RGB24 capture");callback.Attach(new CaptureCallback);check(grab->SetCallback(callback.Get(),0),"Timestamped camera callback");
  check(graph->AddFilter(filter.Get(),L"Grabber"),"Add grabber");
  check(CoCreateInstance(NullRendererCLSID,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&sink)),"Null renderer");
  check(graph->AddFilter(sink.Get(),L"Sink"),"Add sink");
  check(builder->RenderStream(&PIN_CATEGORY_CAPTURE,&MEDIATYPE_Video,source.Get(),filter.Get(),sink.Get()),"Capture connection");
  check(grab->GetConnectedMediaType(&mt),"Capture format");
  if(mt.pbFormat&&mt.formattype==FORMAT_VideoInfo&&mt.cbFormat>=sizeof(VIDEOINFOHEADER))bmi.bmiHeader=reinterpret_cast<VIDEOINFOHEADER*>(mt.pbFormat)->bmiHeader;
  else if(mt.pbFormat&&mt.formattype==FORMAT_VideoInfo2&&mt.cbFormat>=sizeof(VIDEOINFOHEADER2))bmi.bmiHeader=reinterpret_cast<VIDEOINFOHEADER2*>(mt.pbFormat)->bmiHeader;
  else throw std::runtime_error("Unsupported camera format");
  CoTaskMemFree(mt.pbFormat); if(mt.pUnk) mt.pUnk->Release();
  if(bmi.bmiHeader.biWidth<=0||std::abs(bmi.bmiHeader.biHeight)<=0)throw std::runtime_error("Invalid negotiated camera resolution");
  check(graph.As(&control),"Camera control"); check(control->Run(),"Start camera");
  log("Capture="+std::to_string(bmi.bmiHeader.biWidth)+"x"+std::to_string(std::abs(bmi.bmiHeader.biHeight)));
 }
 bool read() {
  if(!callback)return false;std::lock_guard<std::mutex> lock(callback->mutex);if(sequence==callback->sequence)return false;
  bytes=callback->latest;sequence=callback->sequence;sampleTime=callback->time;arrival=callback->arrival;return !bytes.empty();
 }
 ~Camera() {
  if(control) control->Stop();
  if(saveExp&&!externalExposure) cc->Set(CameraControl_Exposure,oldExp,oldExpFlags);
  if(saveWB&&!externalWhiteBalance) vp->Set(VideoProcAmp_WhiteBalance,oldWB,oldWBFlags);
  if(saveGain) vp->Set(VideoProcAmp_Gain,oldGain,oldGainFlags);
 }
} camera;
struct Params { float nits=1000,area=.01f,hue=0,saturation=1; unsigned mode=0,color=3; float probeNits=100, pad=0; unsigned width=2560,height=1440,mosaic=0,reserved=0; } params;
ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGISwapChain3> swap;
ComPtr<ID3D11RenderTargetView> target; ComPtr<ID3D11VertexShader> vs; ComPtr<ID3D11PixelShader> ps; ComPtr<ID3D11Buffer> cb;
ComPtr<ID3D11ShaderResourceView> sceneView;ComPtr<ID3D11SamplerState> sceneSampler;std::string scenePath;
void loadScene(const std::string& path){if(path==scenePath)return;std::ifstream f(path,std::ios::binary);unsigned w=0,h=0;f.read((char*)&w,4);f.read((char*)&h,4);if(!f||!w||!h||w>4096||h>4096)throw std::runtime_error("Invalid scene texture: "+path);std::vector<float> pixels(size_t(w)*h*4);f.read((char*)pixels.data(),pixels.size()*4);if(!f)throw std::runtime_error("Incomplete scene: "+path);D3D11_TEXTURE2D_DESC d{};d.Width=w;d.Height=h;d.MipLevels=d.ArraySize=1;d.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;d.SampleDesc.Count=1;d.Usage=D3D11_USAGE_IMMUTABLE;d.BindFlags=D3D11_BIND_SHADER_RESOURCE;D3D11_SUBRESOURCE_DATA data{pixels.data(),w*16,0};ComPtr<ID3D11Texture2D> t;check(device->CreateTexture2D(&d,&data,&t),"Scene texture");sceneView.Reset();check(device->CreateShaderResourceView(t.Get(),nullptr,&sceneView),"Scene view");scenePath=path;}
HWND surface=nullptr,preview=nullptr; RECT displayRect{},roi{}; bool dragging=false,roiReady=false,managedCalibration=false; POINT dragStart{};
double meanRGB[3]{},p99=0,clipped=0; bool frameValid=false; ULONGLONG changed=0; int autoSteps=0,autoColor=0; double autoPeak=0; bool autoExposure=false;
const char* shader=R"(
cbuffer Params:register(b0){float nits,area,hue,saturation;uint mode,color;float probeNits,pad;uint width,height,mosaic,reserved;};
Texture2D<float4> scene:register(t0);SamplerState sceneSampler:register(s0);
struct V{float4 p:SV_Position;float2 uv:TEXCOORD;};
V vertex(uint i:SV_VertexID){V o;o.uv=float2((i<<1)&2,i&2);o.p=float4(o.uv*float2(2,-2)+float2(-1,1),0,1);return o;}
float3 hsv(float h,float s){float3 p=abs(frac(h+float3(0,2./3.,1./3.))*6-3);return lerp(float3(1,1,1),saturate(p-1),s);}
float3 pq(float3 x){float3 a=pow(max(x/10000,0),2610./16384.);return pow((3424./4096.+2413./128.*a)/(1+2392./128.*a),2523./32.);}
float3 palette(uint c){return c==0?float3(1,0,0):c==1?float3(0,1,0):c==2?float3(0,0,1):c==3?float3(1,1,1):c==5?float3(0,1,1):c==6?float3(1,0,1):c==7?float3(1,1,0):hsv(hue,saturation);}
uint hashPixel(uint x){x^=x>>16;x*=0x7feb352d;x^=x>>15;x*=0x846ca68b;x^=x>>16;return x;}
uint probeSide(){return max(2,min(min(width,height)/2*2,(uint)(sqrt((float)width*height*.01)/2)*2));}
bool inProbe(V i){if(mosaic==0)return all(abs(i.uv-.5)<=.05);uint side=probeSide();int2 origin=(int2(width,height)-int(side))/2;int2 p=int2(i.p.xy)-origin;return all(p>=0)&&all(p<int(side));}
float3 probeColor(V i,float3 legacy){if(mosaic==0)return legacy;uint side=probeSide();int2 origin=(int2(width,height)-int(side))/2;uint2 p=uint2(int2(i.p.xy)-origin);uint key=p.x/2+p.y*4099;float3 v=float3(hashPixel(key)&65535,hashPixel(key+0x9e3779b9)&65535,hashPixel(key+2*0x9e3779b9)&65535)/65535.*2;return (p.x&1)?2-v:v;}
float4 pixel(V i):SV_Target{
 float3 c=palette(color);
 float3 light=0; float2 d=abs(i.uv-.5); bool inside=all(d<=sqrt(area)*.5);
 if(mode==0)light=inside?c*nits:0;
 if(mode==1){float code=saturate(i.uv.x);return float4(code.xxx,1);}
 if(mode==2)light=hsv(i.uv.x,i.uv.y)*nits;
 if(mode==3)light=inside?(((uint)(i.uv.x*16)+(uint)(i.uv.y*16))%2?c:hsv(frac(hue+.5),saturation))*nits:0;
 if(mode==4){light=inside?float3(nits,nits,nits):0;if(inProbe(i))light=probeColor(i,float3(1,1,1))*probeNits;}
 if(mode==5||mode==6){float3 surround=mode==5?c:(((uint)(i.uv.x*16)+(uint)(i.uv.y*16))%2?c:palette((uint)pad));light=inside?surround*nits:0;if(inProbe(i))light=probeColor(i,float3(1,1,1))*probeNits;}
 if(mode==7){light=inside?c*nits:0;if(inProbe(i))light=probeColor(i,c)*probeNits;}
 if(mode==8){float2 uv=(i.uv-.5)/sqrt(area)+.5;light=inside?scene.SampleLevel(sceneSampler,uv,0).rgb*nits:0;if(inProbe(i))light=probeColor(i,c)*probeNits;}
 if(mosaic!=0&&(mode==4||mode==7)&&area<=.010001&&!inProbe(i))light=0;
 return float4(pq(light),1);
})";
std::wstring friendlyName(const wchar_t* gdi) {
 UINT n=0,m=0;if(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,&n,&m)!=ERROR_SUCCESS)return L"";
 std::vector<DISPLAYCONFIG_PATH_INFO> paths(n);std::vector<DISPLAYCONFIG_MODE_INFO> modes(m);
 if(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,&n,paths.data(),&m,modes.data(),nullptr)!=ERROR_SUCCESS)return L"";
 for(UINT i=0;i<n;++i){DISPLAYCONFIG_SOURCE_DEVICE_NAME s{};s.header={DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,sizeof(s),paths[i].sourceInfo.adapterId,paths[i].sourceInfo.id};if(DisplayConfigGetDeviceInfo(&s.header)!=ERROR_SUCCESS||wcscmp(gdi,s.viewGdiDeviceName))continue;
 DISPLAYCONFIG_TARGET_DEVICE_NAME t{};t.header={DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,sizeof(t),paths[i].targetInfo.adapterId,paths[i].targetInfo.id};if(DisplayConfigGetDeviceInfo(&t.header)==ERROR_SUCCESS)return t.monitorFriendlyDeviceName;}
 return L"";
}
void initGraphics() {
 ComPtr<IDXGIFactory2> factory; check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),"DXGI factory");
 ComPtr<IDXGIAdapter1> chosen; DXGI_OUTPUT_DESC1 chosenDesc{}; bool found=false;
 bool oledFound=false,oledHdr=false;wchar_t requestedDisplay[256]{};GetEnvironmentVariableW(L"OLED_CALIBRATION_DISPLAY",requestedDisplay,256);
 for(UINT a=0;;++a) {
  ComPtr<IDXGIAdapter1> adapter; if(factory->EnumAdapters1(a,&adapter)==DXGI_ERROR_NOT_FOUND) break;
  for(UINT o=0;;++o) {
   ComPtr<IDXGIOutput> output; if(adapter->EnumOutputs(o,&output)==DXGI_ERROR_NOT_FOUND) break;
   ComPtr<IDXGIOutput6> out6; if(FAILED(output.As(&out6))) continue; DXGI_OUTPUT_DESC1 d{}; check(out6->GetDesc1(&d),"Display description");
   std::wstring name=std::wstring(d.DeviceName)+L" "+friendlyName(d.DeviceName);
   log("Output "+utf8(name)+" HDR="+std::to_string(d.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)+" peak metadata="+std::to_string(d.MaxLuminance));
   bool oled=name.find(L"XG27AQWMG")!=std::wstring::npos;
   if(oled){oledFound=true;oledHdr=d.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;}
   if(d.AttachedToDesktop&&d.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020&&(requestedDisplay[0]?std::wstring(d.DeviceName)==requestedDisplay:(oled||!found))){chosen=adapter;chosenDesc=d;found=true;}
  }
 }
 if(!requestedDisplay[0]&&oledFound&&!oledHdr)throw std::runtime_error("The ROG OLED has Windows HDR disabled. Enable HDR on that display, then relaunch. Other monitors will not be substituted.");
 if(!found) throw std::runtime_error("No HDR-enabled output found. Enable HDR on the OLED in Windows Settings, then retry.");
 displayRect=chosenDesc.DesktopCoordinates;
 wchar_t mosaicSetting[8]{};GetEnvironmentVariableW(L"OLED_CALIBRATION_MOSAIC",mosaicSetting,8);params.mosaic=mosaicSetting[0]==L'1';
 log(params.mosaic?"Reference probe: fixed per-pixel RGB mosaic, 100-nit mean linear signal":"Reference probe: legacy solid color (existing calibration)");
 log("Calibration display: "+utf8(chosenDesc.DeviceName));
 check(D3D11CreateDevice(chosen.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context),"D3D11 device");
 surface=CreateWindowExW(WS_EX_TOPMOST,L"PQProbe",L"HDR PQ surface",WS_POPUP,displayRect.left,displayRect.top,displayRect.right-displayRect.left,displayRect.bottom-displayRect.top,nullptr,nullptr,GetModuleHandle(nullptr),nullptr);
 DXGI_SWAP_CHAIN_DESC1 d{}; d.Width=displayRect.right-displayRect.left;d.Height=displayRect.bottom-displayRect.top;
 d.Format=DXGI_FORMAT_R10G10B10A2_UNORM;d.SampleDesc.Count=1;d.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;d.BufferCount=2;d.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
 ComPtr<IDXGISwapChain1> s;check(factory->CreateSwapChainForHwnd(device.Get(),surface,&d,nullptr,nullptr,&s),"HDR swap chain"); check(s.As(&swap),"Swap chain3");
 UINT support=0;check(swap->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,&support),"PQ support");
 if(!(support&DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT))throw std::runtime_error("PQ presentation unsupported");
 check(swap->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020),"Set PQ BT.2020");
 ComPtr<ID3D11Texture2D> back;check(swap->GetBuffer(0,IID_PPV_ARGS(&back)),"Back buffer");check(device->CreateRenderTargetView(back.Get(),nullptr,&target),"Render target");
 ComPtr<ID3DBlob> v,p,errors;
 HRESULT hr=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vertex","vs_5_0",0,0,&v,&errors);if(FAILED(hr)&&errors)log(static_cast<char*>(errors->GetBufferPointer()));check(hr,"Vertex shader");
 errors.Reset();hr=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"pixel","ps_5_0",0,0,&p,&errors);if(FAILED(hr)&&errors)log(static_cast<char*>(errors->GetBufferPointer()));check(hr,"Pixel shader");
 check(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs),"Create VS");check(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&ps),"Create PS");
 D3D11_BUFFER_DESC bd{};bd.ByteWidth=sizeof(Params);bd.Usage=D3D11_USAGE_DEFAULT;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;check(device->CreateBuffer(&bd,nullptr,&cb),"Constants");
 D3D11_SAMPLER_DESC sampler{};sampler.Filter=D3D11_FILTER_MIN_MAG_MIP_POINT;sampler.AddressU=sampler.AddressV=sampler.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sampler.MaxLOD=D3D11_FLOAT32_MAX;check(device->CreateSamplerState(&sampler,&sceneSampler),"Scene sampler");
 factory->MakeWindowAssociation(surface,DXGI_MWA_NO_ALT_ENTER);ShowWindow(surface,SW_SHOW);ShowWindow(surface,SW_SHOW);
 log("HDR10 surface ready: "+std::to_string(d.Width)+"x"+std::to_string(d.Height)+"; requested signal range 0..10000 nits, not measured panel output");
}
void render(bool verify=false) {
 params.width=displayRect.right-displayRect.left;params.height=displayRect.bottom-displayRect.top;
 context->UpdateSubresource(cb.Get(),0,nullptr,&params,0,0); auto rt=target.Get();context->OMSetRenderTargets(1,&rt,nullptr);
 D3D11_VIEWPORT vp{0,0,float(displayRect.right-displayRect.left),float(displayRect.bottom-displayRect.top),0,1};context->RSSetViewports(1,&vp);
 context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);auto b=cb.Get();context->PSSetConstantBuffers(0,1,&b);auto sv=sceneView.Get();auto ss=sceneSampler.Get();context->PSSetShaderResources(0,1,&sv);context->PSSetSamplers(0,1,&ss);context->Draw(3,0);
 if(verify){
  ComPtr<ID3D11Resource> resource;target->GetResource(&resource);ComPtr<ID3D11Texture2D> texture;check(resource.As(&texture),"Verification texture");D3D11_TEXTURE2D_DESC d{};texture->GetDesc(&d);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
  ComPtr<ID3D11Texture2D> staging;check(device->CreateTexture2D(&d,nullptr,&staging),"Readback texture");context->CopyResource(staging.Get(),texture.Get());D3D11_MAPPED_SUBRESOURCE mapped{};check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped),"Readback map");
  unsigned center=*reinterpret_cast<unsigned*>(static_cast<char*>(mapped.pData)+d.Height/2*mapped.RowPitch+d.Width/2*4);unsigned corner=*reinterpret_cast<unsigned*>(mapped.pData);context->Unmap(staging.Get(),0);
  double l=std::pow(params.nits/10000.,2610./16384.);int expected=int(std::round(1023*std::pow((3424./4096.+2413./128.*l)/(1+2392./128.*l),2523./32.)));
  if(std::abs(int(center&1023)-expected)>1||std::abs(int((center>>10)&1023)-expected)>1||std::abs(int((center>>20)&1023)-expected)>1||(corner&0x3fffffff)!=0)throw std::runtime_error("PQ render readback verification failed");
  log("PASS GPU readback: white center PQ10="+std::to_string(center&1023)+" expected="+std::to_string(expected)+"; corner black=0");
 }
 check(swap->Present(1,0),"Present");
}
void stats() {
 frameValid=false;if(!roiReady||camera.bytes.empty())return;
 int w=camera.bmi.bmiHeader.biWidth,h=std::abs(camera.bmi.bmiHeader.biHeight),stride=(w*3+3)&~3;
 if(camera.bytes.size()<size_t(stride)*h)return;
 RECT r{roi.left*w/640,roi.top*h/360,roi.right*w/640,roi.bottom*h/360};
 if(r.right<=r.left||r.bottom<=r.top)return;std::vector<int> values;double sum[3]{};int clip=0;
 for(int y=r.top;y<r.bottom;++y)for(int x=r.left;x<r.right;++x){int yy=camera.bmi.bmiHeader.biHeight>0?h-1-y:y;auto px=&camera.bytes[yy*stride+x*3];int m=std::max({px[0],px[1],px[2]});values.push_back(m);clip+=m>=250;for(int c=0;c<3;++c)sum[c]+=px[2-c];}
 for(int c=0;c<3;++c)meanRGB[c]=sum[c]/values.size();std::sort(values.begin(),values.end());p99=values[size_t(.99*(values.size()-1))];clipped=double(clip)/values.size();frameValid=true;
}
#include "white_calibration.hpp"
#include "brightness_graph.hpp"
#include "color_calibration.hpp"
#include "hsv_calibration.hpp"
void phaseClockSelfTest(){
 bool previousValid=frameValid;frameValid=false;
 WhiteCalibration test;test.stage=WhiteCalibration::Exposure;test.started=105;test.stimulus=105;test.pendingPresent=false;
 test.tick(100);
 if(test.stage!=WhiteCalibration::Exposure)throw std::runtime_error("Future phase clock caused a false timeout");
 test.started=95;test.tick(100);
 if(test.stage!=WhiteCalibration::Exposure)throw std::runtime_error("Future stimulus clock caused a false timeout");
 test.started=100;test.stimulus=100;test.pendingPresent=true;test.tick(13000);
 if(test.stage!=WhiteCalibration::Exposure)throw std::runtime_error("Unpresented stimulus caused a false camera timeout");
 frameValid=previousValid;
}
void title() {
 wchar_t t[512];swprintf_s(t,L"Brio | %.0f signal nits | area %.1f%% | exp %ld %s | WB %ld %s | ROI p99 %.0f clip %.2f%%",params.nits,params.area*100,camera.exp,camera.manualExp?L"fixed":L"UNVERIFIED",camera.wb,camera.manualWB?L"fixed":L"UNVERIFIED",p99,clipped*100);std::wstring info(white.status.begin(),white.status.end()); SetWindowTextW(preview,(std::wstring(t)+L" | "+info).c_str());
}
LRESULT CALLBACK proc(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp) {
 switch(msg){
 case WM_KEYDOWN:return 0;
 case WM_ERASEBKGND:return 1;
 case WM_LBUTTONDOWN:if(hwnd==preview&&!managedCalibration&&!hsvExperiment.active&&!hsvExperiment.preparing&&GET_X_LPARAM(lp)>=0&&GET_X_LPARAM(lp)<640&&GET_Y_LPARAM(lp)>=0&&GET_Y_LPARAM(lp)<360){if(white.busy())white.stop("Calibration cancelled: ROI changed");dragging=true;dragStart={std::clamp(GET_X_LPARAM(lp),0,639),std::clamp(GET_Y_LPARAM(lp),0,359)};SetCapture(hwnd);}return 0;
 case WM_CAPTURECHANGED:case WM_CANCELMODE:dragging=false;return 0;
 case WM_LBUTTONUP:if(dragging){dragging=false;ReleaseCapture();long x=std::clamp(GET_X_LPARAM(lp),0,640),y=std::clamp(GET_Y_LPARAM(lp),0,360);roi={std::min(x,dragStart.x),std::min(y,dragStart.y),std::max(x,dragStart.x),std::max(y,dragStart.y)};roiReady=roi.right-roi.left>=3&&roi.bottom-roi.top>=3;autoExposure=false;log("ROI selected="+std::to_string(roiReady));}return 0;
  case WM_PAINT:{
   PAINTSTRUCT paint;HDC windowDC=BeginPaint(hwnd,&paint);
   if(hwnd==preview){
    RECT client;GetClientRect(hwnd,&client);
    HDC dc=CreateCompatibleDC(windowDC);
    HBITMAP bitmap=CreateCompatibleBitmap(windowDC,std::max(1L,client.right),std::max(1L,client.bottom));
    auto oldBitmap=SelectObject(dc,bitmap);
    auto background=CreateSolidBrush(RGB(20,20,20));FillRect(dc,&client,background);DeleteObject(background);
    if(!camera.bytes.empty())StretchDIBits(dc,0,0,640,360,0,0,camera.bmi.bmiHeader.biWidth,std::abs(camera.bmi.bmiHeader.biHeight),camera.bytes.data(),&camera.bmi,DIB_RGB_COLORS,SRCCOPY);
    if(roiReady){auto pen=CreatePen(PS_SOLID,2,RGB(0,255,0));auto old=SelectObject(dc,pen);auto brush=SelectObject(dc,GetStockObject(NULL_BRUSH));Rectangle(dc,roi.left,roi.top,roi.right,roi.bottom);SelectObject(dc,brush);SelectObject(dc,old);DeleteObject(pen);}
    SetBkMode(dc,TRANSPARENT);SetTextColor(dc,RGB(230,230,230));
    RECT status{12,372,628,453};std::wstring text(white.status.begin(),white.status.end());
    DrawTextW(dc,text.c_str(),-1,&status,DT_LEFT|DT_WORDBREAK);
    brightnessGraph.draw(dc,GetTickCount64());
    BitBlt(windowDC,0,0,client.right,client.bottom,dc,0,0,SRCCOPY);
    SelectObject(dc,oldBitmap);DeleteObject(bitmap);DeleteDC(dc);
   }
   EndPaint(hwnd,&paint);return 0;
  }
 case WM_DESTROY:PostQuitMessage(0);return 0;
 }return DefWindowProcW(hwnd,msg,wp,lp);
}
struct MeasurementPowerGuard {bool enabled;MeasurementPowerGuard(bool active):enabled(active){if(enabled&&!SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED))throw std::runtime_error("Could not prevent sleep during measurement");}~MeasurementPowerGuard(){if(enabled)SetThreadExecutionState(ES_CONTINUOUS);}};
int main(int argc,char** argv) {
 if(argc>1&&std::string(argv[1])=="--self-test"){try{ComPtr<ID3DBlob> probeCode,probeErrors;for(auto entry:{"vertex","pixel"}){HRESULT hr=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,entry,strcmp(entry,"vertex")==0?"vs_5_0":"ps_5_0",0,0,&probeCode,&probeErrors);if(FAILED(hr))throw std::runtime_error(probeErrors?(char*)probeErrors->GetBufferPointer():"Probe shader compilation failed");probeErrors.Reset();}flatSweepSelfTest();phaseClockSelfTest();matcherSelfTest();continuousMatcherSelfTest();fineWindowMatcherSelfTest();timingSelfTest();settlingSelfTest();log("PASS: discrete/continuous matchers, 81 delayed/noisy nonlinear cases, plateau detection, timing and settling");return 0;}catch(const std::exception& e){log(e.what());return 1;}}
 managedCalibration=argc>1&&(std::string(argv[1])=="--calibration-session"||std::string(argv[1])=="--hsv-calibrate");
 SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); HRESULT co=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
 try {
  MeasurementPowerGuard power(managedCalibration);
  check(co,"COM");WNDCLASSW wc{};wc.lpfnWndProc=proc;wc.hInstance=GetModuleHandle(nullptr);wc.lpszClassName=L"PQProbe";wc.hCursor=LoadCursor(nullptr,IDC_ARROW);RegisterClassW(&wc);
  camera.open();initGraphics();render(true);
  // Keep controls off the test display when an alternate monitor is available.
  POINT position{displayRect.left+20,displayRect.top+20};
  EnumDisplayMonitors(nullptr,nullptr,[](HMONITOR h,HDC,LPRECT,LPARAM arg)->BOOL{MONITORINFO m{sizeof(m)};GetMonitorInfoW(h,&m);if(!EqualRect(&m.rcMonitor,&displayRect)){*reinterpret_cast<POINT*>(arg)={m.rcWork.left+20,m.rcWork.top+20};return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&position));
  RECT r{0,0,640,680};AdjustWindowRect(&r,WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX,FALSE);
  preview=CreateWindowExW(WS_EX_TOPMOST,L"PQProbe",L"Brio calibration preview",WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX,position.x,position.y,r.right-r.left,r.bottom-r.top,nullptr,nullptr,wc.hInstance,nullptr);ShowWindow(preview,SW_SHOW);ShowWindow(preview,SW_SHOW);SetForegroundWindow(preview);
  samples<<"host_tick_ms,signal_nits,area,mode,color,hue,saturation,exposure_log2_s,white_balance_kelvin,roi_r,roi_g,roi_b,p99_max_channel,clipped_fraction\n";
  log("Ready; controls in preview. Select ROI before exposure search. Camera frames remain local and are not saved.");
  if(argc>1&&(std::string(argv[1])=="--white-calibrate"||std::string(argv[1])=="--color-calibrate")){colors.autoAfterWhite=std::string(argv[1])=="--color-calibrate";white.begin(true);}
 if(argc>2&&std::string(argv[1])=="--hsv-calibrate")hsvExperiment.prepare(argv[2]);
 bool exitAfter=false;for(int i=1;i<argc;i++)if(std::string(argv[i])=="--exit-after-calibration")exitAfter=true;
 std::string requestPath=(argc>2&&std::string(argv[1])=="--calibration-session")?argv[2]:"";
 std::string resultPath;bool phaseRunning=false;
 ULONGLONG last=0;MSG msg{};bool run=true,firstFrame=true;
  while(run){while(PeekMessage(&msg,nullptr,0,0,PM_REMOVE)){if(msg.message==WM_QUIT){run=false;break;}TranslateMessage(&msg);DispatchMessage(&msg);}if(!run)break;if(white.stage==WhiteCalibration::Replay)white.tick(GetTickCount64());render();white.presented(GetTickCount64());colors.presented(GetTickCount64());hsvExperiment.presented(GetTickCount64());ULONGLONG now=GetTickCount64();if(now-last>=10){last=now;
 if(!requestPath.empty()&&!phaseRunning){
  std::ifstream request(requestPath);
  if(request){std::string plan,output,progress;std::getline(request,plan);std::getline(request,output);std::getline(request,progress);std::getline(request,resultPath);request.close();DeleteFileA(requestPath.c_str());
   if(plan.empty()||output.empty()||resultPath.empty())throw std::runtime_error("Invalid calibration session request");
   hsvExperiment.prepare(plan,output,progress);phaseRunning=true;
  }
 }
 if(phaseRunning&&!hsvExperiment.preparing&&!hsvExperiment.active){std::ofstream result(resultPath);result<<white.status<<std::endl;phaseRunning=false;}
 if(managedCalibration&&(hsvExperiment.active||hsvExperiment.preparing)){ULONGLONG lastFrame=std::max(camera.arrival,hsvExperiment.started);if(now>=lastFrame&&now-lastFrame>12000){white.stop("Camera stream timed out");hsvExperiment.finish("Camera acquisition stopped: no fresh frames for 12 seconds");}}
 if(camera.read()){stats();ULONGLONG sampleNow=GetTickCount64();white.tick(sampleNow);colors.tick(sampleNow);hsvExperiment.tick(sampleNow);if(exitAfter&&!hsvExperiment.preparing&&!hsvExperiment.active){if(white.status.rfind("PASS",0)!=0)throw std::runtime_error(white.status);run=false;}brightnessGraph.sample(camera.arrival);if(firstFrame){log("PASS camera buffer: "+std::to_string(camera.bytes.size())+" bytes; preview capture active");firstFrame=false;}if(autoExposure&&frameValid&&now-changed>=700){autoPeak=std::max(autoPeak,p99);if(autoColor<3){params.color=++autoColor;changed=now;}else{long next=camera.exp;if(autoPeak>235)next-=camera.estep;else if(autoPeak<110)next+=camera.estep;if(next!=camera.exp&&next>=camera.emin&&next<=camera.emax&&autoSteps++<12){camera.exposure(next);autoColor=0;params.color=0;autoPeak=0;changed=now;}else{autoExposure=false;params.color=3;log("Exposure search stopped: R/G/B/W peak="+std::to_string(autoPeak)+"; verify ROI and low-level signal manually");}}}}title();InvalidateRect(preview,nullptr,FALSE);}}
 } catch(const std::exception& e){log(std::string("ERROR: ")+e.what());MessageBoxA(nullptr,e.what(),"HDR test error",MB_ICONERROR);return 1;}
 // Camera destructor restores the original device controls before process termination.
 return 0;
}









