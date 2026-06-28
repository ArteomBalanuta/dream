using OpenTK.Mathematics;
using Xunit;

public class EnemyVisualPlacementTests
{
    [Fact]
    public void ResolveRenderPosition_Preserves_Position_When_Already_Outside_Minimum_Distance()
    {
        Vector3 resolved = EnemyVisualPlacement.ResolveRenderPosition(
            new Vector3(5f, 0f, 5f),
            new Vector3(0f, 1.5f, 0f),
            minimumDistanceFromCamera: 2.5f);

        Assert.Equal(new Vector3(5f, 0f, 5f), resolved);
    }

    [Fact]
    public void ResolveRenderPosition_Pushes_Enemy_Away_From_Camera_On_Xz_Plane()
    {
        Vector3 resolved = EnemyVisualPlacement.ResolveRenderPosition(
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1.5f, 0f),
            minimumDistanceFromCamera: 2.5f);

        Assert.Equal(2.5f, resolved.X, 3);
        Assert.Equal(0f, resolved.Y, 3);
        Assert.Equal(0f, resolved.Z, 3);
    }
}
