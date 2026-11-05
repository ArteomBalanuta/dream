using Dream.Simulation.Enemies;

namespace Dream.Rendering.Actors;

// The wolf's clip for a behaviour state. Superseded by EnemyClipSet, which every enemy visual
// carries; this stays as the wolf's named entry point.
//
// With no measured speed a chase resolves to the WALK, which is the gait a caller with no pose to
// measure should get: the gallop is only ever chosen from a speed (EnemyRenderer measures it from
// the drawn pose, because the client holds no speed for an enemy). The speed overload is here so a
// caller that does have one does not have to reach into the catalog.
public static class WolfAnimationResolver
{
    public static string ResolveClip(EnemyBehaviorState behaviorState) =>
        EnemyVisualCatalog.WolfClips.Resolve(behaviorState);

    public static string ResolveClip(EnemyBehaviorState behaviorState, float speed) =>
        EnemyVisualCatalog.WolfClips.Resolve(behaviorState, speed);
}
