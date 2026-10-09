using System.Numerics;

namespace p5rpc.personacraft.World;

/// <summary>
/// The one place P5R space and Minecraft space meet.
///
/// P5R field space is centimetres, Y up, right-handed, like Minecraft (blocks, Y up, right-handed),
/// so no axis flips: 100 cm is one block. Every field gets its own patch of the Minecraft world so
/// blocks placed in Leblanc do not show up in Shibuya:
///
///     mc.x = cm.x / 100 + minor * 1024
///     mc.y = cm.y / 100
///     mc.z = cm.z / 100 + major * 1024 - 131072
///
/// That keeps every coordinate under ~131k blocks (1.5 cm float precision at worst).
/// Minecraft yaw 0 looks along +Z and grows towards -X; pitch is positive looking down.
/// </summary>
internal readonly record struct FieldFrame(int Major, int Minor)
{
    public const double CmPerBlock = 100.0;

    public double OffsetX => Minor % 128 * 1024.0;
    public double OffsetZ => Major % 256 * 1024.0 - 131072.0;

    /// <summary>A stable id for HostState.worldId.</summary>
    public uint WorldId => (uint)(Major * 1000 + Minor);

    public bool IsValid => Major > 0;

    public override string ToString() => $"{Major:D3}_{Minor:D3}";

    public (double X, double Y, double Z) ToMc(Vector3 cm) =>
        (cm.X / CmPerBlock + OffsetX, cm.Y / CmPerBlock, cm.Z / CmPerBlock + OffsetZ);

    public Vector3 ToGame(double x, double y, double z) =>
        new((float)((x - OffsetX) * CmPerBlock), (float)(y * CmPerBlock), (float)((z - OffsetZ) * CmPerBlock));
}

internal static class Look
{
    /// <summary>Unit forward vector for a Minecraft yaw and pitch (degrees). Same axes in both games.</summary>
    public static Vector3 Forward(float yaw, float pitch)
    {
        float y = yaw * MathF.PI / 180f, p = pitch * MathF.PI / 180f;
        return new Vector3(-MathF.Sin(y) * MathF.Cos(p), -MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
    }

    /// <summary>Minecraft yaw and pitch (degrees) looking along <paramref name="forward"/>.</summary>
    public static (float Yaw, float Pitch) FromForward(Vector3 forward)
    {
        forward = Vector3.Normalize(forward);
        float yaw = MathF.Atan2(-forward.X, forward.Z) * 180f / MathF.PI;
        float pitch = -MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)) * 180f / MathF.PI;
        return (yaw, pitch);
    }

    public static float WrapDegrees(float a)
    {
        a %= 360f;
        if (a >= 180f) a -= 360f;
        if (a < -180f) a += 360f;
        return a;
    }
}
