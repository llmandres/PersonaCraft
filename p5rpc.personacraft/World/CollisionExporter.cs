using System.Numerics;
using p5rpc.personacraft.Link;
using PersonaCraft.Core;

namespace p5rpc.personacraft.World;

/// <summary>
/// Sends a field's exact collision (the GFS "atari" triangles) to Minecraft.
///
/// Unlike PeakCraft there is nothing to sample: the whole field is known up front and small (a few
/// thousand triangles), so it is converted once per field into 8-block regions and streamed over a
/// few frames. Every region of the field's box (plus a margin) is sent, empty ones too, so Minecraft
/// knows the space is open. Each triangle goes to every region its bounds touch. The 1/8-block
/// occupancy Minecraft also wants is a voxelised copy of the triangle surfaces (the model's winding
/// does not say which side is solid, so there is no shell under floors). Conversion runs on a worker
/// thread; only the sending happens on the caller's.
/// </summary>
internal sealed unsafe class CollisionExporter
{
    private const int RegionSize = 8;
    private const int Margin = 1;            // regions around the field's box
    private const int AboveMargin = 3;       // extra regions above (jumping, building up)
    private const long MaxBacklogBytes = 8L << 20;
    private const double BudgetMs = 2.0;

    private readonly HostLink _link;
    private byte[] _payload = new byte[1 << 16];

    private FieldFrame _field;
    private List<(int X, int Y, int Z)> _pending = new();
    private Dictionary<(int, int, int), List<Proto.ColTri>> _tris = new();
    private Dictionary<(int, int, int), ulong[]> _occupancy = new();
    private int _sent;
    private List<CollisionMesh>? _meshes;
    private Task? _build;

    public CollisionExporter(HostLink link) => _link = link;

    public uint Epoch { get; private set; } = 1;

    /// <summary>The field whose collision is (being) sent.</summary>
    public FieldFrame Field => _field;

    /// <summary>All of the field's regions reached Minecraft's ring.</summary>
    public bool Complete => _field.IsValid && _meshes != null && _build is { IsCompleted: true } && _pending.Count == 0;

    /// <summary>Drops everything Minecraft has (new field, new guest).</summary>
    public void Clear(string why)
    {
        Epoch++;
        uint epoch = Epoch;
        _link.WriteCollision(Proto.ColClear, &epoch, sizeof(uint));
        _build?.Wait();
        _build = null;
        _pending = new();
        _field = default;
        _meshes = null;
        Log.Info($"collision: cleared, epoch {Epoch} ({why})");
    }

    /// <summary>Starts sending a field. The previous field's collision is cleared first.</summary>
    public void Begin(FieldFrame field, List<CollisionMesh> meshes)
    {
        Clear($"field {field}");
        _field = field;
        _meshes = meshes;
        _build = Task.Run(() => Build(field, meshes));
    }

    /// <summary>Streams pending regions within a time budget. Call every frame.</summary>
    public void Pump()
    {
        if (_build is not { IsCompleted: true } || _pending.Count == 0)
            return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (_pending.Count > 0 && watch.Elapsed.TotalMilliseconds < BudgetMs && _link.CollisionBacklog < MaxBacklogBytes)
        {
            var r = _pending[^1];
            _tris.TryGetValue(r, out var tris);
            _occupancy.TryGetValue(r, out var occupancy);
            if (!SendRegion(r.X, r.Y, r.Z, tris, occupancy))
                break; // ring full; next frame
            _pending.RemoveAt(_pending.Count - 1);
            _sent++;
        }
        if (_pending.Count == 0)
            Log.Info($"collision: field {_field} sent ({_sent} regions, epoch {Epoch})");
    }

