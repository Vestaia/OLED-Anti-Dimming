// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <wrl/client.h>
#include <fstream>
#include <vector>
#include <stdexcept>
#include <unordered_map>
#include <cmath>
using Microsoft::WRL::ComPtr;
struct AdaptiveFilter
{
    struct Config
    {
        UINT width = 0, height = 0, hdr = 1, profileSize = 0, count = 0, vcgt = 0, enabled = 1,
             pad = 0;
        float peak = 250, baseline = 0, epsilon = .3f, white = 250;
        float scale[16]{};
        UINT gridWidth = 1, gridHeight = 1, groupsX = 1, groupsY = 1;
        UINT localNeighbors = 0, histogramBins = 512, reserved[2]{};
    } config;
    struct Frame
    {
        ComPtr<ID3D11Texture2D> original;
        ComPtr<ID3D11ShaderResourceView> view;
    };
    std::unordered_map<void *, Frame> frames;
    bool profileEnabled = false;
    UINT samplingCells = 25000, partialGroups = 0;
    ComPtr<ID3D11ComputeShader> features, infer;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants, partials, result, model, basis;
    ComPtr<ID3D11ShaderResourceView> resultView, modelView, vcgtView, basisView;
    ComPtr<ID3D11UnorderedAccessView> partialUav, resultUav;
    ComPtr<ID3D11ComputeShader> clusterSamples, clusterSeedPoints, clusterSeedReduce, clusterAssign, clusterReduce, clusterInfer;
    struct ClusterBuffer { ComPtr<ID3D11Buffer> buffer;ComPtr<ID3D11ShaderResourceView> view;ComPtr<ID3D11UnorderedAccessView> uav; };
    ClusterBuffer clusterPoints, clusterA, clusterB;
    UINT clusterCells=0;
    ClusterBuffer hsvRaw,hsvA,hsvB,hsvWeights;
    ComPtr<ID3D11ComputeShader> hsvSamples,hsvConvolve,hsvWeightShader,hsvPredict;
    static void clusterBuffer(ID3D11Device* d,UINT count,ClusterBuffer& out) {
        out={};D3D11_BUFFER_DESC desc{};desc.ByteWidth=count*16;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;desc.StructureByteStride=16;
        require(d->CreateBuffer(&desc,nullptr,&out.buffer));require(d->CreateShaderResourceView(out.buffer.Get(),nullptr,&out.view));require(d->CreateUnorderedAccessView(out.buffer.Get(),nullptr,&out.uav));
    }
    static void require(HRESULT hr)
    {
        if (FAILED(hr))
            throw std::runtime_error("Adaptive filter D3D11 initialization failed");
    }
    template <class T> static void read(std::ifstream &f, T &v)
    {
        f.read((char *)&v, sizeof(v));
        if (!f)
            throw std::runtime_error("Truncated adaptive filter configuration");
    }
    void initialize(ID3D11Device *d, const wchar_t *folder, const char *shader)
    {
        std::ifstream sampling(std::wstring(folder) + L"\\sampling.bin", std::ios::binary);
        if (sampling) read(sampling, samplingCells);
        if (samplingCells < 256 || samplingCells > 1000000)
            throw std::runtime_error("Invalid sampling cell count");
        std::ifstream profileFlag(std::wstring(folder) + L"\\profile-enabled.bin",
                                  std::ios::binary);
        UINT enabledProfile = 0;
        if (profileFlag)
            read(profileFlag, enabledProfile);
        profileEnabled = enabledProfile != 0;
        auto compile = [&](const char *entry, const char *target)
        {
            ComPtr<ID3DBlob> b, e;
            HRESULT hr = D3DCompile(shader, strlen(shader), nullptr, nullptr, nullptr, entry,
                                    target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &b, &e);
            if (FAILED(hr))
                throw std::runtime_error(e ? (char *)e->GetBufferPointer() : "Shader failed");
            return b;
        };
        std::ifstream f(std::wstring(folder) + L"\\runtime.bin", std::ios::binary);
        UINT magic = 0;
        if (!f)
            throw std::runtime_error("PCA runtime configuration is missing");
        if (f)
        {
            read(f, magic);
            if (magic != 0x324c5041 && magic != 0x334c5041 && magic != 0x344c5041 && magic != 0x354c5041 && magic != 0x364c5041 && magic != 0x374c5041 && magic != 0x384c5041 && magic != 0x394c5041 && magic != 0x414c5041 && magic != 0x424c5041 && magic != 0x434c5041 && magic != 0x444c5041 && magic != 0x454c5041)
                throw std::runtime_error("Unsupported runtime model");
            config.localNeighbors = magic != 0x324c5041;
            config.histogramBins = magic==0x454c5041?1152:(magic == 0x374c5041 || magic == 0x384c5041 || magic == 0x394c5041 || magic == 0x414c5041 || magic == 0x424c5041) ? 0 : magic == 0x364c5041 ? 9216 : magic == 0x354c5041 ? 5120 : magic == 0x344c5041 ? 768 : 512;
            config.pad = magic == 0x454c5041 ? 9 : magic == 0x444c5041 ? 8 : magic == 0x434c5041 ? 7 : magic == 0x424c5041 ? 6 : magic == 0x414c5041 ? 5 : magic == 0x394c5041 ? 4 : magic == 0x384c5041 ? 3 : magic == 0x374c5041 ? 2 : 1;
            read(f, config.count);
            read(f, config.peak);
            read(f, config.baseline);
            read(f, config.epsilon);
            for (int i = 0; i < 14; i++)
                read(f, config.scale[i]);
            if (config.count > (config.pad>=7?8192u:2048u) || config.peak <= 0)
                throw std::runtime_error("Invalid runtime model");
        }
        else
            for (auto &v : config.scale)
                v = 1;
        UINT rowFloats=config.pad==9?1156:config.pad>=7?9220:config.pad>=3?52:config.pad==2?44:16;
        std::vector<float> weights((std::max)(1u, config.count) * rowFloats);
        if (config.count)
        {
            f.read((char *)weights.data(), config.count * rowFloats * 4);
            if (!f)
                throw std::runtime_error("Truncated model weights");
        }
        auto a = compile("HistogramFeatures", "cs_5_0"), b = compile("HistogramInfer", "cs_5_0"),
             p = compile("FilterPS", "ps_5_0");
        require(
            d->CreateComputeShader(a->GetBufferPointer(), a->GetBufferSize(), nullptr, &features));
        require(d->CreateComputeShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &infer));
        require(d->CreatePixelShader(p->GetBufferPointer(), p->GetBufferSize(), nullptr, &pixel));
        auto buffer =
            [&](UINT bytes, UINT bind, UINT stride, const void *data, ComPtr<ID3D11Buffer> &dest)
        {
            D3D11_BUFFER_DESC bd{};
            bd.ByteWidth = bytes;
            bd.Usage = D3D11_USAGE_DEFAULT;
            bd.BindFlags = bind;
            if (stride)
            {
                bd.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
                bd.StructureByteStride = stride;
            }
            D3D11_SUBRESOURCE_DATA init{data, 0, 0};
            require(d->CreateBuffer(&bd, data ? &init : nullptr, &dest));
        };
        buffer(sizeof(Config), D3D11_BIND_CONSTANT_BUFFER, 0, nullptr, constants);
        buffer(16, D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE, 16, nullptr, result);
        buffer(UINT(weights.size() * 4), D3D11_BIND_SHADER_RESOURCE, 16, weights.data(), model);
        require(d->CreateUnorderedAccessView(result.Get(), nullptr, &resultUav));
        require(d->CreateShaderResourceView(result.Get(), nullptr, &resultView));
        require(d->CreateShaderResourceView(model.Get(), nullptr, &modelView));
        if (config.pad==1)
        {
            std::vector<float> data(config.histogramBins * 16);
            f.read((char *)data.data(), data.size() * 4);
            if (!f)
                throw std::runtime_error("Truncated PCA basis");
            buffer(UINT(data.size() * 4), D3D11_BIND_SHADER_RESOURCE, 16, data.data(), basis);
            require(d->CreateShaderResourceView(basis.Get(), nullptr, &basisView));
        }
        if(config.pad>=7) {
            auto compute=[&](const char* entry,ComPtr<ID3D11ComputeShader>& dest){auto code=compile(entry,"cs_5_0");require(d->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&dest));};
            compute("SmoothHsvSamples",hsvSamples);compute("SmoothHsvConvolve",hsvConvolve);compute("SmoothHsvWeights",hsvWeightShader);compute("SmoothHsvPredict",hsvPredict);
            UINT bins=config.pad==9?1152:9216;clusterBuffer(d,bins,hsvRaw);clusterBuffer(d,bins,hsvA);clusterBuffer(d,bins,hsvB);clusterBuffer(d,(std::max)(1u,config.count),hsvWeights);
            std::vector<float> matrices((config.pad==9?144+36+256:144+144+4096)*4);f.read((char*)matrices.data(),matrices.size()*4);if(!f)throw std::runtime_error("Truncated histogram smoothing matrices");
            buffer(UINT(matrices.size()*4),D3D11_BIND_SHADER_RESOURCE,16,matrices.data(),basis);require(d->CreateShaderResourceView(basis.Get(),nullptr,&basisView));
        }
        if(config.pad>=2 && config.pad<7) {
            auto compute=[&](const char* entry,ComPtr<ID3D11ComputeShader>& dest){auto code=compile(entry,"cs_5_0");require(d->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&dest));};
            compute(config.pad>=3?"ScaledSamples":"ClusterSamples",clusterSamples);compute(config.pad>=3?"ScaledSeedPoints":"ClusterSeedPoints",clusterSeedPoints);compute(config.pad>=3?"ScaledSeedReduce":"ClusterSeedReduce",clusterSeedReduce);compute(config.pad>=3?"ScaledAssign":"ClusterAssign",clusterAssign);compute(config.pad>=3?"ScaledReduce":"ClusterReduce",clusterReduce);compute(config.pad>=3?"ScaledInfer":"ClusterInfer",clusterInfer);
            clusterBuffer(d,16,clusterA);clusterBuffer(d,16,clusterB);
        }
        std::ifstream v(std::wstring(folder) + L"\\vcgt.bin", std::ios::binary);
        if (v)
        {
            UINT n = 0;
            read(v, n);
            if (n < 2 || n > 65536)
                throw std::runtime_error("Invalid VCGT");
            std::vector<float> data(n * 4);
            v.read((char *)data.data(), data.size() * 4);
            if (!v)
                throw std::runtime_error("Truncated VCGT");
            D3D11_TEXTURE1D_DESC td{};
            td.Width = n;
            td.MipLevels = td.ArraySize = 1;
            td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
            td.Usage = D3D11_USAGE_IMMUTABLE;
            td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA init{data.data(), 0, 0};
            ComPtr<ID3D11Texture1D> t;
            require(d->CreateTexture1D(&td, &init, &t));
            require(d->CreateShaderResourceView(t.Get(), nullptr, &vcgtView));
            config.vcgt = 1;
        }
    }
    ID3D11ShaderResourceView *prepare(ID3D11Device *d, ID3D11DeviceContext *c,
                                      ID3D11Texture2D *back, void *identity, const RECT *dirty,
                                      int dirtyCount, ID3D11ShaderResourceView *profile, UINT size,
                                      bool hdr, ID3D11SamplerState *sampler)
    {
        // Restore DWM's compute bindings after our passes. This is API state capture, not image
        // readback.
        struct ComputeState
        {
            ID3D11DeviceContext *context;
            ID3D11ComputeShader *shader = nullptr;
            ID3D11ShaderResourceView *resources[7]{};
            ID3D11UnorderedAccessView *uavs[4]{};
            ID3D11Buffer *buffer = nullptr;
            ID3D11SamplerState *sampler = nullptr;
            ComputeState(ID3D11DeviceContext *c) : context(c)
            {
                c->CSGetShader(&shader, nullptr, nullptr);
                c->CSGetShaderResources(0, 7, resources);
                c->CSGetUnorderedAccessViews(0, 4, uavs);
                c->CSGetConstantBuffers(1, 1, &buffer);
                c->CSGetSamplers(0, 1, &sampler);
            }
            ~ComputeState()
            {
                context->CSSetShader(shader, nullptr, 0);
                context->CSSetShaderResources(0, 7, resources);
                context->CSSetUnorderedAccessViews(0, 4, uavs, nullptr);
                context->CSSetConstantBuffers(1, 1, &buffer);
                context->CSSetSamplers(0, 1, &sampler);
                if (shader)
                    shader->Release();
                for (auto v : resources)
                    if (v)
                        v->Release();
                for (auto v : uavs)
                    if (v)
                        v->Release();
                if (buffer)
                    buffer->Release();
                if (sampler)
                    sampler->Release();
            }
        } saved(c);
        ID3D11ShaderResourceView *noPixelResources[6]{};
        c->PSSetShaderResources(0, 6, noPixelResources);
        D3D11_TEXTURE2D_DESC desc{};
        back->GetDesc(&desc);
        auto &frame = frames[identity];
        bool fresh = !frame.original;
        if (!fresh)
        {
            D3D11_TEXTURE2D_DESC prev{};
            frame.original->GetDesc(&prev);
            fresh = prev.Width != desc.Width || prev.Height != desc.Height ||
                    prev.Format != desc.Format;
        }
        if (fresh)
        {
            frame = {};
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.CPUAccessFlags = desc.MiscFlags = 0;
            require(d->CreateTexture2D(&desc, nullptr, &frame.original));
            require(d->CreateShaderResourceView(frame.original.Get(), nullptr, &frame.view));
            c->CopyResource(frame.original.Get(), back);
        }
        else if (dirtyCount == 0)
        {
            // An empty damage list represents a full-frame present, not a
            // request to redraw the previous pristine frame (e.g. video).
            c->CopyResource(frame.original.Get(), back);
        }
        else
            for (int i = 0; i < dirtyCount; i++)
            {
                D3D11_BOX box{UINT(dirty[i].left),  UINT(dirty[i].top),    0,
                              UINT(dirty[i].right), UINT(dirty[i].bottom), 1};
                c->CopySubresourceRegion(frame.original.Get(), 0, box.left, box.top, 0, back, 0,
                                         &box);
            }
        config.width = desc.Width;
        config.height = desc.Height;
        config.gridWidth = (std::min)(desc.Width, (std::max)(1u, UINT(std::round(std::sqrt(double(samplingCells) * desc.Width / desc.Height)))));
        config.gridHeight = (std::min)(desc.Height, (std::max)(1u, UINT(std::round(double(samplingCells) / config.gridWidth))));
        config.groupsX = (config.gridWidth + 15) / 16;
        config.groupsY = (config.gridHeight + 15) / 16;
        if(config.pad>=2){config.groupsX=(config.gridWidth*config.gridHeight+127)/128;config.groupsY=1;}
        UINT groups = config.groupsX * config.groupsY;
        if(config.pad>=2&&config.pad<7&&clusterCells!=config.gridWidth*config.gridHeight){clusterCells=config.gridWidth*config.gridHeight;clusterBuffer(d,clusterCells*(config.pad>=3?2:1),clusterPoints);}
        if (groups != partialGroups)
        {
            partialUav.Reset(); partials.Reset();
            D3D11_BUFFER_DESC bd{};
            bd.ByteWidth = groups * (config.pad>=2?256:64);
            bd.Usage = D3D11_USAGE_DEFAULT;
            bd.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
            bd.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
            bd.StructureByteStride = 16;
            require(d->CreateBuffer(&bd, nullptr, &partials));
            require(d->CreateUnorderedAccessView(partials.Get(), nullptr, &partialUav));
            partialGroups = groups;
        }
        config.hdr = hdr;
        config.profileSize = profileEnabled ? size : 0;
        c->UpdateSubresource(constants.Get(), 0, nullptr, &config, 0, 0);
        ID3D11ShaderResourceView *srvs[6]{frame.view.Get(), profile, basisView.Get(),
                                          modelView.Get(),  nullptr, vcgtView.Get()};
        c->CSSetShaderResources(0, 6, srvs);
        ID3D11Buffer *cb = constants.Get();
        c->CSSetConstantBuffers(1, 1, &cb);
        c->CSSetSamplers(0, 1, &sampler);
        ID3D11UnorderedAccessView *uavs[2]{partialUav.Get(), resultUav.Get()};
        ID3D11UnorderedAccessView *nil[2]{};
        ID3D11ShaderResourceView *empty[6]{};
        if(config.pad>=7) {
            ID3D11UnorderedAccessView* noUavs[4]{};ID3D11ShaderResourceView* noSrvs[7]{};
            auto clear=[&](){c->CSSetUnorderedAccessViews(0,4,noUavs,nullptr);c->CSSetShaderResources(0,7,noSrvs);};
            auto bind=[&](ID3D11ComputeShader* shader,ID3D11ShaderResourceView* input,ID3D11UnorderedAccessView* raw,ID3D11UnorderedAccessView* output){clear();ID3D11ShaderResourceView* inputs[7]{frame.view.Get(),profile,basisView.Get(),modelView.Get(),nullptr,vcgtView.Get(),input};c->CSSetShaderResources(0,7,inputs);ID3D11UnorderedAccessView* outputs[4]{nullptr,resultUav.Get(),raw,output};c->CSSetUnorderedAccessViews(0,4,outputs,nullptr);c->CSSetShader(shader,nullptr,0);};
            UINT zeros[4]{};c->ClearUnorderedAccessViewUint(hsvRaw.uav.Get(),zeros);
            bind(hsvSamples.Get(),nullptr,hsvRaw.uav.Get(),nullptr);c->Dispatch(groups,1,1);
            for(UINT axis=0;axis<3;axis++) {
                config.reserved[0]=axis;c->UpdateSubresource(constants.Get(),0,nullptr,&config,0,0);
                ID3D11ShaderResourceView* input=axis==0?hsvRaw.view.Get():axis==1?hsvA.view.Get():hsvB.view.Get();
                ID3D11UnorderedAccessView* output=axis==1?hsvB.uav.Get():hsvA.uav.Get();
                bind(hsvConvolve.Get(),input,nullptr,output);c->Dispatch(config.pad==9?9:72,1,1);
            }
            if(config.count){bind(hsvWeightShader.Get(),hsvA.view.Get(),nullptr,hsvWeights.uav.Get());c->Dispatch(config.count,1,1);}
            bind(hsvPredict.Get(),hsvWeights.view.Get(),nullptr,nullptr);c->Dispatch(1,1,1);clear();
        } else if(config.pad>=2) {
            auto clear=[&](){c->CSSetUnorderedAccessViews(0,2,nil,nullptr);c->CSSetShaderResources(0,6,empty);};
            auto bind=[&](ID3D11ComputeShader* shader,ID3D11ShaderResourceView* points,ID3D11ShaderResourceView* centers,ID3D11UnorderedAccessView* partial,ID3D11UnorderedAccessView* out){clear();ID3D11ShaderResourceView* inputs[6]{frame.view.Get(),profile,points,modelView.Get(),centers,vcgtView.Get()};c->CSSetShaderResources(0,6,inputs);ID3D11UnorderedAccessView* outputs[2]{partial,out};c->CSSetUnorderedAccessViews(0,2,outputs,nullptr);c->CSSetShader(shader,nullptr,0);};
            config.reserved[0]=config.reserved[1]=0;c->UpdateSubresource(constants.Get(),0,nullptr,&config,0,0);
            bind(clusterSamples.Get(),nullptr,nullptr,clusterPoints.uav.Get(),nullptr);c->Dispatch(groups,1,1);clear();
            UINT zeros[4]{};c->ClearUnorderedAccessViewUint(clusterA.uav.Get(),zeros);
            for(UINT k=1;k<8;k++) {
                config.reserved[0]=k;c->UpdateSubresource(constants.Get(),0,nullptr,&config,0,0);
                bind(clusterSeedPoints.Get(),clusterPoints.view.Get(),clusterA.view.Get(),partialUav.Get(),nullptr);c->Dispatch(groups,1,1);
                bind(clusterSeedReduce.Get(),nullptr,nullptr,partialUav.Get(),clusterA.uav.Get());c->Dispatch(1,1,1);
            }
            config.reserved[0]=0;c->UpdateSubresource(constants.Get(),0,nullptr,&config,0,0);
            for(UINT iteration=0;iteration<9;iteration++) {
                auto& input=iteration%2?clusterB:clusterA;auto& output=iteration%2?clusterA:clusterB;
                config.reserved[1]=iteration==8?1:0;c->UpdateSubresource(constants.Get(),0,nullptr,&config,0,0);
                bind(clusterAssign.Get(),clusterPoints.view.Get(),input.view.Get(),partialUav.Get(),nullptr);c->Dispatch(groups,1,1);
                bind(clusterReduce.Get(),nullptr,input.view.Get(),partialUav.Get(),output.uav.Get());c->Dispatch(8,1,1);
            }
            bind(clusterInfer.Get(),nullptr,clusterB.view.Get(),nullptr,resultUav.Get());c->Dispatch(1,1,1);clear();
        } else {
            c->CSSetUnorderedAccessViews(0, 2, uavs, nullptr);
            c->CSSetShader(features.Get(), nullptr, 0);
            c->Dispatch(config.groupsX, config.groupsY, 1);
            c->CSSetShader(infer.Get(), nullptr, 0);
            c->Dispatch(1, 1, 1);
            c->CSSetUnorderedAccessViews(0, 2, nil, nullptr);
            c->CSSetShaderResources(0, 6, empty);
        }
        c->CSSetShader(nullptr, nullptr, 0);
        srvs[4] = resultView.Get();
        c->PSSetShaderResources(0, 6, srvs);
        c->PSSetConstantBuffers(1, 1, &cb);
        c->PSSetShader(pixel.Get(), nullptr, 0);
        return frame.view.Get();
    }
};
