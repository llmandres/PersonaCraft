using System.Runtime.InteropServices;

namespace p5rpc.personacraft.Input;

/// <summary>
/// Windows virtual keys to SDL scancodes (USB HID usage ids), which is what Minecraft 26.x reads and
/// what P5R's own key codes are built from (P5R key = 0x20000 + HID usage). Goes through the
/// keyboard layout's scan code, so on a Spanish or French layout the physical key is sent, like a
/// Minecraft running in its own window would see it.
/// </summary>
internal static class Scancodes
{
    [DllImport("user32")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    private const uint MapVkToVscEx = 4;

    public const ushort Escape = 41;

    private static readonly Dictionary<int, ushort> FromVk = Build();

    public static bool TryGet(int vk, out ushort hid) => FromVk.TryGetValue(vk, out hid);

    private static Dictionary<int, ushort> Build()
    {
        // PC set-1 scan codes (0xE0-prefixed ones as 0xE0xx) to HID usages.
        var set1 = new Dictionary<uint, ushort>
        {
            [0x01] = 41, [0x02] = 30, [0x03] = 31, [0x04] = 32, [0x05] = 33, [0x06] = 34, [0x07] = 35, [0x08] = 36,
            [0x09] = 37, [0x0A] = 38, [0x0B] = 39, [0x0C] = 45, [0x0D] = 46, [0x0E] = 42, [0x0F] = 43,
            [0x10] = 20, [0x11] = 26, [0x12] = 8, [0x13] = 21, [0x14] = 23, [0x15] = 28, [0x16] = 24, [0x17] = 12,
            [0x18] = 18, [0x19] = 19, [0x1A] = 47, [0x1B] = 48, [0x1C] = 40, [0x1D] = 224,
            [0x1E] = 4, [0x1F] = 22, [0x20] = 7, [0x21] = 9, [0x22] = 10, [0x23] = 11, [0x24] = 13, [0x25] = 14,
            [0x26] = 15, [0x27] = 51, [0x28] = 52, [0x29] = 53, [0x2A] = 225, [0x2B] = 49,
            [0x2C] = 29, [0x2D] = 27, [0x2E] = 6, [0x2F] = 25, [0x30] = 5, [0x31] = 17, [0x32] = 16, [0x33] = 54,
            [0x34] = 55, [0x35] = 56, [0x36] = 229, [0x37] = 85, [0x38] = 226, [0x39] = 44, [0x3A] = 57,
            [0x3B] = 58, [0x3C] = 59, [0x3D] = 60, [0x3E] = 61, [0x3F] = 62, [0x40] = 63, [0x41] = 64, [0x42] = 65,
            [0x43] = 66, [0x44] = 67, [0x45] = 83, [0x46] = 71,
            [0x47] = 95, [0x48] = 96, [0x49] = 97, [0x4A] = 86, [0x4B] = 92, [0x4C] = 93, [0x4D] = 94, [0x4E] = 87,
            [0x4F] = 89, [0x50] = 90, [0x51] = 91, [0x52] = 98, [0x53] = 99, [0x56] = 100, [0x57] = 68, [0x58] = 69,
            [0xE01C] = 88, [0xE01D] = 228, [0xE035] = 84, [0xE038] = 230,
            [0xE047] = 74, [0xE048] = 82, [0xE049] = 75, [0xE04B] = 80, [0xE04D] = 79, [0xE04F] = 77,
            [0xE050] = 81, [0xE051] = 78, [0xE052] = 73, [0xE053] = 76, [0xE05B] = 227, [0xE05C] = 231, [0xE05D] = 101,
        };
        var map = new Dictionary<int, ushort>();
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12) // generic Shift/Ctrl/Alt: the left/right ones are mapped instead
                continue;
            uint sc = MapVirtualKeyW((uint)vk, MapVkToVscEx);
            if (sc == 0)
                continue;
            if ((sc & 0xFF00) == 0xE000 || (sc & 0xFF00) == 0xE100)
                sc = 0xE000 | (sc & 0xFF);
            // Keys whose VK alone says "extended" but MapVirtualKey reports the base scan code.
            if (vk is >= 0x21 and <= 0x2E || vk is 0x5B or 0x5C or 0x5D or 0xA3 or 0xA5 or 0x6F)
                sc = 0xE000 | (sc & 0xFF);
            if (vk == 0x0D)
                sc = 0x1C; // main Enter (the keypad one has no own VK)
            if (set1.TryGetValue(sc, out ushort hid))
                map[vk] = hid;
        }
        return map;
    }
}
