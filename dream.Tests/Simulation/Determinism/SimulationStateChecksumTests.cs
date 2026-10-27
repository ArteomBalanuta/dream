using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;

namespace Dream.Tests.Simulation.Determinism;

public class SimulationStateChecksumTests
{
    private const ulong MutationSequence = 7UL;
    private static readonly Vector3 PlacedCube = new(2f, 1f, 2f);
    private static readonly GeometryCellKey RubbleCell = new(4, 1, 4);

    // Golden value for BuildMap() + BuildActors() + MutationSequence. It pins the hash layout so an
    // accidental change (field order, quantum, mixing, a dropped field) is caught. If you change
    // SimulationStateChecksum or WorldStateChecksum on purpose, rerun this test, check that only
    // the intended change moved the value, and paste the new value from the failure message here.
    // Mismatched versions of the checksum across a server and a recording are then expected.
    private const ulong GoldenChecksum = 12876812463915946221UL;

    [Fact]
    public void IdenticalState_GivesIdenticalChecksum()
    {
        ulong first = SimulationStateChecksum.Compute(BuildMap(), BuildActors(), MutationSequence);
        ulong second = SimulationStateChecksum.Compute(BuildMap(), BuildActors(), MutationSequence);

        Assert.Equal(first, second);
        Assert.Equal(
            SimulationStateChecksum.ComputeParts(BuildMap(), BuildActors(), MutationSequence),
            SimulationStateChecksum.ComputeParts(BuildMap(), BuildActors(), MutationSequence));
    }

