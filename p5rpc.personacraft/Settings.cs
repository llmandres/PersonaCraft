using System.Text.Json;

namespace p5rpc.personacraft;

/// <summary>
/// personacraft.json next to the mod. Created with defaults on first run; edit it while the game is
/// closed. Everything that writes into the running game can be switched off here.
/// </summary>
internal sealed class Settings
{
    /// <summary>Master switch. False: the mod only logs, P5R runs untouched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Let Minecraft take over Joker in the field. False: link and collision only (Minecraft's player stays parked).</summary>
    public bool TakeOver { get; set; } = true;

    /// <summary>Draw Minecraft's HUD and screens over P5R.</summary>
    public bool Overlay { get; set; } = true;

    /// <summary>Hide Joker while Minecraft has him; in third person (F5) Minecraft's player model is drawn instead.</summary>
    public bool HideJoker { get; set; } = true;

    /// <summary>Key (Windows virtual-key code) that presses P5R's confirm/interact. Default G.</summary>
    public int InteractKey { get; set; } = 0x47;

    /// <summary>P5R key (p5rpc.inputhook Key value) the interact key sends. Default E, P5R's keyboard confirm.</summary>
    public int P5RConfirmKey { get; set; } = 0x20000 + 8;

    /// <summary>Key that opens Minecraft's pause menu. Default O.</summary>
    public int MinecraftMenuKey { get; set; } = 0x4F;

    /// <summary>Keys P5R keeps while Minecraft has the body (virtual-key codes). Default Tab (P5R's menu).</summary>
    public int[] P5RKeys { get; set; } = [0x09];

    /// <summary>Log extra diagnostics (state numbers, memory candidates, per-second summaries).</summary>
    public bool Verbose { get; set; } = true;

    public static Settings Load(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        Settings settings = new();
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), options) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Warn($"{Path.GetFileName(path)} could not be read ({e.Message}); using defaults");
        }
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, options));
        }
        catch
        {
            // read-only folder: defaults still apply
        }
        return settings;
    }
}
