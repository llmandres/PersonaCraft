using Reloaded.Mod.Interfaces;

namespace p5rpc.personacraft;

/// <summary>
/// One log for the whole mod: Reloaded's console plus personacraft.log next to the mod, so a test
/// session can be read back afterwards. Thread-safe; every line carries a timestamp.
/// </summary>
internal static class Log
{
    private static ILogger? _logger;
    private static StreamWriter? _file;
    private static readonly object Gate = new();

    public static void Init(ILogger logger, string path)
    {
        _logger = logger;
        try
        {
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
        catch
        {
            _file = null; // the console still works
        }
    }

    public static void Info(string message) => Write("", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR ", message);

    private static void Write(string level, string message)
    {
        string line = $"[PersonaCraft] {level}{message}";
        _logger?.WriteLine(line);
        lock (Gate)
        {
            _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level}{message}");
        }
    }
}
