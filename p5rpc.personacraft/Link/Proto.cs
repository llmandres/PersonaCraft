// Ported from PeakCraft (peak/src/PeakCraft/Link), itself a mirror of SkyCraft's protocol/skycraft_protocol.h.
// MIT, Copyright chasmlol (SkyCraft). Only the logger changed.
using System;
using System.Runtime.InteropServices;

namespace p5rpc.personacraft.Link;

/// <summary>
/// Mirror of protocol/skycraft_protocol.h (the layout is frozen; P5R takes Skyrim's side of it).
/// The Java mirror is fabric/src/main/java/dev/skycraft/link/Proto.java. Keep all three in sync.
/// </summary>
internal static unsafe class Proto
{
    public const uint Magic = 0x43594B53; // "SKYC"
    public const uint Version = 11;
    public const string MappingName = "Local\\SkyCraft_v1";

    // ---- region offsets ----
    public const long OffHeader = 0x0;
    public const long OffHostState = 0x100;
    public const long OffGuestState = 0x200;
    public const long OffOverlayCtl = 0x300;
    public const long OffOverlaySlotHdr = 0x340;
    public const long OffWaterGrid = 0x400;
    public const long OffInputRing = 0x1000;
    public const long OffActorTable = 0x12000;
    public const long OffEventRing = 0x17000;
    public const long OffWorldEntities = 0x1C000;
    public const long OffCollisionRing = 0x20000;
    public const long CollisionRingBytes = 32L << 20;
    public const long OffOverlayPixels = OffCollisionRing + CollisionRingBytes;
    public const int MaxOverlayW = 3840;
    public const int MaxOverlayH = 2160;
    public const long OverlaySlotBytes = (long)MaxOverlayW * MaxOverlayH * 4;
    public const int OverlaySlots = 3;
    public const long OffRenderRing = OffOverlayPixels + OverlaySlotBytes * OverlaySlots;
    public const long RenderRingBytes = 64L << 20;
    public const long MappingBytes = OffRenderRing + RenderRingBytes;

    // ---- rings: u64 head @0x00, u64 tail @0x40, data @0x80 ----
    public const long RingHeadOff = 0x00;
    public const long RingTailOff = 0x40;
    public const long RingDataOff = 0x80;
    public const int InputRingEntries = 4096;
    public const int EventRingEntries = 512;
    public const long ColRingDataBytes = CollisionRingBytes - RingDataOff;
    public const long RenRingDataBytes = RenderRingBytes - RingDataOff;

    public const int MaxActors = 256;
    public const int ActorRecordBytes = 64;
    public const int MaxWorldEntities = 160;
    public const int WorldEntityBytes = 96;

    public const uint OverlayDirty = 1u << 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Header
    {
        public uint magic;
        public uint version;
        public uint hostPid;
        public uint guestPid;
        public ulong hostHeartbeatMs;  // GetTickCount64() at the host's last frame
        public ulong guestHeartbeatMs; // GetTickCount64() at Minecraft's last frame
    }

    [Flags]
    public enum HostFlags : uint
    {
        InGame = 1u << 0,
        MenuOpen = 1u << 1,
        Loading = 1u << 2,
    }

    /// <summary>SkyState in the header: host to Minecraft, seqlock.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HostState
    {
        public uint seq;
        public uint flags;
        public uint worldId;
        public uint collisionEpoch;
        public double posX, posY, posZ; // player feet, Minecraft coords
        public float yaw, pitch;        // authoritative look, Minecraft degrees
        public uint teleportSeq;
        public uint viewportW, viewportH;
        public float gameHour;
    }

    [Flags]
    public enum GuestFlags : uint
    {
        InWorld = 1u << 0,
        ScreenOpen = 1u << 1,
        OnGround = 1u << 2,
        Sneaking = 1u << 3,
        Sprinting = 1u << 4,
        Dead = 1u << 5,
        Swimming = 1u << 6,
        Flying = 1u << 7,
    }

