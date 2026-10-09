#include <string>
// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include "adaptive_filter.hpp"
#include "cluster_distance.hpp"
#include <iostream>
#include <iterator>
#include <filesystem>
int main(int argc,char** argv){try{
 std::string folder=argc>1?argv[1]:"cluster-test-config";
 ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> ctx;AdaptiveFilter::require(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&ctx));
 std::ifstream shaderFile("../native/adaptive_filter.hlsl");std::string shader((std::istreambuf_iterator<char>(shaderFile)),{});AdaptiveFilter filter;filter.initialize(device.Get(),std::filesystem::path(folder).wstring().c_str(),shader.c_str());
 D3D11_TEXTURE2D_DESC td{};td.Width=30;td.Height=20;td.MipLevels=td.ArraySize=1;td.SampleDesc.Count=1;td.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;ComPtr<ID3D11Texture2D> scene;AdaptiveFilter::require(device->CreateTexture2D(&td,nullptr,&scene));
 D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;ComPtr<ID3D11SamplerState> sampler;AdaptiveFilter::require(device->CreateSamplerState(&sd,&sampler));
 auto readBuffer=[&](ID3D11Buffer* buffer,unsigned floats){D3D11_BUFFER_DESC bd{};buffer->GetDesc(&bd);bd.Usage=D3D11_USAGE_STAGING;bd.BindFlags=bd.MiscFlags=bd.StructureByteStride=0;bd.CPUAccessFlags=D3D11_CPU_ACCESS_READ;ComPtr<ID3D11Buffer> staging;AdaptiveFilter::require(device->CreateBuffer(&bd,nullptr,&staging));ctx->CopyResource(staging.Get(),buffer);D3D11_MAPPED_SUBRESOURCE m{};AdaptiveFilter::require(ctx->Map(staging.Get(),0,D3D11_MAP_READ,0,&m));std::vector<float> result((float*)m.pData,(float*)m.pData+floats);ctx->Unmap(staging.Get(),0);return result;};
 std::ifstream fixtures(folder+"/expected.bin",std::ios::binary);float colors[8][3]{},expected[8]{};for(int i=0;i<8;i++){fixtures.read((char*)colors[i],12);fixtures.read((char*)&expected[i],4);}
 auto evaluate=[&](int mode){std::vector<float> pixels(600*4);for(int i=0;i<600;i++){float window[3]{},black[3]{};if(mode>=9){window[0]=window[1]=window[2]=250;}auto rgb=mode>=9?(i<int(600*(.4+.1*(mode-9)))?window:black):colors[mode<8?mode:i%8];pixels[i*4]=(1.660491*rgb[0]-.587641*rgb[1]-.072850*rgb[2])/80;pixels[i*4+1]=(-.124550*rgb[0]+1.132900*rgb[1]-.008349*rgb[2])/80;pixels[i*4+2]=(-.018151*rgb[0]-.100579*rgb[1]+1.118730*rgb[2])/80;pixels[i*4+3]=1;}ctx->PSSetShaderResources(0,6,std::array<ID3D11ShaderResourceView*,6>{}.data());ctx->UpdateSubresource(scene.Get(),0,nullptr,pixels.data(),30*16,0);filter.samplingCells=600;filter.prepare(device.Get(),ctx.Get(),scene.Get(),scene.Get(),nullptr,0,nullptr,0,true,sampler.Get());return readBuffer(filter.result.Get(),4)[0];};
 for(int i=0;i<8;i++){float gpu=evaluate(i);if(!std::isfinite(gpu)||std::abs(gpu-expected[i])>4e-4)throw std::runtime_error("Cluster GPU/CPU gain disagreement at palette "+std::to_string(i)+" GPU="+std::to_string(gpu)+" CPU="+std::to_string(expected[i]));std::cout<<"Palette "<<i<<" GPU "<<gpu<<" CPU "<<expected[i]<<"\n";}
 std::ifstream mixture(folder+"/mixture.bin",std::ios::binary);double expectedGain;float gain;double error;
 if(filter.config.pad>=3){std::array<double,49> state{},gpuState{};mixture.read((char*)state.data(),392);mixture.read((char*)&expectedGain,8);gain=evaluate(8);auto clusters=readBuffer(filter.clusterB.buffer.Get(),64);for(int i=0;i<8;i++){for(int j=0;j<6;j++)gpuState[i*6+j]=clusters[i*8+j];gpuState[48]+=clusters[i*8+6];}error=filter.config.pad==6?cubeClusterCost(state,gpuState):filter.config.pad>=4?gaussianClusterDistance(state,gpuState):scaledClusterDistance(state,gpuState);}
 else {std::array<double,40> state{},gpuState{};mixture.read((char*)state.data(),320);mixture.read((char*)&expectedGain,8);gain=evaluate(8);auto clusters=readBuffer(filter.clusterB.buffer.Get(),64);for(int i=0;i<8;i++){for(int j=0;j<4;j++)gpuState[i*5+j]=clusters[i*8+j];gpuState[i*5+4]=clusters[i*8+4];}error=clusterDistance(state,gpuState);}
 if(std::abs(gain-expectedGain)>1e-3||error>1e-5)throw std::runtime_error("Mixed cluster distribution or interpolation differs from CPU");
 if(filter.config.pad>=3 && std::filesystem::exists(folder+"/windows.bin")){std::ifstream windows(folder+"/windows.bin",std::ios::binary);for(int i=0;i<4;i++){float expectedWindow;windows.read((char*)&expectedWindow,4);float actual=evaluate(9+i);if(std::abs(actual-expectedWindow)>1e-3)throw std::runtime_error("Quadratic window GPU/CPU disagreement");}std::cout<<"PASS: 40/50/60/70% window inference on GPU\n";}
 std::cout<<"PASS: continuous HSV GPU fitting, population/spread, transport interpolation; mixture gain "<<gain<<" vs "<<expectedGain<<"\n";
}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
