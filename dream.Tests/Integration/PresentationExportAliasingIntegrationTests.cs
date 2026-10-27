using BulletSharp;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// MEM-9 rewrites the map's render and collision descriptor arrays in place when rubble or a coarse
// cube moves (Map.BuildRenderBlocks / BuildCollisionBodies with a previous array of the right
// length). That is only safe if no export ever hands one of those arrays out: a presentation export
// taken on frame N is consumed later (and, on the render thread, while the next tick runs), so its
// values must never change after the fact. Over many frames of moving rubble, every export taken so
// far is compared with a copy made when it was taken. The static part (Task A's cached static
// blocks and cells) is checked the same way and must not be rebuilt by dynamic movement at all.
public class PresentationExportAliasingIntegrationTests(ITestOutputHelper output)
{
    private const int MapSize = 12;
    private const int Frames = 240;
    private const float FixedDelta = 1f / 60f;
    // Rubble is kicked this often, so it keeps moving for the whole run instead of settling.
    private const int KickEveryFrames = 15;
    private const float RenderRadius = 64f;

    private static readonly GeometryCellKey[] FracturedCells = [new(3, 1, 3), new(6, 1, 4), new(4, 1, 7), new(8, 1, 8)];
    private static readonly Vector3[] StaticCubes = [new(1f, 1f, 1f), new(10f, 1f, 1f), new(1f, 1f, 10f)];