    [Fact]
    public void Checksum_IsIndependentOfActorAndRubbleInsertionOrder()
    {
        List<ActorSnapshot> reversedActors = BuildActors();
        reversedActors.Reverse();
        Map reversedMap = new(width: 8, height: 4, depth: 8, loadVisualAssets: false);
        Assert.True(reversedMap.RestoreFracturePieces(RubbleCell, GeometryMaterialId.Rock, RubblePieces(), withPhysics: false));
        Assert.True(reversedMap.RestoreFracturePieces(new GeometryCellKey(5, 1, 1), GeometryMaterialId.Sand, OtherRubblePieces(), withPhysics: false));
        Assert.True(reversedMap.AddCube(PlacedCube, withPhysics: false, materialId: GeometryMaterialId.Stone));

        Map map = BuildMap();
        Assert.True(map.RestoreFracturePieces(new GeometryCellKey(5, 1, 1), GeometryMaterialId.Sand, OtherRubblePieces(), withPhysics: false));

        Assert.Equal(
            SimulationStateChecksum.Compute(map, BuildActors(), MutationSequence),
            SimulationStateChecksum.Compute(reversedMap, reversedActors, MutationSequence));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("facing")]
    [InlineData("health")]
    [InlineData("max health")]
    [InlineData("alive")]
    [InlineData("level")]
    [InlineData("experience")]
    [InlineData("progression removed")]
    [InlineData("actor id")]
    [InlineData("session")]
    [InlineData("actor removed")]
    public void Checksum_ChangesWhenAnActorFieldChanges(string field)
    {
        Map map = BuildMap();
        ulong baseline = SimulationStateChecksum.Compute(map, BuildActors(), MutationSequence);
        List<ActorSnapshot> actors = BuildActors();
        ActorSnapshot player = actors[0];
        PlayerProgressionSnapshot progression = player.Progression!.Value;
        switch (field)
        {
            case "position":
                actors[0] = player with { Position = player.Position + new Vector3(0f, 0f, 0.001f) };
                break;
            case "facing":
                actors[0] = player with { Facing = new Vector3(0f, 0f, -1f) };
                break;
            case "health":
                actors[0] = player with { CurrentHealth = player.CurrentHealth - 1 };
                break;
            case "max health":
                actors[0] = player with { MaxHealth = player.MaxHealth + 1 };
                break;
            case "alive":
                actors[0] = player with { IsAlive = false };
                break;
            case "level":
                actors[0] = player with { Progression = progression with { Level = progression.Level + 1 } };
                break;
            case "experience":
                actors[0] = player with { Progression = progression with { Experience = progression.Experience + 1 } };
                break;
            case "progression removed":
                actors[0] = player with { Progression = null };
                break;
            case "actor id":
                actors[0] = player with { ActorId = player.ActorId + 1 };
                break;
            case "session":
                actors[0] = player with { SessionId = "session-b" };
                break;
            case "actor removed":
                actors.RemoveAt(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        SimulationStateChecksum.Parts parts = SimulationStateChecksum.ComputeParts(map, actors, MutationSequence);
        Assert.NotEqual(baseline, parts.Combined);
        Assert.Equal(SimulationStateChecksum.ComputeParts(map, BuildActors(), MutationSequence) with { Actors = parts.Actors }, parts);
    }

    [Fact]
    public void Checksum_IsUnchangedByAPositionChangeBelowTheQuantum()
    {
        Map map = BuildMap();
        ulong baseline = SimulationStateChecksum.Compute(map, BuildActors(), MutationSequence);
        List<ActorSnapshot> actors = BuildActors();
        actors[0] = actors[0] with { Position = actors[0].Position + new Vector3(0.00001f, -0.00001f, 0.00002f) };
        actors[1] = actors[1] with { Facing = actors[1].Facing + new Vector3(0f, 0.00002f, 0f) };

        Assert.Equal(baseline, SimulationStateChecksum.Compute(map, actors, MutationSequence));

        Assert.True(map.UpdateFracturePiecePosition(RubbleCell, 0, RubblePieces()[0].Center + new Vector3(0.00002f, 0f, 0f)));
        Assert.Equal(baseline, SimulationStateChecksum.Compute(map, actors, MutationSequence));
    }

    [Fact]
    public void Checksum_ChangesWhenARubblePieceMoves()
    {
        Map map = BuildMap();
        SimulationStateChecksum.Parts baseline = SimulationStateChecksum.ComputeParts(map, BuildActors(), MutationSequence);

        Assert.True(map.UpdateFracturePiecePosition(RubbleCell, 1, RubblePieces()[1].Center + new Vector3(0f, -0.01f, 0f)));
        SimulationStateChecksum.Parts moved = SimulationStateChecksum.ComputeParts(map, BuildActors(), MutationSequence);

        Assert.NotEqual(baseline.Combined, moved.Combined);
        Assert.NotEqual(baseline.Rubble, moved.Rubble);
        Assert.Equal(baseline.World, moved.World);
    }

    [Fact]
    public void Checksum_ChangesWhenRubbleIsRemoved()
    {
        Map map = BuildMap();
        ulong baseline = SimulationStateChecksum.Compute(map, BuildActors(), MutationSequence);

        Assert.True(map.RemoveRubble(RubbleCell));

        Assert.NotEqual(baseline, SimulationStateChecksum.Compute(map, BuildActors(), MutationSequence));
    }

    [Fact]
    public void Checksum_ChangesWhenAWorldCellChanges()
    {
        Map map = BuildMap();
        SimulationStateChecksum.Parts baseline = SimulationStateChecksum.ComputeParts(map, BuildActors(), MutationSequence);

        Assert.True(map.AddCube(new Vector3(1f, 1f, 5f), withPhysics: false, materialId: GeometryMaterialId.Stone));
        SimulationStateChecksum.Parts changed = SimulationStateChecksum.ComputeParts(map, BuildActors(), MutationSequence);

        Assert.NotEqual(baseline.Combined, changed.Combined);
        Assert.NotEqual(baseline.World, changed.World);
        Assert.Equal(WorldStateChecksum.Compute(map), changed.World);
    }

    [Fact]
    public void Checksum_ChangesWhenTheMutationSequenceChanges()
    {
        Map map = BuildMap();

        Assert.NotEqual(
            SimulationStateChecksum.Compute(map, BuildActors(), MutationSequence),
            SimulationStateChecksum.Compute(map, BuildActors(), MutationSequence + 1));
    }

    [Fact]
    public void Checksum_OfTheFixedScenario_MatchesTheGoldenValue()
    {
        Assert.Equal(GoldenChecksum, SimulationStateChecksum.Compute(BuildMap(), BuildActors(), MutationSequence));
    }

    [Fact]
    public void RuntimeOverload_MatchesTheComponentOverload_AndIsEqualForTwoRuntimesWithTheSameSeed()
    {
        using WorldSessionRuntime first = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false, simulationSeed: 11UL);
        using WorldSessionRuntime second = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false, simulationSeed: 11UL);

        ulong checksum = SimulationStateChecksum.Compute(first);

        Assert.Equal(
            SimulationStateChecksum.Compute(first.Map, first.CaptureActorSnapshots(), first.LastWorldMutationSequence),
            checksum);
        Assert.Equal(checksum, SimulationStateChecksum.Compute(second));
    }

    private static Map BuildMap()
    {
        Map map = new(width: 8, height: 4, depth: 8, loadVisualAssets: false);
        Assert.True(map.AddCube(PlacedCube, withPhysics: false, materialId: GeometryMaterialId.Stone));
        Assert.True(map.RestoreFracturePieces(RubbleCell, GeometryMaterialId.Rock, RubblePieces(), withPhysics: false));
        return map;
    }

    private static GeometryFracturePiece[] RubblePieces()
    {
        return
        [
            new GeometryFracturePiece(new Vector3(4.25f, 1.25f, 4.25f), 0.5f),
            new GeometryFracturePiece(new Vector3(4.75f, 1.25f, 4.75f), 0.5f)
        ];
    }

    private static GeometryFracturePiece[] OtherRubblePieces()
    {
        return [new GeometryFracturePiece(new Vector3(5.5f, 1.25f, 1.5f), 0.25f)];
    }

    private static List<ActorSnapshot> BuildActors()
    {
        return
        [
            new ActorSnapshot(
                1,
                CreatureKind.Player,
                "session-a",
                new Vector3(1.5f, 1f, 1.5f),
                Vector3.UnitZ,
                90,
                100,
                true,
                Progression: new PlayerProgressionSnapshot(2, 30, 70)),
            new ActorSnapshot(
                4300,
                CreatureKind.Enemy,
                null,
                new Vector3(3.5f, 1f, 3.5f),
                -Vector3.UnitX,
                40,
                50,
                true)
        ];
    }
}
