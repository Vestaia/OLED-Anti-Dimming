// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include "adaptive_filter.hpp"
#include "dwm_context_state.hpp"
#include <iostream>
#include <iterator>
#include <filesystem>
#include <cmath>
#include <DirectXPackedVector.h>
int main(int argc, char **argv)
{
    try
    {
        std::ifstream f("../native/adaptive_filter.hlsl");
        std::string shader((std::istreambuf_iterator<char>(f)), {});
        if (shader.empty())
            throw std::runtime_error("Run filter-test from build");
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> ctx;
        AdaptiveFilter::require(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                                                  nullptr, 0, D3D11_SDK_VERSION, &device, nullptr,
                                                  &ctx));
        AdaptiveFilter filter;
        std::wstring folder = argc > 1 ? std::filesystem::path(argv[1]).wstring() : L".";
        filter.initialize(device.Get(), folder.c_str(), shader.c_str());
        D3D11_SAMPLER_DESC s{};
        s.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        s.AddressU = s.AddressV = s.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        s.MaxLOD = D3D11_FLOAT32_MAX;
        ComPtr<ID3D11SamplerState> sampler;
        AdaptiveFilter::require(device->CreateSamplerState(&s, &sampler));
        bool benchmark = argc > 2 && std::string(argv[2]) == "--benchmark";
        D3D11_TEXTURE2D_DESC d{};
        d.Width = benchmark ? 2560 : 256;
        d.Height = benchmark ? 1440 : 144;
        d.MipLevels = d.ArraySize = 1;
        d.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
        d.SampleDesc.Count = 1;
        d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        std::vector<float> pixels(d.Width * d.Height * 4, 0);
        for (size_t i = 3; i < pixels.size(); i += 4)
            pixels[i] = 1;
        D3D11_SUBRESOURCE_DATA init{pixels.data(), d.Width * 16, 0};
        ComPtr<ID3D11Texture2D> scene;
        AdaptiveFilter::require(device->CreateTexture2D(&d, &init, &scene));
        RECT full{0, 0, LONG(d.Width), LONG(d.Height)};
        filter.prepare(device.Get(), ctx.Get(), scene.Get(), scene.Get(), &full, 1, nullptr, 0,
                       true, sampler.Get());
        D3D11_BUFFER_DESC bd{};
        filter.result->GetDesc(&bd);
        bd.Usage = D3D11_USAGE_STAGING;
        bd.BindFlags = bd.MiscFlags = bd.StructureByteStride = 0;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Buffer> staging;
        AdaptiveFilter::require(device->CreateBuffer(&bd, nullptr, &staging));
        ctx->CopyResource(staging.Get(), filter.result.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        AdaptiveFilter::require(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        float gain = *(float *)mapped.pData;
        ctx->Unmap(staging.Get(), 0);
        if (!std::isfinite(gain) || std::abs(gain - 1) > 1e-4)
            throw std::runtime_error("Black gain must be unity");
        std::ifstream weights(folder + L"\\runtime.bin", std::ios::binary);
        if (weights)
        {
            weights.seekg(76);
            std::vector<float> model(filter.config.count * 16);
            weights.read((char *)model.data(), model.size() * 4);
            auto evaluate = [&](double value)
            {
                for (size_t i = 0; i < pixels.size(); i += 4)
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = float(value * 250 / 80);
                ctx->UpdateSubresource(scene.Get(), 0, nullptr, pixels.data(), d.Width * 16, 0);
                filter.prepare(device.Get(), ctx.Get(), scene.Get(), scene.Get(), &full, 1, nullptr,
                               0, true, sampler.Get());
                ctx->CopyResource(staging.Get(), filter.result.Get());
                AdaptiveFilter::require(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
                float gpu = *(float *)mapped.pData;
                ctx->Unmap(staging.Get(), 0);
                double features[14]{value,
                                    value,
                                    value,
                                    value * value,
                                    value * value,
                                    value * value,
                                    value,
                                    value,
                                    value * value,
                                    value * value,
                                    0,
                                    double(value > .25),
                                    double(value > .5),
                                    double(value > .75)};
                if (filter.config.pad)
                {
                    double histogram[512]{};
                    double coordinate = (std::min)(7., sqrt((std::max)(value, 0.) / 40.) * 7);
                    int lower = (std::min)(6, int(coordinate));
                    double t = coordinate - lower;
                    for (int k = 0; k < 8; k++)
                    {
                        int r = k & 1, g = (k >> 1) & 1, b = (k >> 2) & 1;
                        histogram[(lower + r) * 64 + (lower + g) * 8 + lower + b] =
                            round((r ? t : 1 - t) * (g ? t : 1 - t) * (b ? t : 1 - t) * 4096.) /
                            4096.;
                    }
                    histogram[0] -= 1;
                    std::ifstream basisFile(folder + L"\\runtime.bin", std::ios::binary);
                    basisFile.seekg(76 + filter.config.count * 64);
                    std::vector<float> basis(512 * 16);
                    basisFile.read((char *)basis.data(), basis.size() * 4);
                    for (int j = 0; j < 14; j++)
                    {
                        features[j] = 0;
                        for (int bin = 0; bin < 512; bin++)
                            features[j] += histogram[bin] * basis[bin * 16 + j];
                    }
                }
                double logGain = -filter.config.baseline;
                double normalization = 0;
                for (unsigned n = 0; n < filter.config.count; n++)
                {
                    double distance = 0;
                    for (int j = 0; j < 14; j++)
                    {
                        double delta = features[j] / filter.config.scale[j] - model[n * 16 + j];
                        distance += delta * delta;
                    }
                    double weight = filter.config.localNeighbors ? 1 / pow(distance + .0001, 3) : exp(-.09 * distance);
                    logGain += model[n * 16 + 14] * weight;
                    normalization += weight;
                }
                if (filter.config.localNeighbors) logGain /= normalization;
                double cpu = exp((std::max)(logGain, 0.));
                if (std::abs(gpu - cpu) > 2e-4)
                    throw std::runtime_error("GPU/CPU moment-model disagreement");
                std::cout << "Neutral level " << value << ": GPU gain " << gpu << ", CPU " << cpu
                          << "\n";
            };
            for (UINT cells : {10000u, 25000u, 60000u, 17321u})
            {
                filter.samplingCells = cells;
                evaluate(.04);
                evaluate(.4);
                evaluate(1);
                if (filter.config.gridWidth > d.Width || filter.config.gridHeight > d.Height ||
                    filter.partialGroups != filter.config.groupsX * filter.config.groupsY)
                    throw std::runtime_error("Sampling grid bounds or allocation mismatch");
            }
            filter.samplingCells = 25000;
        }

        // Regression: restore bindings our original hook omitted (PS b1 / t2-t5,
        // viewport, topology). Exercise the real full-state isolation API.
        ComPtr<ID3D11Device> singleDevice;
        ComPtr<ID3D11DeviceContext> singleContext;
        AdaptiveFilter::require(D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_SINGLETHREADED, nullptr,
            0, D3D11_SDK_VERSION, &singleDevice, nullptr, &singleContext));
        DwmContextState singleIsolation;
        singleIsolation.initialize(singleDevice.Get(), singleContext.Get());
        {
            DwmContextState::Scope restore(singleIsolation);
        }
        std::cout << "PASS: single-threaded DWM-style device context state creation\n";
        DwmContextState isolation;
        isolation.initialize(device.Get(), ctx.Get());
        D3D11_BUFFER_DESC sentinelDesc{};
        sentinelDesc.ByteWidth = 16;
        sentinelDesc.Usage = D3D11_USAGE_DEFAULT;
        sentinelDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        ComPtr<ID3D11Buffer> sentinel;
        AdaptiveFilter::require(device->CreateBuffer(&sentinelDesc, nullptr, &sentinel));
        auto sentinelPtr = sentinel.Get();
        ctx->PSSetConstantBuffers(1, 1, &sentinelPtr);
        auto sentinelView = filter.basisView.Get();
        ctx->PSSetShaderResources(2, 1, &sentinelView);
        D3D11_VIEWPORT sentinelViewport{7, 11, 91, 63, .1f, .8f};
        ctx->RSSetViewports(1, &sentinelViewport);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
        {
            DwmContextState::Scope restore(isolation);
            filter.prepare(device.Get(), ctx.Get(), scene.Get(), scene.Get(), &full, 1, nullptr, 0,
                           true, sampler.Get());
            ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        }
        ComPtr<ID3D11Buffer> restoredBuffer;
        ctx->PSGetConstantBuffers(1, 1, &restoredBuffer);
        ComPtr<ID3D11ShaderResourceView> restoredView;
        ctx->PSGetShaderResources(2, 1, &restoredView);
        UINT viewportCount = 1;
        D3D11_VIEWPORT restoredViewport{};
        ctx->RSGetViewports(&viewportCount, &restoredViewport);
        D3D11_PRIMITIVE_TOPOLOGY restoredTopology;
        ctx->IAGetPrimitiveTopology(&restoredTopology);
        if (restoredBuffer.Get() != sentinel.Get() ||
            restoredView.Get() != filter.basisView.Get() ||
            memcmp(&sentinelViewport, &restoredViewport, sizeof(sentinelViewport)) ||
            restoredTopology != D3D11_PRIMITIVE_TOPOLOGY_LINELIST)
            throw std::runtime_error("DWM graphics state was not fully restored");
        std::cout
            << "PASS: full DWM context state restoration, including PS b1/t2 and raster/IA state\n";
        // Render an HDR ramp through the actual pixel shader. Every output must be
        // input * the single inferred gain, including negative scRGB and >1 values.
        for (UINT y = 0; y < d.Height; y++)
            for (UINT x = 0; x < d.Width; x++)
            {
                auto i = (y * d.Width + x) * 4;
                pixels[i] = float(x) / 255 * 12;
                pixels[i + 1] = float(x) / 255 * 3;
                pixels[i + 2] = float(x) / 255 * 1.5f - .05f;
                pixels[i + 3] = 1;
            }
        ctx->UpdateSubresource(scene.Get(), 0, nullptr, pixels.data(), d.Width * 16, 0);
        D3D11_TEXTURE2D_DESC outputDesc = d;
        outputDesc.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> output;
        AdaptiveFilter::require(device->CreateTexture2D(&outputDesc, nullptr, &output));
        ComPtr<ID3D11RenderTargetView> target;
        AdaptiveFilter::require(device->CreateRenderTargetView(output.Get(), nullptr, &target));
        auto rt = target.Get();
        ctx->OMSetRenderTargets(1, &rt, nullptr);
        const char *vs = "float4 VS(uint id:SV_VertexID):SV_POSITION {return "
                         "float4(id==2?3:-1,id==1?3:-1,0,1);}";
        ComPtr<ID3DBlob> vb, errors;
        AdaptiveFilter::require(D3DCompile(vs, strlen(vs), nullptr, nullptr, nullptr, "VS",
                                           "vs_5_0", 0, 0, &vb, &errors));
        ComPtr<ID3D11VertexShader> vertex;
        AdaptiveFilter::require(device->CreateVertexShader(vb->GetBufferPointer(),
                                                           vb->GetBufferSize(), nullptr, &vertex));
        ctx->VSSetShader(vertex.Get(), nullptr, 0);
        ctx->IASetInputLayout(nullptr);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11_VIEWPORT viewport{0, 0, FLOAT(d.Width), FLOAT(d.Height), 0, 1};
        ctx->RSSetViewports(1, &viewport);
        filter.prepare(device.Get(), ctx.Get(), scene.Get(), scene.Get(), &full, 1, nullptr, 0,
                       true, sampler.Get());
        ctx->Draw(3, 0);
        ctx->CopyResource(staging.Get(), filter.result.Get());
        AdaptiveFilter::require(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        float rampGain = *(float *)mapped.pData;
        ctx->Unmap(staging.Get(), 0);
        outputDesc.BindFlags = 0;
        outputDesc.Usage = D3D11_USAGE_STAGING;
        outputDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> readback;
        AdaptiveFilter::require(device->CreateTexture2D(&outputDesc, nullptr, &readback));
        ctx->CopyResource(readback.Get(), output.Get());
        AdaptiveFilter::require(ctx->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        double worst = 0;
        for (UINT y = 0; y < d.Height; y++)
        {
            auto row = (float *)((char *)mapped.pData + y * mapped.RowPitch);
            for (UINT x = 0; x < d.Width; x++)
                for (UINT k = 0; k < 3; k++)
                    worst =
                        (std::max)(worst,
                                   double(std::abs(row[x * 4 + k] -
                                                   pixels[(y * d.Width + x) * 4 + k] * rampGain)));
        }
        ctx->Unmap(readback.Get(), 0);
        if (worst > 2e-5)
            throw std::runtime_error("Pixel output is not a uniform linear scene gain");
        std::cout << "PASS: HDR pixel ramp preserves channel ratios, negative scRGB and "
                     "highlights; maximum error "
                  << worst << "\n";

        if (benchmark)
        {
            // DWM HDR uses FP16 scRGB; correctness tests above deliberately use FP32.
            auto halfDesc = d;
            halfDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            std::vector<DirectX::PackedVector::HALF> halfPixels(pixels.size());
            for (size_t i = 0; i < pixels.size(); i++)
                halfPixels[i] = DirectX::PackedVector::XMConvertFloatToHalf(pixels[i]);
            D3D11_SUBRESOURCE_DATA halfData{halfPixels.data(), d.Width * 8, 0};
            ComPtr<ID3D11Texture2D> halfScene, halfOutput;
            AdaptiveFilter::require(device->CreateTexture2D(&halfDesc, &halfData, &halfScene));
            halfDesc.BindFlags = D3D11_BIND_RENDER_TARGET;
            AdaptiveFilter::require(device->CreateTexture2D(&halfDesc, nullptr, &halfOutput));
            ComPtr<ID3D11RenderTargetView> halfTarget;
            AdaptiveFilter::require(
                device->CreateRenderTargetView(halfOutput.Get(), nullptr, &halfTarget));
            auto halfRt = halfTarget.Get();
            ctx->OMSetRenderTargets(1, &halfRt, nullptr);
            ComPtr<IDXGIDevice> dxgi;
            device.As(&dxgi);
            ComPtr<IDXGIAdapter> adapter;
            dxgi->GetAdapter(&adapter);
            DXGI_ADAPTER_DESC ad{};
            adapter->GetDesc(&ad);
            std::wcout << L"Adapter: " << ad.Description << L"\n";
            ComPtr<ID3D11Query> disjoint, start, end;
            D3D11_QUERY_DESC q{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
            device->CreateQuery(&q, &disjoint);
            q.Query = D3D11_QUERY_TIMESTAMP;
            device->CreateQuery(&q, &start);
            device->CreateQuery(&q, &end);
            for (int i = 0; i < 2000; i++)
            {
                filter.prepare(device.Get(), ctx.Get(), halfScene.Get(), halfScene.Get(), &full, 1,
                               nullptr, 0, true, sampler.Get());
                ctx->Draw(3, 0);
            }
            ctx->Begin(disjoint.Get());
            ctx->End(start.Get());
            for (int i = 0; i < 1000; i++)
            {
                filter.prepare(device.Get(), ctx.Get(), halfScene.Get(), halfScene.Get(), &full, 1,
                               nullptr, 0, true, sampler.Get());
                ctx->Draw(3, 0);
            }
            ctx->End(end.Get());
            ctx->End(disjoint.Get());
            ctx->Flush();
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT timing{};
            UINT64 a = 0, b = 0;
            while (ctx->GetData(disjoint.Get(), &timing, sizeof(timing), 0) == S_FALSE)
                Sleep(1);
            AdaptiveFilter::require(ctx->GetData(start.Get(), &a, sizeof(a), 0));
            AdaptiveFilter::require(ctx->GetData(end.Get(), &b, sizeof(b), 0));
            if (timing.Disjoint)
                throw std::runtime_error("Disjoint GPU timing");
            std::cout << "1440p full-copy + analysis + inference + linear filter: "
                      << double(b - a) / timing.Frequency * 1e6 / 1000
                      << " us/frame (1000-frame batch after 2000 warm-up frames, FP16 surfaces, "
                      << filter.config.count << " centers)\n";
            ctx->Begin(disjoint.Get());
            ctx->End(start.Get());
            for (int i = 0; i < 1000; i++)
                ctx->Draw(3, 0);
            ctx->End(end.Get());
            ctx->End(disjoint.Get());
            ctx->Flush();
            while (ctx->GetData(disjoint.Get(), &timing, sizeof(timing), 0) == S_FALSE)
                Sleep(1);
            AdaptiveFilter::require(ctx->GetData(start.Get(), &a, sizeof(a), 0));
            AdaptiveFilter::require(ctx->GetData(end.Get(), &b, sizeof(b), 0));
            std::cout << "FP16 linear pixel pass alone: "
                      << double(b - a) / timing.Frequency * 1e6 / 1000 << " us/frame\n";
            ID3D11ShaderResourceView *noPixel[6]{};
            ctx->PSSetShaderResources(0, 6, noPixel);
            ID3D11ShaderResourceView *resources[6]{filter.frames[halfScene.Get()].view.Get(),
                                                   nullptr,
                                                   filter.basisView.Get(),
                                                   filter.modelView.Get(),
                                                   nullptr,
                                                   nullptr};
            ctx->CSSetShaderResources(0, 6, resources);
            ID3D11UnorderedAccessView *uavs[2]{filter.partialUav.Get(), filter.resultUav.Get()};
            ctx->CSSetUnorderedAccessViews(0, 2, uavs, nullptr);
            auto cb = filter.constants.Get();
            ctx->CSSetConstantBuffers(1, 1, &cb);
            ctx->Begin(disjoint.Get());
            ctx->End(start.Get());
            for (int i = 0; i < 1000; i++)
            {
                ctx->CSSetShader(filter.features.Get(), nullptr, 0);
                ctx->Dispatch(filter.config.groupsX, filter.config.groupsY, 1);
                ctx->CSSetShader(filter.infer.Get(), nullptr, 0);
                ctx->Dispatch(1, 1, 1);
            }
            ctx->End(end.Get());
            ctx->End(disjoint.Get());
            ctx->Flush();
            while (ctx->GetData(disjoint.Get(), &timing, sizeof(timing), 0) == S_FALSE)
                Sleep(1);
            AdaptiveFilter::require(ctx->GetData(start.Get(), &a, sizeof(a), 0));
            AdaptiveFilter::require(ctx->GetData(end.Get(), &b, sizeof(b), 0));
            std::cout << "Scene analysis + inference alone: "
                      << double(b - a) / timing.Frequency * 1e6 / 1000 << " us/frame\n";
            ID3D11UnorderedAccessView *nil[2]{};
            ctx->CSSetUnorderedAccessViews(0, 2, nil, nullptr);
            ID3D11ShaderResourceView *empty[6]{};
            ctx->CSSetShaderResources(0, 6, empty);
            ctx->CSSetShader(nullptr, nullptr, 0);
            ctx->OMSetRenderTargets(1, &rt, nullptr);
        }
        // Nonlinear device curve must precede the uniform gain, not follow it.
        constexpr UINT curveSize = 4096;
        std::vector<float> curve(curveSize * 4);
        for (UINT i = 0; i < curveSize; i++)
        {
            float v = float(i) / (curveSize - 1);
            curve[i * 4] = curve[i * 4 + 1] = curve[i * 4 + 2] = v * v;
            curve[i * 4 + 3] = 1;
        }
        D3D11_TEXTURE1D_DESC curveDesc{};
        curveDesc.Width = curveSize;
        curveDesc.MipLevels = curveDesc.ArraySize = 1;
        curveDesc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
        curveDesc.Usage = D3D11_USAGE_IMMUTABLE;
        curveDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA curveData{curve.data(), 0, 0};
        ComPtr<ID3D11Texture1D> curveTexture;
        AdaptiveFilter::require(device->CreateTexture1D(&curveDesc, &curveData, &curveTexture));
        AdaptiveFilter::require(
            device->CreateShaderResourceView(curveTexture.Get(), nullptr, &filter.vcgtView));
        filter.config.vcgt = 1;
        for (size_t i = 0; i < pixels.size(); i += 4)
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 4;
        ctx->UpdateSubresource(scene.Get(), 0, nullptr, pixels.data(), d.Width * 16, 0);
        filter.prepare(device.Get(), ctx.Get(), scene.Get(), scene.Get(), &full, 1, nullptr, 0,
                       true, sampler.Get());
        float forcedGain[4]{2, 0, 0, 0};
        ctx->UpdateSubresource(filter.result.Get(), 0, nullptr, forcedGain, 0, 0);
        ctx->Draw(3, 0);
        ctx->CopyResource(readback.Get(), output.Get());
        AdaptiveFilter::require(ctx->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        float actual = *(float *)mapped.pData;
        ctx->Unmap(readback.Get(), 0);
        auto pq = [](double nits)
        {
            double v = pow(nits / 10000, .1593017578125);
            return pow((.8359375 + 18.8515625 * v) / (1 + 18.6875 * v), 78.84375);
        };
        auto unpq = [](double code)
        {
            double v = pow(code, 1 / 78.84375);
            return 10000 * pow((std::max)(v - .8359375, 0.) / (18.8515625 - 18.6875 * v),
                               1 / .1593017578125);
        };
        double code = pq(320);
        double position = code * (curveSize - 1);
        UINT lower = UINT(position);
        double fraction = position - lower;
        double mappedCode = curve[lower * 4] * (1 - fraction) + curve[(lower + 1) * 4] * fraction;
        double expected = unpq(mappedCode) / 80 * 2;
        if (std::abs(actual - expected) > 2e-4)
            throw std::runtime_error("VCGT was not applied before the linear gain");
        std::cout << "PASS: nonlinear VCGT precedes uniform linear gain; output " << actual
                  << ", expected " << expected << "\n";
        std::cout << "PASS: GPU shader compilation, reduction/inference, black anchor and CPU "
                     "agreement\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << "\n";
        return 1;
    }
}
