using System.Diagnostics.CodeAnalysis;
using Dream.Simulation.World.MapSources;

namespace Dream.Simulation.World;

// The world generators this build can build a map with. Only the default generator, version 1.
public static class WorldGeneratorCatalog
{
    public const string DefaultKey = "default";
    public const int DefaultVersion = 1;

    private static readonly IMapGenerator[] s_all = [new DefaultMapGenerator()];

    public static IReadOnlyList<IMapGenerator> All => s_all;

    public static bool TryGet(string key, int version, [NotNullWhen(true)] out IMapGenerator? generator)
    {
        foreach (IMapGenerator candidate in s_all)
        {
            if (candidate.Key == key && candidate.Version == version)
            {
                generator = candidate;
                return true;
            }
        }

        generator = null;
        return false;
    }

    public static bool IsSupported(string key, int version) => TryGet(key, version, out _);
}
