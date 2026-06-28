using OpenTK.Mathematics;

public static class WolfSpawnPolicy
{
    public const float FloorSurfaceY = 1f;
    private static readonly Vector3 DefaultSpawn = new(5.5f, FloorSurfaceY, 2.5f);

    public static Vector3 ResolveInitialSpawn(int width, int depth)
    {
        return new Vector3(
            ClampAxis(DefaultSpawn.X, width),
            FloorSurfaceY,
            ClampAxis(DefaultSpawn.Z, depth));
    }

    private static float ClampAxis(float desiredCenter, int size)
    {
        float min = 0.5f;
        float max = Math.Max(min, size - 1.5f);
        return Math.Clamp(desiredCenter, min, max);
    }
}
