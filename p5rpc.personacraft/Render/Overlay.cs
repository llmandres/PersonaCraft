using System.Runtime.InteropServices;
using p5rpc.personacraft.Game;
using p5rpc.personacraft.Link;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace p5rpc.personacraft.Render;

/// <summary>
/// Draws Minecraft's blocks (<see cref="Blocks"/>) and HUD and screens (the overlay triple buffer)
/// over P5R's picture, from a hook on IDXGISwapChain::Present. It also drains the render ring every
/// frame, visible or not. All drawing happens inside a private D3D11 device-context state that is
/// swapped in and out (ID3D11DeviceContext1::SwapDeviceContextState), so none of P5R's pipeline
/// state is touched. Any error switches the overlay off for the session; P5R keeps presenting.
/// </summary>
internal sealed unsafe class Overlay
{
    [Function(CallingConventions.Microsoft)]
    private delegate int PresentFn(nint swapChain, uint syncInterval, uint flags);

    [Function(CallingConventions.Microsoft)]
    private delegate int ResizeBuffersFn(nint swapChain, uint count, uint width, uint height, int format, uint flags);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32")] private static extern bool DestroyWindow(nint hwnd);

    private const string Shader = @"
Texture2D tex : register(t0);
SamplerState smp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V vs(uint id : SV_VertexID)
{
    V o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = uv;
    return o;
}
float4 ps(V i) : SV_Target { return tex.Sample(smp, i.uv); }";

    private readonly HostLink _link;
    private readonly FieldPlayer _player;
    private IHook<PresentFn>? _present;
    private Blocks? _blocks;
    private volatile bool _clearBlocks;
    private bool _blocksFailed;
    private IHook<ResizeBuffersFn>? _resize;
    private bool _disabled;

    private nint _swapChainPtr;
    private IDXGISwapChain? _swapChain;
    private ID3D11Device1? _device;
    private ID3D11DeviceContext1? _context;
    private ID3DDeviceContextState? _state;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _blend;
    private ID3D11RasterizerState? _raster;
    private ID3D11DepthStencilState? _depth;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _textureView;
    private int _texW, _texH;
    private ID3D11ShaderResourceView? _cursorView;
    private bool _hasFrame;

    public Overlay(HostLink link, FieldPlayer player)
    {
        _link = link;
        _player = player;
    }

    /// <summary>Held around every render-ring drain: this thread and the logic thread's fallback.</summary>
    public readonly object RenderRingLock = new();

    /// <summary>True once Present runs with a device: the render ring is drained here from then on.</summary>
    public volatile bool DrainsRender;

    /// <summary>Drop every block section (link loss, new Minecraft).</summary>
    public void ClearBlocks()
    {
        _clearBlocks = true;
        lock (RenderRingLock)
            _cache.Clear();
    }

    private readonly RenderCache _cache = new();

    /// <summary>The logic thread's drain until this overlay draws: keeps what will be needed.</summary>
    public void DrainIntoCache()
    {
        lock (RenderRingLock)
        {
            if (!DrainsRender)
                _link.DrainRender(8L << 20, _cache.Add);
        }
    }

    public int BlockSections => _blocks?.Count ?? 0;

    /// <summary>P5R's back buffer size, for Minecraft's HUD resolution.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Set by the plugin each frame: draw the overlay, and the cursor where.</summary>
    public volatile bool Visible;
    public volatile bool ShowCursor;
    public float CursorX, CursorY;

