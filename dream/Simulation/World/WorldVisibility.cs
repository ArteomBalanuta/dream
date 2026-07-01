using OpenTK.Mathematics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public static class WorldVisibility
{
    public const float MaxRenderDistance = 48f;
    private const float MinForwardDot = -0.25f;
    private const float NearAlwaysVisibleDistance = 1.5f;

    public static bool IsCubeVisible(Vector3 cameraPosition, Vector3 cameraFront, Cube cube)
    {
        return IsPositionVisible(
            cameraPosition,
            cameraFront,
            cube.Position,
            CalculateBoundingRadius(cube.Side));
    }

    public static bool IsGeometryBlockVisible(
        Vector3 cameraPosition,
        Vector3 cameraFront,
        GeometryRenderBlockDescriptor block)
    {
        return IsPositionVisible(
            cameraPosition,
            cameraFront,
            block.Center,
            CalculateBoundingRadius(block.SideLength));
    }

    public static bool IsPositionVisible(Vector3 cameraPosition, Vector3 cameraFront, Vector3 position)
    {
        return IsPositionVisible(cameraPosition, cameraFront, position, boundingRadius: 0f);
    }

    public static bool IsPositionVisible(
        Vector3 cameraPosition,
        Vector3 cameraFront,
        Vector3 position,
        float boundingRadius)
    {
        Vector3 toCube = position - cameraPosition;
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
        float distance = MathF.Sqrt(distanceSquared);
        float boundSlack = distance > float.Epsilon
            ? Math.Clamp(boundingRadius / distance, 0f, 0.75f)
            : 0f;
        return forwardDot >= (MinForwardDot - boundSlack);
    }

    private static float CalculateBoundingRadius(float sideLength)
    {
        return sideLength * 0.8660254f;
    }
}
