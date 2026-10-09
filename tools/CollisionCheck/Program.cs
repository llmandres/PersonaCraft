using System.Globalization;
using System.Numerics;
using PersonaCraft.Core;

// CollisionCheck <model.GFS> [positions.csv field]
// Prints the collision meshes found, then for each logged position of that field, the nearest
// collision surface straight below the player (should be ~0 cm away when standing on ground).
var meshes = GfsCollision.Read(File.ReadAllBytes(args[0]));
var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
int tris = 0;
foreach (var m in meshes)
{
    tris += m.Indices.Length / 3;
    foreach (var v in m.Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
}
Console.WriteLine($"{meshes.Count} meshes, {tris} triangles, bounds {min} .. {max}");
foreach (var g in meshes.GroupBy(m => m.NodeName).Take(40))
    Console.WriteLine($"  {g.Key}: {g.Sum(m => m.Indices.Length / 3)} tris");

if (args.Length < 3) return;
var ci = CultureInfo.InvariantCulture;
var errors = new List<float>();
foreach (var line in File.ReadLines(args[1]).Skip(1))
{
    var c = line.Split(',');
    if (c[1] != args[2]) continue;
    var p = new Vector3(float.Parse(c[3], ci), float.Parse(c[4], ci), float.Parse(c[5], ci));
    float? best = null;
    foreach (var m in meshes)
        for (int i = 0; i < m.Indices.Length; i += 3)
        {
            var h = DownHit(p + new Vector3(0, 60, 0), m.Vertices[m.Indices[i]], m.Vertices[m.Indices[i + 1]], m.Vertices[m.Indices[i + 2]]);
            if (h is { } y && (best is null || y > best)) best = y;
        }
    string d = best is { } b ? (p.Y - b).ToString("F1", ci) : "none";
    if (best is { } bb) errors.Add(p.Y - bb);
    Console.WriteLine($"{c[0]} player {p.X:F0},{p.Y:F0},{p.Z:F0}  ground below: {(best?.ToString("F1", ci) ?? "-")}  gap {d}");
}
if (errors.Count > 0) Console.WriteLine($"gap median {errors.OrderBy(e => e).ElementAt(errors.Count / 2):F1}, max |gap| {errors.Max(MathF.Abs):F1}, {errors.Count} hits");

// Highest point of the triangle under (x,z) that is below the ray origin, or null.
static float? DownHit(Vector3 o, Vector3 a, Vector3 b, Vector3 c)
{
    float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
    if (MathF.Abs(d) < 1e-6f) return null;
    float u = ((b.Z - c.Z) * (o.X - c.X) + (c.X - b.X) * (o.Z - c.Z)) / d;
    float v = ((c.Z - a.Z) * (o.X - c.X) + (a.X - c.X) * (o.Z - c.Z)) / d;
    float w = 1 - u - v;
    if (u < 0 || v < 0 || w < 0) return null;
    float y = u * a.Y + v * b.Y + w * c.Y;
    return y <= o.Y ? y : null;
}
