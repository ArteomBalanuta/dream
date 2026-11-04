using OpenTK.Mathematics;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Rendering.Actors;

// The clips one enemy model carries, by behaviour. A model without a death clip (the wolf) names
// its idle there, which is what the renderer did before a clip set existed.
public readonly record struct EnemyClipSet(string Idle, string Move, string Attack, string Die)
{
    public string Resolve(EnemyBehaviorState behaviorState) => behaviorState switch
    {
        EnemyBehaviorState.Chasing => Move,
        EnemyBehaviorState.Attacking => Attack,
        EnemyBehaviorState.Dead => Die,
        _ => Idle
    };
}

// How one enemy kind looks on the client. TintOffset is added to 1 and multiplies the base colour
// in enemyFragment.glsl (uActorTintOffset), so (0, 0, 0) is the untinted look. ModelPath, Clips and
// AuthoredScale make the model a property of the visual instead of a constant in the renderer:
// AuthoredScale is a delegate because a forged model measures its authored height from the GLB on
// first use, which must not happen in a static field initializer.
public readonly record struct EnemyVisual(
    string ModelPath,
    float ScaleMultiplier,
    Vector3 TintOffset,
    EnemyClipSet Clips,
    Func<float> AuthoredScale)
{
    // The uniform scale this enemy draws at: the model's own normalization times the template's
    // multiplier. Resolving the normalization reads the GLB the first time only.
    public float ResolveScale() => AuthoredScale() * ScaleMultiplier;
}

// The client's own table of enemy looks, keyed by the visual key the server sends. The wire value is
// hostile: an unknown, null or oversized key draws the default wolf. Allocates nothing.
public static class EnemyVisualCatalog
{
    public const string WolfKey = "enemy.wolf";
    public const string DireWolfKey = "enemy.dire-wolf";
    public const string HoundKey = "enemy.hound";

    public static readonly EnemyClipSet WolfClips = new(
        WolfAssetDefinition.IdleClip,
        WolfAssetDefinition.MoveClip,
        WolfAssetDefinition.AttackClip,
        // The wolf pack has no death clip; dying held the idle before the clip set existed.
        WolfAssetDefinition.IdleClip);

    public static readonly EnemyClipSet HoundClips = new(
        BeastAssetDefinition.IdleClip,
        BeastAssetDefinition.MoveClip,
        BeastAssetDefinition.AttackClip,
        BeastAssetDefinition.DieClip);

    // Static so a Resolve never allocates a closure.
    private static readonly Func<float> WolfScale = static () => WolfAssetDefinition.VisualScale;
    private static readonly Func<float> HoundScale = static () => BeastAssetDefinition.HoundVisualScale;

    private static readonly EnemyVisual Wolf = new(
        WolfAssetDefinition.ModelPath, 1f, Vector3.Zero, WolfClips, WolfScale);

    private static readonly EnemyVisual DireWolf = new(
        WolfAssetDefinition.ModelPath, 1.35f, new Vector3(-0.45f, -0.45f, -0.35f), WolfClips, WolfScale);

    private static readonly EnemyVisual Hound = new(
        BeastAssetDefinition.HoundModelPath, 1f, Vector3.Zero, HoundClips, HoundScale);

    // What an unknown key draws, and the wolf's own look.
    public static EnemyVisual Default => Wolf;

    public static EnemyVisual Resolve(string? visualKey)
    {
        if (visualKey is null)
        {
            return Wolf;
        }

        // Length first so an oversized key is rejected without a compare.
        if (visualKey.Length == DireWolfKey.Length &&
            string.Equals(visualKey, DireWolfKey, StringComparison.Ordinal))
        {
            return DireWolf;
        }

        if (visualKey.Length == HoundKey.Length &&
            string.Equals(visualKey, HoundKey, StringComparison.Ordinal))
        {
            return Hound;
        }

        return Wolf;
    }
}
