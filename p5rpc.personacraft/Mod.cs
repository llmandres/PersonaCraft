using p5rpc.lib.interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace p5rpc.personacraft;

/// <summary>Reloaded-II entry point. Only wires the probe up; all logic lives in <see cref="Probe"/>.</summary>
public class Mod : IMod
{
    private Probe? _probe;

    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        var loader = (IModLoader)loaderApi;
        var logger = (ILogger)loader.GetLogger();

        if (loader.GetController<IP5RLib>() is not { } controller || !controller.TryGetTarget(out var lib))
        {
            logger.WriteLine("[PersonaCraft] p5rpc.lib is missing, so the probe stays off.");
            return;
        }

        string csv = Path.Combine(loader.GetDirectoryForModId(modConfig.ModId), "positions.csv");
        _probe = new Probe(lib, logger, csv);
        _probe.Start();
    }

    public void Suspend() { }
    public void Resume() { }
    public void Unload() => _probe?.Stop();
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing => () => _probe?.Stop();
}