    public void Install(IReloadedHooks hooks)
    {
        nint window = CreateWindowExW(0, "STATIC", "PersonaCraft", 0, 0, 0, 64, 64, 0, 0, 0, 0);
        try
        {
            var desc = new SwapChainDescription
            {
                BufferCount = 1,
                BufferDescription = new ModeDescription(64, 64, Format.R8G8B8A8_UNorm),
                BufferUsage = Usage.RenderTargetOutput,
                OutputWindow = window,
                SampleDescription = new SampleDescription(1, 0),
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
            };
            D3D11.D3D11CreateDeviceAndSwapChain(null, DriverType.Hardware, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0], desc, out var swapChain, out var device, out _, out var context).CheckError();
            nint* vtable = *(nint**)swapChain!.NativePointer;
            nint present = vtable[8], resize = vtable[13];
            context!.Dispose();
            device!.Dispose();
            swapChain.Dispose();
            _present = hooks.CreateHook<PresentFn>(OnPresent, present).Activate();
            _resize = hooks.CreateHook<ResizeBuffersFn>(OnResizeBuffers, resize).Activate();
            Log.Info($"overlay: hooked IDXGISwapChain::Present at 0x{present:X}");
        }
        catch (Exception e)
        {
            Log.Error($"overlay: could not hook Direct3D 11 ({e.Message}); no HUD overlay this session");
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    private int OnPresent(nint swapChain, uint syncInterval, uint flags)
    {
        if (!_disabled)
        {
            try
            {
                Frame(swapChain);
            }
            catch (Exception e)
            {
                _disabled = true;
                DrainsRender = false; // the logic thread takes the render ring back
                Log.Error($"overlay: switched off after an error: {e}");
            }
        }
        return _present!.OriginalFunction(swapChain, syncInterval, flags);
    }

    private int OnResizeBuffers(nint swapChain, uint count, uint width, uint height, int format, uint flags)
    {
        _rtv?.Dispose();
        _rtv = null;
        return _resize!.OriginalFunction(swapChain, count, width, height, format, flags);
    }

    private void Frame(nint swapChainPtr)
    {
        if (swapChainPtr != _swapChainPtr)
            Attach(swapChainPtr);
        var description = _swapChain!.Description;
        Width = (int)description.BufferDescription.Width;
        Height = (int)description.BufferDescription.Height;

        if (_clearBlocks)
        {
            _clearBlocks = false;
            _blocks!.Clear();
        }
        lock (RenderRingLock)
            _link.DrainRender(8L << 20, HandleRender);

        if (!Visible)
        {
            _hasFrame = false;
            return;
        }
        if (_link.AcquireOverlayFrame())
            Upload();
        var view = _player.View;
        if ((!_hasFrame || _textureView == null) && (view == null || _blocks!.Count == 0))
            return;

        if (_rtv == null)
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device!.CreateRenderTargetView(backBuffer);
        }

        var previous = _context!.SwapDeviceContextState(_state!);
        try
        {
            if (view != null && !_blocksFailed)
            {
                try
                {
                    _blocks!.Draw(_context, _rtv, Width, Height, view);
                }
                catch (Exception e)
                {
                    BlocksFailed(e);
                }
            }

            _context.OMSetRenderTargets(_rtv);
            _context.OMSetBlendState(_blend);
            _context.OMSetDepthStencilState(_depth);
            _context.RSSetState(_raster);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetSampler(0, _sampler);

            _context.VSSetConstantBuffer(0, null!);
            _context.RSSetViewport(new Viewport(0, 0, Width, Height));
            if (_hasFrame && _textureView != null)
            {
                _context.PSSetShaderResource(0, _textureView);
                _context.Draw(3, 0);
            }

            if (ShowCursor && _cursorView != null)
            {
                float scale = Math.Max(1f, Height / 720f);
                _context.RSSetViewport(new Viewport(CursorX, CursorY, 16 * scale, 16 * scale));
                _context.PSSetShaderResource(0, _cursorView);
                _context.Draw(3, 0);
            }
            _context.PSSetShaderResource(0, null!);
            _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        }
        finally
        {
            var ours = _context.SwapDeviceContextState(previous);
            ours.Dispose();
            previous.Dispose();
        }
    }

    private void HandleRender(uint type, byte* payload, uint bytes)
    {
        if (_blocksFailed)
            return;
        try
        {
            _blocks!.Handle(_context!, type, payload, bytes);
        }
        catch (Exception e)
        {
            BlocksFailed(e);
        }
    }

    /// <summary>Blocks stop drawing for the session; the HUD overlay and the ring draining carry on.</summary>
    private void BlocksFailed(Exception e)
    {
        _blocksFailed = true;
        _blocks?.Clear();
        Log.Error($"blocks: switched off after an error (the HUD stays): {e}");
    }

