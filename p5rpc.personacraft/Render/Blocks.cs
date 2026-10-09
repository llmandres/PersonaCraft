using System.Numerics;
using p5rpc.personacraft.Game;
using p5rpc.personacraft.Link;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace p5rpc.personacraft.Render;

/// <summary>
/// Minecraft's placed blocks, and in third person the player's own model, drawn over P5R's picture
/// with the same camera P5R is given.
///
/// Minecraft sends each 16-block section as a triangle list (built by its own block renderer: models,
/// tint, ambient occlusion) relative to the section's corner, plus its block atlas. Sections become
/// vertex buffers; drawing is camera-relative so the large field offsets cost no precision.
/// Cutout texels are discarded in the pixel shader; translucent faces (water, glass) are blended in a
/// second pass without depth writes. Blocks have their own depth buffer: they hide each other but not
/// P5R's geometry yet (P5R's depth buffer is not used).
/// </summary>
internal sealed unsafe class Blocks : IDisposable
{
    private const string Shader = @"
cbuffer Frame : register(b0) { row_major float4x4 viewProj; float4 offset; float4 scale; };
Texture2D atlas : register(t0);
SamplerState smp : register(s0);
struct VIn { float3 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 col : COLOR0; };
V vs(VIn i)
{
    V o;
    o.pos = mul(float4(i.pos * scale.xyz + offset.xyz, 1), viewProj);
    o.uv = i.uv;
    o.col = i.col;
    return o;
}
float4 ps(V i) : SV_Target
{
    float4 c = atlas.Sample(smp, i.uv) * i.col;
    if (c.a < 0.1) discard;
    return c;
}";

    private sealed class Section(ID3D11Buffer? solid, uint solidCount, ID3D11Buffer? translucent, uint translucentCount)
    {
        public readonly ID3D11Buffer? Solid = solid;
        public readonly uint SolidCount = solidCount;
        public readonly ID3D11Buffer? Translucent = translucent;
        public readonly uint TranslucentCount = translucentCount;

        public void Dispose()
        {
            Solid?.Dispose();
            Translucent?.Dispose();
        }
    }

    private readonly ID3D11Device1 _device;
    private readonly Dictionary<(int, int, int), Section> _sections = new();
    private readonly List<Proto.RenVertex> _solid = new();
    private readonly List<Proto.RenVertex> _translucent = new();
    private ID3D11Texture2D? _atlas;
    private ID3D11ShaderResourceView? _atlasView;
    private uint _atlasW, _atlasH;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11DepthStencilState _depthWrite, _depthRead;
    private readonly ID3D11BlendState _opaque, _blend;
    private readonly Dictionary<uint, (ID3D11Texture2D Texture, ID3D11ShaderResourceView View)> _textures = new();
    private Proto.RenBatch[] _avatarBatches = [];
    private uint _avatarBatchCount;
    private ID3D11Buffer? _avatarBuffer;
    private uint _avatarCapacity, _avatarVertices;
    private ID3D11Texture2D? _depthTexture;
    private ID3D11DepthStencilView? _dsv;
    private int _depthW, _depthH;

