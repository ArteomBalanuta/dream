using System.Diagnostics.CodeAnalysis;
using Dream.Simulation.World.MapSources;

namespace Dream.Simulation.World;

// The world generators this build can build a map with.
//
// A key and version is a promise: a world row stores the pair it was created with, and both the
// server and the client build that world from it, so a registered generator must keep producing
// byte-identical cells for the same request forever. Change a distribution by adding a key or
// bumping a version, never by editing one in place.
//
// A registered generator must also be seed-independent while the client rebuilds generated worlds
// from the descriptor alone: the simulation seed is server-only (WorldDescriptor).
public static class WorldGeneratorCatalog
{
    public const string DefaultKey = "default";
    public const int DefaultVersion = 1;

    // Noise-driven surface distribution; the same geometry as the default generator.
    public const string NaturalKey = "natural";
    public const int NaturalVersion = 1;

    private static readonly IMapGenerator[] s_all = [new DefaultMapGenerator(), new NaturalMapGenerator()];

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

    // The newest registered generator for a key. New worlds are created with it; existing worlds
    // keep the exact version stored on their row. Every key has exactly one version today, so the
    // version comparison below is untested in practice: the day a v2 is registered, check that a
    // new world is created at v2 and that v1 worlds still load.
    public static bool TryGetLatest(string key, [NotNullWhen(true)] out IMapGenerator? generator)
    {
        generator = null;
        foreach (IMapGenerator candidate in s_all)
        {
            if (candidate.Key == key && (generator == null || candidate.Version > generator.Version))
            {
                generator = candidate;
            }
        }

        return generator != null;
    }
}
