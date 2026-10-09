// SPDX-License-Identifier: GPL-3.0-only
// Standalone benchmark: no DWM injection, camera access or clock overrides.
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <DirectXPackedVector.h>
#include "benchmark_filter.hpp"
#include <algorithm>
#include <chrono>
#include <filesystem>
#include <iostream>
#include <iomanip>
#include <iterator>
#include <string>

int main(int argc,char** argv) {
 try {
    if(argc!=5&&argc!=6)throw std::runtime_error("Usage: benchmark model-folder shader variant output.csv");
    bool idle = argc==6 && std::string(argv[5])=="--idle";
    std::ifstream input(argv[2]);std::string shader((std::istreambuf_iterator<char>(input)),{});
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    AdaptiveFilter::require(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    ComPtr<IDXGIDevice> dxgi;device.As(&dxgi);ComPtr<IDXGIAdapter> adapter;dxgi->GetAdapter(&adapter);
    DXGI_ADAPTER_DESC description{};adapter->GetDesc(&description);std::wcout<<description.Description<<L"\n";
    AdaptiveFilter filter;filter.initialize(device.Get(),std::filesystem::path(argv[1]).wstring().c_str(),shader.c_str());
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
    ComPtr<ID3D11SamplerState> sampler;AdaptiveFilter::require(device->CreateSamplerState(&sd,&sampler));
    D3D11_TEXTURE2D_DESC td{};td.Width=2560;td.Height=1440;td.MipLevels=td.ArraySize=1;td.SampleDesc.Count=1;td.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> scene,output;AdaptiveFilter::require(device->CreateTexture2D(&td,nullptr,&scene));
    td.BindFlags=D3D11_BIND_RENDER_TARGET;AdaptiveFilter::require(device->CreateTexture2D(&td,nullptr,&output));
    ComPtr<ID3D11RenderTargetView> target;AdaptiveFilter::require(device->CreateRenderTargetView(output.Get(),nullptr,&target));
    const char* vs="struct O{float4 p:SV_POSITION;float2 t:TEXCOORD;};O main(uint id:SV_VertexID){O o;o.t=float2((id<<1)&2,id&2);o.p=float4(o.t*float2(2,-2)+float2(-1,1),0,1);return o;}";
    ComPtr<ID3DBlob> code,errors;AdaptiveFilter::require(D3DCompile(vs,strlen(vs),nullptr,nullptr,nullptr,"main","vs_5_0",0,0,&code,&errors));
    ComPtr<ID3D11VertexShader> vertex;AdaptiveFilter::require(device->CreateVertexShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&vertex));
    auto drawState=[&](ID3D11DeviceContext* c){auto rt=target.Get();c->OMSetRenderTargets(1,&rt,nullptr);D3D11_VIEWPORT vp{0,0,2560,1440,0,1};c->RSSetViewports(1,&vp);c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);c->VSSetShader(vertex.Get(),nullptr,0);};
    std::ofstream csv(argv[4]);csv<<"variant,cells,actual_cells,groups,bins,centers,scene,mode,samples,median_us,p95_us,min_us,max_us,gain\n";
    auto query=[&](D3D11_QUERY kind){D3D11_QUERY_DESC q{kind,0};ComPtr<ID3D11Query> result;AdaptiveFilter::require(device->CreateQuery(&q,&result));return result;};
    auto wait=[&](ID3D11Query* q,void* data,UINT bytes){HRESULT hr;while((hr=context->GetData(q,data,bytes,0))==S_FALSE)Sleep(1);AdaptiveFilter::require(hr);};
    auto measure=[&](auto action,int frames,bool paced){
        auto disjoint=query(D3D11_QUERY_TIMESTAMP_DISJOINT);std::vector<ComPtr<ID3D11Query>> starts,ends;
        int slots=paced?frames:1;for(int i=0;i<slots;i++){starts.push_back(query(D3D11_QUERY_TIMESTAMP));ends.push_back(query(D3D11_QUERY_TIMESTAMP));}
        auto wallStart=std::chrono::steady_clock::now();context->Begin(disjoint.Get());
        if(!paced)context->End(starts[0].Get());
        for(int i=0;i<frames;i++){
            if(paced){auto deadline=wallStart+std::chrono::microseconds(i*2000);while(std::chrono::steady_clock::now()<deadline)Sleep(0);context->End(starts[i].Get());}
            action();if(paced){context->End(ends[i].Get());context->Flush();}
        }
        if(!paced)context->End(ends[0].Get());context->End(disjoint.Get());context->Flush();
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT timing{};wait(disjoint.Get(),&timing,sizeof(timing));if(timing.Disjoint)throw std::runtime_error("Disjoint GPU timing");
        std::vector<double> values;for(int i=0;i<slots;i++){UINT64 a,b;wait(starts[i].Get(),&a,8);wait(ends[i].Get(),&b,8);values.push_back(double(b-a)/timing.Frequency*1e6/(paced?1:frames));}return values;
    };
    std::vector<DirectX::PackedVector::HALF> pixels(size_t(2560)*1440*4);
    for(const std::string sceneName:{"gray","mixed"}) {
        if(idle && sceneName!="mixed")continue;
        for(UINT y=0;y<1440;y++)for(UINT x=0;x<2560;x++){
            float c[3]{100.f/80,100.f/80,100.f/80};
            if(sceneName=="mixed") {UINT tile=((x/32)*17+(y/32)*29)%20;float light=tile<4?1000.f/80:25.f/80;for(int j=0;j<3;j++)c[j]=light*(.1f+.9f*((tile*(j+3)+j*7)%11)/10.f);}
            size_t i=(size_t(y)*2560+x)*4;for(int j=0;j<3;j++)pixels[i+j]=DirectX::PackedVector::XMConvertFloatToHalf(c[j]);pixels[i+3]=DirectX::PackedVector::XMConvertFloatToHalf(1);
        }
        context->UpdateSubresource(scene.Get(),0,nullptr,pixels.data(),2560*8,0);
        for(UINT cells:{10000u,25000u,60000u}) {
            if(idle && cells!=25000)continue;
            filter.samplingCells=cells;drawState(context.Get());filter.prepare(device.Get(),context.Get(),scene.Get(),scene.Get(),nullptr,0,nullptr,0,true,sampler.Get());
            auto& frame=filter.frames[scene.Get()];
            auto commands=[&](const std::string& mode,int repeats){
                ComPtr<ID3D11DeviceContext> deferred;AdaptiveFilter::require(device->CreateDeferredContext(0,&deferred));
                auto cb=filter.constants.Get();auto ss=sampler.Get();
                ID3D11ShaderResourceView* resources[6]{frame.view.Get(),nullptr,filter.basisView.Get(),filter.modelView.Get(),nullptr,nullptr};
                for(int repeat=0;repeat<repeats;repeat++) {
                if(mode=="full")deferred->CopyResource(frame.original.Get(),scene.Get());
                if(mode!="pixel"){
                    deferred->CSSetShaderResources(0,6,resources);deferred->CSSetConstantBuffers(1,1,&cb);deferred->CSSetSamplers(0,1,&ss);
                    ID3D11UnorderedAccessView* uavs[2]{filter.partialUav.Get(),filter.resultUav.Get()};deferred->CSSetUnorderedAccessViews(0,2,uavs,nullptr);
                    if(mode!="infer"){deferred->CSSetShader(filter.features.Get(),nullptr,0);deferred->Dispatch(filter.config.groupsX,filter.config.groupsY,1);}
                    if(mode!="features"){deferred->CSSetShader(filter.infer.Get(),nullptr,0);deferred->Dispatch(1,1,1);}
                    ID3D11UnorderedAccessView* none[2]{};deferred->CSSetUnorderedAccessViews(0,2,none,nullptr);
                    ID3D11ShaderResourceView* empty[6]{};deferred->CSSetShaderResources(0,6,empty);
                }
                if(mode=="full"||mode=="pixel"){
                    drawState(deferred.Get());resources[4]=filter.resultView.Get();deferred->PSSetShaderResources(0,6,resources);deferred->PSSetConstantBuffers(1,1,&cb);deferred->PSSetSamplers(0,1,&ss);deferred->PSSetShader(filter.pixel.Get(),nullptr,0);deferred->Draw(3,0);
                }
                }
                ComPtr<ID3D11CommandList> list;AdaptiveFilter::require(deferred->FinishCommandList(FALSE,&list));return list;
            };
            auto paced=commands("full",1),full=commands("full",32),analysis=commands("analysis",32),pixel=commands("pixel",32),features=commands("features",32),infer=commands("infer",32);
            D3D11_BUFFER_DESC readDesc{};filter.result->GetDesc(&readDesc);readDesc.Usage=D3D11_USAGE_STAGING;readDesc.BindFlags=readDesc.MiscFlags=readDesc.StructureByteStride=0;readDesc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Buffer> gainRead;AdaptiveFilter::require(device->CreateBuffer(&readDesc,nullptr,&gainRead));context->CopyResource(gainRead.Get(),filter.result.Get());D3D11_MAPPED_SUBRESOURCE mapped{};AdaptiveFilter::require(context->Map(gainRead.Get(),0,D3D11_MAP_READ,0,&mapped));float gain=*(float*)mapped.pData;context->Unmap(gainRead.Get(),0);if(!std::isfinite(gain))throw std::runtime_error("Invalid gain");
            auto report=[&](std::string mode,std::vector<double> values){std::sort(values.begin(),values.end());double median=values[values.size()/2],p95=values[size_t((values.size()-1)*.95)];csv<<argv[3]<<','<<cells<<','<<filter.config.gridWidth*filter.config.gridHeight<<','<<filter.partialGroups<<','<<filter.config.histogramBins<<','<<filter.config.count<<','<<sceneName<<','<<mode<<','<<values.size()<<','<<std::setprecision(9)<<median<<','<<p95<<','<<values.front()<<','<<values.back()<<','<<gain<<'\n';csv.flush();std::cout<<argv[3]<<" "<<cells<<" "<<sceneName<<" "<<mode<<": "<<median<<" us (p95 "<<p95<<")\n";};
            if(idle) {
                std::cout<<"Cooldown for 10 seconds, no clock override\n";std::cout.flush();Sleep(10000);
                auto values=measure([&]{context->ExecuteCommandList(paced.Get(),FALSE);},250,true);
                report("idle_first_frame",{values.front()});
                report("idle_first_20",std::vector<double>(values.begin(),values.begin()+20));
                report("idle_last_100",std::vector<double>(values.end()-100,values.end()));
                report("idle_paced_500hz",values);
                continue;
            }
            // Paced mode precedes sustained batches, without forcing a power state.
            if(cells==25000)report("paced_500hz",measure([&]{context->ExecuteCommandList(paced.Get(),FALSE);},250,true));
            for(int i=0;i<16;i++)context->ExecuteCommandList(full.Get(),FALSE);
            for(auto entry:{std::pair{"gpu_full",full.Get()},std::pair{"gpu_analysis",analysis.Get()},std::pair{"gpu_pixel",pixel.Get()},std::pair{"gpu_features",features.Get()},std::pair{"gpu_infer",infer.Get()}}){
                std::vector<double> values;for(int i=0;i<7;i++){auto result=measure([&]{context->ExecuteCommandList(entry.second,FALSE);},8,false);for(auto& value:result)value/=32;values.insert(values.end(),result.begin(),result.end());}report(entry.first,values);
            }
            drawState(context.Get());std::vector<double> submitted;
            for(int i=0;i<7;i++){auto result=measure([&]{filter.prepare(device.Get(),context.Get(),scene.Get(),scene.Get(),nullptr,0,nullptr,0,true,sampler.Get());context->Draw(3,0);},256,false);submitted.insert(submitted.end(),result.begin(),result.end());}report("api_full",submitted);
        }
    }
    return 0;
 } catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}
}