    /// <summary>McState in the header: Minecraft to host, seqlock.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GuestState
    {
        public uint seq;
        public uint flags;
        public double x, y, z;
        public float yaw, pitch;
        public float eyeHeight;
        public float sensitivity;
        public uint teleportAck;
        public uint guiScale;
        public ulong frameCounter;
        public float fovDeg;
        public float bobPhase;
        public float bobAmount;
        public uint pad4C;
        public double eyeX, eyeY, eyeZ;
        public long tickQpc;
        public double prevX, prevY, prevZ;
        public double curX, curY, curZ;
        public float tickEyeO, tickEye;
        public float walkDistO, walkDist;
        public float bobO, bob;
        public float tickMs;
        public uint tickPad;
        public uint cameraMode;
        public float cameraDistance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OverlayCtl
    {
        public uint state;
        public uint pad;
        public ulong framesPublished;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x40)]
    public struct OverlaySlotHdr
    {
        public uint width;
        public uint height;
        public uint flags; // bit0: rows are bottom-up
        public uint pad;
        public ulong frameId;
    }

    public enum InputType : ushort
    {
        Key = 1,
        MouseButton = 2,
        Scroll = 3,
        Cursor = 4,
        Text = 5,
        ReleaseAll = 6,
        Hurt = 7,
        OpenMenu = 8,
    }

    public const ushort HurtOther = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct InputEvent
    {
        public ushort type;
        public ushort code;
        public int a, b, c;
    }

    public const uint EvHitActor = 1;
    public const uint EvPlayerDied = 2;

    /// <summary>McEvent in the header.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GuestEvent
    {
        public uint type;
        public uint formId;
        public float a, b, c, d;
        public uint flags;
        public uint weapon;
    }

