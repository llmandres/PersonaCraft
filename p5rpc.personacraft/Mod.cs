using p5rpc.inputhook.interfaces;
using p5rpc.lib.interfaces;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace p5rpc.personacraft;

/// <summary>Reloaded-II entry point: gathers the libraries, then hands over to <see cref="Plugin"/>.</summary>
public class Mod : IMod
{
    private Plugin? _plugin;

    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        var loader = (IModLoader)loaderApi;
        string dir = loader.GetDirectoryForModId(modConfig.ModId);
        Log.Init((ILogger)loader.GetLogger(), Path.Combine(dir, "personacraft.log"));
        Log.Info($"PersonaCraft {modConfig.ModVersion} loading");

        var settings = Settings.Load(Path.Combine(dir, "personacraft.json"));
        if (!settings.Enabled)
        {
            Log.Info("disabled in personacraft.json; P5R runs untouched");
            return;
        }

        if (!TryGet<IP5RLib>(loader, "p5rpc.lib", out var lib)
            || !TryGet<IInputHook>(loader, "p5rpc.inputhook", out var inputHook)
            || !TryGet<IReloadedHooks>(loader, "reloaded.sharedlib.hooks", out var hooks)
            || !TryGet<IStartupScanner>(loader, "Reloaded.Memory.SigScan.ReloadedII", out var scanner))
            return;

        try
        {
            _plugin = new Plugin(settings, lib, inputHook, hooks, scanner);
            _plugin.Start();
        }
        catch (Exception e)
        {
            Log.Error($"not started: {e.Message}");
        }
    }

    private static bool TryGet<T>(IModLoader loader, string modId, out T value) where T : class
    {
        if (loader.GetController<T>() is { } controller && controller.TryGetTarget(out var target))
        {
            value = target;
            return true;
        }
        Log.Error($"{modId} is missing; PersonaCraft stays off");
        value = null!;
        return false;
    }

    public void Suspend() { }
    public void Resume() { }
    public void Unload() => _plugin?.Stop();
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing => () => _plugin?.Stop();
}
