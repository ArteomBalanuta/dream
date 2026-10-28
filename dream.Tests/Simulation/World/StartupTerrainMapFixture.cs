using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Simulation.World;

// One generated 56x56 startup world, shared read-only by the tests of a class. Building it is the
// expensive part of those tests, and the generator is deterministic, so a fresh map per test adds
// nothing. Sharing is only safe while no test mutates the map: Dispose fails the class if the
// static content version or the world checksum moved since the map was built.
public sealed class StartupTerrainMapFixture : IDisposable
{
    private readonly long _builtStaticContentVersion;
    private readonly ulong _builtChecksum;
    private readonly int _builtCellCount;

    public StartupTerrainMapFixture()
    {
        Map = new Map(width: 56, height: 8, depth: 56, generateStartupTerrain: true);
        _builtStaticContentVersion = Map.StaticContentVersion;
        _builtChecksum = WorldStateChecksum.Compute(Map);
        _builtCellCount = Map.GeometryCells.Count;
    }

    public Map Map { get; }

    public void Dispose()
    {
        Assert.Equal(_builtStaticContentVersion, Map.StaticContentVersion);
        Assert.Equal(_builtCellCount, Map.GeometryCells.Count);
        Assert.Equal(_builtChecksum, WorldStateChecksum.Compute(Map));
    }
}
