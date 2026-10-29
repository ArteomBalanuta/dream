namespace Dream.Simulation.World;

// The world generators this build can build a map with. Only the default generator, version 1.
public static class WorldGeneratorCatalog
{
    public const string DefaultKey = "default";
    public const int DefaultVersion = 1;

    public static bool IsSupported(string key, int version) => key == DefaultKey && version == DefaultVersion;
}