    private void Attach(nint swapChainPtr)
    {
        Detach();
        Marshal.AddRef(swapChainPtr);
        _swapChainPtr = swapChainPtr;
        _swapChain = new IDXGISwapChain(swapChainPtr);
        using (var device = _swapChain.GetDevice<ID3D11Device>())
            _device = device.QueryInterface<ID3D11Device1>();
        _context = _device.ImmediateContext1;
        _state = _device.CreateDeviceContextState<ID3D11Device1>(CreateDeviceContextStateFlags.None, [_device.FeatureLevel], out _);

        var vsCode = Compiler.Compile(Shader, "vs", "overlay", "vs_4_0");
        var psCode = Compiler.Compile(Shader, "ps", "overlay", "ps_4_0");
        _vs = _device.CreateVertexShader(vsCode.Span);
        _ps = _device.CreatePixelShader(psCode.Span);
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        var blend = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha);
        _blend = _device.CreateBlendState(blend);
        _raster = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _depth = _device.CreateDepthStencilState(DepthStencilDescription.None);
        _cursorView = MakeCursor();
        _blocks = new Blocks(_device);
        lock (RenderRingLock)
        {
            _cache.Replay(HandleRender);
            DrainsRender = true;
        }
        Log.Info($"overlay: attached to P5R's swap chain 0x{swapChainPtr:X} (feature level {_device.FeatureLevel})");
    }

    private void Detach()
    {
        _blocks?.Dispose();
        _blocks = null;
        _rtv?.Dispose(); _rtv = null;
        _textureView?.Dispose(); _textureView = null;
        _texture?.Dispose(); _texture = null;
        _texW = _texH = 0;
        _cursorView?.Dispose(); _cursorView = null;
        _depth?.Dispose(); _raster?.Dispose(); _blend?.Dispose(); _sampler?.Dispose();
        _ps?.Dispose(); _vs?.Dispose(); _state?.Dispose();
        _context?.Dispose(); _device?.Dispose();
        _swapChain?.Dispose();
        _swapChain = null;
        _swapChainPtr = 0;
        _hasFrame = false;
    }

    /// <summary>Copies the newest overlay frame into the texture (recreated when the size changes).</summary>
    private void Upload()
    {
        var header = _link.FrontHeader;
        int w = (int)header->width, h = (int)header->height;
        if (w <= 0 || h <= 0 || w > Proto.MaxOverlayW || h > Proto.MaxOverlayH)
            return;
        bool bottomUp = (header->flags & 1) != 0;
        if (w != _texW || h != _texH)
        {
            _textureView?.Dispose();
            _texture?.Dispose();
            _texture = _device!.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _textureView = _device.CreateShaderResourceView(_texture);
            _texW = w;
            _texH = h;
        }
        var mapped = _context!.Map(_texture!, 0, MapMode.WriteDiscard);
        byte* src = _link.FrontPixels;
        for (int y = 0; y < h; y++)
        {
            byte* from = src + (long)(bottomUp ? h - 1 - y : y) * w * 4;
            Buffer.MemoryCopy(from, (byte*)mapped.DataPointer + (long)y * mapped.RowPitch, mapped.RowPitch, w * 4);
        }
        _context.Unmap(_texture!, 0);
        _hasFrame = true;
    }

    /// <summary>A 16x16 arrow pointer (white, black outline) for Minecraft's screens.</summary>
    private ID3D11ShaderResourceView MakeCursor()
    {
        string[] rows =
        [
            "X...............", "XX..............", "XOX.............", "XOOX............",
            "XOOOX...........", "XOOOOX..........", "XOOOOOX.........", "XOOOOOOX........",
            "XOOOOOOOX.......", "XOOOOOXXXX......", "XOOXOOX.........", "XOX.XOOX........",
            "XX..XOOX........", "X....XOOX.......", ".....XOOX.......", "......XX........",
        ];
        var pixels = new uint[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                pixels[y * 16 + x] = rows[y][x] switch { 'X' => 0xFF000000u, 'O' => 0xFFFFFFFFu, _ => 0u };
        fixed (uint* p = pixels)
        {
            using var texture = _device!.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, 16, 16, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable),
                [new SubresourceData(p, 16 * 4)]);
            return _device.CreateShaderResourceView(texture);
        }
    }
}
