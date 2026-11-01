using OpenTK.Mathematics;
using Dream.Rendering.Models;

namespace Dream.Rendering.Actors;

// How one enemy kind looks on the client (wave I). TintOffset is added to 1 and multiplies the base
// colour in enemyFragment.glsl (uActorTintOffset), so (0, 0, 0) is today's look.
public readonly record struct EnemyVisual(string ModelPath, float ScaleMultiplier, Vector3 TintOffset);

// The client's own table of enemy looks, keyed by the visual key the server sends. The wire value is
// hostile: an unknown, null or oversized key draws the default wolf. Allocates nothing.
public static class EnemyVisualCatalog
{
    public const string WolfKey = "enemy.wolf";
    public const string DireWolfKey = "enemy.dire-wolf";

    private static readonly EnemyVisual Wolf = new(WolfAssetDefinition.ModelPath, 1f, Vector3.Zero);

    private static readonly EnemyVisual DireWolf = new(
        WolfAssetDefinition.ModelPath, 1.35f, new Vector3(-0.45f, -0.45f, -0.35f));

    public static EnemyVisual Resolve(string? visualKey)
    {
        // Length first so an oversized key is rejected without a compare.
        if (visualKey is not null && visualKey.Length == DireWolfKey.Length &&
            string.Equals(visualKey, DireWolfKey, StringComparison.Ordinal))
        {
            return DireWolf;
        }

        return Wolf;
    }
}
