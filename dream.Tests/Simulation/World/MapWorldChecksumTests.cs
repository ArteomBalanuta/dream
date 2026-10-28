using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.World;

// SCALE-7(d): Map keeps WorldStateChecksum as a running sum, updated on every geometry and rubble
// store write, instead of the server walking every cell for each checksum. The value is part of
// the wire protocol (clients compare it against their own), so it must stay bit-identical to the
// full walk it replaced. LegacyCompute below is that walk, copied verbatim from before the change.
public class MapWorldChecksumTests
{
    private const int Steps = 1000;

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void RandomizedEdits_RunningChecksumEqualsFullRecomputationAfterEveryStep(int seed, bool headless)
    {
        Map map = new(generateStartupTerrain: true, headless: headless);
        Random random = new(seed);
        Assert.Equal(LegacyCompute(map), WorldStateChecksum.Compute(map));

        int changes = 0;
        ulong previous = WorldStateChecksum.Compute(map);
        for (int step = 0; step < Steps; step++)
        {
            MapEditScript.Step(random, [map]);

            ulong running = WorldStateChecksum.Compute(map);
            ulong legacy = LegacyCompute(map);
            Assert.True(legacy == running, $"seed {seed} step {step}: running {running} != full walk {legacy}");
            Assert.Equal(legacy, WorldStateChecksum.ComputeFull(map));
            if (running != previous)
            {
                changes++;
            }

            previous = running;
        }

        Assert.Empty(map.FindInvariantViolations());
        // The script must actually move the checksum, or the comparison proves nothing.
        Assert.True(changes > Steps / 4, $"seed {seed}: the checksum changed on only {changes} of {Steps} steps");
    }

    // 56 is the startup floor size, the smallest whose terrain has the subdivided sculpture cells
    // (carving them rebuilds the merged static bodies, so this world is much slower per step).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomizedEdits_SculptureWorld_RunningChecksumEqualsFullRecomputation(bool headless)
    {
        Map map = new(generateStartupTerrain: true, width: 56, depth: 56, headless: headless);
        Random random = new(5);
        for (int step = 0; step < 100; step++)
        {
            MapEditScript.Step(random, [map]);
            Assert.Equal(LegacyCompute(map), WorldStateChecksum.Compute(map));
        }

        Assert.Empty(map.FindInvariantViolations());
    }

    [Fact]
    public void RandomizedEdits_WithPhysics_RunningChecksumEqualsFullRecomputation()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, replicatedCubeBodies: true, headless: true);
        Random random = new(7);
        for (int step = 0; step < 300; step++)
        {
            MapEditScript.Step(random, [map]);
            Assert.Equal(LegacyCompute(map), WorldStateChecksum.Compute(map));
        }

        Assert.Empty(map.FindInvariantViolations());
    }

    [Fact]
    public void CopyPlacedCubesTo_TargetRunningChecksumMatchesFullWalk()
    {
        Map source = new(generateStartupTerrain: true);
        Random random = new(11);
        for (int step = 0; step < 200; step++)
        {
            MapEditScript.Step(random, [source]);
        }

        Map target = new(generateStartupTerrain: true, width: 30, depth: 30);
        source.CopyPlacedCubesTo(target);

        Assert.Equal(LegacyCompute(target), WorldStateChecksum.Compute(target));
        Assert.Empty(target.FindInvariantViolations());
    }

    [Fact]
    public void Compute_IsIndependentOfEditOrder()
    {
        Map first = new(width: 12, depth: 12);
        Map second = new(width: 12, depth: 12);
        Vector3[] cells = [new(2, 1, 2), new(5, 1, 3), new(7, 2, 9), new(3, 1, 8)];
        foreach (Vector3 cell in cells)
        {
            Assert.True(first.AddCube(cell, withPhysics: false, materialId: GeometryMaterialId.Stone));
        }

        foreach (Vector3 cell in cells.Reverse())
        {
            Assert.True(second.AddCube(cell, withPhysics: false, materialId: GeometryMaterialId.Stone));
        }

        Assert.True(first.FractureCubeIntoPieces(new GeometryCellKey(5, 1, 3)));
        Assert.True(second.FractureCubeIntoPieces(new GeometryCellKey(5, 1, 3)));

        Assert.Equal(WorldStateChecksum.Compute(first), WorldStateChecksum.Compute(second));
        Assert.Equal(LegacyCompute(first), WorldStateChecksum.Compute(second));
    }

    [Fact]
    public void Compute_DoesNotAllocate()
    {
        Map map = new(generateStartupTerrain: true);
        Assert.True(map.AddCube(new Vector3(4, 3, 4), withPhysics: false));
        ulong sink = 0;

        AllocationAssert.AtMost(0, () => sink ^= WorldStateChecksum.Compute(map));

        Assert.Equal(LegacyCompute(map), WorldStateChecksum.Compute(map));
        GC.KeepAlive(sink);
    }

    // WorldStateChecksum.Compute exactly as it was before SCALE-7(d): a full walk over the geometry
    // and rubble stores. Kept here as the bit-identity reference.
    private static ulong LegacyCompute(Map map)
    {
        ulong sum = 0;
        ulong count = 0;
        foreach (GeometryCellRecord record in map.GeometryCells.Values.Concat(map.RubbleCells.Values))
        {
            if (record.Key.Y <= 0)
            {
                continue;
            }

            ulong kind;
            if (record.FracturePieces != null)
            {
                kind = 2;
            }
            else if (record.SubdivisionState == null && record.Definition.ShapeKind == GeometryShapeKind.SolidCube)
            {
                kind = 1;
            }
            else
            {
                continue;
            }

            ulong cellHash = Mix(
                ((ulong)(uint)record.Key.X << 42) ^
                ((ulong)(uint)record.Key.Y << 21) ^
                (ulong)(uint)record.Key.Z ^
                (kind << 60) ^
                ((ulong)record.Definition.MaterialId << 52));
            sum = unchecked(sum + cellHash);
            count++;
        }

        return Mix(sum ^ Mix(count));
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
