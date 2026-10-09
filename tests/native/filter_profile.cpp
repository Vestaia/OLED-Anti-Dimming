// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include "adaptive_filter.hpp"
#include <chrono>
#include <iostream>
#include <iterator>
#include <functional>
using Clock=std::chrono::steady_clock;
double elapsed(Clock::time_point start){return std::chrono::duration<double,std::milli>(Clock::now()-start).count();}
int main(int argc,char** argv){try{
 std::ifstream source("../native/adaptive_filter.hlsl");std::string shader((std::istreambuf_iterator<char>(source)),{});
 ComPtr<ID3D11Device> d;ComPtr<ID3D11DeviceContext> c;auto start=Clock::now();AdaptiveFilter::require(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&d,nullptr,&c));std::cout<<"device_init_ms,"<<elapsed(start)<<"\n";
 AdaptiveFilter f;start=Clock::now();std::string folder=argc>1?argv[1]:"filter-performance";std::wstring wideFolder(folder.begin(),folder.end());f.initialize(d.Get(),wideFolder.c_str(),shader.c_str());std::cout<<"filter_init_ms,"<<elapsed(start)<<"\n";
 const char* entries[]={"HistogramFeatures","HistogramInfer","FilterPS","SmoothHsvSamples","SmoothHsvConvolve","SmoothHsvWeights","SmoothHsvPredict"};
 for(auto entry:entries){ComPtr<ID3DBlob> code,error;start=Clock::now();AdaptiveFilter::require(D3DCompile(shader.data(),shader.size(),nullptr,nullptr,nullptr,entry,strcmp(entry,"FilterPS")==0?"ps_5_0":"cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&error));std::cout<<"compile_"<<entry<<"_ms,"<<elapsed(start)<<"\n";}
 D3D11_TEXTURE2D_DESC td{};td.Width=2560;td.Height=1440;td.MipLevels=td.ArraySize=td.SampleDesc.Count=1;td.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;td.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
 ComPtr<ID3D11Texture2D> input,output;AdaptiveFilter::require(d->CreateTexture2D(&td,nullptr,&input));AdaptiveFilter::require(d->CreateTexture2D(&td,nullptr,&output));ComPtr<ID3D11RenderTargetView> inputRT,outputRT;AdaptiveFilter::require(d->CreateRenderTargetView(input.Get(),nullptr,&inputRT));AdaptiveFilter::require(d->CreateRenderTargetView(output.Get(),nullptr,&outputRT));float color[4]{1,1,1,1};c->ClearRenderTargetView(inputRT.Get(),color);
 const char* vs="struct O{float4 p:SV_POSITION;float2 t:TEXCOORD;};O main(uint id:SV_VertexID){O o;float2 p=float2((id<<1)&2,id&2);o.t=p;o.p=float4(p*float2(2,-2)+float2(-1,1),0,1);return o;}";ComPtr<ID3DBlob> code;AdaptiveFilter::require(D3DCompile(vs,strlen(vs),nullptr,nullptr,nullptr,"main","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,nullptr));ComPtr<ID3D11VertexShader> vertex;AdaptiveFilter::require(d->CreateVertexShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&vertex));c->VSSetShader(vertex.Get(),nullptr,0);c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);D3D11_VIEWPORT viewport{0,0,2560,1440,0,1};c->RSSetViewports(1,&viewport);
 auto frame=[&](bool draw){ID3D11ShaderResourceView* none[6]{};c->PSSetShaderResources(0,6,none);f.prepare(d.Get(),c.Get(),input.Get(),input.Get(),nullptr,0,nullptr,0,true,nullptr);if(draw){ID3D11RenderTargetView* rt=outputRT.Get();c->OMSetRenderTargets(1,&rt,nullptr);c->Draw(3,0);}};
 auto timing=[&](int iterations,const std::function<void()>& action){D3D11_QUERY_DESC q{D3D11_QUERY_TIMESTAMP_DISJOINT,0};ComPtr<ID3D11Query> disjoint,a,b;AdaptiveFilter::require(d->CreateQuery(&q,&disjoint));q.Query=D3D11_QUERY_TIMESTAMP;AdaptiveFilter::require(d->CreateQuery(&q,&a));AdaptiveFilter::require(d->CreateQuery(&q,&b));c->Begin(disjoint.Get());c->End(a.Get());for(int i=0;i<iterations;i++)action();c->End(b.Get());c->End(disjoint.Get());c->Flush();D3D11_QUERY_DATA_TIMESTAMP_DISJOINT info{};while(c->GetData(disjoint.Get(),&info,sizeof(info),0)==S_FALSE)Sleep(1);UINT64 x,y;AdaptiveFilter::require(c->GetData(a.Get(),&x,8,0));AdaptiveFilter::require(c->GetData(b.Get(),&y,8,0));if(info.Disjoint)throw std::runtime_error("Timestamp clock changed");return 1000.*(y-x)/info.Frequency/iterations;};
 unsigned anchors=f.config.count;std::cout<<"anchors,"<<anchors<<"\n";f.samplingCells=25000;
 Sleep(3000);std::cout<<"idle_first_full_ms,"<<timing(1,[&]{frame(true);})<<"\n";
 for(int trial=0;trial<3;trial++)for(unsigned cells:{10000u,25000u,60000u}){f.samplingCells=cells;timing(1024,[&]{frame(true);});double compute=timing(512,[&]{frame(false);});double full=timing(512,[&]{frame(true);});std::cout<<"trial,"<<trial<<",cells,"<<cells<<",prepare_ms,"<<compute<<",full_ms,"<<full<<"\n";}
 system("nvidia-smi --query-gpu=pstate,clocks.gr,clocks.mem,power.draw --format=csv,noheader");
 // Mixed bright/dark/color tiles exercise a less concentrated histogram.
 std::vector<unsigned short> mixed(2560*1440*4);const unsigned short levels[]{0x3000,0x3400,0x3800,0x3c00,0x4000,0x4400,0x4800};
 for(unsigned y=0;y<1440;y++)for(unsigned x=0;x<2560;x++){unsigned hash=(x/32+31*(y/32))*2654435761u;for(unsigned ch=0;ch<3;ch++)mixed[(y*2560+x)*4+ch]=levels[(hash>>(ch*7))%7];mixed[(y*2560+x)*4+3]=0x3c00;}
 c->UpdateSubresource(input.Get(),0,nullptr,mixed.data(),2560*8,0);
 for(unsigned cells:{10000u,25000u,60000u}){f.samplingCells=cells;timing(512,[&]{frame(true);});std::cout<<"mixed_cells,"<<cells<<",full_ms,"<<timing(512,[&]{frame(true);})<<"\n";}
 f.samplingCells=25000;for(unsigned count:{0u,100u,500u,1000u,anchors}){f.config.count=count;timing(16,[&]{frame(true);});std::cout<<"count,"<<count<<",full_ms,"<<timing(96,[&]{frame(true);})<<"\n";}
 // Timestamp each frame separately so idle gaps do not count as GPU work.
 f.config.count=anchors;f.samplingCells=25000;
 auto paced=[&](int delay){D3D11_QUERY_DESC desc{D3D11_QUERY_TIMESTAMP_DISJOINT,0};ComPtr<ID3D11Query> disjoint;AdaptiveFilter::require(d->CreateQuery(&desc,&disjoint));desc.Query=D3D11_QUERY_TIMESTAMP;std::vector<ComPtr<ID3D11Query>> queries(512);for(auto& query:queries)AdaptiveFilter::require(d->CreateQuery(&desc,&query));c->Begin(disjoint.Get());for(int i=0;i<256;i++){Sleep(delay);c->End(queries[i*2].Get());frame(true);c->End(queries[i*2+1].Get());c->Flush();}c->End(disjoint.Get());c->Flush();D3D11_QUERY_DATA_TIMESTAMP_DISJOINT info{};while(c->GetData(disjoint.Get(),&info,sizeof(info),0)==S_FALSE)Sleep(1);double total=0;for(int i=0;i<256;i++){UINT64 a,b;AdaptiveFilter::require(c->GetData(queries[i*2].Get(),&a,8,0));AdaptiveFilter::require(c->GetData(queries[i*2+1].Get(),&b,8,0));total+=1000.*(b-a)/info.Frequency;}if(info.Disjoint)throw std::runtime_error("Paced timestamp clock changed");return total/256;};
 std::cout<<"paced_2ms_full_ms,"<<paced(2)<<"\n";system("nvidia-smi --query-gpu=pstate,clocks.gr,clocks.mem,power.draw --format=csv,noheader");
 Sleep(5000);std::cout<<"paced_16ms_full_ms,"<<paced(16)<<"\n";system("nvidia-smi --query-gpu=pstate,clocks.gr,clocks.mem,power.draw --format=csv,noheader");
 std::cout<<"copy_only_ms,"<<timing(96,[&]{c->CopyResource(output.Get(),input.Get());})<<"\n";
 return 0;
}catch(const std::exception&e){std::cerr<<e.what()<<"\n";return 1;}}
