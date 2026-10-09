// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include "adaptive_filter.hpp"
#include "online_training.hpp"
#include "flat_sweep.hpp"
#include <iostream>
#include <iterator>
int main(int argc,char** argv) {
 try {
  std::ifstream source("../native/adaptive_filter.hlsl");std::string shader((std::istreambuf_iterator<char>(source)),{});
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  AdaptiveFilter::require(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
  HsvZeroRegions zeros;zeros.observe(120,.75,250,.4);if(!zeros.skip(120,.5,100,.2) || !zeros.skip(240,.25,0,1) || zeros.skip(120,1,100,.2) || zeros.skip(0,.5,100,.2) || zeros.skip(120,.5,300,.2) || zeros.skip(120,.5,100,.6))throw std::runtime_error("HSV zero dominance failed");
  if(zeros.skip(120,.5,100,.2,false) || zeros.skip(0,0,100,.2,false) || !zeros.skip(120,.75,100,.2,false) || !zeros.skip(0,1,0,1,false))throw std::runtime_error("No-white-subpixel pruning crossed saturation levels");
  OnlineTraining training;training.enabled=true;training.histogram=true;training.dimensions=1152;std::vector<double> anchor(1152);anchor[0]=1;training.observe(anchor,500);training.observe(anchor,500);if(training.points.size()!=1 || std::abs(training.predict(anchor)-std::log(2.))>1e-10)throw std::runtime_error("Histogram online updates failed");
  OnlineTraining loaded;loaded.load("smooth-hsv-test-config/grid/plan.csv.online.bin");if(!loaded.enabled || !loaded.histogram || loaded.dimensions!=1152)throw std::runtime_error("Histogram seed loading failed");loaded.load("smooth-hsv-test-config/no-validation-seed");if(loaded.enabled || loaded.histogram)throw std::runtime_error("Training state leaked into validation");
  AdaptiveFilter filter;filter.initialize(device.Get(),L"smooth-hsv-test-config",shader.c_str());
  std::ifstream fixtures("smooth-hsv-test-config/scenes.bin",std::ios::binary);unsigned count,width,height;fixtures.read((char*)&count,4);fixtures.read((char*)&width,4);fixtures.read((char*)&height,4);
  filter.samplingCells=width*height;
  D3D11_TEXTURE2D_DESC td{};td.Width=width;td.Height=height;td.MipLevels=td.ArraySize=td.SampleDesc.Count=1;td.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;AdaptiveFilter::require(device->CreateTexture2D(&td,nullptr,&texture));
  auto readBuffer=[&](ID3D11Buffer* buffer) {D3D11_BUFFER_DESC bd{};buffer->GetDesc(&bd);bd.Usage=D3D11_USAGE_STAGING;bd.BindFlags=bd.MiscFlags=bd.StructureByteStride=0;bd.CPUAccessFlags=D3D11_CPU_ACCESS_READ;ComPtr<ID3D11Buffer> staging;AdaptiveFilter::require(device->CreateBuffer(&bd,nullptr,&staging));context->CopyResource(staging.Get(),buffer);D3D11_MAPPED_SUBRESOURCE mapped{};AdaptiveFilter::require(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));std::vector<float> values(bd.ByteWidth/4);std::copy_n((float*)mapped.pData,values.size(),values.begin());context->Unmap(staging.Get(),0);return values;};
  for(unsigned i=0;i<count;i++) {
   double expected;std::vector<double> features(1152);std::vector<float> pixels(width*height*4);
   fixtures.read((char*)&expected,8);fixtures.read((char*)features.data(),features.size()*8);fixtures.read((char*)pixels.data(),pixels.size()*4);if(!fixtures)throw std::runtime_error("Missing fixture");
   context->UpdateSubresource(texture.Get(),0,nullptr,pixels.data(),width*16,0);
   RECT full{0,0,LONG(width),LONG(height)};filter.prepare(device.Get(),context.Get(),texture.Get(),texture.Get(),&full,1,nullptr,0,true,nullptr);
   auto gpu=readBuffer(filter.hsvA.buffer.Get());double distance=0,mass=0;
   for(unsigned j=0;j<1152;j++){double delta=gpu[j*4]-features[j];distance+=delta*delta;mass+=gpu[j*4];}
   double prediction=readBuffer(filter.result.Get())[0];
   std::cout<<"fixture "<<i<<": feature distance="<<std::sqrt(distance)<<", mass="<<mass<<", gain="<<prediction<<", CPU="<<expected<<"\n";
   if(std::sqrt(distance)>1e-6 || std::abs(mass-1)>1e-4 || std::abs(prediction-expected)>1e-4)throw std::runtime_error("GPU histogram/prediction differs from calibration");
  }
  std::cout<<"Smoothed HSV GPU agreement passed\n";
  if(argc>1) {
   AdaptiveFilter benchmark;benchmark.initialize(device.Get(),L"smooth-hsv-benchmark",shader.c_str());benchmark.samplingCells=60000;
   td.Width=2560;td.Height=1440;ComPtr<ID3D11Texture2D> big;AdaptiveFilter::require(device->CreateTexture2D(&td,nullptr,&big));
   std::vector<float> pixels(td.Width*td.Height*4,.5f);context->UpdateSubresource(big.Get(),0,nullptr,pixels.data(),td.Width*16,0);
   auto timing=[&](unsigned iterations) {
    D3D11_QUERY_DESC desc{D3D11_QUERY_TIMESTAMP_DISJOINT,0};ComPtr<ID3D11Query> disjoint,start,end;AdaptiveFilter::require(device->CreateQuery(&desc,&disjoint));desc.Query=D3D11_QUERY_TIMESTAMP;AdaptiveFilter::require(device->CreateQuery(&desc,&start));AdaptiveFilter::require(device->CreateQuery(&desc,&end));
    context->Begin(disjoint.Get());context->End(start.Get());
    for(unsigned iteration=0;iteration<iterations;iteration++)benchmark.prepare(device.Get(),context.Get(),big.Get(),big.Get(),nullptr,0,nullptr,0,true,nullptr);
    context->End(end.Get());context->End(disjoint.Get());context->Flush();D3D11_QUERY_DATA_TIMESTAMP_DISJOINT info{};UINT64 a=0,b=0;
    while(context->GetData(disjoint.Get(),&info,sizeof(info),0)==S_FALSE)Sleep(1);
    AdaptiveFilter::require(context->GetData(start.Get(),&a,sizeof(a),0));AdaptiveFilter::require(context->GetData(end.Get(),&b,sizeof(b),0));
    if(info.Disjoint)throw std::runtime_error("GPU timestamp clock changed; repeat measurement");return 1000.*double(b-a)/info.Frequency/iterations;
   };
   double first=timing(1);timing(16);double warm=timing(32);
   std::cout<<"1440p, 60000 samples, "<<benchmark.config.count<<" anchors: first="<<first<<" ms, warm="<<warm<<" ms/frame; fraction of 500Hz frame="<<warm/2*100<<"%\n";
  }
  return 0;
 } catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}
}