    /// <summary>
    /// Height (cm) of the highest collision surface at or below <paramref name="cm"/> + 60 cm, or null.
    /// Used to check that the field's collision lines up with where the game has Joker.
    /// </summary>
    public static float? GroundBelow(List<CollisionMesh> meshes, Vector3 cm)
    {
        var o = cm + new Vector3(0, 60, 0);
        float? best = null;
        foreach (var m in meshes)
        {
            for (int i = 0; i < m.Indices.Length; i += 3)
            {
                var a = m.Vertices[m.Indices[i]];
                var b = m.Vertices[m.Indices[i + 1]];
                var c = m.Vertices[m.Indices[i + 2]];
                float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (MathF.Abs(d) < 1e-6f)
                    continue;
                float u = ((b.Z - c.Z) * (o.X - c.X) + (c.X - b.X) * (o.Z - c.Z)) / d;
                float v = ((c.Z - a.Z) * (o.X - c.X) + (a.X - c.X) * (o.Z - c.Z)) / d;
                if (u < 0 || v < 0 || u + v > 1)
                    continue;
                float y = u * a.Y + v * b.Y + (1 - u - v) * c.Y;
                if (y <= o.Y && (best is null || y > best))
                    best = y;
            }
        }
        return best;
    }

    private void Build(FieldFrame field, List<CollisionMesh> meshes)
    {
        var tris = new Dictionary<(int, int, int), List<Proto.ColTri>>();
        var occupancy = new Dictionary<(int, int, int), ulong[]>();
        _tris = tris;
        _occupancy = occupancy;
        _sent = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var m in meshes)
        {
            var mc = new Vector3[m.Vertices.Length];
            for (int i = 0; i < mc.Length; i++)
            {
                var (x, y, z) = field.ToMc(m.Vertices[i]);
                mc[i] = new Vector3((float)x, (float)y, (float)z);
                min = Vector3.Min(min, mc[i]);
                max = Vector3.Max(max, mc[i]);
            }
            for (int i = 0; i < m.Indices.Length; i += 3)
            {
                var a = mc[m.Indices[i]];
                var b = mc[m.Indices[i + 1]];
                var c = mc[m.Indices[i + 2]];
                if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-10f)
                    continue; // degenerate
                AddTriangle(a, b, c);
                Voxelise(a, b, c);
            }
        }

        var pending = new List<(int X, int Y, int Z)>();
        if (min.X > max.X)
            return;
        int rx0 = FloorDiv((int)MathF.Floor(min.X), RegionSize) - Margin;
        int ry0 = FloorDiv((int)MathF.Floor(min.Y), RegionSize) - Margin;
        int rz0 = FloorDiv((int)MathF.Floor(min.Z), RegionSize) - Margin;
        int rx1 = FloorDiv((int)MathF.Floor(max.X), RegionSize) + Margin;
        int ry1 = FloorDiv((int)MathF.Floor(max.Y), RegionSize) + AboveMargin;
        int rz1 = FloorDiv((int)MathF.Floor(max.Z), RegionSize) + Margin;
        for (int ry = ry1; ry >= ry0; ry--)
            for (int rz = rz0; rz <= rz1; rz++)
                for (int rx = rx0; rx <= rx1; rx++)
                    pending.Add((rx, ry, rz));
        // Pump sends from the end: the lowest regions (the floors) go first.
        pending.Reverse();
        int entries = tris.Values.Sum(l => l.Count);
        Log.Info($"collision: field {field}: {pending.Count} regions ({tris.Count} with triangles, {entries} triangle entries), MC box {min:F1} .. {max:F1}");
        _pending = pending;
    }

    private void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var tri = new Proto.ColTri { flags = 0 };
        tri.v[0] = a.X; tri.v[1] = a.Y; tri.v[2] = a.Z;
        tri.v[3] = b.X; tri.v[4] = b.Y; tri.v[5] = b.Z;
        tri.v[6] = c.X; tri.v[7] = c.Y; tri.v[8] = c.Z;
        var lo = Vector3.Min(a, Vector3.Min(b, c));
        var hi = Vector3.Max(a, Vector3.Max(b, c));
        for (int ry = FloorDiv((int)MathF.Floor(lo.Y), RegionSize); ry <= FloorDiv((int)MathF.Floor(hi.Y), RegionSize); ry++)
            for (int rz = FloorDiv((int)MathF.Floor(lo.Z), RegionSize); rz <= FloorDiv((int)MathF.Floor(hi.Z), RegionSize); rz++)
                for (int rx = FloorDiv((int)MathF.Floor(lo.X), RegionSize); rx <= FloorDiv((int)MathF.Floor(hi.X), RegionSize); rx++)
                {
                    if (!_tris.TryGetValue((rx, ry, rz), out var list))
                        _tris[(rx, ry, rz)] = list = new List<Proto.ColTri>();
                    list.Add(tri);
                }
    }

    /// <summary>Marks the 1/8-block sub-voxels the triangle's surface passes through.</summary>
    private void Voxelise(Vector3 a, Vector3 b, Vector3 c)
    {
        float longest = MathF.Max((b - a).Length(), MathF.Max((c - b).Length(), (a - c).Length()));
        int steps = Math.Clamp((int)MathF.Ceiling(longest * 8f), 1, 2048); // one sample per sub-voxel
        for (int i = 0; i <= steps; i++)
        {
            for (int j = 0; j <= steps - i; j++)
            {
                float u = (float)i / steps, v = (float)j / steps;
                var p = a + (b - a) * u + (c - a) * v;
                int sx = (int)MathF.Floor(p.X * 8f), sy = (int)MathF.Floor(p.Y * 8f), sz = (int)MathF.Floor(p.Z * 8f);
                Mark(sx, sy, sz);
            }
        }
    }

    private void Mark(int sx, int sy, int sz)
    {
        int bx = sx >> 3, by = sy >> 3, bz = sz >> 3; // blocks (arithmetic shift floors negatives)
        var key = (FloorDiv(bx, RegionSize), FloorDiv(by, RegionSize), FloorDiv(bz, RegionSize));
        if (!_occupancy.TryGetValue(key, out var region))
            _occupancy[key] = region = new ulong[512 * 8];
        int lx = bx - key.Item1 * RegionSize, ly = by - key.Item2 * RegionSize, lz = bz - key.Item3 * RegionSize;
        int block = lx + 8 * (lz + 8 * ly);
        region[block * 8 + (sy & 7)] |= 1ul << ((sz & 7) * 8 + (sx & 7));
    }

    /// <summary>Sends a region's triangles, then its occupancy (null = empty; the region still becomes "known").</summary>
    private bool SendRegion(int rx, int ry, int rz, List<Proto.ColTri>? triangles, ulong[]? region)
    {
        var header = new Proto.ColRegionHdr
        {
            minX = rx * RegionSize,
            minY = ry * RegionSize,
            minZ = rz * RegionSize,
            maxX = rx * RegionSize + RegionSize - 1,
            maxY = ry * RegionSize + RegionSize - 1,
            maxZ = rz * RegionSize + RegionSize - 1,
            epoch = Epoch,
        };
        int triCount = triangles?.Count ?? 0;
        int need = sizeof(Proto.ColRegionHdr) + Math.Max(triCount * sizeof(Proto.ColTri), 512 * sizeof(Proto.ColBlock));
        if (_payload.Length < need)
            _payload = new byte[need];
        fixed (byte* p = _payload)
        {
            header.count = (uint)triCount;
            *(Proto.ColRegionHdr*)p = header;
            var triOut = (Proto.ColTri*)(p + sizeof(Proto.ColRegionHdr));
            for (int t = 0; t < triCount; t++)
                triOut[t] = triangles![t];
            if (!_link.WriteCollision(Proto.ColTris, p, (uint)(sizeof(Proto.ColRegionHdr) + triCount * sizeof(Proto.ColTri))))
                return false;

            uint blocks = 0;
            var blockOut = (Proto.ColBlock*)(p + sizeof(Proto.ColRegionHdr));
            if (region != null)
            {
                for (int b = 0; b < 512; b++)
                {
                    ulong any = 0;
                    for (int layer = 0; layer < 8; layer++)
                        any |= region[b * 8 + layer];
                    if (any == 0)
                        continue;
                    Proto.ColBlock* block = &blockOut[blocks++];
                    block->x = rx * RegionSize + (b & 7);
                    block->z = rz * RegionSize + ((b >> 3) & 7);
                    block->y = ry * RegionSize + (b >> 6);
                    block->pad = 0;
                    for (int layer = 0; layer < 8; layer++)
                        block->bits[layer] = region[b * 8 + layer];
                }
            }
            header.count = blocks;
            *(Proto.ColRegionHdr*)p = header;
            // The triangles went out; if the occupancy does not fit now it is retried with them next frame.
            return _link.WriteCollision(Proto.ColRegion, p, (uint)(sizeof(Proto.ColRegionHdr) + blocks * sizeof(Proto.ColBlock)));
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
