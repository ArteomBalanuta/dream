using OpenTK.Mathematics;

public static class EnemyVisualPlacement
{
    public static Vector3 ResolveRenderPosition(
        Vector3 actorPosition,
        Vector3 cameraPosition,
        float minimumDistanceFromCamera)
    {
        if (minimumDistanceFromCamera <= 0f)
        {
            return actorPosition;
        }

        Vector3 horizontalOffset = actorPosition - cameraPosition;
        horizontalOffset.Y = 0f;
        float currentDistance = horizontalOffset.Length;
        if (currentDistance >= minimumDistanceFromCamera)
        {
            return actorPosition;
        }

        Vector3 direction = currentDistance > 0.0001f
            ? horizontalOffset / currentDistance
            : Vector3.UnitZ;
        Vector3 resolved = cameraPosition + (direction * minimumDistanceFromCamera);
        resolved.Y = actorPosition.Y;
        return resolved;
    }
}