    public Blocks(ID3D11Device1 device)
    {
        _device = device;
        var vsCode = Compiler.Compile(Shader, "vs", "blocks", "vs_4_0");
        var psCode = Compiler.Compile(Shader, "ps", "blocks", "ps_4_0");
        _vs = device.CreateVertexShader(vsCode.Span);
        _ps = device.CreatePixelShader(psCode.Span);
        _layout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 20, 0),
        ], vsCode.Span);
        _constants = device.CreateBuffer(new BufferDescription(96, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _raster = device.CreateRasterizerState(RasterizerDescription.CullNone);
        _depthWrite = device.CreateDepthStencilState(DepthStencilDescription.Default);
        _depthRead = device.CreateDepthStencilState(DepthStencilDescription.DepthRead);
        _opaque = device.CreateBlendState(BlendDescription.Opaque);
        _blend = device.CreateBlendState(BlendDescription.NonPremultiplied);
    }

    public int Count => _sections.Count;

    /// <summary>One render-ring message, on the render thread.</summary>
    public void Handle(ID3D11DeviceContext context, uint type, byte* payload, uint bytes)
    {
        switch (type)
        {
            case Proto.RenAtlas:
                SetAtlas(payload, bytes);
                break;
            case Proto.RenAtlasRegion:
                SetAtlasRegion(context, payload, bytes);
                break;
            case Proto.RenSection:
                if (bytes >= (uint)sizeof(Proto.RenSectionHdr))
                {
                    var header = *(Proto.RenSectionHdr*)payload;
                    uint available = (bytes - (uint)sizeof(Proto.RenSectionHdr)) / (uint)sizeof(Proto.RenVertex);
                    SetSection(header, (Proto.RenVertex*)(payload + sizeof(Proto.RenSectionHdr)), Math.Min(header.vertexCount, available));
                }
                break;
            case Proto.RenClearAll:
                Clear();
                break;
            case Proto.RenTexture:
                SetTexture(payload, bytes);
                break;
            case Proto.RenAvatar:
                SetAvatar(context, payload, bytes);
                break;
        }
    }

    public void Clear()
    {
        foreach (var section in _sections.Values)
            section.Dispose();
        _sections.Clear();
        _avatarBatchCount = 0;
    }

    private void SetAtlas(byte* payload, uint bytes)
    {
        if (bytes < 8)
            return;
        var header = *(Proto.RenAtlasHdr*)payload;
        if (header.width == 0 || header.height == 0 || 8 + (ulong)header.width * header.height * 4 > bytes)
            return;
        _atlasView?.Dispose();
        _atlas?.Dispose();
        _atlas = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, header.width, header.height, 1, 1, BindFlags.ShaderResource),
            [new SubresourceData(payload + 8, header.width * 4)]);
        _atlasView = _device.CreateShaderResourceView(_atlas);
        _atlasW = header.width;
        _atlasH = header.height;
        Log.Info($"blocks: atlas {header.width}x{header.height}");
    }

    /// <summary>An entity texture (player skin, armour) by id, for the avatar's batches.</summary>
    private void SetTexture(byte* payload, uint bytes)
    {
        if (bytes < (uint)sizeof(Proto.RenTextureHdr))
            return;
        var header = *(Proto.RenTextureHdr*)payload;
        if (header.id == 0 || header.width == 0 || header.height == 0
            || (ulong)sizeof(Proto.RenTextureHdr) + (ulong)header.width * header.height * 4 > bytes)
            return;
        if (_textures.Remove(header.id, out var old))
        {
            old.View.Dispose();
            old.Texture.Dispose();
        }
        var texture = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, header.width, header.height, 1, 1, BindFlags.ShaderResource),
            [new SubresourceData(payload + sizeof(Proto.RenTextureHdr), header.width * 4)]);
        _textures[header.id] = (texture, _device.CreateShaderResourceView(texture));
    }

    /// <summary>The player's posed model this frame, relative to its feet; 0 batches = not shown.</summary>
    private void SetAvatar(ID3D11DeviceContext context, byte* payload, uint bytes)
    {
        _avatarBatchCount = 0;
        if (bytes < (uint)sizeof(Proto.RenAvatarHdr))
            return;
        var header = *(Proto.RenAvatarHdr*)payload;
        ulong need = (ulong)sizeof(Proto.RenAvatarHdr) + (ulong)header.batchCount * (ulong)sizeof(Proto.RenBatch)
                     + (ulong)header.vertexCount * (ulong)sizeof(Proto.RenVertex);
        if (header.batchCount == 0 || header.vertexCount == 0 || need > bytes)
            return;
        var batches = (Proto.RenBatch*)(payload + sizeof(Proto.RenAvatarHdr));
        if (_avatarBatches.Length < header.batchCount)
            _avatarBatches = new Proto.RenBatch[header.batchCount];
        for (int b = 0; b < header.batchCount; b++)
            _avatarBatches[b] = batches[b];
        if (_avatarBuffer == null || _avatarCapacity < header.vertexCount)
        {
            _avatarBuffer?.Dispose();
            _avatarCapacity = Math.Max(header.vertexCount, 4096u);
            _avatarBuffer = _device.CreateBuffer(new BufferDescription(_avatarCapacity * (uint)sizeof(Proto.RenVertex),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        var mapped = context.Map(_avatarBuffer, 0, MapMode.WriteDiscard);
        uint size = header.vertexCount * (uint)sizeof(Proto.RenVertex);
        Buffer.MemoryCopy(batches + header.batchCount, (void*)mapped.DataPointer, size, size);
        context.Unmap(_avatarBuffer, 0);
        _avatarVertices = header.vertexCount;
        _avatarBatchCount = header.batchCount;
    }

    private void SetAtlasRegion(ID3D11DeviceContext context, byte* payload, uint bytes)
    {
        if (_atlas == null || bytes < 16)
            return;
        var r = *(Proto.RenAtlasRegionHdr*)payload;
        if (r.width == 0 || r.height == 0 || r.x + r.width > _atlasW || r.y + r.height > _atlasH || 16 + (ulong)r.width * r.height * 4 > bytes)
            return;
        var box = new Box((int)r.x, (int)r.y, 0, (int)(r.x + r.width), (int)(r.y + r.height), 1);
        context.UpdateSubresource(_atlas, 0, box, (nint)(payload + 16), r.width * 4, 0);
    }

    private void SetSection(in Proto.RenSectionHdr header, Proto.RenVertex* vertices, uint count)
    {
        var key = (header.sx, header.sy, header.sz);
        if (_sections.Remove(key, out var old))
            old.Dispose();
        count = count / 3 * 3;
        if (count == 0)
            return;
        _solid.Clear();
        _translucent.Clear();
        for (uint i = 0; i < count; i += 3)
        {
            var list = (vertices[i].flags & 2) != 0 ? _translucent : _solid;
            list.Add(vertices[i]);
            list.Add(vertices[i + 1]);
            list.Add(vertices[i + 2]);
        }
        _sections[key] = new Section(MakeBuffer(_solid), (uint)_solid.Count, MakeBuffer(_translucent), (uint)_translucent.Count);
    }

    private ID3D11Buffer? MakeBuffer(List<Proto.RenVertex> vertices)
    {
        if (vertices.Count == 0)
            return null;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);
        return _device.CreateBuffer(span, BindFlags.VertexBuffer);
    }

    /// <summary>Draws every section into the bound render target, with the camera Minecraft drives.</summary>
    public void Draw(ID3D11DeviceContext context, ID3D11RenderTargetView rtv, int width, int height, CameraView view)
    {
        bool avatar = view.CameraMode != 0 && _avatarBatchCount > 0 && _avatarBuffer != null;
        if (_atlasView == null || (_sections.Count == 0 && !avatar) || width <= 0 || height <= 0)
            return;
        EnsureDepth(width, height);
        context.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1f, 0);
        context.OMSetRenderTargets(rtv, _dsv);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.RSSetState(_raster);
        context.IASetInputLayout(_layout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vs);
        context.VSSetConstantBuffer(0, _constants);
        context.PSSetShader(_ps);
        context.PSSetSampler(0, _sampler);
        context.PSSetShaderResource(0, _atlasView);

        var forward = World.Look.Forward(view.Yaw, view.Pitch);
        var viewMatrix = Matrix4x4.CreateLookAt(Vector3.Zero, forward, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(view.FovY * MathF.PI / 180f, (float)width / height, 0.05f, 2000f);
        var viewProj = viewMatrix * projection;

        context.OMSetBlendState(_opaque);
        context.OMSetDepthStencilState(_depthWrite);
        foreach (var (key, section) in _sections)
        {
            if (section.Solid != null)
                DrawSection(context, key, section.Solid, section.SolidCount, view, viewProj);
        }
        if (avatar)
            DrawAvatar(context, view, viewProj, translucent: false);
        context.OMSetBlendState(_blend);
        context.OMSetDepthStencilState(_depthRead);
        context.PSSetShaderResource(0, _atlasView);
        foreach (var (key, section) in _sections)
        {
            if (section.Translucent != null)
                DrawSection(context, key, section.Translucent, section.TranslucentCount, view, viewProj);
        }
        if (avatar)
            DrawAvatar(context, view, viewProj, translucent: true);
        context.OMSetRenderTargets(rtv);
    }

    /// <summary>Minecraft's player model at its feet, one draw per batch with that batch's texture.</summary>
    private void DrawAvatar(ID3D11DeviceContext context, CameraView view, Matrix4x4 viewProj, bool translucent)
    {
        SetConstants(context, viewProj, new Vector4((float)(view.FeetX - view.X), (float)(view.FeetY - view.Y), (float)(view.FeetZ - view.Z), 0));
        context.IASetVertexBuffer(0, _avatarBuffer!, (uint)sizeof(Proto.RenVertex));
        for (int b = 0; b < _avatarBatchCount; b++)
        {
            var batch = _avatarBatches[b];
            if (((batch.flags & 1) != 0) != translucent)
                continue;
            ID3D11ShaderResourceView? texture = batch.texture == 0 ? _atlasView
                : _textures.TryGetValue(batch.texture, out var t) ? t.View : null;
            if (texture == null || batch.first >= _avatarVertices)
                continue;
            uint count = Math.Min(batch.count, _avatarVertices - batch.first) / 3 * 3;
            context.PSSetShaderResource(0, texture);
            context.Draw(count, batch.first);
        }
        context.PSSetShaderResource(0, _atlasView);
    }

    private void SetConstants(ID3D11DeviceContext context, Matrix4x4 viewProj, Vector4 offset, Vector4? scale = null)
    {
        var mapped = context.Map(_constants, 0, MapMode.WriteDiscard);
        *(Matrix4x4*)mapped.DataPointer = viewProj;
        *(Vector4*)((byte*)mapped.DataPointer + 64) = offset;
        *(Vector4*)((byte*)mapped.DataPointer + 80) = scale ?? Vector4.One;
        context.Unmap(_constants, 0);
    }

    private void DrawSection(ID3D11DeviceContext context, (int X, int Y, int Z) key, ID3D11Buffer buffer, uint count, CameraView view, Matrix4x4 viewProj)
    {
        var offset = new Vector4((float)(key.X * 16.0 - view.X), (float)(key.Y * 16.0 - view.Y), (float)(key.Z * 16.0 - view.Z), 0);
        if (offset.LengthSquared() > 256f * 256f)
            return; // far away: skip
        SetConstants(context, viewProj, offset);
        context.IASetVertexBuffer(0, buffer, (uint)sizeof(Proto.RenVertex));
        context.Draw(count, 0);
    }

    private void EnsureDepth(int width, int height)
    {
        if (_dsv != null && width == _depthW && height == _depthH)
            return;
        _dsv?.Dispose();
        _depthTexture?.Dispose();
        _depthTexture = _device.CreateTexture2D(new Texture2DDescription(Format.D32_Float, (uint)width, (uint)height, 1, 1, BindFlags.DepthStencil));
        _dsv = _device.CreateDepthStencilView(_depthTexture);
        _depthW = width;
        _depthH = height;
    }

    public void Dispose()
    {
        Clear();
        foreach (var (texture, textureView) in _textures.Values)
        {
            textureView.Dispose();
            texture.Dispose();
        }
        _textures.Clear();
        _avatarBuffer?.Dispose();
        _dsv?.Dispose(); _depthTexture?.Dispose();
        _atlasView?.Dispose(); _atlas?.Dispose();
        _vs.Dispose(); _ps.Dispose(); _layout.Dispose(); _constants.Dispose(); _sampler.Dispose();
        _raster.Dispose(); _depthWrite.Dispose(); _depthRead.Dispose(); _opaque.Dispose(); _blend.Dispose();
    }
}
