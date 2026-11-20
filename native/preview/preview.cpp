#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdio>
#include <cmath>
#include <cstring>
#include <memory>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
struct Vertex { float position[3], normal[3], uv[2], colour[4]; }; // colour: particle / trail colour (COLOR0)
struct Surface { float maps[4], tint[4], motion[4], edge[4], modulation[4], uvTransform[4]; };
// material != 0 draws with a pixel shader composed from the game's own fragments (see MaterialShaderBuilder);
// slots are the texture ids bound to t0..t15 for it.
constexpr unsigned MaterialSlots = 16;
constexpr unsigned RefractionBlend = 7;
// normalOffset: world-space offset of the vertices along their normals (inflated outline shells of game materials).
struct Draw { unsigned first, count, base, glow, normal, reflectance, blend; Surface surface; unsigned mask, material, slots[MaterialSlots]; float normalOffset; unsigned pad[2]; };
// Matches cbuffer PmcFrame in the composed material shaders.
// Effect lights (skeleton lights of the scene's objects), world space: xyz position, w unused; rgb colour, a radius.
constexpr unsigned MaxEffectLights = 8;
struct MaterialFrame { float time[4], eye[4], lightDirection[4], lightColour[4], ambient[4], depthInfo[4], viewProjection[16],
    effectLightCount[4], effectLightPosition[MaxEffectLights][4], effectLightColour[MaxEffectLights][4]; };
struct Frame { float viewProjection[16], eye[4], settings[4], lightPosition[3][4], lightColour[3][4], depthInfo[4], postProcess[4]; };
static_assert(sizeof(Vertex) == 48 && sizeof(Surface) == 96 && sizeof(Draw) == 208 && sizeof(Frame) == 224 && sizeof(MaterialFrame) == 160 + 16 + MaxEffectLights * 32);

struct Renderer
{
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VertexShader> meshVertex, screenVertex;
    ComPtr<ID3D11PixelShader> meshPixel, finishPixel, backgroundPixel, bloomCutoffPixel, bloomHorizontalPixel, bloomVerticalPixel, refractPixel;
    ComPtr<ID3D11InputLayout> layout;
    ComPtr<ID3D11RasterizerState> raster;
    ComPtr<ID3D11DepthStencilState> depthWrite, depthRead;
    // additive/alpha/multiply serve the built-in shader; addOne/premultiplied/subtract are the game's modes.
    ComPtr<ID3D11BlendState> alpha, additive, multiply, addOne, premultiplied, subtract;
    ComPtr<ID3D11SamplerState> wrap, clamp, point, comparison;
    ComPtr<ID3D11Buffer> camera, surface, materialFrame, vertexEffects, vertices, indices;
    std::vector<ComPtr<ID3D11PixelShader>> materials;
    float effectLights[MaxEffectLights][8]{};
    unsigned effectLightCount = 0;
    ComPtr<ID3D11Texture2D> staging, colourTarget;
    // Ping-pong half-resolution HDR targets for the separable bloom filter.
    ComPtr<ID3D11RenderTargetView> bloomTarget, bloomScratchTarget;
    ComPtr<ID3D11ShaderResourceView> bloomView, bloomScratchView;
    unsigned bloomWidth = 0, bloomHeight = 0;
    ComPtr<ID3D11RenderTargetView> scene, output;
    ComPtr<ID3D11ShaderResourceView> sceneView, white, black;
    ComPtr<ID3D11Texture2D> distortionBuffer;
    ComPtr<ID3D11RenderTargetView> distortionTarget, refractedTarget;
    ComPtr<ID3D11ShaderResourceView> distortionView, refractedView;
    ComPtr<ID3D11DepthStencilView> depth, readOnlyDepth;
    ComPtr<ID3D11ShaderResourceView> depthView;
    std::vector<ComPtr<ID3D11ShaderResourceView>> textures;
    unsigned width = 0, height = 0, vertexCapacity = 0, indexCapacity = 0, sampleCount = 1;
    bool warp = false;

