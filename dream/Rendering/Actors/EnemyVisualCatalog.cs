using OpenTK.Mathematics;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Rendering.Actors;

// The clips one enemy model carries, by behaviour, plus the two gaits a moving enemy picks between.
// A model with only one gait names its Move clip as its Sprint, so the gait choice is a no-op there.
//
// The client cannot read an enemy's authored speed - RemoteEnemyStateApplier builds client-side
// actors it never simulates - so the speed handed in here is MEASURED from the drawn pose
// (EnemyVisualState.ObserveMotion). MoveReferenceSpeed and SprintReferenceSpeed are the speeds at
// which each gait plays at rate 1.0; ResolvePlaybackSpeed scales the stride to the observed speed so
// the feet do not skate, clamped so a stale or snapped pose cannot produce a blur or a freeze.
public readonly record struct EnemyClipSet(
    string Idle,
    string Move,
    string Attack,
    string Die,
    string Sprint,
    float MoveReferenceSpeed = 1.4f,
    float SprintReferenceSpeed = 5.0f,
    float SprintSpeedThreshold = 3.0f,
    float GaitHysteresisSpeed = 0.4f)
{
    // The band the playback rate is held inside. Below it a stride crawls into a freeze, above it
    // the legs blur; both read worse than a slightly skating foot.
    public const float MinPlaybackSpeed = 0.6f;
    public const float MaxPlaybackSpeed = 1.6f;

    // The clip for a behaviour alone, with no measured speed: a moving enemy walks. Kept because
    // WolfAnimationResolver and a few callers have no pose to measure, and equals Resolve(state, 0f).
    public string Resolve(EnemyBehaviorState behaviorState) => Resolve(behaviorState, 0f);

    public string Resolve(EnemyBehaviorState behaviorState, float speed) => behaviorState switch
    {
        EnemyBehaviorState.Chasing => speed >= SprintSpeedThreshold ? Sprint : Move,
        EnemyBehaviorState.Attacking => Attack,
        EnemyBehaviorState.Dead => Die,
        _ => Idle
    };

    // The rate the resolved clip advances at. Only a moving enemy scales its stride; an idle, an
    // attack and a death play as authored. The gait's reference speed divides out, so a wolf
    // galloping at its authored chase speed plays at exactly 1.0 and one dragged along at half
    // that plays at the clamp floor instead of sliding.
    // Whether a moving enemy belongs on the sprint gait, given the gait it is already on. The
    // boundary is one-sided: rising to SprintSpeedThreshold starts the sprint, and only falling
    // GaitHysteresisSpeed below it returns to the walk. Without that band a measured speed that
    // dithers across the threshold renames the clip every few frames, and a rename resets clip
    // time (EnemyVisualState.ApplyClip -> PlayLoop), so the stride visibly stutters.
    public bool ResolveSprinting(float speed, bool sprinting) =>
        sprinting
            ? speed > SprintSpeedThreshold - GaitHysteresisSpeed
            : speed >= SprintSpeedThreshold;

    public float ResolvePlaybackSpeed(EnemyBehaviorState behaviorState, float speed)
    {
        if (behaviorState != EnemyBehaviorState.Chasing)
        {
            return 1f;
        }

        bool sprinting = speed >= SprintSpeedThreshold;
        float reference = sprinting ? SprintReferenceSpeed : MoveReferenceSpeed;
        // No measurement at all (a caller with no pose to measure, the frame an enemy is first
        // drawn) plays the clip as authored instead of at the slow end of the clamp.
        if (!(speed > 0f) || !(reference > 0f) || !float.IsFinite(speed))
        {
            return 1f;
        }

        return Math.Clamp(speed / reference, MinPlaybackSpeed, MaxPlaybackSpeed);
    }
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
    public const string SkeletonKey = "enemy.skeleton";

    // The gait boundary for every enemy: it sits in the empty middle between the fastest shipped
    // patrol walk and the slowest shipped chase, clear of both by the hysteresis, so nothing an
    // enemy actually does lands in the band (a test pins that against enemies.json). The reference
    // speeds are the ground speeds the wolf's walk and gallop clips were authored for, not the
    // simulation's: the shipped chase is slower than the gallop was authored for, so the clip plays
    // at its floor rate (MinPlaybackSpeed) instead of skating.
    private const float PatrolReferenceSpeed = 1.4f;
    private const float ChaseReferenceSpeed = 5.0f;
    internal const float GaitThresholdSpeed = EnemyPresentationBand.GaitThresholdSpeed;
    internal const float GaitHysteresisSpeed = EnemyPresentationBand.GaitHysteresisSpeed;

    public static readonly EnemyClipSet WolfClips = new(
        WolfAssetDefinition.IdleClip,
        WolfAssetDefinition.WalkClip,
        WolfAssetDefinition.AttackClip,
        // The pack carries a Death clip after all; it was aliased to the idle because nobody had
        // read its animation list. ForgedActorAssetTests asserts all five names against the GLB.
        WolfAssetDefinition.DieClip,
        WolfAssetDefinition.GallopClip,
        PatrolReferenceSpeed,
        ChaseReferenceSpeed,
        GaitThresholdSpeed,
        GaitHysteresisSpeed);

    public static readonly EnemyClipSet HoundClips = new(
        BeastAssetDefinition.IdleClip,
        BeastAssetDefinition.MoveClip,
        BeastAssetDefinition.AttackClip,
        BeastAssetDefinition.DieClip,
        BeastAssetDefinition.GallopClip,
        PatrolReferenceSpeed,
        // The hound is the GraveHowler's model and the howler chases at 6.0.
        6.0f,
        GaitThresholdSpeed,
        GaitHysteresisSpeed);

    // The skeleton has ONE gait, so its Sprint is its Move and the gait choice is a no-op. Its
    // sprint reference is the RisenSkeleton's authored chase speed (3.4), the way the hound's is the
    // Grave Howler's 6.0, so the walk clip's stride is scaled to the speed it actually chases at.
    public static readonly EnemyClipSet SkeletonClips = new(
        SkeletonAssetDefinition.IdleClip,
        SkeletonAssetDefinition.MoveClip,
        SkeletonAssetDefinition.AttackClip,
        SkeletonAssetDefinition.DieClip,
        SkeletonAssetDefinition.MoveClip,
        PatrolReferenceSpeed,
        3.4f,
        GaitThresholdSpeed,
        GaitHysteresisSpeed);

    // Static so a Resolve never allocates a closure.
    private static readonly Func<float> WolfScale = static () => WolfAssetDefinition.VisualScale;
    private static readonly Func<float> HoundScale = static () => BeastAssetDefinition.HoundVisualScale;
    private static readonly Func<float> SkeletonScale = static () => SkeletonAssetDefinition.VisualScale;

    private static readonly EnemyVisual Wolf = new(
        WolfAssetDefinition.ModelPath, 1f, Vector3.Zero, WolfClips, WolfScale);

    private static readonly EnemyVisual DireWolf = new(
        WolfAssetDefinition.ModelPath, 1.35f, new Vector3(-0.45f, -0.45f, -0.35f), WolfClips, WolfScale);

    // The hound replaces the DIRE WOLF's model on GraveHowler, so it keeps the dire wolf's size and
    // tint: spec Q4 sanctions swapping the model, not shrinking the enemy. Both models normalise to
    // the same 0.60 world height, so without the 1.35 multiplier the level-8 top-tier enemy would
    // draw at the plain wolf's size and untinted - the lightest of the four - while its aggro radius
    // (14) and attack range (1.35) stayed those of the big dark wolf.
    private static readonly EnemyVisual Hound = new(
        BeastAssetDefinition.HoundModelPath, 1.35f, new Vector3(-0.45f, -0.45f, -0.35f), HoundClips, HoundScale);

    // Unlike the hound, the skeleton does not replace another enemy's model: it normalises to its own
    // biped height (the player's), so it needs no size multiplier to hold a level's presence. And its
    // materials are authored dark already, so a tint offset would only crush them toward black.
    private static readonly EnemyVisual Skeleton = new(
        SkeletonAssetDefinition.ModelPath, 1f, Vector3.Zero, SkeletonClips, SkeletonScale);

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

        if (visualKey.Length == SkeletonKey.Length &&
            string.Equals(visualKey, SkeletonKey, StringComparison.Ordinal))
        {
            return Skeleton;
        }

        return Wolf;
    }
}
