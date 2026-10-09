using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace PersonaCraft.Core;

/// <summary>One collision mesh from a field model, already in field (world) space, game units (cm).</summary>
public sealed record CollisionMesh(string NodeName, Vector3[] Vertices, int[] Indices);

/// <summary>
/// Reads the walkable collision of a Persona 5 Royal field out of its GFS model
/// (MODEL/FIELD_TEX/Fxxx_yyy_0.GFS). P5R keeps collision as ordinary GFD meshes under nodes named
/// "atari*" (当たり, "hit"), next to the render geometry.
///
/// Rather than parsing the whole model (textures, materials, effects with no length prefixes), it
/// finds each "atari" node by its name, parses that node's subtree, and keeps the meshes. The
/// subtrees hold only meshes, so the reader handles nodes, meshes, properties, cameras, lights and
/// morphs, and gives up on a subtree that contains anything else. The atari root's ancestors are
/// identity transforms in the fields checked (verified against measured player heights).
///
/// GFD is big-endian. Written for model version 0x01105100 (P5R); older and newer layouts are not
/// handled.
/// </summary>
public static class GfsCollision
{
    private const uint P5RVersion = 0x01105100;
    private static readonly byte[] AtariPrefix = "atari"u8.ToArray();

    public static List<CollisionMesh> Read(byte[] file)
    {
        var result = new List<CollisionMesh>();
        if (file.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)) != P5RVersion)
            return result;

        int consumedUntil = 0;
        int search = 16;
        while (true)
        {
            int hit = file.AsSpan(search).IndexOf(AtariPrefix);
            if (hit < 0)
                break;
            int nameStart = search + hit;
            search = nameStart + 1;
            if (nameStart < consumedUntil)
                continue; // inside a subtree already read

            int lengthAt = nameStart - 2;
            if (lengthAt < 16)
                continue;
            var reader = new Reader(file, lengthAt);
            var meshes = new List<CollisionMesh>();
            try
            {
                ReadNodeRecursive(ref reader, Matrix4x4.Identity, meshes);
            }
            catch (FormatException)
            {
                continue; // a texture name or property that merely starts with "atari"
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }
            consumedUntil = reader.Position;
            result.AddRange(meshes);
        }
        return result;
    }

    private static void ReadNodeRecursive(ref Reader r, Matrix4x4 parentWorld, List<CollisionMesh> meshes)
    {
        string name = r.StringWithHash();
        if (name.Length == 0 || name.Length > 128)
            throw new FormatException("bad node name");
        var translation = r.Vector3();
        var rotation = new Quaternion(r.Single(), r.Single(), r.Single(), r.Single());
        var scale = r.Vector3();
        if (!IsSane(translation) || !IsSane(scale) || MathF.Abs(rotation.Length() - 1f) > 0.01f)
            throw new FormatException("bad transform");

        var world = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation)
                    * Matrix4x4.CreateTranslation(translation) * parentWorld;

        int attachments = r.Int32();
        if (attachments is < 0 or > 64)
            throw new FormatException("bad attachment count");
        for (int i = 0; i < attachments; i++)
        {
            int type = r.Int32();
            switch (type)
            {
                case 4: ReadMesh(ref r, name, world, meshes); break;
                case 5: SkipCamera(ref r); break;
                case 6: SkipLight(ref r); break;
                case 9: r.Skip(r.Int32() * 4); r.StringWithHash(); break; // morph
                default: throw new FormatException($"unsupported attachment {type}");
            }
        }

        if (r.Byte() != 0)
            SkipProperties(ref r);
        r.Single(); // FieldE0 (version > 0x1104230)

        int children = r.Int32();
        if (children is < 0 or > 4096)
            throw new FormatException("bad child count");
        for (int i = 0; i < children; i++)
            ReadNodeRecursive(ref r, world, meshes);
    }

    private static void ReadMesh(ref Reader r, string node, Matrix4x4 world, List<CollisionMesh> meshes)
    {
        uint flags = r.UInt32();
        uint attributes = r.UInt32();
        int triangleCount = 0, indexFormat = 0;
        if ((flags & 4) != 0)
        {
            triangleCount = r.Int32();
            indexFormat = r.Int16();
        }
        int vertexCount = r.Int32();
        r.Int32(); // Field14 (version > 0x1103020)
        if (vertexCount is < 0 or > 1_000_000 || triangleCount is < 0 or > 2_000_000)
            throw new FormatException("bad mesh counts");
        if ((attributes & (1u << 11)) != 0)
            throw new FormatException("Color1 in a legacy mesh");

        int stride = 0;
        if ((attributes & (1u << 1)) != 0) stride += 12;  // position
        int positionOffset = 0;
        if ((attributes & (1u << 4)) != 0) stride += 12;  // normal
        if ((attributes & (1u << 28)) != 0) stride += 12; // tangent
        if ((attributes & (1u << 29)) != 0) stride += 12; // binormal
        if ((attributes & (1u << 6)) != 0) stride += 4;   // colour 0
        if ((attributes & (1u << 8)) != 0) stride += 8;   // uv 0
        if ((attributes & (1u << 9)) != 0) stride += 8;   // uv 1
        if ((attributes & (1u << 10)) != 0) stride += 8;  // uv 2
        if ((attributes & (1u << 30)) != 0) stride += 4;  // colour 2
        if ((flags & 1) != 0) stride += 20;               // 4 weights + packed indices

        Vector3[] vertices = Array.Empty<Vector3>();
        bool hasPositions = (attributes & (1u << 1)) != 0;
        if (hasPositions)
        {
            vertices = new Vector3[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                var at = r.Fork(r.Position + v * stride + positionOffset);
                vertices[v] = Vector3.Transform(at.Vector3(), world);
            }
        }
        r.Skip(vertexCount * stride);

        if ((flags & (1u << 6)) != 0) // morph targets
        {
            r.Int32();
            int targets = r.Int32();
            for (int t = 0; t < targets; t++)
            {
                r.Int32();
                r.Skip(r.Int32() * 12);
            }
        }

        int[] indices = new int[triangleCount * 3];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = indexFormat switch
            {
                1 => r.UInt16(),
                2 => r.Int32(),
                _ => throw new FormatException("bad index format"),
            };
            if (indices[i] >= vertexCount)
                throw new FormatException("index out of range");
        }

        if ((flags & 2) != 0) r.StringWithHash();   // material
        if ((flags & 8) != 0) r.Skip(24);           // bounding box
        if ((flags & 16) != 0) r.Skip(16);          // bounding sphere
        if ((flags & (1u << 12)) != 0) r.Skip(8);   // lod range

        if (hasPositions && indices.Length > 0)
            meshes.Add(new CollisionMesh(node, vertices, indices));
    }

    private static void SkipCamera(ref Reader r) => r.Skip(64 + 16 + 4); // view matrix, near/far/fov/aspect, Field190

    private static void SkipLight(ref Reader r)
    {
        uint flags = r.UInt32();
        int type = r.Int32();
        r.Skip(48); // ambient, diffuse, specular
        switch (type)
        {
            case 1: r.Skip(12); break;
            case 2: r.Skip(12); r.Skip((flags & 4) != 0 ? 8 : 12); break;
            case 3: r.Skip(20); r.Skip(12); r.Skip((flags & 4) != 0 ? 8 : 12); break;
        }
    }

    private static void SkipProperties(ref Reader r)
    {
        int count = r.Int32();
        if (count is < 0 or > 256)
            throw new FormatException("bad property count");
        for (int i = 0; i < count; i++)
        {
            int type = r.Int32();
            r.StringWithHash();
            int size = r.Int32();
            switch (type)
            {
                case 1 or 2: r.Skip(4); break;
                case 3: r.Skip(1); break;
                case 4: r.Skip(size - 1); break;
                case 5: r.Skip(3); break;
                case 6: r.Skip(4); break;
                case 7: r.Skip(12); break;
                case 8: r.Skip(16); break;
                case 9: r.Skip(size); break;
                default: throw new FormatException("bad property type");
            }
        }
    }

    private static bool IsSane(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
        && MathF.Abs(v.X) < 1e6f && MathF.Abs(v.Y) < 1e6f && MathF.Abs(v.Z) < 1e6f;

    private ref struct Reader
    {
        private readonly byte[] _data;
        public int Position;

        public Reader(byte[] data, int position)
        {
            _data = data;
            Position = position;
        }

        public readonly Reader Fork(int position) => new(_data, position);

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Position + n > _data.Length)
                throw new ArgumentOutOfRangeException(nameof(n));
            var span = _data.AsSpan(Position, n);
            Position += n;
            return span;
        }

        public void Skip(int n) => Take(n);
        public byte Byte() => Take(1)[0];
        public short Int16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public float Single() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
        public Vector3 Vector3() => new(Single(), Single(), Single());

        public string StringWithHash()
        {
            int length = UInt16();
            if (length == 0)
                return "";
            var bytes = Take(length);
            foreach (byte b in bytes)
                if (b < 0x20)
                    throw new FormatException("control character in name");
            UInt32(); // hash
            return Encoding.ASCII.GetString(bytes);
        }
    }
}
