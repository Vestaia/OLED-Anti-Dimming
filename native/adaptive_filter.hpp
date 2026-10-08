// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <wrl/client.h>
#include <fstream>
#include <vector>
#include <stdexcept>
#include <unordered_map>
using Microsoft::WRL::ComPtr;
struct AdaptiveFilter
{
    struct Config
    {
        UINT width = 0, height = 0, hdr = 1, profileSize = 0, count = 0, vcgt = 0, enabled = 1,
             pad = 0;
        float peak = 250, baseline = 0, epsilon = .3f, white = 250;
        float scale[16]{};
    } config;
    struct Frame
    {
        ComPtr<ID3D11Texture2D> original;
        ComPtr<ID3D11ShaderResourceView> view;
    };
    std::unordered_map<void *, Frame> frames;
    bool profileEnabled = false;
    ComPtr<ID3D11ComputeShader> features, infer;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants, partials, result, model, basis;
    ComPtr<ID3D11ShaderResourceView> resultView, modelView, vcgtView, basisView;
    ComPtr<ID3D11UnorderedAccessView> partialUav, resultUav;
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
            if (magic != 0x324c5041)
                throw std::runtime_error("Unsupported runtime model");
            config.pad = 1;
            read(f, config.count);
            read(f, config.peak);
            read(f, config.baseline);
            read(f, config.epsilon);
            for (int i = 0; i < 14; i++)
                read(f, config.scale[i]);
            if (config.count > 2048 || config.peak <= 0)
                throw std::runtime_error("Invalid runtime model");
        }
        else
            for (auto &v : config.scale)
                v = 1;
        std::vector<float> weights((std::max)(1u, config.count) * 16);
        if (config.count)
        {
            f.read((char *)weights.data(), config.count * 64);
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
        buffer(32 * 512 * 4, D3D11_BIND_UNORDERED_ACCESS, 16, nullptr, partials);
        buffer(16, D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE, 16, nullptr, result);
        buffer(UINT(weights.size() * 4), D3D11_BIND_SHADER_RESOURCE, 16, weights.data(), model);
        require(d->CreateUnorderedAccessView(partials.Get(), nullptr, &partialUav));
        require(d->CreateUnorderedAccessView(result.Get(), nullptr, &resultUav));
        require(d->CreateShaderResourceView(result.Get(), nullptr, &resultView));
        require(d->CreateShaderResourceView(model.Get(), nullptr, &modelView));
        if (config.pad)
        {
            std::vector<float> data(512 * 16);
            f.read((char *)data.data(), data.size() * 4);
            if (!f)
                throw std::runtime_error("Truncated PCA basis");
            buffer(UINT(data.size() * 4), D3D11_BIND_SHADER_RESOURCE, 16, data.data(), basis);
            require(d->CreateShaderResourceView(basis.Get(), nullptr, &basisView));
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
            ID3D11ShaderResourceView *resources[6]{};
            ID3D11UnorderedAccessView *uavs[2]{};
            ID3D11Buffer *buffer = nullptr;
            ID3D11SamplerState *sampler = nullptr;
            ComputeState(ID3D11DeviceContext *c) : context(c)
            {
                c->CSGetShader(&shader, nullptr, nullptr);
                c->CSGetShaderResources(0, 6, resources);
                c->CSGetUnorderedAccessViews(0, 2, uavs);
                c->CSGetConstantBuffers(1, 1, &buffer);
                c->CSGetSamplers(0, 1, &sampler);
            }
            ~ComputeState()
            {
                context->CSSetShader(shader, nullptr, 0);
                context->CSSetShaderResources(0, 6, resources);
                context->CSSetUnorderedAccessViews(0, 2, uavs, nullptr);
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
        c->CSSetUnorderedAccessViews(0, 2, uavs, nullptr);
        c->CSSetShader(features.Get(), nullptr, 0);
        c->Dispatch(8, 4, 1);
        c->CSSetShader(infer.Get(), nullptr, 0);
        c->Dispatch(1, 1, 1);
        ID3D11UnorderedAccessView *nil[2]{};
        c->CSSetUnorderedAccessViews(0, 2, nil, nullptr);
        ID3D11ShaderResourceView *empty[6]{};
        c->CSSetShaderResources(0, 6, empty);
        c->CSSetShader(nullptr, nullptr, 0);
        srvs[4] = resultView.Get();
        c->PSSetShaderResources(0, 6, srvs);
        c->PSSetConstantBuffers(1, 1, &cb);
        c->PSSetShader(pixel.Get(), nullptr, 0);
        return frame.view.Get();
    }
};
