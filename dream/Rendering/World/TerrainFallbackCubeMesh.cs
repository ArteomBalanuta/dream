using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

// Unit cube centered on the origin, used for cubes and blocks without a model asset.
// It shares the static chunk vertex layout (position, normal, UV), so the "cube" shader
// samples the terrain texture instead of reading disabled normal/UV attributes.
public static class TerrainFallbackCubeMesh
{
    public static StaticWorldChunkMesh Create()
    {
        StaticVoxelRenderCell unitCube = new(Vector3.Zero, Vector3.One, GeometryMaterialId.Grass);
        return StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            StaticTerrainMaterialRole.Single,
            [unitCube],
            Array.Empty<StaticVoxelRenderCell>());
    }
}
