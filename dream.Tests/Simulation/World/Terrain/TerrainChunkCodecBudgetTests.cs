using Dream.Multiplayer.Protocol;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.World.Terrain;

// Persistence wave E, C0: the decode budget bounds the subcell overrides a hostile blob can make the
// decoder allocate, and the generated World10k terrain sits far below the transfer limit.
public class TerrainChunkCodecBudgetTests(ITestOutputHelper output)
{
    private static MapDefinition Def(int size) =>
        new(size, 8, size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);

    [Fact]
    public void World10k_GeneratedDecodedOverrideTotal_IsRecordedAndFarBelowTheLimit()
    {
        MapDefinition definition = Def(100);
        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(new GeneratedMapSource(definition));

        long total = TerrainChunkBudgetProbe.Total(definition, chunks);

        output.WriteLine($"World10k generated decoded subcell override total: {total:N0} (limit {WorldTransferLimits.Default.MaxTerrainSubcellOverrides:N0})");
        Assert.True(total <= WorldTransferLimits.Default.MaxTerrainSubcellOverrides / 4);
        Assert.Equal(ExpectedWorld10kOverrideTotal, total);
    }

    // Recorded from the default generator at C0. A generator change moves it (and the golden terrain).
    private const long ExpectedWorld10kOverrideTotal = 246_382;

    [Fact]
    public void World10k_GeneratedImpliedSubcellTotal_IsRecordedAndFarBelowTheLimit()
    {
        MapDefinition definition = Def(100);
        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(new GeneratedMapSource(definition));

        long total = TerrainChunkBudgetProbe.ImpliedTotal(definition, chunks);

        output.WriteLine($"World10k generated implied subcell total: {total:N0} (limit {WorldTransferLimits.Default.MaxTerrainSubcells:N0})");
        Assert.True(total <= WorldTransferLimits.Default.MaxTerrainSubcells / 2);
        Assert.Equal(ExpectedWorld10kImpliedTotal, total);
    }

    private const long ExpectedWorld10kImpliedTotal = 1_667_072;

    [Fact]
    public void TheBudget_IsSpentExactly_AndExhaustedBeforeAnyOverrideSetIsAllocated()
    {
        MapDefinition definition = Def(100);
        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(new GeneratedMapSource(definition));
        PersistedTerrainChunk heavy = chunks.OrderByDescending(Spent).First();
        long spent = Spent(heavy);
        Assert.True(spent > 0, "the probe chunk must hold subcell overrides");

        // Exactly enough: decodes, budget lands on zero.
        long budget = spent;
        _ = TerrainChunkCodec.Decode(definition, heavy.ChunkX, heavy.ChunkZ, heavy.Data, ref budget);
        Assert.Equal(0, budget);

        // One short: rejected as invalid data, and the failing set was never subtracted or allocated.
        budget = spent - 1;
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => TerrainChunkCodec.Decode(definition, heavy.ChunkX, heavy.ChunkZ, heavy.Data, ref budget));
        Assert.Contains("budget", error.Message, StringComparison.Ordinal);
        Assert.True(budget >= 0);

        budget = 0;
        Assert.Throws<InvalidDataException>(
            () => TerrainChunkCodec.Decode(definition, heavy.ChunkX, heavy.ChunkZ, heavy.Data, ref budget));
        Assert.Equal(0, budget);
    }

    [Fact]
    public void TheUnbudgetedOverload_DecodesTheSameRecords()
    {
        MapDefinition definition = Def(56);
        PersistedTerrainChunk chunk = TerrainChunkExporter.Export(new GeneratedMapSource(definition))[0];
        long budget = long.MaxValue;

        var viaBudget = TerrainChunkCodec.Decode(definition, chunk.ChunkX, chunk.ChunkZ, chunk.Data, ref budget);
        var plain = TerrainChunkCodec.Decode(definition, chunk.ChunkX, chunk.ChunkZ, chunk.Data);

        Assert.Equal(plain.Count, viaBudget.Count);
        Assert.True(plain.Select(c => c.Key).SequenceEqual(viaBudget.Select(c => c.Key)));
    }

    private static long Spent(PersistedTerrainChunk chunk)
    {
        long budget = long.MaxValue;
        _ = TerrainChunkCodec.Decode(Def(100), chunk.ChunkX, chunk.ChunkZ, chunk.Data, ref budget);
        return long.MaxValue - budget;
    }
}
