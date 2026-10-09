using System.Runtime.InteropServices;
using p5rpc.inputhook.interfaces;
using p5rpc.personacraft.Link;
using p5rpc.personacraft.Player;
using p5rpc.personacraft.World;
using static p5rpc.inputhook.interfaces.Inputs;

namespace p5rpc.personacraft.Input;

/// <summary>
/// Keyboard and mouse for Minecraft, and the filter that keeps them away from P5R.
///
/// Minecraft side (like PeakCraft's InputBridge): raw key state is polled and sent as SDL scancodes,
/// mouse buttons as SDL numbers, the wheel and typed characters come from the window hook. With no
/// Minecraft screen open the mouse turns the look (Minecraft's own sensitivity curve), which this
/// class owns; with one open it moves a cursor in overlay pixels.
///
/// P5R side: P5R reads the keyboard as a list of pressed keys (P5R key = 0x20000 + HID usage) that
/// p5rpc.inputhook lets us rewrite. While Minecraft has Joker the list is emptied except for P5R's
/// own keys (Tab for its menu, Esc when no Minecraft screen is open), and the interact key (G)
/// becomes P5R's confirm key (E) for as long as it is held.
/// </summary>
internal sealed class InputBridge
{
    [DllImport("user32")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private const int VkEscape = 0x1B;
    private static readonly (int Vk, int Sdl)[] MouseButtons = [(0x01, 1), (0x04, 2), (0x02, 3), (0x05, 4), (0x06, 5)];

    private readonly HostLink _link;
    private readonly Settings _settings;
    private readonly bool[] _held = new bool[256];
    private readonly HashSet<int> _p5rKeysVk;
    private readonly HashSet<int> _p5rKeysHid = new();
    private bool _routing;
    private bool _screenOpen;
    private bool _recentred;
    private float _cursorX, _cursorY;
    private float _sensitivity = 0.5f;
    private int _repeatVk = -1;
    private long _repeatAt;

    // Read by the P5R input hook on the game's thread.
    private volatile bool _filterP5R;
    private volatile bool _interactDown;
    private volatile bool _escToP5R = true;

    /// <summary>The authoritative look, Minecraft degrees.</summary>
    public float Yaw;
    public float Pitch;

    public InputBridge(HostLink link, Settings settings)
    {
        _link = link;
        _settings = settings;
        _p5rKeysVk = new HashSet<int>(settings.P5RKeys);
        foreach (int vk in settings.P5RKeys)
        {
            if (Scancodes.TryGet(vk, out ushort hid))
                _p5rKeysHid.Add(hid);
        }
    }

    public bool ScreenOpen => _routing && _screenOpen;
    public (float X, float Y) Cursor => (_cursorX, _cursorY);

    /// <summary>Hooks P5R's keyboard list through p5rpc.inputhook.</summary>
    public void HookP5R(IInputHook hook)
    {
        hook.OnInputIntercept += FilterP5R;
        Log.Info("input: filtering P5R's keyboard through p5rpc.inputhook");
    }

    private void FilterP5R(List<Key> keys)
    {
        if (!_filterP5R)
            return;
        bool esc = _escToP5R;
        keys.RemoveAll(k =>
        {
            int hid = (int)k - 0x20000;
            return !(_p5rKeysHid.Contains(hid) || (esc && hid == Scancodes.Escape));
        });
        if (_interactDown)
        {
            foreach (int code in _settings.P5RConfirmKeys)
            {
                var confirm = (Key)code;
                if (!keys.Contains(confirm))
                    keys.Add(confirm);
            }
        }
    }

    public void SetLook(float yaw, float pitch)
    {
        Yaw = yaw;
        Pitch = Math.Clamp(pitch, -90f, 90f);
    }

    public void Update(Ownership ownership, in Proto.GuestState guest, int viewportW, int viewportH)
    {
        _filterP5R = ownership.P5RKeyboardOff;
        bool focused = WindowHook.Window != 0 && GetForegroundWindow() == WindowHook.Window;
        bool route = ownership.MinecraftHasInput && focused;
        WindowHook.Swallow = route;
        if (route != _routing)
        {
            _routing = route;
            _recentred = false;
            ReleaseAll();
            Log.Info(route ? "input: keyboard and mouse go to Minecraft" : "input: released (release-all sent)");
        }
        if (!route)
        {
            _interactDown = false;
            _escToP5R = true;
            while (WindowHook.Chars.TryDequeue(out _)) { }
            WindowHook.TakeWheel();
            return;
        }
        if (guest.sensitivity > 0f)
            _sensitivity = guest.sensitivity;

        bool nowOpen = (guest.flags & (uint)Proto.GuestFlags.ScreenOpen) != 0;
        if (nowOpen && !_screenOpen)
        {
            _cursorX = viewportW * 0.5f;
            _cursorY = viewportH * 0.5f;
            _link.PushInput(Proto.InputType.Cursor, 0, (int)_cursorX, (int)_cursorY);
        }
        _screenOpen = nowOpen;
        _escToP5R = !_screenOpen;

        var (dx, dy) = MouseDelta();
        if (_screenOpen)
        {
            if (dx != 0 || dy != 0)
            {
                _cursorX = Math.Clamp(_cursorX + dx, 0f, Math.Max(viewportW - 1, 0));
                _cursorY = Math.Clamp(_cursorY + dy, 0f, Math.Max(viewportH - 1, 0));
                _link.PushInput(Proto.InputType.Cursor, 0, (int)_cursorX, (int)_cursorY);
            }
            while (WindowHook.Chars.TryDequeue(out char c))
            {
                if (c >= ' ' && c != 127)
                    _link.PushInput(Proto.InputType.Text, 0, c);
            }
        }
        else
        {
            while (WindowHook.Chars.TryDequeue(out _)) { }
            // Minecraft's own mouse curve (MouseHandler.turnPlayer): (s * 0.6 + 0.2)^3 * 8, times 0.15 degrees.
            float s = _sensitivity * 0.6f + 0.2f;
            float factor = s * s * s * 8f * 0.15f;
            Yaw = Look.WrapDegrees(Yaw + dx * factor);
            Pitch = Math.Clamp(Pitch + dy * factor, -90f, 90f);
        }

        PumpKeys();
        foreach (var (vk, sdl) in MouseButtons)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down != _held[vk])
            {
                _held[vk] = down;
                _link.PushInput(Proto.InputType.MouseButton, (ushort)sdl, down ? 1 : 0);
            }
        }
        int wheel = WindowHook.TakeWheel();
        for (int i = 0; i < Math.Abs(wheel); i++)
            _link.PushInput(Proto.InputType.Scroll, 0, wheel > 0 ? 120 : -120);
    }

    /// <summary>Mouse movement since the last call, from re-centring the OS cursor in P5R's window.</summary>
    private (int X, int Y) MouseDelta()
    {
        nint hwnd = WindowHook.Window;
        if (!GetClientRect(hwnd, out var rect))
            return (0, 0);
        var centre = new Point { X = (rect.Right - rect.Left) / 2, Y = (rect.Bottom - rect.Top) / 2 };
        ClientToScreen(hwnd, ref centre);
        GetCursorPos(out var pos);
        SetCursorPos(centre.X, centre.Y);
        if (!_recentred)
        {
            _recentred = true;
            return (0, 0);
        }
        return (pos.X - centre.X, pos.Y - centre.Y);
    }

    private void PumpKeys()
    {
        bool interact = false;
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            // With a Minecraft screen up every key is Minecraft's, so typing works and Esc closes it.
            if (!_screenOpen)
            {
                if (vk == VkEscape || _p5rKeysVk.Contains(vk))
                {
                    Release(vk);
                    continue; // P5R's
                }
                if (vk == _settings.InteractKey)
                {
                    interact = down;
                    continue;
                }
                if (vk == _settings.HandBackKey)
                    continue; // the plugin's
                if (vk == _settings.MinecraftMenuKey)
                {
                    if (down && !_held[vk])
                    {
                        ReleaseAllKeys();
                        _link.PushInput(Proto.InputType.OpenMenu);
                    }
                    _held[vk] = down;
                    continue;
                }
            }
            if (!Scancodes.TryGet(vk, out ushort scancode))
                continue;
            if (down && !_held[vk])
            {
                _held[vk] = true;
                _link.PushInput(Proto.InputType.Key, scancode, 1);
                _repeatVk = vk;
                _repeatAt = Environment.TickCount64 + 400;
            }
            else if (!down && _held[vk])
            {
                _held[vk] = false;
                _link.PushInput(Proto.InputType.Key, scancode, 0);
                if (_repeatVk == vk)
                    _repeatVk = -1;
            }
        }
        _interactDown = interact;
        // Key repeat for Minecraft's text fields (held backspace, arrows): a second "down" is a repeat there.
        if (_screenOpen && _repeatVk >= 0 && Environment.TickCount64 >= _repeatAt && Scancodes.TryGet(_repeatVk, out ushort repeat))
        {
            _repeatAt = Environment.TickCount64 + 40;
            _link.PushInput(Proto.InputType.Key, repeat, 1);
        }
    }

    private void Release(int vk)
    {
        if (_held[vk] && Scancodes.TryGet(vk, out ushort scancode))
            _link.PushInput(Proto.InputType.Key, scancode, 0);
        _held[vk] = false;
    }

    private void ReleaseAllKeys()
    {
        for (int vk = 0x08; vk < _held.Length; vk++)
            Release(vk);
        _repeatVk = -1;
    }

    /// <summary>Input focus left Minecraft (P5R took Joker, alt-tab, link loss): lift everything.</summary>
    public void ReleaseAll()
    {
        Array.Clear(_held);
        _repeatVk = -1;
        _screenOpen = false;
        _interactDown = false;
        _link.PushInput(Proto.InputType.ReleaseAll);
    }
}
