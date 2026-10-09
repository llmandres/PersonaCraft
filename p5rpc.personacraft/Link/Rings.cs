// Ported from PeakCraft (peak/src/PeakCraft/Link), itself a mirror of SkyCraft's protocol/skycraft_protocol.h.
// MIT, Copyright chasmlol (SkyCraft). Only the logger changed.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace p5rpc.personacraft.Link;

/// <summary>
/// The protocol's three shared-memory shapes: a seqlock slot, a ring of fixed-size entries
/// (input, events) and a byte ring of {type, payloadBytes} messages (collision, render).
/// Each works on a raw base pointer so the same code runs on the mapping and on a test buffer.
/// </summary>
internal static unsafe class Rings
{
    // ---- seqlock: u32 seq at offset 0, odd while the writer is inside ----

    public static void SeqlockWrite<T>(byte* slot, in T value) where T : unmanaged
    {
        uint s = *(uint*)slot;
        Volatile.Write(ref *(uint*)slot, s + 1);
        Thread.MemoryBarrier();
        fixed (T* src = &value)
        {
            Buffer.MemoryCopy((byte*)src + 4, slot + 4, sizeof(T) - 4, sizeof(T) - 4);
        }
        Volatile.Write(ref *(uint*)slot, s + 2);
    }

    public static bool SeqlockRead<T>(byte* slot, out T value) where T : unmanaged
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            uint s1 = Volatile.Read(ref *(uint*)slot);
            if ((s1 & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }
            T copy = *(T*)slot;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref *(uint*)slot) == s1)
            {
                value = copy;
                return true;
            }
        }
        value = default;
        return false;
    }

    // ---- ring of fixed-size entries; entries is a power of two ----

    public static bool EntryPush<T>(byte* ring, int entries, in T entry) where T : unmanaged
    {
        ulong head = *(ulong*)(ring + Proto.RingHeadOff);
        ulong tail = Volatile.Read(ref *(ulong*)(ring + Proto.RingTailOff));
        if (head - tail >= (ulong)entries)
        {
            return false;
        }
        ((T*)(ring + Proto.RingDataOff))[head & (ulong)(entries - 1)] = entry;
        Volatile.Write(ref *(ulong*)(ring + Proto.RingHeadOff), head + 1);
        return true;
    }

    public static bool EntryPop<T>(byte* ring, int entries, out T entry) where T : unmanaged
    {
        ulong head = Volatile.Read(ref *(ulong*)(ring + Proto.RingHeadOff));
        ulong tail = *(ulong*)(ring + Proto.RingTailOff);
        if (tail >= head)
        {
            entry = default;
            return false;
        }
        if (head - tail > (ulong)entries)
        {
            tail = head - (ulong)entries; // the producer lapped us; drop the oldest
        }
        entry = ((T*)(ring + Proto.RingDataOff))[tail & (ulong)(entries - 1)];
        Volatile.Write(ref *(ulong*)(ring + Proto.RingTailOff), tail + 1);
        return true;
    }

    // ---- byte ring: every message starts 8-byte aligned with a MsgHeader; type 0 = skip to start ----

    public static bool ByteWrite(byte* ring, long dataBytes, uint type, void* payload, uint payloadBytes)
    {
        byte* data = ring + Proto.RingDataOff;
        ulong size = (ulong)dataBytes;
        ulong msgBytes = ((ulong)sizeof(Proto.MsgHeader) + payloadBytes + 7) & ~7ul;
        if (msgBytes > size / 2)
        {
            return false;
        }
        ulong head = *(ulong*)(ring + Proto.RingHeadOff);
        ulong tail = Volatile.Read(ref *(ulong*)(ring + Proto.RingTailOff));
        ulong pos = head % size;
        ulong padBytes = pos + msgBytes > size ? size - pos : 0;
        if (size - (head - tail) < msgBytes + padBytes)
        {
            return false;
        }
        if (padBytes != 0)
        {
            *(Proto.MsgHeader*)(data + pos) = new Proto.MsgHeader { type = Proto.ColPad, payloadBytes = 0 };
            head += padBytes;
            pos = 0;
        }
        *(Proto.MsgHeader*)(data + pos) = new Proto.MsgHeader { type = type, payloadBytes = payloadBytes };
        if (payloadBytes != 0)
        {
            Buffer.MemoryCopy(payload, data + pos + sizeof(Proto.MsgHeader), payloadBytes, payloadBytes);
        }
        Volatile.Write(ref *(ulong*)(ring + Proto.RingHeadOff), head + msgBytes);
        return true;
    }

    public delegate void MessageHandler(uint type, byte* payload, uint payloadBytes);

    /// <summary>Hands every pending message to <paramref name="handler"/>, up to about maxBytes.</summary>
    public static void ByteDrain(byte* ring, long dataBytes, long maxBytes, MessageHandler handler)
    {
        byte* data = ring + Proto.RingDataOff;
        ulong size = (ulong)dataBytes;
        ulong head = Volatile.Read(ref *(ulong*)(ring + Proto.RingHeadOff));
        ulong tail = *(ulong*)(ring + Proto.RingTailOff);
        ulong done = 0;
        while (tail < head && done < (ulong)maxBytes)
        {
            ulong pos = tail % size;
            var hdr = (Proto.MsgHeader*)(data + pos);
            if (hdr->type == Proto.RenPad)
            {
                tail += size - pos;
                continue;
            }
            handler(hdr->type, data + pos + sizeof(Proto.MsgHeader), hdr->payloadBytes);
            ulong msgBytes = ((ulong)sizeof(Proto.MsgHeader) + hdr->payloadBytes + 7) & ~7ul;
            tail += msgBytes;
            done += msgBytes;
        }
        Volatile.Write(ref *(ulong*)(ring + Proto.RingTailOff), tail);
    }

    /// <summary>
    /// Writes and reads both ring kinds across a wrap-around on a private buffer and checks that
    /// what comes out is what went in. Run once at startup next to the layout self-check.
    /// </summary>
    public static bool SelfTest()
    {
        bool ok = true;
        const int entries = 8;
        const long dataBytes = 256;
        byte* entryRing = (byte*)Marshal.AllocHGlobal((IntPtr)(Proto.RingDataOff + entries * sizeof(Proto.InputEvent)));
        byte* byteRing = (byte*)Marshal.AllocHGlobal((IntPtr)(Proto.RingDataOff + dataBytes));
        try
        {
            new Span<byte>(entryRing, (int)Proto.RingDataOff).Clear();
            new Span<byte>(byteRing, (int)Proto.RingDataOff).Clear();

            // 3 rounds of 5 entries through an 8-entry ring: the second round wraps.
            int next = 0, expect = 0;
            for (int round = 0; round < 3 && ok; round++)
            {
                for (int i = 0; i < 5; i++, next++)
                {
                    ok &= EntryPush(entryRing, entries, new Proto.InputEvent { type = 1, code = (ushort)next, a = next * 7, b = -next, c = next ^ 0x55 });
                }
                for (int i = 0; i < 5; i++, expect++)
                {
                    ok &= EntryPop(entryRing, entries, out Proto.InputEvent e)
                        && e.code == expect && e.a == expect * 7 && e.b == -expect && e.c == (expect ^ 0x55);
                }
                ok &= !EntryPop(entryRing, entries, out Proto.InputEvent _);
            }
            // A full ring refuses the next entry.
            for (int i = 0; i < entries; i++)
            {
                ok &= EntryPush(entryRing, entries, default(Proto.InputEvent));
            }
            ok &= !EntryPush(entryRing, entries, default(Proto.InputEvent));
            Log.Info($"ring self-test: entry ring wrap-around {(ok ? "ok" : "FAIL")}");

            // 40-byte payloads (48 with the header) through a 256-byte ring: every 6th message pads and wraps.
            bool bytesOk = true;
            byte* payload = stackalloc byte[40];
            int written = 0, read = 0;
            for (int round = 0; round < 12 && bytesOk; round++)
            {
                for (int i = 0; i < 2; i++, written++)
                {
                    for (int k = 0; k < 40; k++)
                    {
                        payload[k] = (byte)(written * 31 + k);
                    }
                    bytesOk &= ByteWrite(byteRing, dataBytes, (uint)(written + 1), payload, 40);
                }
                ByteDrain(byteRing, dataBytes, long.MaxValue, (type, p, n) =>
                {
                    bytesOk &= type == (uint)(read + 1) && n == 40;
                    for (int k = 0; k < 40 && bytesOk; k++)
                    {
                        bytesOk &= p[k] == (byte)(read * 31 + k);
                    }
                    read++;
                });
                bytesOk &= read == written;
            }
            bytesOk &= *(ulong*)(byteRing + Proto.RingHeadOff) > (ulong)dataBytes; // it did wrap
            Log.Info($"ring self-test: byte ring wrap-around {(bytesOk ? "ok" : "FAIL")}");
            ok &= bytesOk;
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)entryRing);
            Marshal.FreeHGlobal((IntPtr)byteRing);
        }
        if (ok)
        {
            Log.Info("ring self-test: PASS");
        }
        else
        {
            Log.Error("ring self-test: FAIL");
        }
        return ok;
    }
}
