using OpenTK.Mathematics;

public static class WorldVisibility
{
    public const float MaxRenderDistance = 48f;
    private const float MinForwardDot = -0.25f;
    private const float NearAlwaysVisibleDistance = 1.5f;

    public static bool IsCubeVisible(Vector3 cameraPosition, Vector3 cameraFront, Cube cube)
    {
        Vector3 toCube = cube.Position - cameraPosition;
        float distanceSquared = toCube.LengthSquared;

        if (distanceSquared <= NearAlwaysVisibleDistance * NearAlwaysVisibleDistance)
        {
            return true;
        }

        if (distanceSquared > MaxRenderDistance * MaxRenderDistance)
        {
            return false;
        }

        if (distanceSquared <= float.Epsilon)
        {
            return true;
        }

        Vector3 directionToCube = Vector3.Normalize(toCube);
        float forwardDot = Vector3.Dot(cameraFront, directionToCube);
        return forwardDot >= MinForwardDot;
    }
}
