// Ported from PeakCraft's PeakLink (peak/src/PeakCraft/Link/Link.cs), which mirrors SkyCraft's
// skse/src/Link.cpp. MIT, Copyright chasmlol (SkyCraft).
using System.Runtime.InteropServices;

namespace p5rpc.personacraft.Link;

/// <summary>
/// The P5R end of the shared-memory link. P5R creates the mapping; Minecraft (SkyCraft's Fabric mod)
/// opens it when it appears, and both sides watch each other's heartbeat.
/// </summary>
internal sealed unsafe class HostLink
{
    // Same clocks as SkyLink.java: GetTickCount64 for heartbeats, QueryPerformanceCounter for tick timestamps.
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32")] private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32")] private static extern ulong GetTickCount64();
    [DllImport("kernel32")] private static extern bool QueryPerformanceCounter(out long count);
    [DllImport("kernel32")] private static extern bool QueryPerformanceFrequency(out long frequency);
    [DllImport("kernel32")] private static extern uint GetCurrentProcessId();

    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0xF001F;
    private const int ErrorAlreadyExists = 183;
    private const ulong GuestTimeoutMs = 3000;

    private IntPtr _mapping;
    private byte* _base;
    private int _overlayFront = 2;

    public HostLink()
    {
        QueryPerformanceFrequency(out long frequency);
        QpcFrequency = frequency;
    }

    public bool Created => _base != null;
    public long QpcFrequency { get; }
    public static ulong TickCount => GetTickCount64();

    public static long Qpc()
    {
        QueryPerformanceCounter(out long count);
        return count;
    }

    private Proto.Header* Header => (Proto.Header*)(_base + Proto.OffHeader);

    public bool Create(string name)
    {
        if (_base != null)
            return true;
        long size = Proto.MappingBytes;
        _mapping = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PageReadWrite, (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), name);
        int created = Marshal.GetLastWin32Error();
        if (_mapping == IntPtr.Zero)
        {
            Log.Error($"shared memory {name}: CreateFileMapping failed (Windows error {created})");
            return false;
        }
        _base = (byte*)MapViewOfFile(_mapping, FileMapAllAccess, 0, 0, UIntPtr.Zero);
        if (_base == null)
        {
            Log.Error($"shared memory {name}: MapViewOfFile failed (Windows error {Marshal.GetLastWin32Error()})");
            CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
            return false;
        }

        // A stale mapping survives if Minecraft still has it open from a previous P5R run.
        // Reset everything the host owns so rings and the overlay swap start from a known state.
        Clear(Proto.OffHostState, sizeof(Proto.HostState));
        Clear(Proto.OffOverlayCtl, 0x100);
        Clear(Proto.OffWaterGrid, 0x10);
        Clear(Proto.OffInputRing, Proto.RingDataOff);
        Clear(Proto.OffCollisionRing, Proto.RingDataOff);
        Clear(Proto.OffActorTable, 0x40);
        Clear(Proto.OffEventRing, Proto.RingDataOff);
        Clear(Proto.OffWorldEntities, 0x40);
        Clear(Proto.OffRenderRing, Proto.RingDataOff);
        _overlayFront = 2;
        Header->version = Proto.Version;
        Header->hostPid = GetCurrentProcessId();
        Header->hostHeartbeatMs = GetTickCount64();
        Volatile.Write(ref Header->magic, Proto.Magic);

        Log.Info($"shared memory {name} ({size >> 20} MB, {(created == ErrorAlreadyExists ? "reused" : "created")})");
        return true;
    }

    public void Close()
    {
        if (_base != null)
        {
            Volatile.Write(ref Header->magic, 0);
            UnmapViewOfFile((IntPtr)_base);
            _base = null;
        }
        if (_mapping != IntPtr.Zero)
        {
            CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
        }
    }

    private void Clear(long offset, long bytes) => new Span<byte>(_base + offset, (int)bytes).Clear();

    public void Heartbeat() => Volatile.Write(ref Header->hostHeartbeatMs, GetTickCount64());

    public bool GuestAlive
    {
        get
        {
            ulong last = Volatile.Read(ref Header->guestHeartbeatMs);
            return last != 0 && GetTickCount64() - last < GuestTimeoutMs;
        }
    }

    public uint GuestPid => Header->guestPid;

    /// <summary>Publishes the host state. Minecraft paces its frames on this seq, so call it every frame.</summary>
    public void WriteHostState(in Proto.HostState state) => Rings.SeqlockWrite(_base + Proto.OffHostState, state);

    public bool ReadGuestState(out Proto.GuestState state) => Rings.SeqlockRead(_base + Proto.OffGuestState, out state);

    public void PushInput(Proto.InputType type, ushort code = 0, int a = 0, int b = 0, int c = 0) =>
        Rings.EntryPush(_base + Proto.OffInputRing, Proto.InputRingEntries,
            new Proto.InputEvent { type = (ushort)type, code = code, a = a, b = b, c = c });

    public bool PopEvent(out Proto.GuestEvent e) => Rings.EntryPop(_base + Proto.OffEventRing, Proto.EventRingEntries, out e);

    public bool WriteCollision(uint type, void* payload, uint bytes) =>
        Rings.ByteWrite(_base + Proto.OffCollisionRing, Proto.ColRingDataBytes, type, payload, bytes);

    /// <summary>Bytes Minecraft has not consumed yet.</summary>
    public long CollisionBacklog
    {
        get
        {
            byte* ring = _base + Proto.OffCollisionRing;
            return (long)(*(ulong*)(ring + Proto.RingHeadOff) - Volatile.Read(ref *(ulong*)(ring + Proto.RingTailOff)));
        }
    }

    public void DrainRender(long maxBytes, Rings.MessageHandler handler) =>
        Rings.ByteDrain(_base + Proto.OffRenderRing, Proto.RenRingDataBytes, maxBytes, handler);

    // ---- overlay triple buffer: we own the front slot, Minecraft the back, the state word names the middle ----

    public bool AcquireOverlayFrame()
    {
        ref int state = ref *(int*)(_base + Proto.OffOverlayCtl);
        if ((Volatile.Read(ref state) & (int)Proto.OverlayDirty) == 0)
            return false;
        int old = Interlocked.Exchange(ref state, _overlayFront);
        _overlayFront = old & 3;
        return true;
    }

    public void ResetOverlay()
    {
        Volatile.Write(ref *(int*)(_base + Proto.OffOverlayCtl), 0);
        _overlayFront = 2;
    }

    public byte* FrontPixels => _base + Proto.OffOverlayPixels + Proto.OverlaySlotBytes * _overlayFront;

    public Proto.OverlaySlotHdr* FrontHeader =>
        (Proto.OverlaySlotHdr*)(_base + Proto.OffOverlaySlotHdr + sizeof(Proto.OverlaySlotHdr) * _overlayFront);
}
