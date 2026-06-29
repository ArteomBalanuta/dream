using OpenTK.Mathematics;

public static class EnemyPlayerCollisionResolver
{
    public static Vector3 ResolvePlayerPosition(
        Vector3 playerPosition,
        Vector3 enemyPosition,
        float minimumDistance,
        Vector3? fallbackDirection = null)
    {
        if (minimumDistance <= 0f)
        {
            return playerPosition;
        }

        Vector3 enemyToPlayer = playerPosition - enemyPosition;
        enemyToPlayer.Y = 0f;
        float distance = enemyToPlayer.Length;
        if (distance >= minimumDistance)
        {
            return playerPosition;
        }

        Vector3 direction = distance > 0.0001f
            ? enemyToPlayer / distance
            : ResolveFallbackDirection(fallbackDirection);

        Vector3 resolved = enemyPosition + (direction * minimumDistance);
        resolved.Y = playerPosition.Y;
        return resolved;
    }

    private static Vector3 ResolveFallbackDirection(Vector3? fallbackDirection)
    {
        if (fallbackDirection.HasValue)
        {
            Vector3 direction = fallbackDirection.Value;
            direction.Y = 0f;
            if (direction.LengthSquared > 0.0001f)
            {
                return direction.Normalized();
            }
        }

        return Vector3.UnitZ;
    }
}