    public const uint ColPad = 0;
    public const uint ColClear = 1;
    public const uint ColRegion = 2;
    public const uint ColTris = 3;
    public const uint TriTerrain = 1u << 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct MsgHeader
    {
        public uint type;
        public uint payloadBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ColRegionHdr
    {
        public int minX, minY, minZ;
        public int maxX, maxY, maxZ;
        public uint epoch;
        public uint count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ColBlock
    {
        public int x, y, z;
        public uint pad;
        public fixed ulong bits[8]; // bits[y] bit (z * 8 + x): 1/8-block sub-voxel
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ColTri
    {
        public fixed float v[9];
        public uint flags;
    }

    public const uint RenPad = 0;
    public const uint RenAtlas = 1;
    public const uint RenSection = 2;
    public const uint RenClearAll = 3;
    public const uint RenTexture = 4;
    public const uint RenAvatar = 5;
    public const uint RenScene = 6;
    public const uint RenAtlasRegion = 7;
    public const uint RenRagdoll = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RenVertex
    {
        public float x, y, z;
        public float u, v;
        public uint color; // RGBA8, r in the low byte
        public uint light;
        public uint flags; // bit0 cutout, bit1 translucent, bits 4-6 face normal + 1
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenSectionHdr
    {
        public int sx, sy, sz;
        public uint vertexCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenAtlasHdr
    {
        public uint width, height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenAtlasRegionHdr
    {
        public uint x, y, width, height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenTextureHdr
    {
        public uint id;
        public uint width, height;
        public uint pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenAvatarHdr
    {
        public uint batchCount;
        public uint vertexCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenSceneHdr
    {
        public double originX, originY, originZ; // Minecraft block the positions are relative to
        public uint batchCount;
        public uint vertexCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenBatch
    {
        public uint texture; // 0: block atlas, else a RenTexture id
        public uint first;
        public uint count;
        public uint flags; // bit0: translucent
    }

    /// <summary>
    /// Logs pass or fail for every struct size and region offset against the values the header
    /// static_asserts (sizes) and Proto.java spells out (offsets). Returns false on any mismatch.
    /// </summary>
    public static bool SelfCheck()
    {
        bool ok = true;

        void Check(string what, long actual, long expected)
        {
            if (actual == expected)
            {
                Log.Info($"layout ok   {what} = 0x{actual:X}");
            }
            else
            {
                ok = false;
                Log.Error($"layout FAIL {what} = 0x{actual:X}, expected 0x{expected:X}");
            }
        }

        long Off<T>(string field) => Marshal.OffsetOf<T>(field).ToInt64();

        Check("sizeof(Header)", sizeof(Header), 0x20);
        Check("sizeof(HostState)", sizeof(HostState), 0x40);
        Check("sizeof(GuestState)", sizeof(GuestState), 0xC8);
        Check("sizeof(OverlaySlotHdr)", sizeof(OverlaySlotHdr), 0x40);
        Check("sizeof(InputEvent)", sizeof(InputEvent), 16);
        Check("sizeof(GuestEvent)", sizeof(GuestEvent), 32);
        Check("sizeof(ColRegionHdr)", sizeof(ColRegionHdr), 32);
        Check("sizeof(ColBlock)", sizeof(ColBlock), 80);
        Check("sizeof(ColTri)", sizeof(ColTri), 40);
        Check("sizeof(RenVertex)", sizeof(RenVertex), 32);

        Check("Header.hostHeartbeatMs", Off<Header>(nameof(Header.hostHeartbeatMs)), 0x10);
        Check("Header.guestHeartbeatMs", Off<Header>(nameof(Header.guestHeartbeatMs)), 0x18);
        Check("HostState.posX", Off<HostState>(nameof(HostState.posX)), 0x10);
        Check("HostState.yaw", Off<HostState>(nameof(HostState.yaw)), 0x28);
        Check("HostState.teleportSeq", Off<HostState>(nameof(HostState.teleportSeq)), 0x30);
        Check("HostState.gameHour", Off<HostState>(nameof(HostState.gameHour)), 0x3C);
        Check("GuestState.x", Off<GuestState>(nameof(GuestState.x)), 0x08);
        Check("GuestState.yaw", Off<GuestState>(nameof(GuestState.yaw)), 0x20);
        Check("GuestState.teleportAck", Off<GuestState>(nameof(GuestState.teleportAck)), 0x30);
        Check("GuestState.frameCounter", Off<GuestState>(nameof(GuestState.frameCounter)), 0x38);
        Check("GuestState.fovDeg", Off<GuestState>(nameof(GuestState.fovDeg)), 0x40);
        Check("GuestState.eyeX", Off<GuestState>(nameof(GuestState.eyeX)), 0x50);
        Check("GuestState.tickQpc", Off<GuestState>(nameof(GuestState.tickQpc)), 0x68);
        Check("GuestState.prevX", Off<GuestState>(nameof(GuestState.prevX)), 0x70);
        Check("GuestState.curX", Off<GuestState>(nameof(GuestState.curX)), 0x88);
        Check("GuestState.tickEyeO", Off<GuestState>(nameof(GuestState.tickEyeO)), 0xA0);
        Check("GuestState.tickMs", Off<GuestState>(nameof(GuestState.tickMs)), 0xB8);
        Check("GuestState.cameraMode", Off<GuestState>(nameof(GuestState.cameraMode)), 0xC0);
        Check("GuestState.cameraDistance", Off<GuestState>(nameof(GuestState.cameraDistance)), 0xC4);

        Check("OffHostState", OffHostState, 0x100);
        Check("OffGuestState", OffGuestState, 0x200);
        Check("OffOverlayCtl", OffOverlayCtl, 0x300);
        Check("OffOverlaySlotHdr", OffOverlaySlotHdr, 0x340);
        Check("OffInputRing", OffInputRing, 0x1000);
        Check("OffActorTable", OffActorTable, 0x12000);
        Check("OffEventRing", OffEventRing, 0x17000);
        Check("OffWorldEntities", OffWorldEntities, 0x1C000);
        Check("OffCollisionRing", OffCollisionRing, 0x20000);
        // Computed in the header: collision ring end, then 3 overlay slots of 3840x2160 RGBA.
        Check("OffOverlayPixels", OffOverlayPixels, 0x2020000);
        Check("OffRenderRing", OffRenderRing, 0x2020000L + 3L * 3840 * 2160 * 4);
        Check("MappingBytes", MappingBytes, 0x2020000L + 3L * 3840 * 2160 * 4 + (64L << 20));
        Check("InputRing end", OffInputRing + RingDataOff + InputRingEntries * 16L, 0x11080);
        Check("ActorTable end", OffActorTable + 0x40 + (long)MaxActors * ActorRecordBytes, 0x16040);
        Check("EventRing end", OffEventRing + RingDataOff + EventRingEntries * 32L, 0x1B080);
        Check("WorldEntities end", OffWorldEntities + 0x40 + (long)MaxWorldEntities * WorldEntityBytes, 0x1FC40);

        if (ok)
        {
            Log.Info("protocol layout self-check: PASS");
        }
        else
        {
            Log.Error("protocol layout self-check: FAIL");
        }
        return ok;
    }
}
