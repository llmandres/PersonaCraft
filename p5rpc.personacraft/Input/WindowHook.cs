using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace p5rpc.personacraft.Input;

/// <summary>
/// Subclasses P5R's window for what cannot be polled: typed characters (WM_CHAR, so the keyboard
/// layout and dead keys work in Minecraft's chat) and the mouse wheel. While Minecraft has the input,
/// mouse buttons and the wheel are not passed on to P5R.
/// </summary>
internal static class WindowHook
{
    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32", EntryPoint = "CallWindowProcW")] private static extern nint CallWindowProc(nint previous, nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(nint hwnd, nint param);

    private const int GwlpWndProc = -4;
    private const uint WmChar = 0x0102, WmMouseWheel = 0x020A;
    private const uint WmMouseFirst = 0x0201, WmMouseLast = 0x020E; // buttons and wheels, not WM_MOUSEMOVE

    private static WndProc? _proc;
    private static nint _previous;

    public static nint Window { get; private set; }

    /// <summary>True while Minecraft has the input; set by the input bridge.</summary>
    public static volatile bool Swallow;

    public static readonly ConcurrentQueue<char> Chars = new();
    private static int _wheel;

    /// <summary>Wheel notches since the last call (positive = away from the user).</summary>
    public static int TakeWheel() => Interlocked.Exchange(ref _wheel, 0);

    /// <summary>Finds P5R's main window and subclasses it. Returns false until the window exists.</summary>
    public static bool TryInstall()
    {
        if (Window != 0)
            return true;
        uint self = (uint)Environment.ProcessId;
        nint found = 0;
        long bestArea = 0;
        var name = new System.Text.StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != self || !IsWindowVisible(hwnd))
                return true;
            name.Clear();
            GetClassNameW(hwnd, name, name.Capacity);
            if (name.ToString() is "ConsoleWindowClass" or "PseudoConsoleWindow")
                return true; // Reloaded's console
            GetClientRect(hwnd, out var r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea)
            {
                bestArea = area;
                found = hwnd;
            }
            return true;
        }, 0);
        if (found == 0)
            return false;
        _proc = Proc;
        _previous = SetWindowLongPtr(found, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        if (_previous == 0)
            return false;
        Window = found;
        Log.Info($"input: hooked P5R's window 0x{found:X}");
        return true;
    }

    private static nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (Swallow)
        {
            if (msg == WmChar)
            {
                char c = (char)(wParam & 0xFFFF);
                if (Chars.Count < 256)
                    Chars.Enqueue(c);
                return 0;
            }
            if (msg == WmMouseWheel)
            {
                Interlocked.Add(ref _wheel, (short)((wParam >> 16) & 0xFFFF) / 120);
                return 0;
            }
            if (msg is >= WmMouseFirst and <= WmMouseLast)
                return 0;
        }
        return CallWindowProc(_previous, hwnd, msg, wParam, lParam);
    }
}
