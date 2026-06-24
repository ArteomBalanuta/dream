using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class WorldGenerator
{
    private readonly bool _loadVisualAssets;

    public WorldGenerator(bool loadVisualAssets)
    {
        _loadVisualAssets = loadVisualAssets;
    }

    public Cube CreateSolidCube(Vector3 cellPosition)
    {
        return new Cube
        {
            Position = new Vector3(cellPosition.X + 0.5f, cellPosition.Y + 0.5f, cellPosition.Z + 0.5f),
            IsEmpty = false,
            IsSolid = true,
            Animation = _loadVisualAssets ? ModelAssets.GrassCube : null,
            ShaderName = "cube"
        };
    }
}
