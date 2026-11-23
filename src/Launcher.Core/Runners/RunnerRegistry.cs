namespace Launcher.Core.Runners;

public static class RunnerRegistry
{
    static readonly Dictionary<string, IRunner> Runners = new IRunner[]
    {
        new SupermodelRunner(),
        new DaytonaRecompRunner(),
        new VCThunderRunner(),
        new FlycastRunner(),
        new XeniaRunner(),
        new TeknoParrotRunner(),
        new XemuRunner(),
        new ExeRunner(),
        new SteamRunner(),
    }.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The runner for an id, or null if it hasn't been built yet.</summary>
    public static IRunner? Get(string id) => Runners.GetValueOrDefault(id);
}
