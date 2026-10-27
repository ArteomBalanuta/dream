using Dream.Client.Replication;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Replication;

// When the local cube's material differs from the mutation's, the fallback fracture used to build
// the rubble with the local material and then rebuild it with the resolved one. The cell now
// carries the resolved material before the fracture, so the result must equal a direct fracture
// of a cube that already had that material, with no leftover bodies from a first build.
public class FallbackFractureMaterialTests
{
    private static readonly GeometryCellKey Cell = new(3, 1, 3);

    [Fact]
    public void MaterialMismatch_MatchesDirectFractureOfResolvedMaterial()
    {
        using PhysicsIntegrator referencePhysics = new();
        Map reference = new(referencePhysics, width: 10, depth: 10);
        Assert.True(reference.AddCube(new Vector3(3f, 1f, 3f), materialId: GeometryMaterialId.Rock));
        Assert.True(reference.FractureCubeIntoPieces(Cell));

        using PhysicsIntegrator clientPhysics = new();
        Map client = new(clientPhysics, width: 10, depth: 10);
        Assert.True(client.AddCube(new Vector3(3f, 1f, 3f), materialId: GeometryMaterialId.Grass));

        RemoteWorldMutationApplier.ApplyWorldMutations(
            client,
            [new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(3f, 1f, 3f), MaterialId: GeometryMaterialId.Rock)]);

        GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(Cell));
        GeometryCellRecord referenceRubble = Assert.IsType<GeometryCellRecord>(reference.TryGetRubbleAtCell(Cell));
        Assert.Equal(GeometryMaterialId.Rock, clientRubble.Definition.MaterialId);
        Assert.Equal(CubeFracturePattern.CreateFirstPassRubble(Cell), clientRubble.FracturePieces);
        Assert.Equal(referenceRubble.FracturePieces, clientRubble.FracturePieces);
        Assert.Null(client.TryGetCubeAtCell(3, 1, 3));
        Assert.Null(client.TryGetGeometryCellAtCell(3, 1, 3));
        Assert.Equal(
            referencePhysics.World.NumCollisionObjects,
            clientPhysics.World.NumCollisionObjects);
    }

    [Fact]
    public void MaterialMatch_KeepsLocalCubeUntilFracture()
    {
        Map client = new(width: 10, depth: 10);
        Assert.True(client.AddCube(new Vector3(3f, 1f, 3f), withPhysics: false, materialId: GeometryMaterialId.Stone));

        RemoteWorldMutationApplier.ApplyWorldMutations(
            client,
            [new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(3f, 1f, 3f), MaterialId: GeometryMaterialId.Stone)]);

        GeometryCellRecord clientRubble = Assert.IsType<GeometryCellRecord>(client.TryGetRubbleAtCell(Cell));
        Assert.Equal(GeometryMaterialId.Stone, clientRubble.Definition.MaterialId);
        Assert.Equal(CubeFracturePattern.CreateFirstPassRubble(Cell), clientRubble.FracturePieces);
        Assert.Null(client.TryGetCubeAtCell(3, 1, 3));
    }
}