    HRESULT Texture(const void* pixels, unsigned w, unsigned h, bool srgb, bool mips, ID3D11ShaderResourceView** result)
    {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w; desc.Height = h; desc.MipLevels = mips ? 0 : 1; desc.ArraySize = 1;
        desc.SampleDesc.Count = 1;
        desc.Format = srgb ? DXGI_FORMAT_B8G8R8A8_UNORM_SRGB : DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (mips ? D3D11_BIND_RENDER_TARGET : 0);
        desc.MiscFlags = mips ? D3D11_RESOURCE_MISC_GENERATE_MIPS : 0;
        D3D11_SUBRESOURCE_DATA data{pixels, w * 4, 0};
        ComPtr<ID3D11Texture2D> texture;
        HRESULT hr = device->CreateTexture2D(&desc, mips ? nullptr : &data, &texture);
        if (FAILED(hr)) return hr;
        hr = device->CreateShaderResourceView(texture.Get(), nullptr, result);
        if (SUCCEEDED(hr) && mips)
        {
            context->UpdateSubresource(texture.Get(), 0, nullptr, pixels, w * 4, 0);
            context->GenerateMips(*result);
        }
        return hr;
    }

    HRESULT Init(const char* shader, unsigned shaderSize, char* error, unsigned errorSize, bool forceWarp)
    {
        D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
        auto create = [&](D3D_DRIVER_TYPE type) {
            return D3D11CreateDevice(nullptr, type, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, 1, D3D11_SDK_VERSION, &device, nullptr, &context);
        };
        HRESULT hr = forceWarp ? E_FAIL : create(D3D_DRIVER_TYPE_HARDWARE);
        if (FAILED(hr)) { warp = true; hr = create(D3D_DRIVER_TYPE_WARP); }
        if (FAILED(hr)) return hr;

        auto compile = [&](const char* entry, const char* profile, ID3DBlob** byteCode) {
            ComPtr<ID3DBlob> messages;
            HRESULT status = D3DCompile(shader, shaderSize, "Preview.hlsl", nullptr, nullptr, entry, profile,
                D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_ENABLE_STRICTNESS, 0, byteCode, &messages);
            if (FAILED(status) && messages && errorSize)
                snprintf(error, errorSize, "%s", static_cast<const char*>(messages->GetBufferPointer()));
            return status;
        };
        D3D11_INPUT_ELEMENT_DESC elements[] = {
            {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 32, D3D11_INPUT_PER_VERTEX_DATA, 0}
        };
        ComPtr<ID3DBlob> code;
#define CHECK(call) do { hr = (call); if (FAILED(hr)) return hr; } while (false)
        CHECK(compile("MeshVertex", "vs_5_0", &code));
        CHECK(device->CreateInputLayout(elements, 4, code->GetBufferPointer(), code->GetBufferSize(), &layout));
        CHECK(device->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &meshVertex));
        CHECK(compile("MeshPixel", "ps_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &meshPixel));
        CHECK(compile("ScreenVertex", "vs_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &screenVertex));
        CHECK(compile("FinishPixel", "ps_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &finishPixel));
        CHECK(compile("BackgroundPixel", "ps_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &backgroundPixel));
        CHECK(compile("BloomCutoffPixel", "ps_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &bloomCutoffPixel));
        CHECK(compile("BloomHorizontalPixel", "ps_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &bloomHorizontalPixel));
        CHECK(compile("BloomVerticalPixel", "ps_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &bloomVerticalPixel));
        CHECK(compile("RefractPixel", "ps_5_0", code.ReleaseAndGetAddressOf()));
        CHECK(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &refractPixel));

        D3D11_RASTERIZER_DESC rasterDesc{};
        rasterDesc.FillMode = D3D11_FILL_SOLID; rasterDesc.CullMode = D3D11_CULL_NONE;
        rasterDesc.DepthClipEnable = TRUE; rasterDesc.MultisampleEnable = TRUE;
        CHECK(device->CreateRasterizerState(&rasterDesc, &raster));
        D3D11_DEPTH_STENCIL_DESC depthDesc{};
        depthDesc.DepthEnable = TRUE; depthDesc.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
        depthDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
        CHECK(device->CreateDepthStencilState(&depthDesc, &depthWrite));
        depthDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        CHECK(device->CreateDepthStencilState(&depthDesc, &depthRead));

        D3D11_BLEND_DESC blend{};
        auto& target = blend.RenderTarget[0];
        target.BlendEnable = TRUE; target.SrcBlend = D3D11_BLEND_SRC_ALPHA;
        target.DestBlend = D3D11_BLEND_INV_SRC_ALPHA; target.BlendOp = D3D11_BLEND_OP_ADD;
        target.SrcBlendAlpha = D3D11_BLEND_ONE; target.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        target.BlendOpAlpha = D3D11_BLEND_OP_ADD; target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        CHECK(device->CreateBlendState(&blend, &alpha));
        target.DestBlend = D3D11_BLEND_ONE;
        target.SrcBlendAlpha = D3D11_BLEND_ZERO; target.DestBlendAlpha = D3D11_BLEND_ONE;
        CHECK(device->CreateBlendState(&blend, &additive));
        target.SrcBlend = D3D11_BLEND_DEST_COLOR; target.DestBlend = D3D11_BLEND_ZERO;
        CHECK(device->CreateBlendState(&blend, &multiply));
        // Plain one/one sum for the distortion buffer. Game additive materials (state 4) use `additive`
        // (src alpha, one): their masks write only albedo alpha (e.g. MASK_2.fxgraph) and the engine's
        // PixelOutputColor does not premultiply, so the blend state must apply the alpha.
        target.SrcBlend = D3D11_BLEND_ONE; target.DestBlend = D3D11_BLEND_ONE;
        CHECK(device->CreateBlendState(&blend, &addOne));
        target.SrcBlend = D3D11_BLEND_ONE; target.DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        target.SrcBlendAlpha = D3D11_BLEND_ONE; target.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        CHECK(device->CreateBlendState(&blend, &premultiplied));
        target.SrcBlend = D3D11_BLEND_SRC_ALPHA; target.DestBlend = D3D11_BLEND_ONE; target.BlendOp = D3D11_BLEND_OP_REV_SUBTRACT;
        target.SrcBlendAlpha = D3D11_BLEND_ZERO; target.DestBlendAlpha = D3D11_BLEND_ONE;
        CHECK(device->CreateBlendState(&blend, &subtract));

        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_ANISOTROPIC; sampler.MaxAnisotropy = 8;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_WRAP;
        sampler.ComparisonFunc = D3D11_COMPARISON_NEVER; sampler.MaxLOD = D3D11_FLOAT32_MAX;
        CHECK(device->CreateSamplerState(&sampler, &wrap));
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        CHECK(device->CreateSamplerState(&sampler, &clamp));
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
        CHECK(device->CreateSamplerState(&sampler, &point));
        sampler.Filter = D3D11_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT; sampler.ComparisonFunc = D3D11_COMPARISON_LESS_EQUAL;
        CHECK(device->CreateSamplerState(&sampler, &comparison));

        D3D11_BUFFER_DESC buffer{};
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER; buffer.ByteWidth = sizeof(Frame);
        CHECK(device->CreateBuffer(&buffer, nullptr, &camera));
        buffer.ByteWidth = sizeof(Surface);
        CHECK(device->CreateBuffer(&buffer, nullptr, &surface));
        buffer.ByteWidth = sizeof(MaterialFrame);
        CHECK(device->CreateBuffer(&buffer, nullptr, &materialFrame));
        buffer.ByteWidth = 16;
        CHECK(device->CreateBuffer(&buffer, nullptr, &vertexEffects));
        const unsigned whitePixel = 0xffffffff, clearPixel = 0;
        CHECK(Texture(&whitePixel, 1, 1, false, false, &white));
        CHECK(Texture(&clearPixel, 1, 1, false, false, &black));
        unsigned colourSamples = 0, depthSamples = 0;
        device->CheckMultisampleQualityLevels(DXGI_FORMAT_R16G16B16A16_FLOAT, 4, &colourSamples);
        device->CheckMultisampleQualityLevels(DXGI_FORMAT_D24_UNORM_S8_UINT, 4, &depthSamples);
        if (!warp && colourSamples && depthSamples) sampleCount = 4;
        return S_OK;
    }

    HRESULT Resize(unsigned w, unsigned h)
    {
        if (w == width && h == height) return S_OK;
        context->ClearState();
        width = height = 0;
        scene.Reset(); sceneView.Reset(); colourTarget.Reset(); depth.Reset(); readOnlyDepth.Reset(); depthView.Reset(); output.Reset(); staging.Reset();
        bloomTarget.Reset(); bloomView.Reset(); bloomScratchTarget.Reset(); bloomScratchView.Reset();
        distortionBuffer.Reset(); distortionTarget.Reset(); distortionView.Reset(); refractedTarget.Reset(); refractedView.Reset();
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w; desc.Height = h; desc.MipLevels = desc.ArraySize = 1;
        desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr;
        CHECK(device->CreateTexture2D(&desc, nullptr, &colourTarget));
        CHECK(device->CreateShaderResourceView(colourTarget.Get(), nullptr, &sceneView));
        if (sampleCount > 1)
        {
            ComPtr<ID3D11Texture2D> multisampled;
            desc.SampleDesc.Count = sampleCount;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET;
            CHECK(device->CreateTexture2D(&desc, nullptr, &multisampled));
            CHECK(device->CreateRenderTargetView(multisampled.Get(), nullptr, &scene));
        }
        else CHECK(device->CreateRenderTargetView(colourTarget.Get(), nullptr, &scene));

        desc.Format = DXGI_FORMAT_R24G8_TYPELESS; desc.BindFlags = D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> depthTexture;
        CHECK(device->CreateTexture2D(&desc, nullptr, &depthTexture));
        D3D11_DEPTH_STENCIL_VIEW_DESC depthDesc{};
        depthDesc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
        depthDesc.ViewDimension = sampleCount > 1 ? D3D11_DSV_DIMENSION_TEXTURE2DMS : D3D11_DSV_DIMENSION_TEXTURE2D;
        CHECK(device->CreateDepthStencilView(depthTexture.Get(), &depthDesc, &depth));
        depthDesc.Flags = D3D11_DSV_READ_ONLY_DEPTH | D3D11_DSV_READ_ONLY_STENCIL;
        CHECK(device->CreateDepthStencilView(depthTexture.Get(), &depthDesc, &readOnlyDepth));
        D3D11_SHADER_RESOURCE_VIEW_DESC depthReadDesc{};
        depthReadDesc.Format = DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
        depthReadDesc.ViewDimension = sampleCount > 1 ? D3D11_SRV_DIMENSION_TEXTURE2DMS : D3D11_SRV_DIMENSION_TEXTURE2D;
        if (sampleCount == 1) depthReadDesc.Texture2D.MipLevels = 1;
        CHECK(device->CreateShaderResourceView(depthTexture.Get(), &depthReadDesc, &depthView));

        {
            D3D11_TEXTURE2D_DESC bloom{};
            bloom.Width = bloomWidth = (w + 1) / 2; bloom.Height = bloomHeight = (h + 1) / 2;
            bloom.ArraySize = 1; bloom.SampleDesc.Count = 1;
            bloom.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            bloom.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            bloom.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
            ComPtr<ID3D11Texture2D> bright, scratch;
            CHECK(device->CreateTexture2D(&bloom, nullptr, &bright));
            D3D11_RENDER_TARGET_VIEW_DESC topMip{};
            topMip.Format = bloom.Format;
            topMip.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            CHECK(device->CreateRenderTargetView(bright.Get(), &topMip, &bloomTarget));
            CHECK(device->CreateShaderResourceView(bright.Get(), nullptr, &bloomView));
            bloom.MipLevels = 1;
            bloom.MiscFlags = 0;
            CHECK(device->CreateTexture2D(&bloom, nullptr, &scratch));
            CHECK(device->CreateRenderTargetView(scratch.Get(), nullptr, &bloomScratchTarget));
            CHECK(device->CreateShaderResourceView(scratch.Get(), nullptr, &bloomScratchView));
        }

        desc.SampleDesc.Count = 1; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> result;
        CHECK(device->CreateTexture2D(&desc, nullptr, &result));
        CHECK(device->CreateRenderTargetView(result.Get(), nullptr, &output));
        desc.BindFlags = 0; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        CHECK(device->CreateTexture2D(&desc, nullptr, &staging));
        width = w; height = h;
        return S_OK;
    }

    HRESULT EnsureRefraction()
    {
        if (refractedView) return S_OK;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width; desc.Height = height; desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        desc.SampleDesc.Count = 1;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> field, composite;
        ComPtr<ID3D11RenderTargetView> fieldTarget, compositeTarget;
        ComPtr<ID3D11ShaderResourceView> fieldView, compositeView;
        HRESULT hr;
        CHECK(device->CreateTexture2D(&desc, nullptr, &field));
        CHECK(device->CreateShaderResourceView(field.Get(), nullptr, &fieldView));
        CHECK(device->CreateTexture2D(&desc, nullptr, &composite));
        CHECK(device->CreateRenderTargetView(composite.Get(), nullptr, &compositeTarget));
        CHECK(device->CreateShaderResourceView(composite.Get(), nullptr, &compositeView));
        if (sampleCount > 1)
        {
            ComPtr<ID3D11Texture2D> msField;
            desc.SampleDesc.Count = sampleCount;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET;
            CHECK(device->CreateTexture2D(&desc, nullptr, &msField));
            CHECK(device->CreateRenderTargetView(msField.Get(), nullptr, &fieldTarget));
        }
        else CHECK(device->CreateRenderTargetView(field.Get(), nullptr, &fieldTarget));
        distortionBuffer = std::move(field);
        distortionTarget = std::move(fieldTarget);
        distortionView = std::move(fieldView);
        refractedTarget = std::move(compositeTarget);
        refractedView = std::move(compositeView);
        return S_OK;
    }

    HRESULT Upload(const void* data, unsigned bytes, unsigned bind, ComPtr<ID3D11Buffer>& buffer, unsigned& capacity)
    {
        HRESULT hr;
        if (capacity < bytes)
        {
            D3D11_BUFFER_DESC desc{};
            desc.ByteWidth = std::max(bytes, capacity * 2); desc.BindFlags = bind;
            desc.Usage = D3D11_USAGE_DYNAMIC; desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            ComPtr<ID3D11Buffer> next;
            CHECK(device->CreateBuffer(&desc, nullptr, &next));
            capacity = desc.ByteWidth;
            buffer = std::move(next);
        }
        D3D11_MAPPED_SUBRESOURCE map{};
        CHECK(context->Map(buffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &map));
        std::memcpy(map.pData, data, bytes);
        context->Unmap(buffer.Get(), 0);
        return S_OK;
    }

    ID3D11ShaderResourceView* Find(unsigned id, ID3D11ShaderResourceView* fallback)
    {
        return id && id <= textures.size() ? textures[id - 1].Get() : fallback;
    }

    HRESULT Render(const Frame& frame, const Vertex* points, unsigned pointCount,
        const unsigned* triangles, unsigned indexCount, const Draw* draws, unsigned drawCount,
        unsigned w, unsigned h, void* pixels, unsigned stride)
    {
        if (!pointCount || !indexCount || !w || !h || w > 2048 || h > 2048 || stride < w * 4) return E_INVALIDARG;
        HRESULT hr;
        CHECK(Resize(w, h));
        CHECK(Upload(points, pointCount * sizeof(Vertex), D3D11_BIND_VERTEX_BUFFER, vertices, vertexCapacity));
        CHECK(Upload(triangles, indexCount * sizeof(unsigned), D3D11_BIND_INDEX_BUFFER, indices, indexCapacity));
        Frame cameraData = frame;
        cameraData.depthInfo[2] = float(sampleCount);
        context->UpdateSubresource(camera.Get(), 0, nullptr, &cameraData, 0, 0);
        // Same key light as the built-in surface shader, so both paths sit in one scene.
        const bool warm = frame.settings[3] == 1.0f;
        const float kx = -.63f, ky = .76f, kz = -.53f, kl = 1.0f / sqrtf(kx * kx + ky * ky + kz * kz);
        MaterialFrame gameFrame{
            {frame.settings[0], frame.settings[3], float(w), float(h)}, {frame.eye[0], frame.eye[1], frame.eye[2], 1}, {kx * kl, ky * kl, kz * kl, 0},
            {warm ? 4.1f : 2.9f, warm ? 2.65f : 2.82f, warm ? 1.4f : 2.65f, 1}, {.36f, .39f, .46f, 1},
            {frame.depthInfo[0], frame.depthInfo[1], float(sampleCount), 0}, {}};
        std::memcpy(gameFrame.viewProjection, frame.viewProjection, sizeof(frame.viewProjection));
        gameFrame.effectLightCount[0] = float(effectLightCount);
        for (unsigned l = 0; l < effectLightCount; ++l)
        {
            std::memcpy(gameFrame.effectLightPosition[l], effectLights[l], 4 * sizeof(float));
            std::memcpy(gameFrame.effectLightColour[l], effectLights[l] + 4, 4 * sizeof(float));
        }
        context->UpdateSubresource(materialFrame.Get(), 0, nullptr, &gameFrame, 0, 0);
        ID3D11SamplerState* materialSamplers[] = {wrap.Get(), clamp.Get(), point.Get(), comparison.Get()};
        auto bindMaterial = [&](const Draw& draw)
        {
            context->PSSetShader(materials[draw.material - 1].Get(), nullptr, 0);
            context->PSSetConstantBuffers(0, 1, materialFrame.GetAddressOf());
            context->PSSetSamplers(0, 4, materialSamplers);
            ID3D11ShaderResourceView* slots[MaterialSlots];
            for (unsigned s = 0; s < MaterialSlots; ++s) slots[s] = Find(draw.slots[s], white.Get());
            context->PSSetShaderResources(0, MaterialSlots, slots);
        };
        bool materialBound = false;
        ID3D11Buffer* constants[] = {camera.Get(), surface.Get()};
        context->VSSetConstantBuffers(0, 1, constants);
        // Per-draw vertex offset at b1 of the mesh vertex shader; uploaded only when it changes.
        float currentEffects[4]{};
        context->UpdateSubresource(vertexEffects.Get(), 0, nullptr, currentEffects, 0, 0);
        context->VSSetConstantBuffers(1, 1, vertexEffects.GetAddressOf());
        auto setVertexEffects = [&](const Draw& draw)
        {
            if (currentEffects[0] == draw.normalOffset) return;
            currentEffects[0] = draw.normalOffset;
            context->UpdateSubresource(vertexEffects.Get(), 0, nullptr, currentEffects, 0, 0);
        };
        context->PSSetConstantBuffers(0, 2, constants);
        ID3D11SamplerState* samplers[] = {wrap.Get(), clamp.Get()};
        context->PSSetSamplers(0, 2, samplers);
        D3D11_VIEWPORT viewport{0, 0, float(w), float(h), 0, 1};
        context->RSSetViewports(1, &viewport);
        context->RSSetState(raster.Get());
        context->ClearDepthStencilView(depth.Get(), D3D11_CLEAR_DEPTH, 1, 0);
        context->OMSetRenderTargets(1, scene.GetAddressOf(), nullptr);
        context->OMSetBlendState(nullptr, nullptr, ~0u);
        context->IASetInputLayout(nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(screenVertex.Get(), nullptr, 0);
        context->PSSetShader(backgroundPixel.Get(), nullptr, 0);
        context->Draw(3, 0);
        context->OMSetRenderTargets(1, scene.GetAddressOf(), depth.Get());
        context->IASetInputLayout(layout.Get());
        UINT vertexStride = sizeof(Vertex), offset = 0;
        context->IASetVertexBuffers(0, 1, vertices.GetAddressOf(), &vertexStride, &offset);
        context->IASetIndexBuffer(indices.Get(), DXGI_FORMAT_R32_UINT, 0);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(meshVertex.Get(), nullptr, 0);
        context->PSSetShader(meshPixel.Get(), nullptr, 0);
        bool readingDepth = false;
        bool hasRefraction = false;
        // Resolve the base scene first. Multiplicative particle masks darken that scene before
        // luminous layers are added, even if the PET lists its darkening pass last.
        for (unsigned phase = 0; phase < 3; ++phase)
        for (unsigned i = 0; i < drawCount; ++i)
        {
            const Draw& draw = draws[i];
            if (!draw.count) continue;
            const bool floorOrShadow = !draw.material && (draw.surface.maps[3] == 5 || draw.surface.maps[3] == 6);
            const unsigned drawPhase = !draw.blend || floorOrShadow ? 0 : draw.blend == 3 ? 1 : 2;
            if (drawPhase != phase) continue;
            if (draw.first > indexCount || draw.count > indexCount - draw.first) return E_INVALIDARG;
            if (draw.blend == RefractionBlend)
            {
                hasRefraction |= draw.material != 0 && draw.material <= materials.size();
                continue;
            }
            bool useReadOnly = draw.blend != 0 && (draw.material != 0 || draw.surface.modulation[3] > 0);
            if (useReadOnly != readingDepth)
            {
                ID3D11ShaderResourceView* views[2]{};
                if (!useReadOnly) context->PSSetShaderResources(MaterialSlots, 2, views);
                context->OMSetRenderTargets(1, scene.GetAddressOf(), useReadOnly ? readOnlyDepth.Get() : depth.Get());
                if (useReadOnly)
                {
                    views[sampleCount > 1 ? 1 : 0] = depthView.Get();
                    context->PSSetShaderResources(MaterialSlots, 2, views);
                }
                readingDepth = useReadOnly;
            }
            context->OMSetDepthStencilState(draw.blend ? depthRead.Get() : depthWrite.Get(), 0);
            ID3D11BlendState* const blendStates[] = {nullptr, alpha.Get(), additive.Get(), multiply.Get(), additive.Get(), premultiplied.Get(), subtract.Get()};
            context->OMSetBlendState(draw.blend < 7 ? blendStates[draw.blend] : nullptr, nullptr, ~0u);
            setVertexEffects(draw);
            if (draw.material && draw.material <= materials.size())
            {
                bindMaterial(draw);
                materialBound = true;
                context->DrawIndexed(draw.count, draw.first, 0);
                continue;
            }
            if (materialBound)
            {
                context->PSSetShader(meshPixel.Get(), nullptr, 0);
                context->PSSetConstantBuffers(0, 2, constants);
                ID3D11ShaderResourceView* none[MaterialSlots]{};
                context->PSSetShaderResources(0, MaterialSlots, none);
                materialBound = false;
            }
            context->UpdateSubresource(surface.Get(), 0, nullptr, &draw.surface, 0, 0);
            ID3D11ShaderResourceView* maps[] = {Find(draw.base, white.Get()), Find(draw.glow, black.Get()),
                Find(draw.normal, black.Get()), Find(draw.reflectance, black.Get()), Find(draw.mask, white.Get())};
            context->PSSetShaderResources(0, 5, maps);
            context->DrawIndexed(draw.count, draw.first, 0);
        }

        if (hasRefraction)
        {
            CHECK(EnsureRefraction());
            const float zero[4]{};
            context->ClearRenderTargetView(distortionTarget.Get(), zero);
            context->OMSetRenderTargets(1, distortionTarget.GetAddressOf(), readOnlyDepth.Get());
            context->OMSetDepthStencilState(depthRead.Get(), 0);
            context->OMSetBlendState(addOne.Get(), nullptr, ~0u);
            ID3D11ShaderResourceView* views[2]{};
            views[sampleCount > 1 ? 1 : 0] = depthView.Get();
            context->PSSetShaderResources(MaterialSlots, 2, views);
            for (unsigned i = 0; i < drawCount; ++i)
            {
                const Draw& draw = draws[i];
                if (draw.blend != RefractionBlend || !draw.count || !draw.material || draw.material > materials.size()) continue;
                setVertexEffects(draw);
                bindMaterial(draw);
                context->DrawIndexed(draw.count, draw.first, 0);
            }
        }
        ID3D11ShaderResourceView* noDepth[2]{};
        context->PSSetShaderResources(MaterialSlots, 2, noDepth);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        if (sampleCount > 1)
        {
            ComPtr<ID3D11Resource> resource;
            scene->GetResource(&resource);
            context->ResolveSubresource(colourTarget.Get(), 0, resource.Get(), 0, DXGI_FORMAT_R16G16B16A16_FLOAT);
            if (hasRefraction)
            {
                distortionTarget->GetResource(resource.ReleaseAndGetAddressOf());
                context->ResolveSubresource(distortionBuffer.Get(), 0, resource.Get(), 0, DXGI_FORMAT_R16G16B16A16_FLOAT);
            }
        }
        context->OMSetBlendState(nullptr, nullptr, ~0u);
        context->OMSetDepthStencilState(nullptr, 0);
        context->IASetInputLayout(nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(screenVertex.Get(), nullptr, 0);
        context->PSSetConstantBuffers(0, 2, constants);
        context->PSSetSamplers(0, 2, samplers);
        ID3D11ShaderResourceView* empty[MaterialSlots]{};
        context->PSSetShaderResources(0, MaterialSlots, empty);
        ID3D11ShaderResourceView* hdrScene = sceneView.Get();
        if (hasRefraction)
        {
            context->OMSetRenderTargets(1, refractedTarget.GetAddressOf(), nullptr);
            context->PSSetShader(refractPixel.Get(), nullptr, 0);
            ID3D11ShaderResourceView* inputs[] = {hdrScene, distortionView.Get()};
            context->PSSetShaderResources(0, 2, inputs);
            context->Draw(3, 0);
            context->OMSetRenderTargets(0, nullptr, nullptr);
            context->PSSetShaderResources(0, 2, empty);
            hdrScene = refractedView.Get();
        }

        D3D11_VIEWPORT half{0, 0, float(bloomWidth), float(bloomHeight), 0, 1};
        context->RSSetViewports(1, &half);
        context->OMSetRenderTargets(1, bloomTarget.GetAddressOf(), nullptr);
        context->PSSetShader(bloomCutoffPixel.Get(), nullptr, 0);
        context->PSSetShaderResources(0, 1, &hdrScene);
        context->Draw(3, 0);
        if (frame.postProcess[0] > 0)
        {
            context->OMSetRenderTargets(0, nullptr, nullptr);
            context->GenerateMips(bloomView.Get());
        }
        else
        {
            context->OMSetRenderTargets(1, bloomScratchTarget.GetAddressOf(), nullptr);
            context->PSSetShader(bloomHorizontalPixel.Get(), nullptr, 0);
            context->PSSetShaderResources(0, 1, bloomView.GetAddressOf());
            context->Draw(3, 0);
            context->PSSetShaderResources(0, 1, empty);
            context->OMSetRenderTargets(1, bloomTarget.GetAddressOf(), nullptr);
            context->PSSetShader(bloomVerticalPixel.Get(), nullptr, 0);
            context->PSSetShaderResources(0, 1, bloomScratchView.GetAddressOf());
            context->Draw(3, 0);
        }
        context->OMSetRenderTargets(0, nullptr, nullptr);
        context->PSSetShaderResources(0, 1, empty);

        context->RSSetViewports(1, &viewport);
        context->OMSetRenderTargets(1, output.GetAddressOf(), nullptr);
        context->PSSetShader(finishPixel.Get(), nullptr, 0);
        ID3D11ShaderResourceView* finish[] = {hdrScene, bloomView.Get()};
        context->PSSetShaderResources(0, 2, finish);
        context->Draw(3, 0);
        context->PSSetShaderResources(0, MaterialSlots, empty);

        ComPtr<ID3D11Resource> result;
        output->GetResource(&result);
        context->CopyResource(staging.Get(), result.Get());
        D3D11_MAPPED_SUBRESOURCE map{};
        CHECK(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &map));
        for (unsigned row = 0; row < h; ++row)
            std::memcpy(static_cast<char*>(pixels) + row * stride, static_cast<const char*>(map.pData) + row * map.RowPitch, w * 4);
        context->Unmap(staging.Get(), 0);
        return S_OK;
    }
#undef CHECK
};
}

#define EXPORT extern "C" __declspec(dllexport)
EXPORT Renderer* preview_create(const char* shader, unsigned bytes, char* error, unsigned errorSize, int forceWarp)
{
    try
    {
        auto renderer = std::make_unique<Renderer>();
        HRESULT status = renderer->Init(shader, bytes, error, errorSize, forceWarp != 0);
        if (SUCCEEDED(status)) return renderer.release();
        if (errorSize && !error[0]) snprintf(error, errorSize, "Direct3D initialization failed (0x%08x)", unsigned(status));
    }
    catch (...) { if (errorSize) snprintf(error, errorSize, "Direct3D initialization failed"); }
    return nullptr;
}

EXPORT void preview_destroy(Renderer* renderer) { delete renderer; }
EXPORT int preview_is_software(Renderer* renderer) { return renderer->warp ? 1 : 0; }
// Effect lights for the next frames: count records of 8 floats (x, y, z, 0, r, g, b, radius) in world space.
EXPORT void preview_set_lights(Renderer* renderer, const float* lights, unsigned count)
{
    renderer->effectLightCount = std::min(count, MaxEffectLights);
    if (lights) std::memcpy(renderer->effectLights, lights, renderer->effectLightCount * 8 * sizeof(float));
}

EXPORT void preview_clear_textures(Renderer* renderer) { renderer->textures.clear(); renderer->materials.clear(); }

// Creates a pixel shader from compiled ps_5_0 bytecode of a composed game material. Returns its id (0 = failed).
EXPORT unsigned preview_material(Renderer* renderer, const void* bytecode, unsigned bytes)
{
    try
    {
        ComPtr<ID3D11PixelShader> shader;
        if (!bytecode || !bytes || FAILED(renderer->device->CreatePixelShader(bytecode, bytes, nullptr, &shader))) return 0;
        renderer->materials.push_back(std::move(shader));
        return unsigned(renderer->materials.size());
    }
    catch (...) { return 0; }
}

EXPORT unsigned preview_texture(Renderer* renderer, const void* pixels, unsigned w, unsigned h, int srgb)
{
    try
    {
        if (!w || !h || w > 8192 || h > 8192) return 0;
        ComPtr<ID3D11ShaderResourceView> texture;
        HRESULT status = renderer->Texture(pixels, w, h, srgb != 0, w > 1 && h > 1, &texture);
        if (FAILED(status)) status = renderer->Texture(pixels, w, h, srgb != 0, false, texture.ReleaseAndGetAddressOf());
        if (FAILED(status)) return 0;
        renderer->textures.push_back(std::move(texture));
        return unsigned(renderer->textures.size());
    }
    catch (...) { return 0; }
}

EXPORT HRESULT preview_draw(Renderer* renderer, const Frame* frame, const Vertex* points, unsigned pointCount,
    const unsigned* indices, unsigned indexCount, const Draw* draws, unsigned drawCount,
    unsigned w, unsigned h, void* pixels, unsigned stride)
{
    return renderer->Render(*frame, points, pointCount, indices, indexCount, draws, drawCount, w, h, pixels, stride);
}