    [Fact]
    public void MapPresentationExport_IsNeverMutatedAfterTheFact_WhileRubbleMoves()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: MapSize, depth: MapSize);
        BuildScene(map);
        PhysicsSyncSystem sync = new();

        RunFrames(
            output,
            physics,
            () => map.CapturePresentationGeometryRenderExport(new Vector3(MapSize / 2f, 2f, MapSize / 2f), RenderRadius),
            () =>
            {
                physics.StepSimulationFixed(FixedDelta);
                sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
                Assert.Empty(map.FindInvariantViolations());
            });
    }

    [Fact]
    public void ClientWorldSessionPresentationState_IsNeverMutatedAfterTheFact_WhileRubbleMovesThroughPredictionTicks()
    {
        using ClientWorldSession session = new(MapSize, new Vector3(6f, 1.5f, 1.5f), new SceneEventHub());
        BuildScene(session.Map);
        ulong tick = 0;

        RunFrames(
            output,
            session.Physics,
            () => session.CapturePresentationState(RenderRadius).VisibleWorld,
            () =>
            {
                tick++;
                session.AdvancePredictionTick(
                    tick,
                    FixedDelta,
                    PlayerCommandFrame.CreateIdle(PlayerSessionId.LocalDefault, tick),
                    PlayerSimulationTuning.AuthoritativeDefault);
                Assert.Empty(session.Map.FindInvariantViolations());
            });
    }

    private static void BuildScene(Map map)
    {
        foreach (Vector3 position in StaticCubes)
        {
            Assert.True(map.AddCube(position, isStatic: true, materialId: GeometryMaterialId.Stone));
        }

        // A surviving physics cube keeps the cube sync path (UpdateCubePosition) in the run too.
        Assert.True(map.AddCube(new Vector3(5f, 1f, 5f), withPhysics: true));
        GeometryMaterialId[] materials = [GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Sand, GeometryMaterialId.Stone];
        for (int i = 0; i < FracturedCells.Length; i++)
        {
            GeometryCellKey cell = FracturedCells[i];
            Assert.True(map.AddCube(new Vector3(cell.X, cell.Y, cell.Z), withPhysics: true, materialId: materials[i]));
            Assert.True(map.FractureCubeIntoPieces(cell));
        }
    }

    private static void RunFrames(ITestOutputHelper output, PhysicsIntegrator physics, Func<GeometryRenderExport> capture, Action advance)
    {
        List<(GeometryRenderExport Export, ExportCopy Copy, int Frame)> taken = [];
        int framesWithMovedRubble = 0;
        ExportCopy? previous = null;
        Random random = new(1234);
        for (int frame = 0; frame < Frames; frame++)
        {
            GeometryRenderExport export = capture();
            ExportCopy copy = ExportCopy.Of(export);
            Assert.True(copy.DynamicBlocks.Length > 0, $"frame {frame}: no rubble in the export.");
            if (previous != null)
            {
                // The static content never changes in this scene, so the cache is reused as is.
                Assert.Equal(previous.StaticContentVersion, copy.StaticContentVersion);
                Assert.Equal(previous.StaticBlocks, copy.StaticBlocks);
                Assert.Equal(previous.StaticCompatibilityCubes, copy.StaticCompatibilityCubes);
                // Moving rubble never rebuilds the static cache: the same cached arrays are reused.
                GeometryRenderExport previousExport = taken[^1].Export;
                Assert.Same(previousExport.Blocks, export.Blocks);
                Assert.Same(previousExport.CompatibilityCubes, export.CompatibilityCubes);
                if (!previous.DynamicBlocks.SequenceEqual(copy.DynamicBlocks))
                {
                    framesWithMovedRubble++;
                }
            }

            taken.Add((export, copy, frame));
            previous = copy;

            if (frame % KickEveryFrames == 0)
            {
                KickRubble(physics, random);
            }

            advance();

            // Every export taken so far still holds exactly what it held when it was taken.
            foreach ((GeometryRenderExport earlier, ExportCopy earlierCopy, int takenAt) in taken)
            {
                earlierCopy.AssertUnchanged(earlier, $"export of frame {takenAt}, checked after frame {frame}");
            }
        }

        output.WriteLine($"frames {Frames}, frames whose rubble moved {framesWithMovedRubble}, rubble blocks {previous!.DynamicBlocks.Length}");
        // Rubble really moved for most of the run, so the in-place rewrite paths ran.
        Assert.True(framesWithMovedRubble > Frames / 2, $"Rubble moved in only {framesWithMovedRubble} of {Frames} frames.");
    }

    private static void KickRubble(PhysicsIntegrator physics, Random random)
    {
        foreach (RigidBody body in physics.DynamicMapCubeBodies)
        {
            if (body.UserObject is not GeometryDynamicBodyReference)
            {
                continue;
            }

            body.Activate(true);
            body.LinearVelocity = new BulletSharp.Math.Vector3(
                (float)(random.NextDouble() - 0.5) * 2f,
                3f,
                (float)(random.NextDouble() - 0.5) * 2f);
        }
    }

    // Deep copy of what an export exposes, by value. Cubes are compared by reference and cell: a
    // Cube is a live object whose Position is the map's own (by design), but the list of cubes an
    // export names must not change.
    private sealed record ExportCopy(
        long StaticContentVersion,
        GeometryRenderBlockDescriptor[] StaticBlocks,
        GeometryRenderBlockDescriptor[] DynamicBlocks,
        Cube[] StaticCompatibilityCubes,
        Cube[] DynamicCompatibilityCubes,
        GeometryCellKey[] Cells,
        GeometryCellKey[] DirtyCells,
        GeometryCellKey[] StaticDirtyCells)
    {
        public static ExportCopy Of(GeometryRenderExport export)
        {
            return new ExportCopy(
                export.StaticContentVersion,
                export.Blocks.ToArray(),
                export.DynamicBlocks.ToArray(),
                export.CompatibilityCubes.ToArray(),
                export.DynamicCompatibilityCubes.ToArray(),
                export.Cells.Select(cell => cell.Key).ToArray(),
                export.DirtyCells.ToArray(),
                export.StaticDirtyCells.ToArray());
        }

        public void AssertUnchanged(GeometryRenderExport export, string what)
        {
            AssertBlocks(DynamicBlocks, export.DynamicBlocks, what + ", dynamic blocks");
            AssertBlocks(StaticBlocks, export.Blocks, what + ", static blocks");
            Assert.True(StaticCompatibilityCubes.SequenceEqual(export.CompatibilityCubes, ReferenceEqualityComparer.Instance), $"{what}: static compatibility cubes changed.");
            Assert.True(DynamicCompatibilityCubes.SequenceEqual(export.DynamicCompatibilityCubes, ReferenceEqualityComparer.Instance), $"{what}: dynamic compatibility cubes changed.");
            Assert.True(Cells.SequenceEqual(export.Cells.Select(cell => cell.Key)), $"{what}: cells changed.");
            Assert.True(DirtyCells.SequenceEqual(export.DirtyCells), $"{what}: dirty cells changed.");
            Assert.True(StaticDirtyCells.SequenceEqual(export.StaticDirtyCells), $"{what}: static dirty cells changed.");
        }

        private static void AssertBlocks(GeometryRenderBlockDescriptor[] expected, IReadOnlyList<GeometryRenderBlockDescriptor> actual, string what)
        {
            Assert.True(expected.Length == actual.Count, $"{what}: count changed from {expected.Length} to {actual.Count}.");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(
                    expected[i] == actual[i],
                    $"{what}: block {i} changed after the fact from {expected[i].Center} to {actual[i].Center} (cell {expected[i].CellKey}).");
            }
        }
    }
}
