using p5rpc.personacraft.Link;

namespace p5rpc.personacraft.Render;

/// <summary>
/// Keeps the render-ring messages that matter later (the block atlas, entity textures, block
/// sections) while nothing can draw yet. Minecraft sends its atlas and skin once, when its world
/// opens, which can be seconds before the overlay attaches to P5R's swap chain; without this they
/// were drained and dropped, and the player model and blocks never showed. Per-frame messages
/// (avatar, scene, animated atlas regions) are not kept: the next frame brings new ones.
/// </summary>
internal sealed unsafe class RenderCache
{
    private byte[]? _atlas;
    private readonly Dictionary<uint, byte[]> _textures = new();
    private readonly Dictionary<(int, int, int), byte[]> _sections = new();

    public void Add(uint type, byte* payload, uint bytes)
    {
        switch (type)
        {
            case Proto.RenAtlas:
                _atlas = Copy(payload, bytes);
                break;
            case Proto.RenTexture when bytes >= (uint)sizeof(Proto.RenTextureHdr):
                _textures[((Proto.RenTextureHdr*)payload)->id] = Copy(payload, bytes);
                break;
            case Proto.RenSection when bytes >= (uint)sizeof(Proto.RenSectionHdr):
                var header = (Proto.RenSectionHdr*)payload;
                var key = (header->sx, header->sy, header->sz);
                if (header->vertexCount == 0)
                    _sections.Remove(key);
                else
                    _sections[key] = Copy(payload, bytes);
                break;
            case Proto.RenClearAll:
                _sections.Clear();
                break;
        }
    }

    /// <summary>Hands everything kept to <paramref name="handler"/> (atlas first) and forgets it.</summary>
    public void Replay(Rings.MessageHandler handler)
    {
        if (_atlas != null)
            Send(handler, Proto.RenAtlas, _atlas);
        foreach (var texture in _textures.Values)
            Send(handler, Proto.RenTexture, texture);
        foreach (var section in _sections.Values)
            Send(handler, Proto.RenSection, section);
        Log.Info($"render cache: replayed {(_atlas != null ? "the atlas, " : "no atlas, ")}{_textures.Count} textures, {_sections.Count} block sections");
        Clear();
    }

    public void Clear()
    {
        _atlas = null;
        _textures.Clear();
        _sections.Clear();
    }

    private static void Send(Rings.MessageHandler handler, uint type, byte[] data)
    {
        fixed (byte* p = data)
            handler(type, p, (uint)data.Length);
    }

    private static byte[] Copy(byte* payload, uint bytes) => new ReadOnlySpan<byte>(payload, (int)bytes).ToArray();
}
